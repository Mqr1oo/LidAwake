using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LidAwake
{
    // The running instance: a tray icon (on/off), a hidden window for messages, and the jobs keeping it on.
    sealed class TrayApp : ApplicationContext
    {
        // Turn off this long after the last job let go (Claude often starts the next step within seconds).
        static readonly TimeSpan Linger = TimeSpan.FromSeconds(60);
        // A job that has said nothing for this long is assumed dead (e.g. the turn was interrupted with Esc,
        // which fires no Stop hook). Long autonomous runs refresh it on every Stop that still has background work.
        static readonly TimeSpan HoldTimeout = TimeSpan.FromHours(4);
        // Hibernate when the battery drops to this with the lid closed.
        const int LowBattery = 10;

        sealed class Hold
        {
            public DateTime LastSeen;
            public IntPtr Process;   // the agent's process; when it exits, the hold goes away
        }

        readonly Keeper keeper = new Keeper();
        readonly Dictionary<string, Hold> holds = new Dictionary<string, Hold>(StringComparer.Ordinal);
        readonly SynchronizationContext ui;
        readonly MessageWindow window;
        readonly NotifyIcon tray;
        readonly MenuHeader header;
        readonly ToolStripMenuItem toggleItem, autostartItem;
        readonly Icon iconOn, iconOff;
        readonly System.Windows.Forms.Timer timer;
        readonly bool resident;   // keep the tray icon when off
        DateTime lastRelease = DateTime.UtcNow, lastLowBattery = DateTime.MinValue;
        bool active, manual, exiting;

        public TrayApp(bool resident, bool turnOn, string holder, int pid)
        {
            this.resident = resident;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            iconOn = App.TrayIcon();
            iconOff = App.Grayed(iconOn);
            toggleItem = TrayMenu.Row("", Glyph.Play, null, delegate { Toggle(); });
            autostartItem = TrayMenu.Row(Strings.MenuAutostart, Glyph.Check, null, delegate { ToggleAutostart(); });
            var quit = TrayMenu.Row(Strings.MenuQuit, Glyph.None, Strings.MenuQuitHint, delegate { Quit("quit from the tray"); });
            ToolStripMenuItem[] rows = resident
                ? new[] { toggleItem, autostartItem, quit }
                : new[] { toggleItem };
            ContextMenuStrip menu = TrayMenu.Build(App.FullIcon(), out header, rows);

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Toggle();   // one click: on / off
            };
            Refresh();
            tray.Visible = true;

            window = new MessageWindow();
            window.Command += OnCommand;
            window.LidChanged += keeper.OnLid;
            window.SessionEnding += delegate { Quit("Windows is shutting down / signing out"); };
            window.ListenForLid();   // Windows replies right away with the current lid state

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 5000;
            timer.Tick += delegate { Evaluate(); };
            timer.Start();

            if (turnOn) TurnOnManually();
            else if (holder != null) { Add(holder, pid); Activate(); }
            else if (ShowWelcome) tray.ShowBalloonTip(6000, "LidAwake", Strings.Welcome, ToolTipIcon.None);
        }

        public static bool ShowWelcome;   // first start right after installing

        // ---- on / off ----------------------------------------------------------------------

        void Toggle()
        {
            if (active) Deactivate("turned off from the tray", false);
            else TurnOnManually();
        }

        void TurnOnManually()
        {
            manual = true;
            if (Activate()) tray.ShowBalloonTip(3000, "LidAwake", Strings.BalloonOn, ToolTipIcon.None);
        }

        bool Activate()
        {
            if (active) { Refresh(); return true; }
            try { keeper.Engage(); }
            catch (Exception ex)
            {
                App.Log("Could not turn on: " + ex);
                manual = false;
                tray.ShowBalloonTip(6000, "LidAwake", ex.Message, ToolTipIcon.Error);
                if (!resident) ui.Post(delegate { Quit("could not turn on"); }, null);
                return false;
            }
            active = true;
            lastRelease = DateTime.UtcNow;
            if (keeper.Problem != null) tray.ShowBalloonTip(6000, "LidAwake", keeper.Problem, ToolTipIcon.Warning);
            Refresh();
            return true;
        }

        // honorLid: the work is done, so if the lid is shut, hibernate (power off with everything saved).
        void Deactivate(string why, bool honorLid)
        {
            if (!active) return;
            App.Log("Turning off: " + why);
            foreach (string id in new List<string>(holds.Keys)) Remove(id, "turning off");
            manual = false;
            active = false;
            bool lidClosed = keeper.LidClosed;
            try { keeper.Release(); }
            catch (Exception ex) { App.Log("Release failed: " + ex); }
            Refresh();

            if (!resident) CloseUi();
            if (honorLid && lidClosed) Keeper.Hibernate("all work is done and the lid is closed");
            if (!resident) ExitThread();
        }

        void Quit(string why)
        {
            if (exiting) return;
            bool wasResident = resident;
            if (active) Deactivate(why, false);
            if (!wasResident) return;   // Deactivate already closed everything
            CloseUi();
            ExitThread();
        }

        void CloseUi()
        {
            if (exiting) return;
            exiting = true;
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            window.Dispose();
        }

        void ToggleAutostart()
        {
            try { App.Autostart = !App.Autostart; }
            catch (Exception ex) { App.Log("Autostart change failed: " + ex.Message); }
            Refresh();
        }

        // ---- jobs ---------------------------------------------------------------------------

        void Add(string id, int pid)
        {
            Hold h;
            if (!holds.TryGetValue(id, out h))
            {
                h = new Hold();
                holds[id] = h;
                App.Log("Hold: " + id + (pid > 0 ? " (pid " + pid + ")" : ""));
            }
            h.LastSeen = DateTime.UtcNow;
            if (h.Process == IntPtr.Zero && pid > 0)
                h.Process = Native.OpenProcess(Native.SYNCHRONIZE, false, pid);
        }

        void Remove(string id, string why)
        {
            Hold h;
            if (!holds.TryGetValue(id, out h)) return;
            if (h.Process != IntPtr.Zero) Native.CloseHandle(h.Process);
            holds.Remove(id);
            lastRelease = DateTime.UtcNow;
            App.Log("Release: " + id + " (" + why + ")");
        }

        // Messages from "LidAwake start/stop/off/..." run from the command line or a Claude Code hook.
        string OnCommand(string cmd, string id, int pid)
        {
            switch (cmd)
            {
                case "start":
                    Add(id, pid);
                    ui.Post(delegate { Activate(); }, null);
                    break;
                case "stop":
                    Remove(id, "job done");
                    // a session ending also ends the subagents it started
                    foreach (string key in new List<string>(holds.Keys))
                        if (key.StartsWith(id + "/", StringComparison.Ordinal)) Remove(key, "parent done");
                    break;
                case "manual":
                    ui.Post(delegate { TurnOnManually(); }, null);
                    return "ok";
                case "off":
                    // act after the reply has gone back to the caller
                    ui.Post(delegate { Deactivate("turned off", true); }, null);
                    return "ok";
                case "quit":
                    ui.Post(delegate { Quit("quit"); }, null);
                    return "ok";
                case "status":
                    return Describe();
                default:
                    return "unknown command";
            }
            ui.Post(delegate { Evaluate(); }, null);
            return "ok";
        }

        void Evaluate()
        {
            if (exiting || !active) return;
            DateTime now = DateTime.UtcNow;
            foreach (var pair in new List<KeyValuePair<string, Hold>>(holds))
            {
                Hold h = pair.Value;
                if (h.Process != IntPtr.Zero && Native.WaitForSingleObject(h.Process, 0) == Native.WAIT_OBJECT_0)
                    Remove(pair.Key, "its process ended");
                else if (now - h.LastSeen > HoldTimeout)
                    Remove(pair.Key, "no news for " + HoldTimeout.TotalHours + "h");
            }

            // Battery almost empty with the lid shut: hibernate so nothing is lost. The job is paused, not
            // ended; it carries on when the laptop is opened again.
            int battery = PowerPlan.BatteryPercentOnBattery();
            if (keeper.LidClosed && battery >= 0 && battery <= LowBattery && now - lastLowBattery > TimeSpan.FromMinutes(3))
            {
                lastLowBattery = now;
                Keeper.Hibernate("battery at " + battery + "% with the lid closed");
                return;
            }

            if (!manual && holds.Count == 0 && now - lastRelease >= Linger)
                Deactivate("all jobs done", true);
            else
                Refresh();
        }

        string Describe()
        {
            if (!active) return "running in the tray, off";
            var parts = new List<string>();
            if (manual) parts.Add("manual");
            foreach (string k in holds.Keys) parts.Add(k);
            string who = parts.Count == 0 ? "nothing (turning off soon)" : string.Join(", ", parts.ToArray());
            return "on; held by: " + who + "; lid " + (keeper.LidClosed ? "closed" : "open")
                + (keeper.Problem != null ? "; problem: " + keeper.Problem : "");
        }

        void Refresh()
        {
            if (exiting || header == null) return;
            tray.Icon = active ? iconOn : iconOff;
            tray.Text = active ? Strings.TrayTipOn : Strings.TrayTipOff;

            string s;
            if (!active) s = Strings.StatusOff;
            else if (manual) s = Strings.StatusManual;
            else if (holds.Count > 0) s = string.Format(Strings.StatusJobs, holds.Count);
            else s = Strings.StatusEnding;
            header.Set(active, s, active ? keeper.Problem : null);

            toggleItem.Text = active ? Strings.MenuTurnOff : Strings.MenuTurnOn;
            var t = (RowInfo)toggleItem.Tag;
            t.Glyph = active ? Glyph.Stop : Glyph.Play;
            t.Hint = active ? Strings.MenuTurnOffHint : Strings.MenuTurnOnHint;
            bool auto = false;
            try { auto = App.Autostart; } catch { }
            ((RowInfo)autostartItem.Tag).Checked = auto;
            toggleItem.Invalidate();
            autostartItem.Invalidate();
        }

        // Last line of defence: put the settings back even if something throws.
        public void Emergency(string why)
        {
            App.Log("Emergency restore: " + why);
            try { keeper.Release(); } catch (Exception ex) { App.Log("Emergency release failed: " + ex.Message); }
            try { tray.Visible = false; } catch { }
        }
    }

    // Hidden top-level window: receives command-line messages, lid changes and the shutdown notice.
    sealed class MessageWindow : NativeWindow, IDisposable
    {
        public delegate string CommandHandler(string cmd, string id, int pid);
        public event CommandHandler Command;
        public event Action<bool> LidChanged;
        public event Action SessionEnding;

        IntPtr lidNotification = IntPtr.Zero;

        public MessageWindow()
        {
            var cp = new CreateParams();
            cp.Caption = App.IpcWindowTitle;
            CreateHandle(cp);
        }

        public void ListenForLid()
        {
            Guid lid = PowerPlan.LidSwitchState;
            lidNotification = Native.RegisterPowerSettingNotification(Handle, ref lid, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (lidNotification == IntPtr.Zero)
                App.Log("Lid notifications unavailable, error " + Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_COPYDATA:
                    m.Result = HandleCopyData(m.LParam);
                    return;

                case Native.WM_POWERBROADCAST:
                    if (m.WParam.ToInt64() == Native.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                    {
                        // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[] }
                        var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                        int len = Marshal.ReadInt32(m.LParam, 16);
                        if (setting == PowerPlan.LidSwitchState && len >= 4 && LidChanged != null)
                            LidChanged(Marshal.ReadInt32(m.LParam, 20) == 0);   // 0 = closed, 1 = open
                    }
                    m.Result = new IntPtr(1);
                    return;

                case Native.WM_QUERYENDSESSION:
                    m.Result = new IntPtr(1);
                    return;

                case Native.WM_ENDSESSION:
                    if (m.WParam != IntPtr.Zero && SessionEnding != null) SessionEnding();
                    m.Result = IntPtr.Zero;
                    return;
            }
            base.WndProc(ref m);
        }

        // Reply text goes back through a shared reply slot: the caller reads the result code only,
        // and "status" answers are written into the reply file (see Ipc).
        IntPtr HandleCopyData(IntPtr lParam)
        {
            try
            {
                var cds = (Native.COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.COPYDATASTRUCT));
                if (cds.dwData.ToInt64() != Ipc.Magic || cds.cbData <= 0 || cds.cbData > 4096) return IntPtr.Zero;
                string text = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2);
                string[] f = text.Split('\t');
                string cmd = f.Length > 0 ? f[0] : "";
                string id = f.Length > 1 ? f[1] : "";
                int pid = 0;
                if (f.Length > 2) int.TryParse(f[2], out pid);
                string reply = Command != null ? Command(cmd, id, pid) : "";
                if (cmd == "status") Ipc.WriteReply(reply);
                return new IntPtr(1);
            }
            catch (Exception ex)
            {
                App.Log("Bad message: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (lidNotification != IntPtr.Zero)
            {
                Native.UnregisterPowerSettingNotification(lidNotification);
                lidNotification = IntPtr.Zero;
            }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
