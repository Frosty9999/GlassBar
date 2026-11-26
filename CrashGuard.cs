using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBar
{
    /// <summary>
    /// Gives the Windows taskbar back if GlassBar dies without cleaning up (a crash, or "End task").
    ///
    /// At startup GlassBar records the taskbar's original auto-hide state in a marker file and starts a second,
    /// window-less copy of itself ("GlassBar.exe --watchdog &lt;pid&gt;"). A clean exit deletes the marker. When the
    /// watchdog sees GlassBar's process end while the marker still names it, it restores the taskbar's
    /// auto-hide state, shows the taskbar again, points app progress/badge messages back at Explorer and
    /// asks apps to re-add their tray icons.
    /// </summary>
    internal static class CrashGuard
    {
        private static string MarkerPath =>
            Path.Combine(Path.GetDirectoryName(Settings.FilePath) ?? "", "taskbar-restore.txt");

        #region Main process

        /// <summary>Call before ManagedShell touches the taskbar.</summary>
        public static void Begin()
        {
            try
            {
                int state;
                if (TryReadMarker(out int savedState, out _))
                {
                    // The previous run never cleaned up (and its watchdog didn't either). Put the taskbar back the way
                    // it was before that run, so ManagedShell records the real original state this time.
                    state = savedState;
                    SetTaskbarState(state);
                }
                else
                {
                    state = GetTaskbarState();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath) ?? "");
                File.WriteAllText(MarkerPath, state + " " + Environment.ProcessId);

                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo(exe, "--watchdog " + Environment.ProcessId)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.Dispose();
                }
            }
            catch (Exception)
            {
                // Without the watchdog GlassBar still works; only crash recovery is lost.
            }
        }

        /// <summary>Call after a clean shutdown has given the taskbar back.</summary>
        public static void End()
        {
            try
            {
                if (TryReadMarker(out _, out int owner) && owner == Environment.ProcessId)
                {
                    File.Delete(MarkerPath);
                }
            }
            catch (Exception)
            {
                // Worst case the watchdog restores a taskbar that is already restored, which is harmless.
            }
        }

        #endregion

        #region Watchdog process

        /// <summary>Entry point of the watchdog copy. Returns when GlassBar has exited.</summary>
        public static int RunWatchdog(int pid)
        {
            Process glassBar;
            try
            {
                glassBar = Process.GetProcessById(pid);
            }
            catch (Exception)
            {
                return 0; // already gone
            }

            Native.TrimWorkingSet();

            using (glassBar)
            {
                glassBar.WaitForExit();
            }

            // A clean exit removes the marker. If it still names this GlassBar, it died without cleaning up.
            if (!TryReadMarker(out int state, out int owner) || owner != pid)
            {
                return 0;
            }

            try
            {
                RestoreTaskbar(state);
                File.Delete(MarkerPath);
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(Settings.FilePath) ?? "", "error.log"), DateTime.Now.ToString("s") +
                    "  GlassBar (process " + pid + ") stopped without cleaning up. The watchdog restored the Windows taskbar." +
                    Environment.NewLine + Environment.NewLine);
            }
            catch (Exception)
            {
                // Nothing more to do.
            }

            return 0;
        }

        private static void RestoreTaskbar(int state)
        {
            SetTaskbarState(state);

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                SetWindowPos(tray, HWND_BOTTOM, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

                // ManagedShell redirected the ITaskbarList messages (progress, badges) to its own window, which is gone.
                IntPtr taskList = FindChild(tray, "MSTaskSwWClass");
                if (taskList != IntPtr.Zero)
                {
                    SetProp(tray, "TaskbandHWND", taskList);
                }
            }

            IntPtr secondary = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
            while (secondary != IntPtr.Zero)
            {
                SetWindowPos(secondary, HWND_BOTTOM, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
            }

            // Apps re-add their tray icons when they get this message, so Explorer's tray fills up again.
            SendNotifyMessage(HWND_BROADCAST, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
        }

        #endregion

        #region Marker file and taskbar state

        private static bool TryReadMarker(out int state, out int owner)
        {
            state = 0;
            owner = 0;
            try
            {
                if (!File.Exists(MarkerPath))
                {
                    return false;
                }

                string[] parts = File.ReadAllText(MarkerPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 2 && int.TryParse(parts[0], out state) && int.TryParse(parts[1], out owner);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private const int ABM_GETSTATE = 4;
        private const int ABM_SETSTATE = 10;

        private static int GetTaskbarState()
        {
            APPBARDATA data = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = FindWindow("Shell_TrayWnd", null) };
            return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
        }

        private static void SetTaskbarState(int state)
        {
            APPBARDATA data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = FindWindow("Shell_TrayWnd", null),
                lParam = (IntPtr)state
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }

        private static IntPtr FindChild(IntPtr parent, string className)
        {
            IntPtr found = IntPtr.Zero;
            StringBuilder name = new StringBuilder(64);
            EnumChildWindows(parent, (hwnd, _) =>
            {
                name.Clear();
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() == className)
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        #endregion

        #region Interop

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        private static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern bool SendNotifyMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        #endregion
    }
}
