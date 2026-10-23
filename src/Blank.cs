using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LidAwake
{
    // Modern Standby laptops go into standby the moment the screen times out, which stops the work. So just
    // before Windows would turn the screen off, this holds it on and shows plain black instead: on OLED
    // panels black pixels are off, so nothing burns in. Any key, mouse or touch input brings the desktop back.
    // If another app keeps the screen on (a video), Windows would not turn it off either, so this does nothing.
    sealed class ScreenGuard : IDisposable
    {
        // Act this early: the caller checks every 5 s and Windows must not get there first.
        const uint Margin = 15;

        readonly Keeper keeper;
        readonly List<Form> forms = new List<Form>();
        readonly Timer watch = new Timer();
        uint shownAt;
        bool locked, paused;
        IntPtr previous;   // the app the user was in
        Form front;        // must be the active window, or the taskbar stays on top of the black

        public ScreenGuard(Keeper keeper)
        {
            this.keeper = keeper;
            // started while locked (say, installed from a remote prompt): no lock notice will come, so ask
            locked = Native.SessionIsLocked();
            if (locked) App.Log("Session locked.");
            watch.Interval = 250;
            watch.Tick += delegate
            {
                if (Native.LastInputTick() != shownAt) Stop("input");
                else if (front != null && Native.GetForegroundWindow() != front.Handle) Native.BringToFront(front.Handle);
            };
        }

        bool Showing { get { return forms.Count > 0; } }

        // Called every few seconds while LidAwake is on.
        public void Check()
        {
            if (Showing || locked || paused || !keeper.ScreenGuarded) return;
            uint timeout = PowerPlan.ScreenTimeout();
            if (timeout == 0 || Native.IdleSeconds() + Margin < timeout) return;
            if ((Native.SystemExecutionState() & Native.ES_DISPLAY_REQUIRED) != 0) return;   // something else keeps it on
            if (keeper.HoldScreen(true)) Show();
        }

        // The lock screen can't be covered, so let Windows turn it off as usual (the laptop then goes to standby).
        public void SessionLocked(bool isLocked)
        {
            locked = isLocked;
            App.Log(isLocked ? "Session locked." : "Session unlocked.");
            if (isLocked) Stop("session locked");
        }

        // While LidAwake works the desktop itself (the Wi-Fi list, for the phone hotspot): no black cover, which
        // would take the focus, but the screen stays held so its timeout still can't start standby.
        public void Pause(bool on)
        {
            if (on == paused) return;
            paused = on;
            if (on)
            {
                Hide("busy");
                keeper.HoldScreen(true);
                return;
            }
            uint timeout = PowerPlan.ScreenTimeout();
            if (keeper.ScreenGuarded && !locked && timeout > 0 && Native.IdleSeconds() + Margin >= timeout)
            {
                if (keeper.HoldScreen(true)) Show();   // past the timeout already: straight to black
            }
            else keeper.HoldScreen(false);
        }

        public void Stop(string why)
        {
            keeper.HoldScreen(false);
            Hide(why);
        }

        void Hide(string why)
        {
            watch.Stop();
            if (!Showing) return;
            // give the focus back to where the user was
            if (previous != IntPtr.Zero && front != null && !front.IsDisposed && Native.GetForegroundWindow() == front.Handle)
                Native.SetForegroundWindow(previous);
            foreach (Form f in forms) { if (!f.IsDisposed) f.Close(); f.Dispose(); }
            forms.Clear();
            front = null;
            Cursor.Show();
            App.Log("Screen back (" + why + ").");
        }

        void Show()
        {
            shownAt = Native.LastInputTick();
            previous = Native.GetForegroundWindow();
            foreach (Screen s in Screen.AllScreens)
            {
                var f = new BlackForm();
                f.Bounds = s.Bounds;
                forms.Add(f);
                f.Show();
                if (s.Primary || front == null) front = f;
            }
            bool onTop = Native.BringToFront(front.Handle);
            Cursor.Hide();
            watch.Start();
            App.Log("Screen black instead of off (idle " + Native.IdleSeconds() + " s"
                + (onTop ? "" : ", taskbar may still show until it can come to the front") + ").");
        }

        public void Dispose()
        {
            Stop("closing");
            watch.Dispose();
        }

        sealed class BlackForm : Form
        {
            public BlackForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                BackColor = Color.Black;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }

            // Don't take the focus from the app the user was in.
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW: not in Alt+Tab */ | 0x8 /* WS_EX_TOPMOST */;
                    return cp;
                }
            }
        }
    }
}
