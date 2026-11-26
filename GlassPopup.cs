using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell.Common.Helpers;

namespace GlassBar
{
    /// <summary>
    /// A small floating glass panel next to a bar: window previews, folder stacks and the calendar.
    /// Like the bars it is a transparent window with Windows' blur behind it; Windows 11 rounds its corners.
    /// </summary>
    public class GlassPopup : Window
    {
        protected static Settings S => Settings.Instance;

        /// <summary>The rounded panel; derived popups put their content in it.</summary>
        protected readonly Border Panel;

        protected IntPtr Hwnd { get; private set; }

        // Where to show: a rectangle on screen (physical pixels) to sit above or below.
        private Rect _anchor;
        private bool _above;

        /// <summary>Gap between the popup and the thing it belongs to, in WPF units.</summary>
        private const double Gap = 8;

        /// <param name="closeWhenDeactivated">Popups the user clicks into (stacks, calendar) close when focus moves elsewhere.</param>
        public GlassPopup(bool closeWhenDeactivated)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = closeWhenDeactivated;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;

            Panel = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Background = PanelBrush(),
                BorderBrush = S.DockEdgeBrush
            };
            TextElement.SetForeground(Panel, S.TextBrush);
            TextElement.SetFontFamily(Panel, S.FontFamilyValue);
            TextElement.SetFontSize(Panel, 12);
            Content = Panel;

            SourceInitialized += (s, e) =>
            {
                Hwnd = new WindowInteropHelper(this).Handle;
                WindowHelper.HideWindowFromTasks(Hwnd);
                if (!Native.SetCornerPreference(Hwnd, Native.CornerRound))
                {
                    Panel.CornerRadius = new CornerRadius(0);
                }

                Native.SetAccent(Hwnd, S.DockBlurMode == BlurMode.None ? BlurMode.None : BlurMode.Acrylic);
            };

            SizeChanged += (s, e) => Reposition();
            DpiChanged += (s, e) => Reposition(); // moved onto a monitor with a different scale

            if (closeWhenDeactivated)
            {
                Deactivated += (s, e) => HidePopup();
            }
        }

        /// <summary>The dock's tint, but never so thin that text over a busy background gets hard to read.</summary>
        private static Brush PanelBrush()
        {
            Color color = Color.FromRgb(0x1C, 0x1C, 0x1E);
            try
            {
                color = (Color)ColorConverter.ConvertFromString(S.DockTintColor);
            }
            catch (Exception)
            {
                // Keep the default.
            }

            color.A = (byte)Math.Round(255 * Math.Max(0.72, S.DockTintOpacity));
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        protected double Scale
        {
            get
            {
                double scale = Hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : 0;
                return scale > 0 ? scale : SystemScale;
            }
        }

        private static double SystemScale
        {
            get
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    return g.DpiX / 96.0;
                }
            }
        }

        /// <summary>Shows the popup centered on <paramref name="anchor"/> (screen pixels), above or below it.</summary>
        public void ShowAt(Rect anchor, bool above)
        {
            _anchor = anchor;
            _above = above;
            Panel.Background = PanelBrush();

            if (!IsVisible)
            {
                // Place it before it appears, so it doesn't flash at the old spot.
                Opacity = 0;
                Show();
            }

            UpdateLayout();
            Reposition();
            Opacity = 1;

            if (ShowActivated)
            {
                Activate();
            }
        }

        private void Reposition()
        {
            if (_anchor.IsEmpty || ActualWidth < 1)
            {
                return;
            }

            double scale = Scale;
            Rect screen = ScreenDip(_anchor, scale);

            double centerX = (_anchor.Left + _anchor.Width / 2) / scale;
            double left = Math.Round(centerX - ActualWidth / 2);
            left = Math.Max(screen.Left + 4, Math.Min(screen.Right - ActualWidth - 4, left));

            double top = _above
                ? _anchor.Top / scale - Gap - ActualHeight
                : _anchor.Bottom / scale + Gap;
            top = Math.Max(screen.Top, Math.Min(screen.Bottom - ActualHeight, top));

            Left = left;
            Top = Math.Round(top);
            OnPositioned();
        }

        /// <summary>The bounds of the monitor the anchor is on, in WPF units.</summary>
        private static Rect ScreenDip(Rect anchor, double scale)
        {
            System.Drawing.Rectangle b = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
                (int)anchor.X, (int)anchor.Y, Math.Max(1, (int)anchor.Width), Math.Max(1, (int)anchor.Height))).Bounds;
            return new Rect(b.Left / scale, b.Top / scale, b.Width / scale, b.Height / scale);
        }

        /// <summary>Called after every move or resize (window previews re-place their thumbnails here).</summary>
        protected virtual void OnPositioned() { }

        public virtual void HidePopup()
        {
            if (IsVisible)
            {
                Hide();
            }
        }

        #region Shared building blocks

        /// <summary>A flat clickable area that lights up on hover, like the bar buttons.</summary>
        protected static Border MakeHoverTile(UIElement content, Thickness padding)
        {
            Border tile = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = padding,
                Child = content
            };
            tile.MouseEnter += (s, e) => tile.Background = S.HoverBrush;
            tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
            return tile;
        }

        #endregion
    }
}
