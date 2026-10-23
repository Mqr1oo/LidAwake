using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LidAwake
{
    // Win32 declarations. Everything here is documented API.
    static class Native
    {
        // ---- power settings (powrprof.dll) -------------------------------------------------

        [DllImport("powrprof.dll")]
        public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        public static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll")]
        public static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid settingGuid, uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupGuid, ref Guid settingGuid, uint value);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SetSuspendState([MarshalAs(UnmanagedType.I1)] bool hibernate,
            [MarshalAs(UnmanagedType.I1)] bool forceCritical, [MarshalAs(UnmanagedType.I1)] bool disableWakeEvent);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr mem);

        // ---- power requests (keep the system out of idle sleep) ----------------------------

        // Power mode (Settings > System > Power > Power mode). Not in the SDK headers, but exported and what
        // Settings itself calls. Windows keeps one value for battery and one for plugged in; Set changes the
        // one for the current power source.
        [DllImport("powrprof.dll")]
        public static extern uint PowerGetActualOverlayScheme(out Guid overlay);

        [DllImport("powrprof.dll")]
        public static extern uint PowerSetActiveOverlayScheme(Guid overlay);

        public const int PowerRequestDisplayRequired = 0;
        public const int PowerRequestSystemRequired = 1;
        public const uint POWER_REQUEST_CONTEXT_VERSION = 0;

        // SYSTEM_POWER_CAPABILITIES is 76 bytes; byte 20 is AoAc (Modern Standby).
        [DllImport("powrprof.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetPwrCapabilities([Out] byte[] capabilities);
        public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct REASON_CONTEXT
        {
            public uint Version;
            public uint Flags;
            public string SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;       // 0 battery, 1 plugged in, 255 unknown
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;   // 1 = battery/energy saver is on
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        // ---- power setting notifications (lid open/closed) ---------------------------------

        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_POWERSETTINGCHANGE = 0x8013;
        public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll")]
        public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        // ---- IPC between the command line and the running tray instance --------------------

        // ---- idle time, who keeps the screen on, session lock ------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        // Tick count of the last keyboard / mouse / touch input in this session.
        public static uint LastInputTick()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = 8;
            return GetLastInputInfo(ref info) ? info.dwTime : (uint)Environment.TickCount;
        }

        public static uint IdleSeconds() { return unchecked((uint)Environment.TickCount - LastInputTick()) / 1000; }

        [DllImport("powrprof.dll")]
        static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out uint output, uint outputLength);

        // The whole system's ES_* flags: every app's power requests added together.
        public static uint SystemExecutionState()
        {
            uint state;
            return CallNtPowerInformation(16 /* SystemExecutionState */, IntPtr.Zero, 0, out state, 4) == 0 ? state : 0;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

        // Windows only lets the app the user is in bring another window to the front. Sharing its input
        // queue for a moment is the usual way around that.
        public static bool BringToFront(IntPtr hWnd)
        {
            IntPtr front = GetForegroundWindow();
            if (front == hWnd) return true;
            uint me = GetCurrentThreadId();
            uint them = front != IntPtr.Zero ? GetWindowThreadProcessId(front, IntPtr.Zero) : 0;
            bool attached = them != 0 && them != me && AttachThreadInput(me, them, true);
            try { SetForegroundWindow(hWnd); }
            finally { if (attached) AttachThreadInput(me, them, false); }
            return GetForegroundWindow() == hWnd;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

        [DllImport("wtsapi32.dll")]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        // Locked right now? WTSSessionInfoEx: Level 1, then (8-byte aligned) SessionId, SessionState,
        // SessionFlags at offset 16, where 0 = locked.
        public static bool SessionIsLocked()
        {
            IntPtr buf;
            int bytes;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, 25, out buf, out bytes)) return false;
            try { return bytes >= 20 && Marshal.ReadInt32(buf, 0) == 1 && Marshal.ReadInt32(buf, 16) == 0; }
            finally { WTSFreeMemory(buf); }
        }

        public const int WM_WTSSESSION_CHANGE = 0x02B1;
        public const int WTS_SESSION_LOCK = 0x7;
        public const int WTS_SESSION_UNLOCK = 0x8;

        public const int WM_COPYDATA = 0x004A;
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION = 0x0016;
        public const int SMTO_ABORTIFHUNG = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam,
            ref COPYDATASTRUCT lParam, int flags, int timeoutMs, out IntPtr result);

        // ---- processes ---------------------------------------------------------------------

        public const int SYNCHRONIZE = 0x00100000;
        public const uint WAIT_OBJECT_0 = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public int dwSize;
            public int cntUsage;
            public int th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID;
            public int cntThreads;
            public int th32ParentProcessID;
            public int pcPriClassBase;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll")]
        public static extern int GetCurrentProcessId();

        public struct ProcInfo { public int Parent; public string Exe; }

        // pid -> (parent pid, exe name) for every running process
        public static Dictionary<int, ProcInfo> ProcessTable()
        {
            var table = new Dictionary<int, ProcInfo>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return table;
            try
            {
                var e = new PROCESSENTRY32W();
                e.dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32W));
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    var info = new ProcInfo();
                    info.Parent = e.th32ParentProcessID;
                    info.Exe = e.szExeFile ?? "";
                    table[e.th32ProcessID] = info;
                }
            }
            finally { CloseHandle(snap); }
            return table;
        }

        // ---- shutdown privilege (needed by SetSuspendState / ExitWindowsEx) ----------------

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
            int bufferLength, IntPtr previousState, IntPtr returnLength);

        public static bool EnableShutdownPrivilege()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008 /* ADJUST_PRIVILEGES | QUERY */, out token))
                return false;
            try
            {
                var tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Attributes = 0x2; // SE_PRIVILEGE_ENABLED
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid)) return false;
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                    && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ExitWindowsEx(uint flags, uint reason);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

        // ---- console output for a windowed exe ---------------------------------------------

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int stdHandle);
    }
}
