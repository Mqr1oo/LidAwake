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

        public static int Install(bool gui)
        {
            QuitRunning();   // an older copy may be running from the place we copy to
            Directory.CreateDirectory(InstallDir);
            if (!IsInstalledCopy) File.Copy(App.ExePath, InstalledExe, true);
            CreateShortcut();
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                k.SetValue("LidAwake", "\"" + InstalledExe + "\" tray");

            // Claude Code: ask once; on later installs (updates) just refresh what is there
            string claude = "not found";
            if (ClaudeSetup.ClaudeInstalled)
            {
                bool connected = ClaudeSetup.HooksPresent();
                if (connected || MessageBox.Show(Strings.AskConnect, "LidAwake", MessageBoxButtons.YesNo,
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

        public static int Uninstall()
        {
            QuitRunning();
            Snapshot.RecoverLeftovers();
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (k.GetValue("LidAwake") != null) k.DeleteValue("LidAwake");
            try { File.Delete(ShortcutPath); } catch { }
            ClaudeSetup.Disconnect();
            if (!IsInstalledCopy)
            {
                try { Directory.Delete(InstallDir, true); } catch { }
            }
            Cli.Out("LidAwake removed: tray, Start menu, start with Windows and the Claude Code hooks."
                + (IsInstalledCopy ? " Delete " + InstallDir + " yourself (this exe is running from it)." : ""));
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
`%LOCALAPPDATA%\Programs\LidAwake\LidAwake.exe`. It lives in the tray as a coffee cup: grey = off, coral = on.

- **It runs by itself.** Hooks in `~/.claude/settings.json` (`LidAwake.exe hook`) turn it on when a turn starts.
  It stays on while background tasks run and turns off 60 s after everything is done. Normal work needs nothing
  from you.
- **While it is on:** the lid action is ""Do nothing"" and idle sleep is ""Never"". When the lid is closed on
  battery, Energy Saver turns on. It puts the user's settings back afterwards.
- **When work ends with the lid closed:** the laptop hibernates (powers off with everything saved). It also
  hibernates at 10% battery with the lid closed; the work resumes when the lid is opened.
- **Never change sleep or lid settings yourself** (powercfg, Control Panel) to keep the machine awake.
  LidAwake does it and restores them.
- **Commands, only when needed** (e.g. a detached process you start that must outlive your turn):
  `LidAwake.exe start <name>` before it and `LidAwake.exe stop <name>` when it is done.
  - `LidAwake.exe status` shows the state.
  - `LidAwake.exe restore` puts the settings back if something looks off.
  - The log is at `%LOCALAPPDATA%\LidAwake\lidawake.log`.
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
