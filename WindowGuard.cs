using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GlassBar
{
    /// <summary>
    /// Keeps app windows from being left under the bars. Windows tells us whenever the user finishes moving or
    /// resizing a window; if it ended up behind the top bar or the dock on its monitor, it is nudged just below
    /// the top bar / just above the dock (and made shorter if it would not fit otherwise). Windows can still use
    /// the free space beside the dock. One guard serves every monitor.
    /// </summary>
    internal sealed class WindowGuard : IDisposable
    {
        private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int OBJID_WINDOW = 0;

        private readonly WinEventDelegate _callback; // kept in a field so it is not garbage-collected
        private IntPtr _hook;

        public WindowGuard()
        {
            _callback = OnWinEvent;
            _hook = SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _callback, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
            {
                return;
            }

            // Wait a moment: Snap layouts and some apps adjust the window again right after the drop.
            DispatcherTimer delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            delay.Tick += (s, e) =>
            {
                delay.Stop();
                KeepClear(hwnd);
            };
            delay.Start();
        }

        private static void KeepClear(IntPtr hwnd)
        {
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd))
                {
                    return; // maximized windows follow the bars' "keep maximized windows clear" settings instead
                }

                Forms.Screen screen = Forms.Screen.FromHandle(hwnd);
                BarLimits limits = App.Instance.BarLimitsOn(screen.DeviceName);
                if (!limits.TopBarBottom.HasValue && !limits.DockTop.HasValue)
                {
                    return; // no bar to keep clear of on this monitor
                }

                if (!GetWindowRect(hwnd, out RECT outer))
                {
                    return;
                }

                // The visible frame: Windows 10/11 windows have invisible resize borders outside it.
                RECT visible = outer;
                DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out visible, Marshal.SizeOf(typeof(RECT)));
                if (visible.Right <= visible.Left || visible.Bottom <= visible.Top)
                {
                    visible = outer;
                }

                int height = visible.Bottom - visible.Top;
                int newTop = visible.Top;
                int newBottom = visible.Bottom;

                // The window's top edge may not go above this: below the top bar, else the top of the work area.
                int topLimit = Math.Max(screen.WorkingArea.Top, limits.TopBarBottom ?? int.MinValue);

                // The dock only matters where the window is actually over it (windows may sit beside the dock).
                bool besideDock = !limits.DockTop.HasValue ||
                                  visible.Right <= limits.DockLeft || visible.Left >= limits.DockRight;
                int dockLimit = limits.DockTop ?? int.MaxValue;

                // 1. Behind the dock: move up so the bottom edge sits just above it.
                if (!besideDock && newBottom > dockLimit)
                {
                    newBottom = dockLimit;
                    newTop = dockLimit - height;
                }

                // 2. Behind the top bar (or pushed there by step 1): move down below it, and if that puts it
                //    behind the dock again, make it shorter (when it can be resized).
                if (newTop < topLimit)
                {
                    newTop = topLimit;
                    newBottom = topLimit + height;
                    if (!besideDock && newBottom > dockLimit && IsResizable(hwnd))
                    {
                        newBottom = Math.Max(topLimit + 100, dockLimit);
                    }
                }

                if (newTop == visible.Top && newBottom == visible.Bottom)
                {
                    return; // already clear
                }

                // Translate the visible frame back to the full window rectangle (including the invisible borders).
                int top = newTop - (visible.Top - outer.Top);
                int bottom = newBottom + (outer.Bottom - visible.Bottom);

                SetWindowPos(hwnd, IntPtr.Zero, outer.Left, top, outer.Right - outer.Left, bottom - top,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("WindowGuard: " + ex.Message);
            }
        }

        private static bool IsResizable(IntPtr hwnd)
        {
            return (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_THICKFRAME) != 0;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }

        #region Interop

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int GWL_STYLE = -16;
        private const long WS_THICKFRAME = 0x00040000;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        #endregion
    }

    /// <summary>
    /// What windows on one monitor must stay clear of, in physical pixels. Null when that bar is absent, hidden,
    /// or its "don't let windows go behind it" setting is off.
    /// </summary>
    internal struct BarLimits
    {
        /// <summary>Windows' top edge may not go above this.</summary>
        public int? TopBarBottom;

        /// <summary>Windows over the dock may not reach below this (the dock's top, minus a small gap).</summary>
        public int? DockTop;

        /// <summary>The dock's horizontal extent: windows entirely beside it are fine.</summary>
        public int DockLeft, DockRight;
    }
}
