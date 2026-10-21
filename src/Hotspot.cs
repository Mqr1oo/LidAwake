using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace LidAwake
{
    // Turns on the phone's hotspot through Windows' "Instant hotspot" (Phone Link / Mobile devices) and connects
    // to it. Windows has no API for that, so this does what the user would do: open the Wi-Fi list, press Connect
    // on the phone's "Mobile hotspot" row, close the list, then wait until the laptop is online through it.
    // Windows turns the phone's hotspot on over Bluetooth but doesn't always switch to it (it didn't with the lid
    // closed), so after a few seconds this joins the phone's network itself, like any saved Wi-Fi.
    static class PhoneHotspot
    {
        const string Key = @"Software\LidAwake";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && (k.GetValue("PhoneHotspot") as int? ?? 0) == 1;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue("PhoneHotspot", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        // Windows' own "is there internet" answer (the same one behind the network icon).
        public static bool Online()
        {
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                object nlm = Activator.CreateInstance(t);
                try { return (bool)t.InvokeMember("IsConnectedToInternet", BindingFlags.GetProperty, null, nlm, null); }
                finally { Marshal.ReleaseComObject(nlm); }
            }
            catch { return true; }
        }

        static string Netsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return text;
                }
            }
            catch { return ""; }
        }

        static string CurrentSsid()
        {
            Match m = Regex.Match(Netsh("wlan show interfaces"), @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : "";
        }

        // Networks in Windows' last Wi-Fi scan.
        static List<string> VisibleSsids()
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(Netsh("wlan show networks"), @"^SSID \d+\s*:\s*(.+?)\s*$", RegexOptions.Multiline))
                list.Add(m.Groups[1].Value);
            return list;
        }

        // Join a network Windows already has a profile for, then wait until it's online through it.
        static bool Join(string ssid, int seconds)
        {
            App.Log("Joining '" + ssid + "' directly.");
            Netsh("wlan connect name=\"" + ssid.Replace("\"", "") + "\"");
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                if (CurrentSsid() == ssid && Online()) return true;
            }
            return false;
        }

        // remember: the network appeared only after the phone was asked to start its hotspot, so it is the phone's
        static string Connected(string ssid, bool remember = true)
        {
            if (remember) try { Prefs.HotspotSsid = ssid; } catch { }
            App.Log("Online through '" + ssid + "'.");
            return null;
        }

        // Returns null when the laptop is online through the phone, otherwise why not. Runs on a worker thread.
        public static string Connect(out string phone) { return Connect(out phone, false); }

        // dryRun: find the phone and its Connect button, press nothing (LidAwake hotspot-check).
        public static string Connect(out string phone, bool dryRun)
        {
            phone = null;
            string before = CurrentSsid();
            string known = Prefs.HotspotSsid;
            List<string> seen = VisibleSsids();
            if (!dryRun && known.Length > 0)
            {
                phone = known;
                if (before == known && Online()) return null;
                // the phone's hotspot is already on: just join it
                if (seen.Contains(known) && Join(known, 20)) return Connected(known);
            }

            int flyoutPid = 0;
            AutomationElement list = null, row = null;
            // second try: if the list was already open, the first open closes it instead
            for (int attempt = 0; attempt < 2 && row == null; attempt++)
            {
                if (attempt > 0) { CloseFlyout(flyoutPid); Thread.Sleep(1000); }
                list = OpenWifiList(out flyoutPid);
                if (list != null) row = FindPhoneRow(list, out phone);
            }
            if (list == null) return "the Wi-Fi list did not open";
            try
            {
                if (row == null) return "no phone with Instant hotspot in the Wi-Fi list (set it up in Phone Link)";
                if (Regex.IsMatch(row.Current.Name, @"\bConnected\b") && Online()) return null;

                ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                AutomationElement button = null;
                for (int i = 0; i < 40 && button == null; i++)
                {
                    button = FindByIdSuffix(row, "_ConnectButton");
                    if (button == null) Thread.Sleep(100);
                }
                if (button == null) return "no Connect button for " + phone;
                if (dryRun) return "dry run OK: found " + phone + " and its Connect button; online now: " + Online()
                    + "; phone's network: " + (known.Length > 0 ? known : "not learned yet");
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                App.Log("Pressed Connect on " + phone + "'s hotspot (was on '" + before + "').");
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                Thread.Sleep(1500);
                CloseFlyout(flyoutPid);
            }

            // The phone needs a few seconds (up to ~30) to start its hotspot. Every 10 s without a switch, scan
            // and join it directly: the learned network, or (first time) a network that has just appeared.
            for (int i = 1; i <= 60; i++)
            {
                Thread.Sleep(1000);
                string now = CurrentSsid();
                if (now.Length > 0 && now != before && Online()) return Connected(now, now == known || !seen.Contains(now));
                if (i % 10 != 0) continue;
                WifiScan();
                Thread.Sleep(2000);
                var candidates = new List<string>();
                if (known.Length > 0) candidates.Add(known);
                else foreach (string ssid in VisibleSsids()) if (!seen.Contains(ssid) && candidates.Count < 3) candidates.Add(ssid);
                foreach (string ssid in candidates)
                    if (Join(ssid, 15)) return Connected(ssid);
            }
            return "the laptop did not get online through the phone's hotspot within a minute";
        }

        // ---- Wi-Fi scan (so a hotspot that just came up shows in the list) -------------------

        [DllImport("wlanapi.dll")]
        static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);

        [DllImport("wlanapi.dll")]
        static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll")]
        static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        static extern void WlanFreeMemory(IntPtr memory);

        [DllImport("wlanapi.dll")]
        static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

        static void WifiScan()
        {
            try
            {
                IntPtr h;
                uint version;
                if (WlanOpenHandle(2, IntPtr.Zero, out version, out h) != 0) return;
                try
                {
                    IntPtr list;
                    if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return;
                    try
                    {
                        // WLAN_INTERFACE_INFO_LIST: 2 DWORDs, then 532-byte entries starting with the interface GUID
                        int n = Marshal.ReadInt32(list);
                        for (int i = 0; i < n; i++)
                        {
                            var g = (Guid)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + 8 + i * 532), typeof(Guid));
                            WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    finally { WlanFreeMemory(list); }
                }
                finally { WlanCloseHandle(h, IntPtr.Zero); }
            }
            catch { }
        }

        // ---- the Wi-Fi list ------------------------------------------------------------------

        static AutomationElement OpenWifiList(out int flyoutPid)
        {
            flyoutPid = 0;
            var psi = new ProcessStartInfo("ms-availablenetworks:");
            psi.UseShellExecute = true;
            Process.Start(psi);
            TreeWalker walker = TreeWalker.RawViewWalker;
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(100);
                try
                {
                    for (AutomationElement e = AutomationElement.FocusedElement; e != null && e != AutomationElement.RootElement; e = walker.GetParent(e))
                        if (e.Current.AutomationId.EndsWith("Wi-Fi_ListView", StringComparison.Ordinal))
                        {
                            flyoutPid = e.Current.ProcessId;
                            return e;
                        }
                }
                catch (ElementNotAvailableException) { }
            }
            return null;
        }

        static AutomationElement FindPhoneRow(AutomationElement list, out string phone)
        {
            phone = null;
            TreeWalker walker = TreeWalker.RawViewWalker;
            for (AutomationElement row = walker.GetFirstChild(list); row != null; row = walker.GetNextSibling(row))
            {
                AutomationElement desc = FindByIdSuffix(row, "_DescriptionTextBlock");
                string text = desc != null ? desc.Current.Name : row.Current.Name;
                if (text.IndexOf("hotspot", StringComparison.OrdinalIgnoreCase) < 0) continue;
                AutomationElement title = FindByIdSuffix(row, "_TitleTextBlock");
                phone = title != null ? title.Current.Name : row.Current.Name;
                return row;
            }
            return null;
        }

        static AutomationElement FindByIdSuffix(AutomationElement root, string suffix)
        {
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                if (e.Current.AutomationId.EndsWith(suffix, StringComparison.Ordinal)) return e;
            return null;
        }

        // Escape closes the Wi-Fi list, but only send it while that list still has the focus.
        static void CloseFlyout(int flyoutPid)
        {
            try
            {
                AutomationElement f = AutomationElement.FocusedElement;
                if (f == null || flyoutPid == 0 || f.Current.ProcessId != flyoutPid) return;
                Native.keybd_event(0x1B, 0, 0, UIntPtr.Zero);
                Native.keybd_event(0x1B, 0, 2 /* KEYUP */, UIntPtr.Zero);
            }
            catch { }
        }
    }
}
