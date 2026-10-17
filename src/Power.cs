using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace LidAwake
{
    // One value of the power plan, for plugged in (AC) or on battery (DC).
    sealed class PlanSetting
    {
        public readonly string Key;
        public readonly Guid Sub, Id;
        public readonly bool Ac;
        public PlanSetting(string key, Guid sub, Guid id, bool ac) { Key = key; Sub = sub; Id = id; Ac = ac; }
    }

    // Reads and writes values of the active power plan (the same ones Control Panel > Power Options shows).
    static class PowerPlan
    {
        static readonly Guid SubButtons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        static readonly Guid LidActionId = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
        static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");    // "Put the computer to sleep after"
        static readonly Guid UnattendSleep = new Guid("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");  // sleep timeout at the lock screen
        static readonly Guid SubEnergySaver = new Guid("de830923-a562-41af-a086-e3a2c6bad2da");
        static readonly Guid SaverThreshold = new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4"); // energy saver turns on at X% battery
        public static readonly Guid LidSwitchState = new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");

        public static readonly PlanSetting LidAc = new PlanSetting("lid.ac", SubButtons, LidActionId, true);
        public static readonly PlanSetting LidDc = new PlanSetting("lid.dc", SubButtons, LidActionId, false);
        public static readonly PlanSetting SaverDc = new PlanSetting("saver.dc", SubEnergySaver, SaverThreshold, false);

        // Set to 0 ("Do nothing" / "Never") for as long as LidAwake is on.
        public static readonly PlanSetting[] KeptAwake =
        {
            LidAc, LidDc,
            new PlanSetting("standby.ac", SubSleep, StandbyIdle, true),
            new PlanSetting("standby.dc", SubSleep, StandbyIdle, false),
            new PlanSetting("unattend.ac", SubSleep, UnattendSleep, true),
            new PlanSetting("unattend.dc", SubSleep, UnattendSleep, false),
        };

        public static IEnumerable<PlanSetting> All()
        {
            foreach (var s in KeptAwake) yield return s;
            yield return SaverDc;
        }

        public static Guid ActiveScheme()
        {
            IntPtr p;
            uint err = Native.PowerGetActiveScheme(IntPtr.Zero, out p);
            if (err != 0) throw new Win32Exception((int)err, "Could not read the active power plan");
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { Native.LocalFree(p); }
        }

        public static uint? Read(Guid scheme, PlanSetting s)
        {
            Guid sub = s.Sub, id = s.Id;
            uint value;
            uint err = s.Ac
                ? Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, out value)
                : Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, out value);
            return err == 0 ? value : (uint?)null;
        }

        public static void Write(Guid scheme, PlanSetting s, uint value)
        {
            if (App.DryRun) { App.Log("[dry run] would set " + s.Key + "=" + value); return; }
            Guid sub = s.Sub, id = s.Id;
            uint err = s.Ac
                ? Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, value)
                : Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, value);
            if (err != 0) throw new Win32Exception((int)err, "Could not change " + s.Key);
        }

        // Written values only take effect once their plan is re-applied. Never switches the user's plan.
        public static void Apply(Guid scheme)
        {
            if (ActiveScheme() != scheme || App.DryRun) return;
            uint err = Native.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (err != 0) throw new Win32Exception((int)err, "Could not apply the power plan");
        }

        static Native.SYSTEM_POWER_STATUS Status()
        {
            Native.SYSTEM_POWER_STATUS s;
            if (!Native.GetSystemPowerStatus(out s)) { s = new Native.SYSTEM_POWER_STATUS(); s.ACLineStatus = 255; s.BatteryLifePercent = 255; }
            return s;
        }

        public static bool OnBattery() { return Status().ACLineStatus == 0; }
        public static bool EnergySaverOn() { return (Status().SystemStatusFlag & 1) != 0; }

        // Battery percent while running on battery, or -1 (plugged in / unknown).
        public static int BatteryPercentOnBattery()
        {
            var s = Status();
            return s.ACLineStatus == 0 && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : -1;
        }

        public static string LidName(uint? v)
        {
            if (!v.HasValue) return "n/a";
            switch (v.Value)
            {
                case 0: return "Do nothing";
                case 1: return "Sleep";
                case 2: return "Hibernate";
                case 3: return "Shut down";
                default: return v.Value.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    // The user's own values, saved to disk BEFORE anything is changed, so even a crash can be undone
    // (the next start, or "LidAwake restore", puts them back).
    sealed class Snapshot
    {
        public Guid Scheme;
        public readonly Dictionary<string, uint> Values = new Dictionary<string, uint>();

        static string FilePath { get { return Path.Combine(App.DataDir, "restore.txt"); } }

        public static bool Exists { get { return File.Exists(FilePath); } }

        public uint? Get(PlanSetting s)
        {
            uint v;
            return Values.TryGetValue(s.Key, out v) ? v : (uint?)null;
        }

        public static Snapshot Capture()
        {
            var snap = new Snapshot();
            snap.Scheme = PowerPlan.ActiveScheme();
            foreach (var s in PowerPlan.All())
            {
                uint? v = PowerPlan.Read(snap.Scheme, s);
                if (v.HasValue) snap.Values[s.Key] = v.Value;
            }
            return snap;
        }

        public void Save()
        {
            Directory.CreateDirectory(App.DataDir);
            var lines = new List<string>();
            lines.Add("scheme=" + Scheme.ToString("D"));
            foreach (var kv in Values) lines.Add(kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
            string tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, lines);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public static Snapshot Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var snap = new Snapshot();
                bool haveScheme = false;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim(), val = raw.Substring(eq + 1).Trim();
                    uint n;
                    if (key == "scheme") haveScheme = Guid.TryParse(val, out snap.Scheme);
                    else if (uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) snap.Values[key] = n;
                }
                return haveScheme ? snap : null;
            }
            catch (Exception ex)
            {
                App.Log("Could not read " + FilePath + ": " + ex.Message);
                return null;
            }
        }

        public static void Delete()
        {
            try { File.Delete(FilePath); } catch (Exception ex) { App.Log("Could not delete restore file: " + ex.Message); }
        }

        // Writes the saved values back. Returns false if something could not be restored.
        public bool Restore()
        {
            bool ok = true;
            foreach (var s in PowerPlan.All())
            {
                uint? v = Get(s);
                if (!v.HasValue) continue;
                try { PowerPlan.Write(Scheme, s, v.Value); }
                catch (Exception ex) { App.Log("Restore failed: " + ex.Message); ok = false; }
            }
            try { PowerPlan.Apply(Scheme); }
            catch (Exception ex) { App.Log("Apply failed while restoring: " + ex.Message); ok = false; }
            App.Log("Restored the original settings (lid: plugged in=" + PowerPlan.LidName(Get(PowerPlan.LidAc))
                + ", battery=" + PowerPlan.LidName(Get(PowerPlan.LidDc)) + ")" + (ok ? "" : " WITH ERRORS"));
            return ok;
        }

        // A previous run was killed before it could clean up: put its saved originals back.
        public static bool RecoverLeftovers()
        {
            if (!Exists) return true;
            var old = Load();
            if (old == null) { Delete(); return true; }
            App.Log("Found settings from a run that did not exit cleanly - restoring them first.");
            if (!old.Restore()) return false;
            Delete();
            return true;
        }
    }

    // Does the actual work: lid -> "Do nothing", sleep -> "Never", energy saver while the lid is shut
    // on battery, and puts everything back afterwards.
    sealed class Keeper
    {
        const int PowerRequestExecutionRequired = 3;

        Snapshot saved;
        IntPtr request = IntPtr.Zero;
        bool threadStateUsed, saverForced, engaged;

        public bool LidClosed { get; private set; }
        public string Problem { get; private set; }

        public void Engage()
        {
            if (engaged) return;
            Problem = null;
            if (!Snapshot.RecoverLeftovers())
                throw new InvalidOperationException("Could not restore the power settings left over from an earlier run.");

            saved = Snapshot.Capture();
            saved.Save();   // before touching anything
            engaged = true;
            App.Log("Engaged. Plan " + saved.Scheme + ", lid: plugged in=" + PowerPlan.LidName(saved.Get(PowerPlan.LidAc))
                + ", battery=" + PowerPlan.LidName(saved.Get(PowerPlan.LidDc)));

            try
            {
                foreach (var s in PowerPlan.KeptAwake)
                {
                    uint? v = saved.Get(s);
                    if (v.HasValue && v.Value != 0) PowerPlan.Write(saved.Scheme, s, 0);
                }
                PowerPlan.Apply(saved.Scheme);
                if (!saved.Get(PowerPlan.LidAc).HasValue && !saved.Get(PowerPlan.LidDc).HasValue) Problem = Strings.NoLid;
            }
            catch (Exception ex)
            {
                Problem = Strings.LidBlocked;
                App.Log("Could not change the power plan: " + ex.Message);
            }

            KeepSystemOn();
            if (LidClosed) ForceEnergySaver();   // turned on while the lid is already shut
        }

        void KeepSystemOn()
        {
            var ctx = new Native.REASON_CONTEXT();
            ctx.Version = Native.POWER_REQUEST_CONTEXT_VERSION;
            ctx.Flags = Native.POWER_REQUEST_CONTEXT_SIMPLE_STRING;
            ctx.SimpleReasonString = "LidAwake: keeping the PC awake until the job is done";
            IntPtr h = Native.PowerCreateRequest(ref ctx);
            if (h != IntPtr.Zero && h != new IntPtr(-1) && Native.PowerSetRequest(h, Native.PowerRequestSystemRequired))
            {
                Native.PowerSetRequest(h, PowerRequestExecutionRequired);
                request = h;
                return;
            }
            if (h != IntPtr.Zero && h != new IntPtr(-1)) Native.CloseHandle(h);
            // fallback, same effect for this (UI) thread
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            threadStateUsed = true;
        }

        void LetSystemSleep()
        {
            if (request != IntPtr.Zero)
            {
                Native.PowerClearRequest(request, PowerRequestExecutionRequired);
                Native.PowerClearRequest(request, Native.PowerRequestSystemRequired);
                Native.CloseHandle(request);
                request = IntPtr.Zero;
            }
            if (threadStateUsed)
            {
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
                threadStateUsed = false;
            }
        }

        public void OnLid(bool closed)
        {
            if (!engaged || closed == LidClosed) { LidClosed = closed; return; }
            LidClosed = closed;
            App.Log(closed ? "Lid closed." : "Lid opened.");
            if (closed) ForceEnergySaver(); else UnforceEnergySaver();
        }

        // Energy saver switches on whenever the battery is at or below this threshold, so 100% means
        // "on whenever running on battery" (it is what Settings writes). The user's own value comes back after.
        void ForceEnergySaver()
        {
            if (saverForced || saved == null || !saved.Get(PowerPlan.SaverDc).HasValue) return;
            try
            {
                PowerPlan.Write(saved.Scheme, PowerPlan.SaverDc, 100);
                PowerPlan.Apply(saved.Scheme);
                saverForced = true;
                App.Log("Energy saver set to turn on while on battery. Active now: " + PowerPlan.EnergySaverOn());
            }
            catch (Exception ex) { App.Log("Could not turn on energy saver: " + ex.Message); }
        }

        void UnforceEnergySaver()
        {
            if (!saverForced) return;
            try
            {
                PowerPlan.Write(saved.Scheme, PowerPlan.SaverDc, saved.Get(PowerPlan.SaverDc).Value);
                PowerPlan.Apply(saved.Scheme);
                saverForced = false;
                App.Log("Energy saver back to the user's setting. Active now: " + PowerPlan.EnergySaverOn());
            }
            catch (Exception ex) { App.Log("Could not restore energy saver: " + ex.Message); }
        }

        // Puts every setting back.
        public void Release()
        {
            if (!engaged) return;
            engaged = false;
            LetSystemSleep();
            if (saved.Restore()) Snapshot.Delete();
            saverForced = false;
        }

        // Hibernate = everything is saved to disk and the laptop powers off; opening it resumes exactly
        // where it was. Falls back to sleep if hibernation is turned off.
        public static void Hibernate(string why)
        {
            App.Log("Hibernating: " + why);
            if (App.DryRun) return;
            Native.EnableShutdownPrivilege();
            if (Native.SetSuspendState(true, false, false)) return;
            App.Log("Hibernate failed (error " + Marshal.GetLastWin32Error() + "), trying sleep.");
            if (!Native.SetSuspendState(false, false, false))
                App.Log("Sleep failed too (error " + Marshal.GetLastWin32Error() + ").");
        }
    }
}
