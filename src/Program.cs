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
[assembly: AssemblyVersion("1.5.1.0")]
[assembly: AssemblyFileVersion("1.5.1.0")]

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
                    case "hotspot-check": { string phone; Cli.Out(PhoneHotspot.Connect(out phone, true) ?? "already connected to " + phone); return 0; }
                    case "run": return RunInstance(false, false, arg1 ?? "job", args.Length > 2 ? ParseInt(args[2]) : 0);
                    case "hook": return Hook();
                    case "start": Start(arg1 ?? "cli", FindAgentPid()); return 0;
                    case "stop": Ipc.Send("stop", arg1 ?? "cli", 0); return 0;
                    case "off": Ipc.Send("off", "", 0); return 0;
                    case "status": return Status();
                    case "for": return For(arg1);
                    case "until": return Until(arg1);
                    case "battery": return Battery(arg1);
                    case "notify": return OnOff("notify", arg1, "Notify when done:");
                    case "heat": return Heat(arg1);
                    case "end": return End(arg1);
                    case "hotspot": return OnOff("hotspot", arg1, "Phone hotspot (while on with the lid closed):");
                    case "restore": return Restore();
                    case "install": return Installer.Install(false, arg1 == "--connect");
                    case "uninstall": return Installer.Uninstall(arg1 == "--gui");
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
                var toggle = TrayMenu.Row(on ? Strings.MenuTurnOff : Strings.MenuTurnOn, on ? Glyph.Stop : Glyph.Play, null, null);
                var timer = TrayMenu.Row(Strings.MenuTimer, Glyph.None, on ? string.Format(Strings.TimerUntil, "08:00") : Strings.TimerOff, null);
                var battery = TrayMenu.Row(Strings.MenuBattery, Glyph.None, "10%", null);
                var end = TrayMenu.Row(Strings.MenuEnd, Glyph.None, Strings.EndHibernate, null);
                var hotspot = TrayMenu.Row(Strings.MenuHotspot, Glyph.Check, null, null);
                var notify = TrayMenu.Row(Strings.MenuNotify, Glyph.Check, null, null);
                ((RowInfo)notify.Tag).Checked = true;
                var auto = TrayMenu.Row(Strings.MenuAutostart, Glyph.Check, null, null);
                ((RowInfo)auto.Tag).Checked = true;
                var quit = TrayMenu.Row(Strings.MenuQuit, Glyph.None, null, null);
                ContextMenuStrip menu = TrayMenu.Build(App.FullIcon(), out header, toggle, timer, new ToolStripSeparator(),
                    battery, end, hotspot, notify, auto, new ToolStripSeparator(), quit);
                header.Set(on, on ? string.Format(Strings.StatusUntil, "08:00") : Strings.StatusOff, null);
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

        // "LidAwake for 2h" / "90m" / "1.5h" / "off": on now, off again after that long.
        static int For(string arg)
        {
            string a = (arg ?? "").Trim().ToLowerInvariant();
            int minutes = 0;
            if (a != "off" && a != "0")
            {
                double n;
                bool hours = a.EndsWith("h");
                if (!double.TryParse(a.TrimEnd('h', 'm'), NumberStyles.Float, CultureInfo.InvariantCulture, out n) || n <= 0 || n > 7 * 24 * 60)
                {
                    Cli.Out("Usage: LidAwake for 2h | 90m | 1.5h | off");
                    return 2;
                }
                minutes = (int)Math.Ceiling(hours ? n * 60 : n);
            }
            return ToTray("timer", minutes.ToString(CultureInfo.InvariantCulture),
                minutes == 0 ? "Timer off (LidAwake stays as it is)." : "On until " + DateTime.Now.AddMinutes(minutes).ToString("HH:mm") + ", then off.");
        }

        // "LidAwake until 8:00" / "until 23": on now, off at that time (today or tomorrow).
        static int Until(string arg)
        {
            string[] p = (arg ?? "").Split(':');
            int h = p.Length > 0 ? ParseIntOr(p[0], -1) : -1, m = p.Length > 1 ? ParseIntOr(p[1], -1) : 0;
            if (h < 0 || h > 23 || m < 0 || m > 59) { Cli.Out("Usage: LidAwake until 8:00"); return 2; }
            int minutes = Prefs.MinutesUntil(h, m);
            return ToTray("timer", minutes.ToString(CultureInfo.InvariantCulture),
                "On until " + DateTime.Now.AddMinutes(minutes).ToString("HH:mm") + ", then off.");
        }

        static int Battery(string arg)
        {
            int n = ParseIntOr(arg, -1);
            if (n < Prefs.MinBattery || n > Prefs.MaxBattery)
            {
                Cli.Out("Usage: LidAwake battery <" + Prefs.MinBattery + "-" + Prefs.MaxBattery + ">   (now " + Prefs.HibernateAt + ")");
                return 2;
            }
            return ToTray("battery", n.ToString(CultureInfo.InvariantCulture),
                "At " + n + "% battery with the lid closed it hibernates, or shuts down if \"When done\" is set to shut down.");
        }

        // "LidAwake heat 90" / "heat off": hibernate when the CPU or GPU stays this hot with the lid closed.
        static int Heat(string arg)
        {
            int n = arg == "off" ? 0 : ParseIntOr(arg, -1);
            if (n != 0 && (n < Prefs.MinHeat || n > Prefs.MaxHeat))
            {
                Cli.Out("Usage: LidAwake heat <" + Prefs.MinHeat + "-" + Prefs.MaxHeat + "> | off   (now " + (Prefs.HeatLimit > 0 ? Prefs.HeatLimit + "" : "off") + ")");
                return 2;
            }
            return ToTray("heat", n.ToString(CultureInfo.InvariantCulture),
                n == 0 ? "Heat limit off." : "Hibernates when the CPU or GPU stays at " + n + "°C for a minute with the lid closed.");
        }

        // "LidAwake end hibernate|shutdown": what happens when the work ends with the lid closed.
        static int End(string arg)
        {
            if (arg != "hibernate" && arg != "shutdown") { Cli.Out("Usage: LidAwake end hibernate|shutdown"); return 2; }
            return ToTray("end", arg, "When the work ends with the lid closed: " + (arg == "shutdown" ? "shut down." : "hibernate."));
        }

        static int OnOff(string cmd, string arg, string what)
        {
            if (arg != "on" && arg != "off") { Cli.Out("Usage: LidAwake " + cmd + " on|off"); return 2; }
            return ToTray(cmd, arg, what + " " + arg + ".");
        }

        static int ParseIntOr(string s, int fallback)
        {
            int n;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        // Timers and settings live in the tray instance (it writes the real HKCU even when this command runs
        // inside an app container), so start it first if it isn't running.
        static int ToTray(string cmd, string arg, string done)
        {
            if (!Ipc.Send(cmd, arg, 0))
            {
                var psi = new ProcessStartInfo(App.ExePath, "tray");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = App.ExeDir;
                Process.Start(psi);
                Ipc.WaitForInstance(5000);
                if (!Ipc.Send(cmd, arg, 0)) { Cli.Out("LidAwake is not running and could not be started."); return 1; }
            }
            Cli.Out(done);
            return 0;
        }

        static int Status()
        {
            var sb = new StringBuilder();
            string reply = Ipc.Query("status");
            sb.AppendLine("LidAwake " + (reply ?? "is not running"));
            sb.AppendLine("Temperature now: " + (Thermal.Summary() ?? "not available on this PC"));
            try
            {
                Guid scheme = PowerPlan.ActiveScheme();
                sb.AppendLine("Lid close action now: plugged in = " + PowerPlan.LidName(PowerPlan.Read(scheme, PowerPlan.LidAc))
                    + ", on battery = " + PowerPlan.LidName(PowerPlan.Read(scheme, PowerPlan.LidDc)));
                Guid mode;
                if (Native.PowerGetActualOverlayScheme(out mode) == 0)
                    sb.AppendLine("Power mode now: " + (mode == new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a") ? "best power efficiency" : mode.ToString())
                        + (PowerPlan.EnergySaverOn() ? ", energy saver on" : ""));
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
            PowerMode.Restore();
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
            else
            {
                // piped (Claude Code, scripts): UTF-8, so "°C" survives
                var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                o.AutoFlush = true;
                Console.SetOut(o);
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

        public static readonly string TipOn = Ro ? "LidAwake: pornit" : "LidAwake: on";
        public static readonly string TipOff = Ro ? "LidAwake: oprit" : "LidAwake: off";
        public static readonly string GpuOff = Ro ? "oprit" : "off";
        public static readonly string MenuEnd = Ro ? "La final" : "When done";
        public static readonly string EndHibernate = Ro ? "hibernare" : "hibernate";
        public static readonly string EndShutdown = Ro ? "oprire" : "shut down";
        public static readonly string BadgeOn = Ro ? "PORNIT" : "ON";
        public static readonly string BadgeOff = Ro ? "OPRIT" : "OFF";
        public static readonly string MenuTurnOn = Ro ? "Pornește" : "Turn on";
        public static readonly string MenuTurnOff = Ro ? "Oprește" : "Turn off";
        public static readonly string MenuAutostart = Ro ? "Pornește cu Windows" : "Start with Windows";
        public static readonly string MenuQuit = Ro ? "Ieșire" : "Quit";
        public static readonly string StatusManual = Ro ? "Pornit de tine" : "Turned on by you";
        public static readonly string StatusUntil = Ro ? "Pornit până la {0}" : "On until {0}";
        public static readonly string StatusJobs = Ro ? "Lucrează pentru {0} job(uri) Claude" : "Working for {0} Claude job(s)";
        public static readonly string StatusEnding = Ro ? "Gata - se oprește în câteva secunde" : "Done - turning off in a moment";
        public static readonly string StatusOff = Ro ? "Click pe cană ca să-l pornești" : "Click the cup to turn it on";
        public static readonly string BalloonOn = Ro ? "Poți închide capacul: ecranul se stinge, treaba continuă." : "You can close the lid: the screen turns off, the work keeps going.";
        public static readonly string AskConnect = Ro
            ? "Conectez LidAwake la Claude Code?\n\nAșa pornește singur cât lucrează Claude și se oprește când Claude a terminat tot (inclusiv taskurile din fundal).\n\nSe adaugă 4 hook-uri în .claude\\settings.json (se face o copie de siguranță) și o scurtă notă în .claude\\CLAUDE.md, ca să știe Claude de el."
            : "Connect LidAwake to Claude Code?\n\nIt will turn on by itself while Claude works and off when Claude is done, including background tasks.\n\nThis adds 4 hooks to .claude\\settings.json (a backup is kept) and a short note to .claude\\CLAUDE.md so Claude knows about it.";
        public static readonly string Welcome = Ro
            ? "LidAwake e în tray (cana de cafea). Click pe ea ca să-l pornești; Claude îl pornește singur."
            : "LidAwake is in the tray (the coffee cup). Click it to turn it on; Claude turns it on by itself.";
        public static readonly string MenuTimer = Ro ? "Timer" : "Timer";
        public static readonly string TimerOff = Ro ? "oprit" : "off";
        public static readonly string TimerUntil = Ro ? "până la {0}" : "until {0}";
        public static readonly string MenuBattery = Ro ? "Hibernează la" : "Hibernate at";
        public static readonly string MenuBatteryShutDown = Ro ? "Oprește la" : "Shut down at";
        public static readonly string MenuNotify = Ro ? "Notificare la final" : "Notify when done";
        public static readonly string ClaudeDone = Ro ? "Claude a terminat (a lucrat {0} min)." : "Claude is done (worked {0} min).";
        public static readonly string TimerDone = Ro ? "Timer-ul s-a terminat. LidAwake se oprește dacă nu mai lucrează nimic." : "The timer is over. LidAwake turns off unless something is still working.";
        public static readonly string MenuHotspot = Ro ? "Hotspot de pe telefon" : "Phone hotspot";
        public static readonly string HotspotOk = Ro ? "Online prin hotspotul lui {0}." : "Online through {0}'s hotspot.";
        public static readonly string HotspotFail = Ro ? "Nu m-am putut conecta la hotspotul telefonului: {0}" : "Couldn't connect to the phone hotspot: {0}";
        public static readonly string Removed = Ro
            ? "LidAwake a fost dezinstalat.\n\nSetările tale de energie sunt exact cum erau, iar hook-urile și nota din Claude Code au fost scoase."
            : "LidAwake has been uninstalled.\n\nYour power settings are as they were, and the Claude Code hooks and note were removed.";
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
  LidAwake quit       turn off and leave the tray

  LidAwake for 2h     on now, off after 2h (also 90m, 1.5h; ""for off"" clears the timer)
  LidAwake until 8:00 on now, off at 8:00
  LidAwake battery 15 at 15% battery with the lid closed: hibernate, or shut down if ""end shutdown"" (3-50, default 10)
  LidAwake heat 90    hibernate if the CPU or GPU stays this hot for a minute, lid closed (70-100 or off, default 95)
  LidAwake end hibernate|shutdown   what to do when the work ends or the battery runs low, lid closed
  LidAwake notify on|off   balloon when Claude finishes a long job or a timer ends
  LidAwake hotspot on|off  connect to the phone's hotspot while on with the lid closed";
    }
}
