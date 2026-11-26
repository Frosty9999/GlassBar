using System;
using System.Runtime.InteropServices;

namespace GlassBar
{
    public enum BlurMode
    {
        None = 0,
        Blur = 1,
        Acrylic = 2
    }

    /// <summary>Win32 calls GlassBar makes directly (glass effect, window shape, system stats).</summary>
    internal static class Native
    {
        #region Glass (accent policy)

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>Turns the blur behind a window on or off. The tint itself is painted by WPF.</summary>
        public static void SetAccent(IntPtr hwnd, BlurMode mode)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            AccentPolicy accent = new AccentPolicy();
            switch (mode)
            {
                case BlurMode.Acrylic:
                    accent.AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    accent.GradientColor = 0x00FFFFFF; // ABGR, zero alpha: no extra tint from the system
                    break;
                case BlurMode.Blur:
                    accent.AccentState = ACCENT_ENABLE_BLURBEHIND;
                    break;
                default:
                    accent.AccentState = ACCENT_DISABLED;
                    break;
            }

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        #endregion

        #region Window shape

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        /// <summary>
        /// Clips the window (and therefore the blur) to a rounded rectangle, in physical pixels relative to the window.
        /// </summary>
        public static void SetRoundedRegion(IntPtr hwnd, int left, int top, int width, int height, int radius)
        {
            if (hwnd == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return;
            }

            IntPtr region = radius > 0
                ? CreateRoundRectRgn(left, top, left + width + 1, top + height + 1, radius * 2, radius * 2)
                : CreateRectRgn(left, top, left + width, top + height);

            // On success the system owns the region; only free it if the call failed.
            if (SetWindowRgn(hwnd, region, true) == 0)
            {
                DeleteObject(region);
            }
        }

        public static void ClearRegion(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
            }
        }

        #endregion

        #region System stats

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        public static bool TryGetSystemTimes(out long idle, out long total)
        {
            if (GetSystemTimes(out long i, out long k, out long u))
            {
                idle = i;
                total = k + u; // kernel time already includes idle time
                return true;
            }

            idle = 0;
            total = 0;
            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        /// <summary>Percent of physical memory in use, or -1 if unavailable.</summary>
        public static int GetMemoryLoad()
        {
            MEMORYSTATUSEX status = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(status) ? (int)status.dwMemoryLoad : -1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>Returns false on machines without a battery.</summary>
        public static bool TryGetBattery(out int percent, out bool charging)
        {
            percent = 0;
            charging = false;

            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s))
            {
                return false;
            }

            // BatteryFlag 128 = no system battery, 255 = unknown; percent 255 = unknown
            if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent == 255)
            {
                return false;
            }

            percent = s.BatteryLifePercent;
            charging = s.ACLineStatus == 1;
            return true;
        }

        #endregion

        #region Dark title bar

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;               // Windows 11 only

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;    // Windows 11 only
        public const int CornerDefault = 0, CornerSquare = 1, CornerRound = 2, CornerRoundSmall = 3;

        /// <summary>
        /// Asks Windows 11 to round the window's corners itself (anti-aliased, about 8 px, or 4 px for small).
        /// Returns false where unsupported (Windows 10).
        /// </summary>
        public static bool SetCornerPreference(IntPtr hwnd, int preference)
        {
            return hwnd != IntPtr.Zero && DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) == 0;
        }

        /// <summary>Dark title bar, and on Windows 11 a caption color matching the window. Ignored where unsupported.</summary>
        public static void UseDarkTitleBar(IntPtr hwnd, byte r, byte g, byte b)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }

            int colorRef = r | (g << 8) | (b << 16); // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        }

        #endregion

        #region Window activation

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>
        /// Clicking the window no longer takes focus from the app in front (like a Mac menu bar). Mouse clicks,
        /// buttons and menus still work.
        /// </summary>
        public static void SetNoActivate(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE));
            }
        }

        /// <summary>True when the window in front belongs to GlassBar itself (a bar, popup, menu or settings).</summary>
        public static bool ForegroundIsGlassBar()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        #endregion

        #region Known folders

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, int flags, IntPtr token, out IntPtr path);

        private static readonly Guid FOLDERID_Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        /// <summary>The user's Downloads folder, wherever it has been moved to.</summary>
        public static string DownloadsFolder
        {
            get
            {
                if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, IntPtr.Zero, out IntPtr path) == 0)
                {
                    try
                    {
                        return Marshal.PtrToStringUni(path);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(path);
                    }
                }

                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        #endregion

        #region Memory

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        /// <summary>Hands memory pages GlassBar is not actively using back to Windows.</summary>
        public static void TrimWorkingSet()
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        #endregion

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        public static int DoubleClickTime => (int)GetDoubleClickTime();
    }
}
