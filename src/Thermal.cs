using System;
using System.Management;
using System.Runtime.InteropServices;

namespace LidAwake
{
    // CPU and GPU temperatures without admin rights, on any laptop:
    // - ASUS: the ATKACPI driver (what Armoury Crate and G-Helper read), CPU and dedicated GPU;
    // - CPU otherwise: Windows' ACPI thermal zone;
    // - GPU otherwise: the graphics driver's own reading (what Task Manager shows), NVIDIA, AMD or Intel.
    // -1 = not available (e.g. the dedicated GPU is switched off).
    static class Thermal
    {
        // ---- ASUS ----------------------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr device, uint code, byte[] input, int inputLength,
            byte[] output, int outputLength, out int returned, IntPtr overlapped);

        static int Asus(uint sensor)
        {
            IntPtr h = CreateFile(@"\\.\ATKACPI", 0xC0000000 /* read | write */, 3, IntPtr.Zero, 3 /* existing */, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) return -1;
            try
            {
                // ACPI method DSTS ("device status") with the sensor id as its only argument
                var input = new byte[16];
                BitConverter.GetBytes(0x53545344u).CopyTo(input, 0);
                BitConverter.GetBytes(8u).CopyTo(input, 4);
                BitConverter.GetBytes(sensor).CopyTo(input, 8);
                var output = new byte[16];
                int returned;
                if (!DeviceIoControl(h, 0x0022240C, input, input.Length, output, output.Length, out returned, IntPtr.Zero)) return -1;
                return Plausible(BitConverter.ToInt32(output, 0) - 65536);
            }
            finally { Native.CloseHandle(h); }
        }

        static int Plausible(int celsius) { return celsius > 0 && celsius < 130 ? celsius : -1; }

        // ---- any laptop ----------------------------------------------------------------------

        static int ThermalZone()
        {
            try
            {
                int max = -1;
                using (var q = new ManagementObjectSearcher("SELECT HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                    foreach (ManagementObject o in q.Get())
                        using (o) max = Math.Max(max, Plausible((int)Math.Round(Convert.ToDouble(o["HighPrecisionTemperature"]) / 10 - 273.15)));
                return max;
            }
            catch { return -1; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EnumAdapters { public uint Count; public IntPtr Adapters; }   // D3DKMT_ENUMADAPTERS2

        [StructLayout(LayoutKind.Sequential)]
        struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }

        [DllImport("gdi32.dll")]
        static extern int D3DKMTEnumAdapters2(ref EnumAdapters e);

        [DllImport("gdi32.dll")]
        static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);

        [DllImport("gdi32.dll")]
        static extern int D3DKMTCloseAdapter(ref uint adapter);

        // Hottest graphics adapter, as its driver reports it (KMTQAITYPE_ADAPTERPERFDATA, Windows 10 2004+).
        static int GraphicsDriver()
        {
            const int AdapterInfoSize = 20, PerfDataSize = 64, TemperatureOffset = 56;
            int max = -1;
            try
            {
                var e = new EnumAdapters();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.Count == 0) return -1;
                e.Adapters = Marshal.AllocHGlobal((int)e.Count * AdapterInfoSize);
                IntPtr perf = Marshal.AllocHGlobal(PerfDataSize);
                try
                {
                    if (D3DKMTEnumAdapters2(ref e) != 0) return -1;
                    for (int i = 0; i < e.Count; i++)
                    {
                        uint adapter = (uint)Marshal.ReadInt32(new IntPtr(e.Adapters.ToInt64() + i * AdapterInfoSize));
                        for (int b = 0; b < PerfDataSize; b++) Marshal.WriteByte(perf, b, 0);
                        var q = new QueryAdapterInfo { Adapter = adapter, Type = 62, Data = perf, Size = PerfDataSize };
                        if (D3DKMTQueryAdapterInfo(ref q) == 0)
                            max = Math.Max(max, Plausible(Marshal.ReadInt32(perf, TemperatureOffset) / 10));   // tenths of °C
                        D3DKMTCloseAdapter(ref adapter);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(perf);
                    Marshal.FreeHGlobal(e.Adapters);
                }
            }
            catch { }
            return max;
        }

        // ---- public --------------------------------------------------------------------------

        public static int Cpu()
        {
            int t = Asus(0x00120094);
            return t > 0 ? t : ThermalZone();
        }

        // askDriver: also ask the graphics driver. Fine when the user hovers; the heat guard skips it so a
        // sleeping dedicated GPU is never woken every few seconds.
        public static int Gpu(bool askDriver)
        {
            int t = Asus(0x00120097);
            return t > 0 || !askDriver ? t : GraphicsDriver();
        }

        // "CPU 52°C · GPU 49°C" for the tray tooltip, or null when nothing can be read.
        public static string Summary()
        {
            int cpu = Cpu(), gpu = Gpu(true);
            if (cpu < 0 && gpu < 0) return null;
            return "CPU " + (cpu > 0 ? cpu + "°C" : "–") + " · GPU " + (gpu > 0 ? gpu + "°C" : Strings.GpuOff);
        }
    }
}
