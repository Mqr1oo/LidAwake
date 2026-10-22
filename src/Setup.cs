using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LidAwake
{
    // Installs itself on the first double-click: %LOCALAPPDATA%\Programs\LidAwake, Start menu, tray,
    // start with Windows, and (after asking once) the Claude Code hooks.
    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LidAwake"); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "LidAwake.exe"); } }

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(App.ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LidAwake.lnk"); }
        }

        static void QuitRunning()
        {
            Ipc.Send("quit", "", 0);
            for (int i = 0; i < 50 && Native.FindWindow(null, App.IpcWindowTitle) != IntPtr.Zero; i++) Thread.Sleep(100);
        }

        // The old copy can still be closing after its window is gone, or a hook may be running it for a
        // moment, so the file stays locked briefly: keep trying for up to 10 s.
        static void CopyWhenFree(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i >= 40) throw; Thread.Sleep(250); }
            }
        }

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LidAwake";

        public static int Install(bool gui, bool connect = false)
        {
            QuitRunning();   // an older copy may be running from the place we copy to
            Directory.CreateDirectory(InstallDir);
            if (!IsInstalledCopy) CopyWhenFree(App.ExePath, InstalledExe);
            CreateShortcut();
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                k.SetValue("LidAwake", "\"" + InstalledExe + "\" tray");
            RegisterInstalledApp();

            // Claude Code: ask once; on later installs (updates) just refresh what is there
            string claude = "not found";
            if (ClaudeSetup.ClaudeInstalled)
            {
                bool connected = ClaudeSetup.HooksPresent();
                if (connected || connect || MessageBox.Show(Strings.AskConnect, "LidAwake", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    claude = ClaudeSetup.Connect(InstalledExe) ? "connected" : "could not edit settings.json (see the log)";
                }
                else claude = "not connected (run: LidAwake connect)";
            }
            App.Log("Installed " + InstalledExe + "; Claude Code: " + claude);

            var psi = new ProcessStartInfo(InstalledExe, "tray --welcome");
            psi.UseShellExecute = true;
            psi.WorkingDirectory = InstallDir;
            Process.Start(psi);

            if (!gui)
                Cli.Out("Installed: " + InstalledExe + Environment.NewLine
                    + "It sits in the tray (coffee cup), is in the Start menu and starts with Windows." + Environment.NewLine
                    + "Claude Code: " + claude);
            return 0;
        }

        static void CreateShortcut()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                Type t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Keep the laptop working with the lid closed" });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe + ",0" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        // Settings > Apps > Installed apps entry (per user, no admin), with an Uninstall button.
        static void RegisterInstalledApp()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                string exe = "\"" + InstalledExe + "\"";
                k.SetValue("DisplayName", "LidAwake");
                k.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                k.SetValue("Publisher", "Mqr1oo");
                k.SetValue("DisplayIcon", InstalledExe + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("URLInfoAbout", "https://github.com/Mqr1oo/LidAwake");
                k.SetValue("UninstallString", exe + " uninstall --gui");
                k.SetValue("QuietUninstallString", exe + " uninstall");
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static int Uninstall(bool gui)
        {
            QuitRunning();
            Snapshot.RecoverLeftovers();   // settings back first, before the restore file goes away
            PowerMode.Restore();           // only possible on battery
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (k.GetValue("LidAwake") != null) k.DeleteValue("LidAwake");
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { File.Delete(ShortcutPath); } catch { }
            ClaudeSetup.Disconnect();

            // Remove the program and its log. A running exe can't delete itself, so a hidden cmd does it after
            // we exit, retrying a few times in case a scanner briefly holds a file.
            if (IsInstalledCopy)
            {
                string a = "\"" + InstallDir + "\"", b = "\"" + App.DataDir + "\"";
                string del = "rmdir /s /q " + a + " 2>nul & rmdir /s /q " + b + " 2>nul & if not exist " + a
                    + " if not exist " + b + " exit";   // stop once gone, so a quick reinstall survives
                var psi = new ProcessStartInfo("cmd.exe", "/c for /l %i in (1,1,6) do (ping -n 3 127.0.0.1 >nul & " + del + ")");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
            else
            {
                try { Directory.Delete(InstallDir, true); } catch { }
                try { Directory.Delete(App.DataDir, true); } catch { }
            }

            if (gui) MessageBox.Show(Strings.Removed, "LidAwake", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else Cli.Out("LidAwake removed: program, tray, Start menu, start with Windows, Claude Code hooks and note. Power settings are as before.");
            return 0;
        }

        public static string HooksJson(string exe)
        {
            var root = new Dictionary<string, object>();
            var hooks = new Dictionary<string, object>();
            foreach (string ev in ClaudeSetup.Events) hooks[ev] = new List<object> { ClaudeSetup.Group(exe) };
            root["hooks"] = hooks;
            return Json.Pretty(new JavaScriptSerializer().Serialize(root));
        }
    }

    // Wires LidAwake into Claude Code: hooks in ~/.claude/settings.json (everything else in that file is
    // kept, and a backup is made first) and a short note in ~/.claude/CLAUDE.md so Claude knows about it.
    static class ClaudeSetup
    {
        public static readonly string[] Events = { "UserPromptSubmit", "Stop", "StopFailure", "SessionEnd" };
        const string NoteBegin = "<!-- LidAwake:begin -->", NoteEnd = "<!-- LidAwake:end -->";

        static string Dir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LIDAWAKE_CLAUDE_DIR");   // for testing
                return !string.IsNullOrEmpty(over) ? over
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }
        }

        static string SettingsPath { get { return Path.Combine(Dir, "settings.json"); } }
        static string NotePath { get { return Path.Combine(Dir, "CLAUDE.md"); } }

        public static bool ClaudeInstalled { get { return Directory.Exists(Dir); } }

        public static Dictionary<string, object> Group(string exe)
        {
            var hook = new Dictionary<string, object>();
            hook["type"] = "command";
            hook["command"] = exe;
            hook["args"] = new List<object> { "hook" };
            hook["timeout"] = 10;
            var group = new Dictionary<string, object>();
            group["hooks"] = new List<object> { hook };
            return group;
        }

        public static bool HooksPresent()
        {
            Dictionary<string, object> root;
            if (!TryLoad(out root)) return false;
            var hooks = Get(root, "hooks") as Dictionary<string, object>;
            if (hooks == null) return false;
            foreach (object g in List(Get(hooks, "Stop")))
                foreach (object h in List(Get(g as Dictionary<string, object>, "hooks")))
                    if (IsOurs(h)) return true;
            return false;
        }

        public static bool Connect(string exe)
        {
            bool ok = SetHooks(exe);
            WriteNote(true);
            return ok;
        }

        public static void Disconnect()
        {
            if (!ClaudeInstalled) return;
            SetHooks(null);
            WriteNote(false);
        }

        // Puts our hook on each event (exe != null) or removes it (exe == null), keeping all other hooks.
        static bool SetHooks(string exe)
        {
            Dictionary<string, object> root;
            if (!TryLoad(out root))
            {
                App.Log("settings.json is not plain JSON - left untouched. Add the hooks by hand: LidAwake hooks");
                return false;
            }
            var hooks = Get(root, "hooks") as Dictionary<string, object> ?? new Dictionary<string, object>();
            foreach (string ev in Events)
            {
                var kept = new List<object>();
                foreach (object g in List(Get(hooks, ev)))
                {
                    var gd = g as Dictionary<string, object>;
                    if (gd == null || !gd.ContainsKey("hooks")) { kept.Add(g); continue; }
                    var others = new List<object>();
                    foreach (object h in List(gd["hooks"])) if (!IsOurs(h)) others.Add(h);
                    if (others.Count == 0) continue;   // the group held only our hook
                    gd["hooks"] = others;
                    kept.Add(gd);
                }
                if (exe != null) kept.Add(Group(exe));
                if (kept.Count > 0) hooks[ev] = kept; else hooks.Remove(ev);
            }
            if (hooks.Count > 0) root["hooks"] = hooks; else root.Remove("hooks");

            try
            {
                Directory.CreateDirectory(Dir);
                string backup = SettingsPath + ".lidawake-backup";
                if (File.Exists(SettingsPath)) File.Copy(SettingsPath, backup, true);
                File.WriteAllText(SettingsPath, Json.Pretty(new JavaScriptSerializer().Serialize(root)) + "\n", new UTF8Encoding(false));
                App.Log((exe != null ? "Added" : "Removed") + " the Claude Code hooks in " + SettingsPath);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("Could not write " + SettingsPath + ": " + ex.Message);
                return false;
            }
        }

        static bool TryLoad(out Dictionary<string, object> root)
        {
            root = new Dictionary<string, object>();
            if (!File.Exists(SettingsPath)) return true;
            try
            {
                string text = File.ReadAllText(SettingsPath);
                if (text.Trim().Length == 0) return true;
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                root = serializer.DeserializeObject(text) as Dictionary<string, object>;
                return root != null;
            }
            catch (Exception ex)
            {
                App.Log("Could not read " + SettingsPath + ": " + ex.Message);
                root = null;
                return false;
            }
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        static List<object> List(object v)
        {
            var list = new List<object>();
            var e = v as IEnumerable;
            if (e != null && !(v is string) && !(v is IDictionary)) foreach (object o in e) list.Add(o);
            return list;
        }

        static bool IsOurs(object hook)
        {
            var c = Get(hook as Dictionary<string, object>, "command") as string;
            return c != null && c.IndexOf("LidAwake.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The note between the markers is ours; the rest of CLAUDE.md is never touched.
        static void WriteNote(bool add)
        {
            try
            {
                string text = File.Exists(NotePath) ? File.ReadAllText(NotePath) : "";
                int b = text.IndexOf(NoteBegin, StringComparison.Ordinal);
                int e = text.IndexOf(NoteEnd, StringComparison.Ordinal);
                string rest = (b >= 0 && e > b ? text.Substring(0, b) + text.Substring(e + NoteEnd.Length) : text).Trim();
                string result = add
                    ? (rest.Length > 0 ? rest + "\n\n" : "") + NoteBegin + "\n" + Note + NoteEnd + "\n"
                    : (rest.Length > 0 ? rest + "\n" : "");
                if (result.Length == 0) { if (File.Exists(NotePath)) File.Delete(NotePath); return; }
                File.WriteAllText(NotePath, result, new UTF8Encoding(false));
            }
            catch (Exception ex) { App.Log("Could not update " + NotePath + ": " + ex.Message); }
        }

        const string Note =
@"# LidAwake — keeps this laptop working with the lid closed

The user's tool (https://github.com/Mqr1oo/LidAwake), installed at
`%LOCALAPPDATA%\Programs\LidAwake\LidAwake.exe` (call it by that full path). It lives in the tray as a coffee
cup: grey = off, coral = on.

## What it does by itself

- **Hooks** in `~/.claude/settings.json` (`LidAwake.exe hook`) turn it on when a prompt is sent. It stays on
  while background tasks run and turns off 60 s after everything is done. Normal work needs nothing from you.
- **While it is on:** the lid action is ""Do nothing"" and idle sleep is ""Never"". With the lid closed on
  battery, the power mode is ""Best power efficiency"". The user's settings come back afterwards, even after a crash.
- **Screen:** on Modern Standby laptops a screen that turns off by itself puts the laptop into standby and stops
  the work. So at that moment LidAwake shows a black screen instead (safe for OLED); any key or mouse move
  brings the desktop back. Locking the PC (Win+L) still pauses the work after ~30 s: tell the user to close
  the lid instead of locking.
- **Awake while charging** (on by default): while the charger is in, LidAwake stays on even between prompts, so
  the laptop stays reachable (Remote Control) with the lid open or closed. Turning it off by hand while charging
  keeps it off until the charger comes out.
- **When work ends with the lid closed on battery,** or the battery reaches the threshold (default 10%), the laptop hibernates
  (powers off, everything saved; the work resumes when it is opened), or shuts down if the user chose that.
  If the CPU or GPU stays at the heat limit (default 95°C) for a minute with the lid closed, it always hibernates.
- **Temperatures:** hovering the tray cup shows the CPU and GPU temperature; `LidAwake.exe status` prints them.
- **Notifications** (on by default): ""Claude is done"" after jobs longer than 2 minutes, and when a timer ends.
- If another app (e.g. G-Helper on ASUS laptops) resets the lid setting, LidAwake sets it again within a second.

## Controlling it

Run these when the user asks for it or the task needs it. Each prints what it did.

| The user wants | Command |
|---|---|
| keep the laptop awake for a while / overnight | `LidAwake.exe for 2h` (or `90m`, `1.5h`), `LidAwake.exe until 8:00`; `for off` clears the timer |
| turn it on now / off now | `LidAwake.exe on` / `LidAwake.exe off` (off with the lid closed on battery hibernates the laptop) |
| hibernate / shut down at another battery level | `LidAwake.exe battery 15` (3-50) |
| another heat limit, or none | `LidAwake.exe heat 90` (70-100) / `LidAwake.exe heat off` |
| shut down instead of hibernating when the work ends or the battery runs low, lid closed | `LidAwake.exe end shutdown` / `end hibernate` |
| notifications on or off | `LidAwake.exe notify on` / `off` |
| stay on (reachable) whenever the charger is in, or not | `LidAwake.exe charging on` / `off` |
| the laptop on the phone's hotspot whenever LidAwake is on and the lid is closed (even from Wi-Fi) | `LidAwake.exe hotspot on` / `off` (needs Instant hotspot set up in Phone Link first) |
| a detached process of yours must outlive your turn | `LidAwake.exe start <name>` before it, `LidAwake.exe stop <name>` when it is done |
| see the state and all settings | `LidAwake.exe status` |
| something looks wrong | `LidAwake.exe restore` (puts the power settings back), log at `%LOCALAPPDATA%\LidAwake\lidawake.log` |

## Rules

- **Never change sleep, lid, screen or power-mode settings yourself** (powercfg, Control Panel, registry) to keep
  the machine awake. Use the commands above; LidAwake restores everything.
- The user can close the lid while you work; you don't need to ask them to keep it open.
- Cloud sessions don't run these hooks; this works only for sessions running on this laptop.
";
    }

    // Indents compact JSON (2 spaces) and turns the serializer's <-style escapes back into plain characters.
    static class Json
    {
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        if (json[i + 1] == 'u' && i + 5 < json.Length)
                        {
                            string hex = json.Substring(i + 2, 4).ToLowerInvariant();
                            string plain = hex == "003c" ? "<" : hex == "003e" ? ">" : hex == "0026" ? "&" : hex == "0027" ? "'" : null;
                            if (plain != null) { sb.Append(plain); i += 5; continue; }
                        }
                        sb.Append(c).Append(json[i + 1]);
                        i++;
                        continue;
                    }
                    if (c == '"') inString = false;
                    sb.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        char close = c == '{' ? '}' : ']';
                        if (i + 1 < json.Length && json[i + 1] == close) { sb.Append(c).Append(close); i++; break; }
                        sb.Append(c).Append('\n').Append(' ', ++indent * 2);
                        break;
                    case '}':
                    case ']':
                        sb.Append('\n').Append(' ', --indent * 2).Append(c);
                        break;
                    case ',': sb.Append(",\n").Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
