using System;
using Microsoft.Win32;

namespace LidAwake
{
    // The user's choices, in HKCU\Software\LidAwake (next to PhoneHotspot). The tray writes them, so a command
    // run from inside an app container (the Claude desktop app) still changes the real ones.
    static class Prefs
    {
        const string Key = @"Software\LidAwake";

        static int Get(string name, int fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }

        static void Set(string name, int value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
                k.SetValue(name, value, RegistryValueKind.DWord);
        }

        public static readonly int[] BatteryChoices = { 5, 10, 15, 20, 25, 30 };
        public const int MinBattery = 3, MaxBattery = 50;

        // Hibernate when the battery drops to this with the lid closed.
        public static int HibernateAt
        {
            get { int v = Get("HibernateAt", 10); return v < MinBattery || v > MaxBattery ? 10 : v; }
            set { Set("HibernateAt", Math.Max(MinBattery, Math.Min(MaxBattery, value))); }
        }

        // A balloon when Claude finishes a long job or a timer runs out.
        public static bool NotifyDone
        {
            get { return Get("NotifyDone", 1) == 1; }
            set { Set("NotifyDone", value ? 1 : 0); }
        }

        // The Wi-Fi name of the phone's hotspot, learned on the first successful connection.
        public static string HotspotSsid
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Key))
                        return (k == null ? null : k.GetValue("HotspotSsid") as string) ?? "";
                }
                catch { return ""; }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue("HotspotSsid", value ?? "", RegistryValueKind.String);
            }
        }

        // Minutes from now until the next hour:minute on the clock (today or tomorrow).
        public static int MinutesUntil(int hour, int minute)
        {
            DateTime now = DateTime.Now, t = DateTime.Today.AddHours(hour).AddMinutes(minute);
            if (t <= now) t = t.AddDays(1);
            return (int)Math.Ceiling((t - now).TotalMinutes);
        }
    }
}
