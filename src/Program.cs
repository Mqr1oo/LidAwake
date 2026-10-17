using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("LidAwake")]
[assembly: AssemblyDescription("Keeps a Windows laptop working with the lid closed while a job runs")]
[assembly: AssemblyProduct("LidAwake")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace LidAwake
{
    static class Program
    {
        const string MutexName = "Local\\LidAwake.Instance.v1";

        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "";
            string arg1 = args.Length > 1 ? args[1] : null;
            try
            {
                switch (cmd)
                {
                    case "": return Installer.IsInstalledCopy ? Manual() : Installer.Install(true);
                    case "on": return Manual();
                    case "tray": return Native.FindWindow(null, App.IpcWindowTitle) != IntPtr.Zero ? 0 : RunInstance(true, false, null, 0, arg1 == "--welcome");
                    case "quit": Ipc.Send("quit", "", 0); return 0;
                    case "preview": return Preview(arg1);
                    case "run": return RunInstance(false, false, arg1 ?? "job", args.Length > 2 ? ParseInt(args[2]) : 0);
                    case "hook": return Hook();
                    case "start": Start(arg1 ?? "cli", FindAgentPid()); return 0;
                    case "stop": Ipc.Send("stop", arg1 ?? "cli", 0); return 0;
                    case "off": Ipc.Send("off", "", 0); return 0;
                    case "status": return Status();
                    case "restore": return Restore();
                    case "install": return Installer.Install(false);
                    case "uninstall": return Installer.Uninstall();
                    case "connect": Cli.Out(ClaudeSetup.Connect(Installer.InstalledExe) ? "Connected to Claude Code." : "Could not edit settings.json, see the log."); return 0;
                    case "disconnect": ClaudeSetup.Disconnect(); Cli.Out("Disconnected from Claude Code."); return 0;
                    case "hooks": Cli.Out(Installer.HooksJson(Installer.InstalledExe)); return 0;
                    default: Cli.Out(Strings.Usage); return cmd == "help" || cmd == "h" || cmd == "?" ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                App.Log("Command '" + cmd + "' failed: " + ex);
                if (cmd == "hook") return 0;   // a hook must never block Claude
                Cli.Err(ex.Message);
                return 1;
            }
        }

        // Renders the tray menu (on and off) to a PNG, to check the look without touching anything.
        static int Preview(string path)
        {
            Application.EnableVisualStyles();
            var images = new List<Bitmap>();
            foreach (bool on in new[] { true, false })
            {
                MenuHeader header;
                var toggle = TrayMenu.Row(on ? Strings.MenuTurnOff : Strings.MenuTurnOn, on ? Glyph.Stop : Glyph.Play,
                    on ? Strings.MenuTurnOffHint : Strings.MenuTurnOnHint, null);
                var auto = TrayMenu.Row(Strings.MenuAutostart, Glyph.Check, null, null);
                ((RowInfo)auto.Tag).Checked = true;
                var quit = TrayMenu.Row(Strings.MenuQuit, Glyph.None, Strings.MenuQuitHint, null);
                ContextMenuStrip menu = TrayMenu.Build(App.FullIcon(), out header, toggle, auto, quit);
                header.Set(on, on ? Strings.StatusManual : Strings.StatusOff, null);
                menu.Show(new Point(-4000, -4000));
                Application.DoEvents();
                var bmp = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
                menu.Close();
                images.Add(bmp);
            }
            int gap = 16;
            using (var sheet = new Bitmap(images[0].Width + images[1].Width + gap * 3, Math.Max(images[0].Height, images[1].Height) + gap * 2))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Theme.Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(230, 230, 230));
                g.DrawImage(images[0], gap, gap);
                g.DrawImage(images[1], gap * 2 + images[0].Width, gap);
                sheet.Save(path ?? "preview.png", System.Drawing.Imaging.ImageFormat.Png);
            }
            return 0;
        }

        static int ParseInt(string s)
        {
            int n;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // Double-click / Start menu: turns on and stays in the tray.
        static int Manual()
        {
            if (Ipc.Send("manual", "", 0)) return 0;
            return RunInstance(true, true, null, 0);
        }

        // resident: keep the tray icon after turning off (Start menu / Windows startup).
        // Otherwise (started by a hook) exit once the jobs are done.
        static int RunInstance(bool resident, bool manual, string holder, int pid, bool welcome = false)
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // someone else is already running: hand over our job and leave
                    string forward = manual ? "manual" : holder != null ? "start" : null;
                    if (forward != null && !Ipc.Send(forward, holder, pid, 5000))
                        App.Log("Another instance owns the lock but did not answer.");
                    return 0;
                }

                Environment.CurrentDirectory = App.ExeDir;   // never keep a project folder locked
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                TrayApp app = null;
                UnhandledExceptionEventHandler crash = delegate(object s, UnhandledExceptionEventArgs e)
                {
                    if (app != null) app.Emergency("unhandled exception: " + e.ExceptionObject);
                };
                AppDomain.CurrentDomain.UnhandledException += crash;
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    if (app != null) app.Emergency("UI exception: " + e.Exception);
                    Application.ExitThread();
                };

                try
                {
                    TrayApp.ShowWelcome = welcome;
                    app = new TrayApp(resident, manual, holder, pid);
                }
                catch (Exception ex)
                {
                    App.Log("Could not start: " + ex);
                    MessageBox.Show(ex.Message, "LidAwake", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                Application.Run(app);
                mutex.ReleaseMutex();
                return 0;
            }
        }

        // Called by Claude Code hooks. Reads the event JSON from stdin, prints nothing, always exits 0.
        static int Hook()
        {
            string json = Cli.ReadStdin(2000) ?? "";
            string ev = JsonString(json, "hook_event_name") ?? "";
            string session = JsonString(json, "session_id") ?? "claude";
            switch (ev)
            {
                case "UserPromptSubmit":
                    Start(session, FindAgentPid());
                    break;
                case "Stop":
                    // background tasks still running (agents, workflows, shells): keep holding
                    if (HasBackgroundTasks(json)) Start(session, FindAgentPid());
                    else Ipc.Send("stop", session, 0);
                    break;
                case "StopFailure":
                case "SessionEnd":
                    Ipc.Send("stop", session, 0);
                    break;
            }
            return 0;
        }

        static void Start(string id, int pid)
        {
            if (Ipc.Send("start", id, pid)) return;
            // Nothing running yet: launch the tray instance detached. ShellExecute does not inherit our
            // stdin/stdout, so the hook that called us can finish right away.
            var psi = new ProcessStartInfo(App.ExePath, "run \"" + id.Replace("\"", "") + "\" " + pid);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = App.ExeDir;
            Process.Start(psi);
            Ipc.WaitForInstance(5000);
        }

        static int Status()
        {
            var sb = new StringBuilder();
            string reply = Ipc.Query("status");
            sb.AppendLine("LidAwake " + (reply ?? "is not running"));
            try
            {
                Guid scheme = PowerPlan.ActiveScheme();
                sb.AppendLine("Lid close action now: plugged in = " + PowerPlan.LidName(PowerPlan.Read(scheme, PowerPlan.LidAc))
                    + ", on battery = " + PowerPlan.LidName(PowerPlan.Read(scheme, PowerPlan.LidDc)));
                uint? t = PowerPlan.Read(scheme, PowerPlan.SaverDc);
                sb.AppendLine("Energy saver: " + (PowerPlan.EnergySaverOn() ? "on" : "off")
                    + " (turns on at " + (t.HasValue ? t.Value + "%" : "n/a") + " battery)");
            }
            catch (Exception ex) { sb.AppendLine("Could not read power settings: " + ex.Message); }
            if (Snapshot.Exists) sb.AppendLine("Saved original settings: " + Path.Combine(App.DataDir, "restore.txt"));
            sb.Append("Log: " + App.LogPath);
            Cli.Out(sb.ToString());
            return 0;
        }

        static int Restore()
        {
            if (Ipc.Send("off", "", 0))
            {
                Cli.Out("Asked the running LidAwake to stop and restore your settings.");
                return 0;
            }
            if (!Snapshot.Exists) { Cli.Out("Nothing to restore - your settings are untouched."); return 0; }
            bool ok = Snapshot.RecoverLeftovers();
            Cli.Out(ok ? "Your original power settings are back." : "Some settings could not be restored, see " + App.LogPath);
            return ok ? 0 : 1;
        }

        // ---- helpers -------------------------------------------------------------------------

        static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        static bool HasBackgroundTasks(string json)
        {
            var m = Regex.Match(json, "\"background_tasks\"\\s*:\\s*\\[\\s*(\\S)");
            return m.Success && m.Groups[1].Value != "]";
        }

        // The process that ran us through a hook (claude.exe / node.exe), found by walking up past shells.
        // When it exits, its hold goes away even if no Stop hook ever arrives.
        static int FindAgentPid()
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "lidawake.exe", "cmd.exe", "bash.exe", "sh.exe", "dash.exe", "zsh.exe", "env.exe",
                "powershell.exe", "pwsh.exe", "conhost.exe", "wsl.exe", "busybox.exe", "timeout.exe"
            };
            try
            {
                var table = Native.ProcessTable();
                int pid = Native.GetCurrentProcessId();
                for (int depth = 0; depth < 12; depth++)
                {
                    Native.ProcInfo me, parent;
                    if (!table.TryGetValue(pid, out me) || !table.TryGetValue(me.Parent, out parent)) return 0;
                    if (!skip.Contains(parent.Exe)) return me.Parent;
                    pid = me.Parent;
                }
            }
            catch (Exception ex) { App.Log("Parent lookup failed: " + ex.Message); }
            return 0;
        }
    }

    static class App
    {
        public const string IpcWindowTitle = "LidAwake.IPC.v1";

        // LIDAWAKE_DRYRUN=1: everything runs, but no power setting is written (for testing)
        public static readonly bool DryRun = Environment.GetEnvironmentVariable("LIDAWAKE_DRYRUN") == "1";

        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string ExeDir { get { return Path.GetDirectoryName(ExePath); } }

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake"); }
        }

        public static string LogPath { get { return Path.Combine(DataDir, "lidawake.log"); } }

        static readonly object logLock = new object();

        public static void Log(string line)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(DataDir);
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + " [" + Native.GetCurrentProcessId() + "] " + line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(DataDir, "log-error.txt"), ex.ToString()); } catch { }
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // "Start with Windows": a per-user Run entry that opens LidAwake in the tray, switched off.
        public static bool Autostart
        {
            get
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("LidAwake") != null;
            }
            set
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("LidAwake", "\"" + ExePath + "\" tray");
                    else if (k.GetValue("LidAwake") != null) k.DeleteValue("LidAwake");
                }
            }
        }

        // every size from the .ico, so callers can pick a crisp one
        public static Icon FullIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LidAwake.ico"))
                    if (s != null) return new Icon(s);
            }
            catch (Exception ex) { Log("Icon load failed: " + ex.Message); }
            return SystemIcons.Application;
        }

        // the "off" look: grey and a little transparent
        public static Icon Grayed(Icon source)
        {
            using (Bitmap src = source.ToBitmap())
            using (var dst = new Bitmap(src.Width, src.Height))
            {
                for (int y = 0; y < src.Height; y++)
                    for (int x = 0; x < src.Width; x++)
                    {
                        Color c = src.GetPixel(x, y);
                        int l = (int)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
                        l = 70 + l * 120 / 255;
                        dst.SetPixel(x, y, Color.FromArgb(c.A * 9 / 10, l, l, l));
                    }
                return Icon.FromHandle(dst.GetHicon());
            }
        }

        public static Icon TrayIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LidAwake.ico"))
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch (Exception ex) { Log("Icon load failed: " + ex.Message); }
            return SystemIcons.Application;
        }
    }

    // Talks to the running instance through WM_COPYDATA on its hidden window.
    static class Ipc
    {
        public const long Magic = 0x4C41574B; // "LAWK"

        static string ReplyPath { get { return Path.Combine(App.DataDir, "status-reply.txt"); } }

        public static bool Send(string cmd, string id, int pid, int waitMs = 0)
        {
            IntPtr hwnd = waitMs > 0 ? WaitForInstance(waitMs) : Native.FindWindow(null, App.IpcWindowTitle);
            if (hwnd == IntPtr.Zero) return false;
            string text = cmd + "\t" + (id ?? "") + "\t" + pid.ToString(CultureInfo.InvariantCulture);
            IntPtr buf = Marshal.StringToHGlobalUni(text);
            try
            {
                var cds = new Native.COPYDATASTRUCT();
                cds.dwData = new IntPtr(Magic);
                cds.cbData = text.Length * 2;
                cds.lpData = buf;
                IntPtr result;
                IntPtr ok = Native.SendMessageTimeout(hwnd, Native.WM_COPYDATA, IntPtr.Zero, ref cds,
                    Native.SMTO_ABORTIFHUNG, 5000, out result);
                return ok != IntPtr.Zero && result != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static IntPtr WaitForInstance(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                IntPtr h = Native.FindWindow(null, App.IpcWindowTitle);
                if (h != IntPtr.Zero || sw.ElapsedMilliseconds >= ms) return h;
                Thread.Sleep(100);
            }
        }

        public static void WriteReply(string text)
        {
            try { Directory.CreateDirectory(App.DataDir); File.WriteAllText(ReplyPath, text); } catch { }
        }

        public static string Query(string cmd)
        {
            try { File.Delete(ReplyPath); } catch { }
            if (!Send(cmd, "", 0)) return null;
            try { return File.Exists(ReplyPath) ? File.ReadAllText(ReplyPath) : "is running"; } catch { return "is running"; }
        }
    }

    // Console output for a windowed exe (works from cmd, PowerShell and redirected pipes).
    static class Cli
    {
        static bool ready;

        static void Prepare()
        {
            if (ready) return;
            ready = true;
            IntPtr h = Native.GetStdHandle(-11);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                if (Native.AttachConsole(-1))
                {
                    var o = new StreamWriter(Console.OpenStandardOutput()); o.AutoFlush = true; Console.SetOut(o);
                    var e = new StreamWriter(Console.OpenStandardError()); e.AutoFlush = true; Console.SetError(e);
                    Console.WriteLine();
                }
            }
        }

        public static void Out(string s) { Prepare(); Console.WriteLine(s); }
        public static void Err(string s) { Prepare(); Console.Error.WriteLine("LidAwake: " + s); }

        public static string ReadStdin(int timeoutMs)
        {
            IntPtr h = Native.GetStdHandle(-10);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
            string text = null;
            var t = new Thread(delegate()
            {
                try
                {
                    using (var r = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)) text = r.ReadToEnd();
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
            t.Join(timeoutMs);
            return text;
        }
    }

    // UI text, Romanian when Windows is in Romanian, English otherwise.
    static class Strings
    {
        static readonly bool Ro = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ro";

        public static readonly string TrayTipOn = Ro ? "LidAwake: pornit - capacul închis nu-l adoarme" : "LidAwake: on - closing the lid won't sleep";
        public static readonly string TrayTipOff = Ro ? "LidAwake: oprit - click ca să-l pornești" : "LidAwake: off - click to turn on";
        public static readonly string BadgeOn = Ro ? "PORNIT" : "ON";
        public static readonly string BadgeOff = Ro ? "OPRIT" : "OFF";
        public static readonly string MenuInfo = Ro ? "Capac închis = ecran stins, PC-ul lucrează" : "Lid closed = screen off, the PC keeps working";
        public static readonly string InfoOff = Ro ? "Click ca să lucreze cu capacul închis" : "Click to keep working with the lid closed";
        public static readonly string MenuTurnOn = Ro ? "Pornește" : "Turn on";
        public static readonly string MenuTurnOnHint = Ro ? "capacul închis nu-l adoarme" : "lid closed keeps working";
        public static readonly string MenuTurnOff = Ro ? "Oprește" : "Turn off";
        public static readonly string MenuTurnOffHint = Ro ? "somn normal din nou" : "normal sleep again";
        public static readonly string MenuAutostart = Ro ? "Pornește cu Windows" : "Start with Windows";
        public static readonly string MenuQuit = Ro ? "Ieșire" : "Quit";
        public static readonly string MenuQuitHint = Ro ? "scoate iconița din tray" : "remove from the tray";
        public static readonly string StatusManual = Ro ? "Pornit de tine - până apeși Oprește" : "Started by you - on until you turn it off";
        public static readonly string StatusJobs = Ro ? "Lucrează pentru {0} job(uri) Claude" : "Working for {0} Claude job(s)";
        public static readonly string StatusEnding = Ro ? "Gata - se oprește în câteva secunde" : "Done - turning off in a moment";
        public static readonly string StatusOff = Ro ? "Setările tale de energie sunt neatinse" : "Your power settings are untouched";
        public static readonly string BalloonOn = Ro ? "Poți închide capacul: ecranul se stinge, treaba continuă." : "You can close the lid: the screen turns off, the work keeps going.";
        public static readonly string AskConnect = Ro
            ? "Conectez LidAwake la Claude Code?\n\nAșa pornește singur cât lucrează Claude și se oprește când Claude a terminat tot (inclusiv taskurile din fundal).\n\nSe adaugă 4 hook-uri în .claude\\settings.json (se face o copie de siguranță) și o scurtă notă în .claude\\CLAUDE.md, ca să știe Claude de el."
            : "Connect LidAwake to Claude Code?\n\nIt will turn on by itself while Claude works and off when Claude is done, including background tasks.\n\nThis adds 4 hooks to .claude\\settings.json (a backup is kept) and a short note to .claude\\CLAUDE.md so Claude knows about it.";
        public static readonly string Welcome = Ro
            ? "LidAwake e în tray (cana de cafea). Click pe ea ca să-l pornești; Claude îl pornește singur."
            : "LidAwake is in the tray (the coffee cup). Click it to turn it on; Claude turns it on by itself.";
        public static readonly string LidBlocked = Ro ? "Windows nu a permis schimbarea acțiunii capacului." : "Windows did not allow changing the lid action.";
        public static readonly string NoLid = Ro ? "Acest PC nu are setare pentru capac." : "This PC has no lid setting.";

        public const string Usage =
@"LidAwake - keeps a Windows laptop working with the lid closed while a job runs.

  LidAwake            turn on (stays in the tray)    LidAwake tray       sit in the tray, off
  LidAwake start [id] keep awake for a job          LidAwake stop [id]  that job is done
  LidAwake off        turn off now                     LidAwake status     show state
  LidAwake restore    put power settings back       LidAwake hook       for Claude Code hooks
  LidAwake install    install (also on double-click) LidAwake uninstall
  LidAwake connect    add the Claude Code hooks     LidAwake disconnect remove them
  LidAwake hooks      print the Claude Code hooks JSON
  LidAwake quit       turn off and leave the tray";
    }
}
