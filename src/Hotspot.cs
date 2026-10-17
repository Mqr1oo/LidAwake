using System;
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

        static string CurrentSsid()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    Match m = Regex.Match(text, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
                    return m.Success ? m.Groups[1].Value : "";
                }
            }
            catch { return ""; }
        }

        // Returns null when the laptop is online through the phone, otherwise why not. Runs on a worker thread.
        public static string Connect(out string phone) { return Connect(out phone, false); }

        // dryRun: find the phone and its Connect button, press nothing (LidAwake hotspot-check).
        public static string Connect(out string phone, bool dryRun)
        {
            phone = null;
            string before = CurrentSsid();
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
                if (dryRun) return "dry run OK: found " + phone + " and its Connect button; online now: " + Online();
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                App.Log("Pressed Connect on " + phone + "'s hotspot (was on '" + before + "').");
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                Thread.Sleep(1500);
                CloseFlyout(flyoutPid);
            }

            // the phone needs a few seconds to start its hotspot
            for (int i = 0; i < 45; i++)
            {
                Thread.Sleep(1000);
                string now = CurrentSsid();
                if (now.Length > 0 && (now == phone || now != before) && Online())
                {
                    App.Log("Online through '" + now + "'.");
                    return null;
                }
            }
            return "the phone's hotspot did not come up within 45 s";
        }

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
