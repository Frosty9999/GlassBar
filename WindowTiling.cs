using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace GlassBar
{
    /// <summary>Where a window goes when snapped from the dock. Thirds suit ultrawide screens.</summary>
    public enum TileLayout
    {
        LeftHalf,
        RightHalf,
        LeftThird,
        CenterThird,
        RightThird,
        LeftTwoThirds,
        RightTwoThirds,
        Fill
    }

    /// <summary>
    /// Moves app windows into screen regions (halves, thirds, two-thirds, or filling the screen), on the
    /// monitor each window is on. The usable area stops above the dock when the dock floats over that screen
    /// and "don't let windows be dropped behind the dock" is on, so tiled windows are never covered.
    /// </summary>
    internal static class WindowTiling
    {
        /// <summary>Left edge and width of each layout, as fractions of the usable width.</summary>
        public static (double Left, double Width) Span(TileLayout layout)
        {
            switch (layout)
            {
                case TileLayout.LeftHalf: return (0, 0.5);
                case TileLayout.RightHalf: return (0.5, 0.5);
                case TileLayout.LeftThird: return (0, 1.0 / 3);
                case TileLayout.CenterThird: return (1.0 / 3, 1.0 / 3);
                case TileLayout.RightThird: return (2.0 / 3, 1.0 / 3);
                case TileLayout.LeftTwoThirds: return (0, 2.0 / 3);
                case TileLayout.RightTwoThirds: return (1.0 / 3, 2.0 / 3);
                default: return (0, 1);
            }
        }

        public static string Describe(TileLayout layout)
        {
            switch (layout)
            {
                case TileLayout.LeftHalf: return "Left half";
                case TileLayout.RightHalf: return "Right half";
                case TileLayout.LeftThird: return "Left third";
                case TileLayout.CenterThird: return "Middle third";
                case TileLayout.RightThird: return "Right third";
                case TileLayout.LeftTwoThirds: return "Left two thirds";
                case TileLayout.RightTwoThirds: return "Right two thirds";
                default: return "Fill the screen (above the dock)";
            }
        }

        public static void Apply(IntPtr hwnd, TileLayout layout)
        {
            (double left, double width) = Span(layout);
            Place(hwnd, left, width);
        }

        /// <summary>Puts several windows next to each other in equal columns, on the first window's screen.</summary>
        public static void SideBySide(IList<IntPtr> windows)
        {
            if (windows.Count == 0)
            {
                return;
            }

            Forms.Screen screen = Forms.Screen.FromHandle(windows[0]);
            for (int i = 0; i < windows.Count; i++)
            {
                Place(windows[i], (double)i / windows.Count, 1.0 / windows.Count, screen);
            }
        }

        private static void Place(IntPtr hwnd, double leftFraction, double widthFraction, Forms.Screen screen = null)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // Maximized or minimized windows have to be restored before they can be moved.
            if (IsZoomed(hwnd) || IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }

            screen ??= Forms.Screen.FromHandle(hwnd);
            System.Drawing.Rectangle area = UsableArea(screen);

            int x = area.Left + (int)Math.Round(area.Width * leftFraction);
            int right = area.Left + (int)Math.Round(area.Width * (leftFraction + widthFraction));
            RECT target = new RECT { Left = x, Top = area.Top, Right = right, Bottom = area.Bottom };

            // Windows 10/11 windows have invisible resize borders around the visible frame. Grow the target by
            // those borders so the visible edges line up exactly with the region.
            if (GetWindowRect(hwnd, out RECT outer) &&
                DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visible, Marshal.SizeOf(typeof(RECT))) == 0 &&
                visible.Right > visible.Left)
            {
                target.Left -= visible.Left - outer.Left;
                target.Top -= visible.Top - outer.Top;
                target.Right += outer.Right - visible.Right;
                target.Bottom += outer.Bottom - visible.Bottom;
            }

            SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Right - target.Left, target.Bottom - target.Top,
                SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// The screen's work area, starting below the top bar and stopping above the dock when windows are kept
        /// clear of them (needed when a bar floats over apps instead of reserving its space).
        /// </summary>
        private static System.Drawing.Rectangle UsableArea(Forms.Screen screen)
        {
            System.Drawing.Rectangle area = screen.WorkingArea;
            BarLimits limits = App.Instance.BarLimitsOn(screen.DeviceName);

            int top = area.Top;
            int bottom = area.Bottom;
            if (limits.TopBarBottom.HasValue && limits.TopBarBottom.Value > top && limits.TopBarBottom.Value < bottom)
            {
                top = limits.TopBarBottom.Value;
            }

            if (limits.DockTop.HasValue && limits.DockTop.Value < bottom && limits.DockTop.Value > top)
            {
                bottom = limits.DockTop.Value;
            }

            return new System.Drawing.Rectangle(area.Left, top, area.Width, bottom - top);
        }

        #region Interop

        private const int SW_RESTORE = 9;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        #endregion
    }
}
