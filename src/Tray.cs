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

        sealed class Hold
        {
            public DateTime LastSeen;
            public DateTime Started = DateTime.UtcNow;
            public IntPtr Process;   // the agent's process; when it exits, the hold goes away
        }

        readonly Keeper keeper = new Keeper();
        readonly ScreenGuard guard;
        readonly Dictionary<string, Hold> holds = new Dictionary<string, Hold>(StringComparer.Ordinal);
        readonly SynchronizationContext ui;
        readonly MessageWindow window;
        readonly NotifyIcon tray;
        readonly MenuHeader header;
        readonly ToolStripMenuItem toggleItem, autostartItem, hotspotItem, timerItem, batteryItem, notifyItem, endItem;
        readonly Icon iconOn, iconOff;
        readonly System.Windows.Forms.Timer timer, recheck;
        readonly bool resident;   // keep the tray icon when off
        DateTime lastRelease = DateTime.UtcNow, lastLowBattery = DateTime.MinValue;
        bool active, manual, exiting, hotspotBusy;
        DateTime lastHotspotTry = DateTime.MinValue;
        DateTime? until;   // timer: turn off at this time (UTC)
        DateTime? offlineSince;   // on the phone's hotspot but no internet since then
        bool onHotspot;           // connected to the phone since the lid closed
        DateTime? hotSince;       // lid closed and too hot since then
        DateTime lastHeat = DateTime.MinValue, lastTempLog = DateTime.MinValue, lastTip = DateTime.MinValue;
        string temps;             // last CPU / GPU reading, for the tooltip
        int timerChoice;   // 0 none, 1 one hour, 2 two hours, 3 until 8:00

        public TrayApp(bool resident, bool turnOn, string holder, int pid)
        {
            this.resident = resident;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            guard = new ScreenGuard(keeper);

            iconOn = App.TrayIcon();
            iconOff = App.Grayed(iconOn);
            toggleItem = TrayMenu.Row("", Glyph.Play, null, delegate { Toggle(); });
            timerItem = TrayMenu.Row(Strings.MenuTimer, Glyph.None, null, delegate { CycleTimer(); });
            batteryItem = TrayMenu.Row(Strings.MenuBattery, Glyph.None, null, delegate { CycleBattery(); });
            endItem = TrayMenu.Row(Strings.MenuEnd, Glyph.None, null, delegate { SetEnd(!Prefs.ShutDownWhenDone); });
            hotspotItem = TrayMenu.Row(Strings.MenuHotspot, Glyph.Check, null, delegate { ToggleHotspot(); });
            notifyItem = TrayMenu.Row(Strings.MenuNotify, Glyph.Check, null, delegate { SetNotify(!Prefs.NotifyDone); });
            autostartItem = TrayMenu.Row(Strings.MenuAutostart, Glyph.Check, null, delegate { ToggleAutostart(); });
            var quit = TrayMenu.Row(Strings.MenuQuit, Glyph.None, null, delegate { Quit("quit from the tray"); });
            ToolStripItem[] rows = resident
                ? new ToolStripItem[] { toggleItem, timerItem, new ToolStripSeparator(),
                    batteryItem, endItem, hotspotItem, notifyItem, autostartItem, new ToolStripSeparator(), quit }
                : new ToolStripItem[] { toggleItem };
            ContextMenuStrip menu = TrayMenu.Build(App.FullIcon(), out header, rows);
            // the timer and battery rows change on each click, so keep the menu open for them
            menu.Closing += delegate(object s, ToolStripDropDownClosingEventArgs e)
            {
                if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked) return;
                ToolStripItem under = menu.GetItemAt(menu.PointToClient(Cursor.Position));
                if (under == timerItem || under == batteryItem || under == endItem) e.Cancel = true;
            };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Toggle();   // one click: on / off
            };
            tray.MouseMove += delegate { ReadTemps(); };   // hover: fresh temperatures in the tooltip
            Refresh();
            tray.Visible = true;

            window = new MessageWindow();
            window.Command += OnCommand;
            window.LidChanged += keeper.OnLid;
            window.LidChanged += delegate { ui.Post(delegate { Evaluate(); }, null); };   // hotspot right away
            window.LockChanged += guard.SessionLocked;
            window.SessionEnding += delegate { Quit("Windows is shutting down / signing out"); };
            window.ListenForLid();   // Windows replies right away with the current lid state

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 5000;
            timer.Tick += delegate { Evaluate(); };
            timer.Start();

            recheck = new System.Windows.Forms.Timer();
            recheck.Interval = 1000;
            recheck.Tick += delegate { if (active) keeper.Reassert(); };
            recheck.Start();

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
            if (!Activate()) return;
            tray.ShowBalloonTip(3000, "LidAwake", Strings.BalloonOn, ToolTipIcon.None);
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
            until = null;
            timerChoice = 0;
            onHotspot = false;
            lastHotspotTry = DateTime.MinValue;
            active = false;
            bool lidClosed = keeper.LidClosed;
            guard.Stop("LidAwake turned off");
            try { keeper.Release(); }
            catch (Exception ex) { App.Log("Release failed: " + ex); }
            Refresh();

            if (!resident) CloseUi();
            if (honorLid && lidClosed)
            {
                if (Prefs.ShutDownWhenDone) Keeper.ShutDown("all work is done and the lid is closed");
                else Keeper.Hibernate("all work is done and the lid is closed");
            }
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
            recheck.Stop();
            guard.Dispose();
            tray.Visible = false;
            tray.Dispose();
            window.Dispose();
        }

        static bool Enabled()
        {
            try { return PhoneHotspot.Enabled; } catch { return false; }
        }

        void ToggleHotspot()
        {
            try { PhoneHotspot.Enabled = !PhoneHotspot.Enabled; }
            catch (Exception ex) { App.Log("Hotspot setting change failed: " + ex.Message); }
            Refresh();
        }

        // Runs the Wi-Fi list clicking on a worker thread and reports back on the UI thread.
        void StartHotspot()
        {
            if (hotspotBusy || exiting) return;
            hotspotBusy = true;
            lastHotspotTry = DateTime.UtcNow;
            guard.Pause(true);
            var worker = new Thread(delegate()
            {
                string phone = null, error;
                try { error = PhoneHotspot.Connect(out phone); }
                catch (Exception ex) { error = ex.Message; }
                ui.Post(delegate
                {
                    hotspotBusy = false;
                    lastHotspotTry = DateTime.UtcNow;
                    onHotspot = error == null;
                    if (exiting) return;
                    guard.Pause(false);
                    if (error == null)
                        tray.ShowBalloonTip(4000, "LidAwake", string.Format(Strings.HotspotOk, phone), ToolTipIcon.None);
                    else
                    {
                        App.Log("Phone hotspot: " + error);
                        tray.ShowBalloonTip(6000, "LidAwake", string.Format(Strings.HotspotFail, error), ToolTipIcon.Warning);
                    }
                }, null);
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // Each click: one hour, two hours, until 8:00, no limit. Picking a time also turns LidAwake on.
        void CycleTimer()
        {
            timerChoice = (timerChoice + 1) % 4;
            SetTimer(timerChoice == 1 ? 60 : timerChoice == 2 ? 120 : timerChoice == 3 ? Prefs.MinutesUntil(8, 0) : 0);
        }

        // minutes <= 0: no time limit (stays on until turned off).
        void SetTimer(int minutes)
        {
            if (minutes <= 0)
            {
                if (until.HasValue) App.Log("Timer cleared.");
                until = null;
                timerChoice = 0;
                Refresh();
                return;
            }
            until = DateTime.UtcNow.AddMinutes(minutes);
            App.Log("Timer: on until " + until.Value.ToLocalTime().ToString("HH:mm") + ".");
            if (!active || !manual) TurnOnManually();
            Refresh();
        }

        void CycleBattery()
        {
            int now = Prefs.HibernateAt, next = Prefs.BatteryChoices[0];
            foreach (int c in Prefs.BatteryChoices) if (c > now) { next = c; break; }
            SetBattery(next);
        }

        void SetBattery(int percent)
        {
            try { Prefs.HibernateAt = percent; App.Log("Hibernate at " + Prefs.HibernateAt + "% battery (lid closed)."); }
            catch (Exception ex) { App.Log("Battery setting change failed: " + ex.Message); }
            Refresh();
        }

        void SetEnd(bool shutDown)
        {
            try { Prefs.ShutDownWhenDone = shutDown; App.Log("When done with the lid closed: " + (shutDown ? "shut down" : "hibernate") + "."); }
            catch (Exception ex) { App.Log("End setting change failed: " + ex.Message); }
            Refresh();
        }

        void SetHeat(int celsius)
        {
            try { Prefs.HeatLimit = celsius; App.Log("Heat limit: " + (Prefs.HeatLimit > 0 ? Prefs.HeatLimit + "°C" : "off") + "."); }
            catch (Exception ex) { App.Log("Heat setting change failed: " + ex.Message); }
            Refresh();
        }

        // Hovering the cup: read the temperatures (at most every 2 s) and show them under on / off.
        void ReadTemps()
        {
            DateTime now = DateTime.UtcNow;
            if (exiting || now - lastTip < TimeSpan.FromSeconds(2)) return;
            lastTip = now;
            temps = Thermal.Summary();
            SetTip();
        }

        void SetTip()
        {
            string text = (active ? Strings.TipOn : Strings.TipOff) + (temps != null ? "\n" + temps : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;   // Windows' limit
        }

        void SetNotify(bool on)
        {
            try { Prefs.NotifyDone = on; }
            catch (Exception ex) { App.Log("Notify setting change failed: " + ex.Message); }
            Refresh();
        }

        void SetHotspot(bool on)
        {
            if (Enabled() != on) ToggleHotspot();
        }

        void Notify(string text)
        {
            App.Log("Notify: " + text);
            if (!exiting && Prefs.NotifyDone) tray.ShowBalloonTip(6000, "LidAwake", text, ToolTipIcon.None);
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
            // Claude finished a long job (short replies don't count) and nothing else is running
            TimeSpan took = lastRelease - h.Started;
            if (why == "job done" && holds.Count == 0 && id.IndexOf('/') < 0 && took >= TimeSpan.FromMinutes(2))
                Notify(string.Format(Strings.ClaudeDone, (int)Math.Round(took.TotalMinutes)));
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
                case "timer":
                    ui.Post(delegate { SetTimer(ParseNumber(id)); }, null);
                    return "ok";
                case "battery":
                    ui.Post(delegate { SetBattery(ParseNumber(id)); }, null);
                    return "ok";
                case "notify":
                    ui.Post(delegate { SetNotify(id == "on"); }, null);
                    return "ok";
                case "heat":
                    ui.Post(delegate { SetHeat(ParseNumber(id)); }, null);
                    return "ok";
                case "end":
                    ui.Post(delegate { SetEnd(id == "shutdown"); }, null);
                    return "ok";
                case "hotspot":
                    ui.Post(delegate { SetHotspot(id == "on"); }, null);
                    return "ok";
                default:
                    return "unknown command";
            }
            ui.Post(delegate { Evaluate(); }, null);
            return "ok";
        }

        static int ParseNumber(string s)
        {
            int n;
            return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        void Evaluate()
        {
            if (exiting) return;
            keeper.WatchPowerMode();
            if (!active) return;
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
            guard.Check();
            if (until.HasValue && now >= until.Value)
            {
                App.Log("Timer done.");
                until = null;
                timerChoice = 0;
                if (manual) { manual = false; lastRelease = now; }
                Notify(Strings.TimerDone);
            }

            // Too hot with the lid shut (say, in a bag): hibernate, so nothing is lost and nothing cooks.
            // The GPU reading here comes only from sources that don't wake a sleeping dedicated GPU.
            if (keeper.LidClosed)
            {
                int cpu = Thermal.Cpu(), gpu = Thermal.Gpu(false), hot = Math.Max(cpu, gpu), limit = Prefs.HeatLimit;
                if (now - lastTempLog >= TimeSpan.FromMinutes(5))
                {
                    lastTempLog = now;
                    App.Log("Lid closed: CPU " + (cpu > 0 ? cpu + "°C" : "n/a") + ", GPU " + (gpu > 0 ? gpu + "°C" : "n/a") + ".");
                }
                if (limit > 0 && hot >= limit)
                {
                    if (!hotSince.HasValue) { hotSince = now; App.Log("Hot: " + hot + "°C with the lid closed (limit " + limit + "°C)."); }
                    if (now - hotSince.Value >= TimeSpan.FromMinutes(1) && now - lastHeat > TimeSpan.FromMinutes(3))
                    {
                        lastHeat = now;
                        hotSince = null;
                        Keeper.Hibernate(hot + "°C for a minute with the lid closed (limit " + limit + "°C)");
                        return;
                    }
                }
                else hotSince = null;
            }
            else
            {
                hotSince = null;
                lastTempLog = DateTime.MinValue;   // log right away on the next lid close
            }

            int battery = PowerPlan.BatteryPercentOnBattery();
            if (keeper.LidClosed && battery >= 0 && battery <= Prefs.HibernateAt && now - lastLowBattery > TimeSpan.FromMinutes(3))
            {
                lastLowBattery = now;
                // what the user picked under "When done": shut down, or hibernate (the job carries on when opened)
                string why = "battery at " + battery + "% with the lid closed";
                if (Prefs.ShutDownWhenDone) Keeper.ShutDown(why);
                else Keeper.Hibernate(why);
                return;
            }

            // "Phone hotspot" ticked: as soon as the lid closes, go online through the phone, even from Wi-Fi.
            // Until it works, try once a minute; after that only if the internet drops for 10 s.
            if (!hotspotBusy)
            {
                if (keeper.LidClosed && Enabled())
                {
                    bool due = now - lastHotspotTry > TimeSpan.FromMinutes(1);
                    if (!onHotspot) { if (due) StartHotspot(); }
                    else if (!PhoneHotspot.Online())
                    {
                        if (!offlineSince.HasValue) offlineSince = now;
                        if (now - offlineSince.Value >= TimeSpan.FromSeconds(10) && due) StartHotspot();
                    }
                    else offlineSince = null;
                }
                else
                {
                    onHotspot = false;
                    offlineSince = null;
                    lastHotspotTry = DateTime.MinValue;   // the next lid close connects right away
                }
            }

            if (!manual && holds.Count == 0 && now - lastRelease >= Linger)
                Deactivate("all jobs done", true);
            else
                Refresh();
        }

        string Describe()
        {
            string prefs = "\nSettings: hibernate at " + Prefs.HibernateAt + "% battery with the lid closed; heat limit "
                + (Prefs.HeatLimit > 0 ? Prefs.HeatLimit + "°C" : "off") + "; when done with the lid closed: "
                + (Prefs.ShutDownWhenDone ? "shut down" : "hibernate") + "; notify when done "
                + (Prefs.NotifyDone ? "on" : "off") + "; phone hotspot " + (Enabled() ? "on" : "off");
            if (!active) return "running in the tray, off" + prefs;
            var parts = new List<string>();
            if (manual) parts.Add(until.HasValue ? "manual, timer until " + until.Value.ToLocalTime().ToString("HH:mm") : "manual");
            foreach (string k in holds.Keys) parts.Add(k);
            string who = parts.Count == 0 ? "nothing (turning off soon)" : string.Join(", ", parts.ToArray());
            return "on; held by: " + who + "; lid " + (keeper.LidClosed ? "closed" : "open")
                + (keeper.Problem != null ? "; problem: " + keeper.Problem : "") + prefs;
        }

        void Refresh()
        {
            if (exiting || header == null) return;
            tray.Icon = active ? iconOn : iconOff;
            SetTip();

            string s;
            if (!active) s = Strings.StatusOff;
            else if (manual && until.HasValue) s = string.Format(Strings.StatusUntil, until.Value.ToLocalTime().ToString("HH:mm"));
            else if (manual) s = Strings.StatusManual;
            else if (holds.Count > 0) s = string.Format(Strings.StatusJobs, holds.Count);
            else s = Strings.StatusEnding;
            header.Set(active, s, active ? keeper.Problem : null);

            toggleItem.Text = active ? Strings.MenuTurnOff : Strings.MenuTurnOn;
            var t = (RowInfo)toggleItem.Tag;
            t.Glyph = active ? Glyph.Stop : Glyph.Play;
            bool auto = false;
            try { auto = App.Autostart; } catch { }
            ((RowInfo)autostartItem.Tag).Checked = auto;
            ((RowInfo)hotspotItem.Tag).Checked = Enabled();
            hotspotItem.Invalidate();
            ((RowInfo)timerItem.Tag).Hint = until.HasValue
                ? string.Format(Strings.TimerUntil, until.Value.ToLocalTime().ToString("HH:mm")) : Strings.TimerOff;
            timerItem.Invalidate();
            batteryItem.Text = Prefs.ShutDownWhenDone ? Strings.MenuBatteryShutDown : Strings.MenuBattery;
            ((RowInfo)batteryItem.Tag).Hint = Prefs.HibernateAt + "%";
            batteryItem.Invalidate();
            ((RowInfo)endItem.Tag).Hint = Prefs.ShutDownWhenDone ? Strings.EndShutdown : Strings.EndHibernate;
            endItem.Invalidate();
            ((RowInfo)notifyItem.Tag).Checked = Prefs.NotifyDone;
            notifyItem.Invalidate();
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
        public event Action<bool> LockChanged;   // true = locked

        IntPtr lidNotification = IntPtr.Zero;

        public MessageWindow()
        {
            var cp = new CreateParams();
            cp.Caption = App.IpcWindowTitle;
            CreateHandle(cp);
            Native.WTSRegisterSessionNotification(Handle, 0 /* this session */);
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

                case Native.WM_WTSSESSION_CHANGE:
                    int change = m.WParam.ToInt32();
                    if ((change == Native.WTS_SESSION_LOCK || change == Native.WTS_SESSION_UNLOCK) && LockChanged != null)
                        LockChanged(change == Native.WTS_SESSION_LOCK);
                    break;

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
            if (Handle != IntPtr.Zero)
            {
                Native.WTSUnRegisterSessionNotification(Handle);
                DestroyHandle();
            }
        }
    }
}
