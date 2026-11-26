using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace GlassBar
{
    /// <summary>
    /// Shared behavior for the dock and the top bar. Each bar is two windows:
    ///
    ///  1. This window: the visible glass panel, sized exactly to its content. Windows applies the blur to a
    ///     window's whole rectangle, so keeping the window the size of the panel keeps the glass inside it.
    ///  2. A <see cref="ReserveBar"/>: an invisible, click-through strip along the screen edge registered as an
    ///     app bar, so maximized windows stop short of the bar. It is removed while auto-hide is on.
    /// </summary>
    public class BarWindow : Window
    {
        protected readonly ShellManager Shell;
        protected static Settings S => Settings.Instance;

        private readonly AppBarEdge _edge;
        private readonly string _screenName;
        private readonly DispatcherTimer _autoHideTimer;

        private FrameworkElement _pill;
        private ReserveBar _reserve;
        private double _reservedHeight;
        private bool _closing;
        private bool _glassQueued;

        private bool _autoHide;
        private double _hideAmount;   // 0 = fully shown, 1 = fully hidden
        private double _hideTarget;
        private double _animFrom;
        private DateTime _animStart;
        private bool _animating;
        private DateTime _lastInteraction = DateTime.Now;
        private bool _menuOpen;
        private bool _dragInside;
        private bool _fullScreenHidden;

        /// <param name="screenDeviceName">The monitor this bar belongs to (Screen.DeviceName, e.g. \\.\DISPLAY1).</param>
        public BarWindow(ShellManager shell, AppBarEdge edge, string screenDeviceName)
        {
            Shell = shell;
            _edge = edge;
            _screenName = screenDeviceName;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            UseLayoutRounding = true;

            _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _autoHideTimer.Tick += AutoHideTimer_Tick;

            SourceInitialized += Bar_SourceInitialized;
            SizeChanged += (s, e) => { Position(); QueueGlassUpdate(); };
            DpiChanged += (s, e) => { Position(); QueueGlassUpdate(); };

            ContextMenuOpening += (s, e) => _menuOpen = true;
            ContextMenuClosing += (s, e) => { _menuOpen = false; _lastInteraction = DateTime.Now; };
            PreviewDragEnter += (s, e) => _dragInside = true;
            PreviewDragLeave += (s, e) => { _dragInside = false; _lastInteraction = DateTime.Now; };
            PreviewDrop += (s, e) => _dragInside = false;

            S.PropertyChanged += Settings_PropertyChanged;
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            Shell.FullScreenHelper.FullScreenApps.CollectionChanged += FullScreenApps_CollectionChanged;
        }

        #region For derived windows

        /// <summary>Call once after InitializeComponent with the rounded panel that fills the window.</summary>
        protected void InitBar(FrameworkElement pill)
        {
            _pill = pill;
            _pill.SizeChanged += (s, e) => QueueGlassUpdate();
        }

        protected virtual double PillCornerRadius => 0;
        protected virtual BlurMode PillBlurMode => BlurMode.Acrylic;

        /// <summary>Gap between the panel and the screen edge(s).</summary>
        protected virtual double EdgeMargin => 0;

        /// <summary>True: stretch across the screen (top bar). False: centered at its own width (dock).</summary>
        protected virtual bool StretchAcross => false;

        /// <summary>Called for every settings change so derived windows can update their layout.</summary>
        protected virtual void OnSettingChanged(string name) { }

        protected virtual void OnBarClosing() { }

        protected IntPtr Hwnd { get; private set; }

        /// <summary>The monitor this bar is on (Screen.DeviceName).</summary>
        public string ScreenDeviceName => _screenName;

        /// <summary>This bar's monitor as it is now; the main monitor if it was unplugged.</summary>
        protected Forms.Screen BarScreen =>
            Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _screenName) ?? Forms.Screen.PrimaryScreen;

        public bool IsOnPrimaryScreen => BarScreen.Primary;

        /// <summary>True while the bar is fully on screen (not auto-hidden or sliding, not hidden for a full-screen app).</summary>
        public bool IsFullyShown => IsVisible && IsShownNow;

        /// <summary>The bar's rectangle on screen, in physical pixels.</summary>
        public NativeMethods.Rect BarScreenRect => ScreenRect;

        public double BarScale => Scale;

        protected double Scale
        {
            get
            {
                double scale = Hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1.0;
                return scale > 0 ? scale : 1.0;
            }
        }

        /// <summary>True unless the bar is auto-hidden or sliding.</summary>
        protected bool IsShownNow => !_fullScreenHidden && _hideAmount < 0.001 && _hideTarget < 0.5;

        /// <summary>The bar's on-screen rectangle in physical pixels.</summary>
        protected NativeMethods.Rect ScreenRect
        {
            get
            {
                NativeMethods.Rect rect = new NativeMethods.Rect();
                if (Hwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowRect(Hwnd, out rect);
                }

                return rect;
            }
        }

        /// <summary>How much screen space to keep free for this bar (WPF units), so maximized windows stop short of it.</summary>
        protected void SetReservedHeight(double height)
        {
            _reservedHeight = height;
            SyncReservation();
        }

        protected void SetAutoHide(bool autoHide)
        {
            if (_autoHide == autoHide)
            {
                return;
            }

            _autoHide = autoHide;
            SyncReservation();

            if (autoHide)
            {
                _lastInteraction = DateTime.Now;
                _autoHideTimer.Start();
            }
            else
            {
                _autoHideTimer.Stop();
                AnimateTo(0);
            }
        }

        #endregion

        #region Lifetime

        private void Bar_SourceInitialized(object sender, EventArgs e)
        {
            Hwnd = new WindowInteropHelper(this).Handle;

            // Keep the bar out of Alt+Tab, Task View and Aero Peek.
            WindowHelper.HideWindowFromTasks(Hwnd);

            SyncReservation();
            Position();
            QueueGlassUpdate();
        }

        /// <summary>Closes the bar and its space reservation.</summary>
        public void CloseBar()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            OnBarClosing();

            _autoHideTimer.Stop();
            if (_animating)
            {
                CompositionTarget.Rendering -= Animation_Frame;
                _animating = false;
            }

            S.PropertyChanged -= Settings_PropertyChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            Shell.FullScreenHelper.FullScreenApps.CollectionChanged -= FullScreenApps_CollectionChanged;

            _reserve?.CloseBar();
            _reserve = null;

            Close();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closing)
            {
                return;
            }

            OnSettingChanged(e.PropertyName);
            Position();
            QueueGlassUpdate();
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing)
                {
                    return;
                }

                // Rebuild the reservation for the new screen layout, then move the panel.
                _reserve?.CloseBar();
                _reserve = null;
                SyncReservation();
                Position();
                QueueGlassUpdate();
            }));
        }

        #endregion

        #region Position and space reservation

        private void SyncReservation()
        {
            bool wanted = !_autoHide && !_closing && Hwnd != IntPtr.Zero && _reservedHeight > 0;

            if (wanted && _reserve == null)
            {
                _reserve = new ReserveBar(Shell, _edge, _reservedHeight, AppBarScreen.FromScreen(BarScreen));
                _reserve.Show();
            }
            else if (!wanted && _reserve != null)
            {
                _reserve.CloseBar();
                _reserve = null;
            }
            else if (_reserve != null)
            {
                _reserve.SetHeight(_reservedHeight);
            }
        }

        private Rect ScreenDip()
        {
            System.Drawing.Rectangle b = BarScreen.Bounds;
            double scale = Scale;
            return new Rect(b.Left / scale, b.Top / scale, b.Width / scale, b.Height / scale);
        }

        protected void Position()
        {
            if (Hwnd == IntPtr.Zero || _closing)
            {
                return;
            }

            Rect screen = ScreenDip();
            double margin = Math.Max(0, EdgeMargin);

            if (StretchAcross)
            {
                double width = Math.Max(100, screen.Width - 2 * margin);
                if (Math.Abs(Width - width) > 0.5)
                {
                    Width = width;
                }

                Left = screen.Left + margin;
            }
            else
            {
                Left = Math.Round(screen.Left + (screen.Width - ActualWidth) / 2);
            }

            // While hiding, slide the panel past the screen edge.
            double travel = (margin + ActualHeight) * _hideAmount;
            Top = _edge == AppBarEdge.Bottom
                ? screen.Bottom - margin - ActualHeight + travel
                : screen.Top + margin - travel;
        }

        #endregion

        #region Auto-hide

        private void AutoHideTimer_Tick(object sender, EventArgs e)
        {
            if (!_autoHide || _closing || _fullScreenHidden)
            {
                return;
            }

            System.Drawing.Point cursor = Forms.Cursor.Position;
            System.Drawing.Rectangle screen = BarScreen.Bounds;
            bool withinScreenWidth = cursor.X >= screen.Left && cursor.X < screen.Right;

            if (_hideTarget > 0.5)
            {
                // Hidden: come back when the mouse touches the screen edge.
                bool atEdge = _edge == AppBarEdge.Bottom ? cursor.Y >= screen.Bottom - 2 : cursor.Y <= screen.Top + 1;
                if (withinScreenWidth && atEdge)
                {
                    _lastInteraction = DateTime.Now;
                    AnimateTo(0);
                }

                return;
            }

            if (IsCursorNearBar(cursor, screen) || _menuOpen || _dragInside)
            {
                _lastInteraction = DateTime.Now;
                return;
            }

            if ((DateTime.Now - _lastInteraction).TotalMilliseconds > 700)
            {
                AnimateTo(1);
            }
        }

        /// <summary>Over the panel, or in the gap between the panel and the screen edge.</summary>
        private bool IsCursorNearBar(System.Drawing.Point cursor, System.Drawing.Rectangle screen)
        {
            NativeMethods.Rect r = ScreenRect;
            int left = r.Left - 4;
            int right = r.Right + 4;
            int top = _edge == AppBarEdge.Bottom ? r.Top - 4 : screen.Top;
            int bottom = _edge == AppBarEdge.Bottom ? screen.Bottom : r.Bottom + 4;
            return cursor.X >= left && cursor.X < right && cursor.Y >= top && cursor.Y < bottom;
        }

        private void AnimateTo(double target)
        {
            if (Math.Abs(_hideTarget - target) < 0.001 && (_animating || Math.Abs(_hideAmount - target) < 0.001))
            {
                return;
            }

            _hideTarget = target;
            _animFrom = _hideAmount;
            _animStart = DateTime.Now;

            if (target < 0.5 && !_fullScreenHidden && Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            if (!_animating)
            {
                _animating = true;
                CompositionTarget.Rendering += Animation_Frame;
            }
        }

        private void Animation_Frame(object sender, EventArgs e)
        {
            double duration = _hideTarget > 0.5 ? 240 : 170;
            double t = Math.Min(1, (DateTime.Now - _animStart).TotalMilliseconds / duration);
            double eased = 1 - Math.Pow(1 - t, 3);

            _hideAmount = _animFrom + (_hideTarget - _animFrom) * eased;
            Position();

            if (t >= 1)
            {
                CompositionTarget.Rendering -= Animation_Frame;
                _animating = false;

                // Fully hidden: take the window off screen entirely, so it never shows on a monitor beyond the edge.
                if (_hideTarget > 0.5)
                {
                    Visibility = Visibility.Hidden;
                }
            }
        }

        #endregion

        #region Full-screen apps

        private void FullScreenApps_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateFullScreen));
        }

        /// <summary>Gets out of the way of full-screen games and videos on this bar's screen.</summary>
        private void UpdateFullScreen()
        {
            if (_closing)
            {
                return;
            }

            string mine = BarScreen.DeviceName;
            bool fullScreen = false;
            foreach (FullScreenApp app in Shell.FullScreenHelper.FullScreenApps)
            {
                if (app.screen != null && (app.screen.DeviceName == mine || app.screen.IsVirtualScreen))
                {
                    fullScreen = true;
                    break;
                }
            }

            if (fullScreen == _fullScreenHidden)
            {
                return;
            }

            _fullScreenHidden = fullScreen;

            if (fullScreen)
            {
                Visibility = Visibility.Hidden;
            }
            else if (!_autoHide || _hideTarget < 0.5)
            {
                Visibility = Visibility.Visible;
                Position();
                QueueGlassUpdate();
            }
        }

        #endregion

        #region Glass

        /// <summary>Re-applies blur and rounded shape after the current layout pass has finished.</summary>
        protected void QueueGlassUpdate()
        {
            if (_glassQueued)
            {
                return;
            }

            _glassQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateGlass));
        }

        private void UpdateGlass()
        {
            _glassQueued = false;

            if (Hwnd == IntPtr.Zero || _pill == null || _closing || _pill.ActualWidth < 1 || _pill.ActualHeight < 1)
            {
                return;
            }

            double scale = Scale;
            int width = (int)Math.Round(_pill.ActualWidth * scale);
            int height = (int)Math.Round(_pill.ActualHeight * scale);

            double maxRadius = Math.Min(_pill.ActualWidth, _pill.ActualHeight) / 2;
            int radius = (int)Math.Round(Math.Max(0, Math.Min(PillCornerRadius, maxRadius)) * scale);

            int dwmCorner = DwmCornerFor(PillCornerRadius);
            if (dwmCorner != Native.CornerDefault && Native.SetCornerPreference(Hwnd, dwmCorner))
            {
                // Windows 11 rounds (and anti-aliases) the corners of the window and its blur itself.
                Native.ClearRegion(Hwnd);
            }
            else
            {
                // Custom roundness, or Windows 10: clip the window (and the blur) to the panel's rounded shape.
                // Region edges are not anti-aliased.
                Native.SetCornerPreference(Hwnd, Native.CornerSquare);
                Native.SetRoundedRegion(Hwnd, 0, 0, width, height, radius);
            }

            Native.SetAccent(Hwnd, PillBlurMode);
        }

        /// <summary>Windows 11's own rounded corners are about 8 px (4 px for the small style) at 100% scale.</summary>
        private const double DwmRadius = 8, DwmRadiusSmall = 4;

        /// <summary>Which Windows 11 corner style to use, or CornerDefault for a custom-radius region.</summary>
        private static int DwmCornerFor(double requestedRadius)
        {
            if (!S.SmoothCorners || !EnvironmentHelper.IsWindows11OrBetter)
            {
                return Native.CornerDefault;
            }

            if (requestedRadius < 2)
            {
                return Native.CornerSquare;
            }

            return requestedRadius < 6 ? Native.CornerRoundSmall : Native.CornerRound;
        }

        /// <summary>
        /// The corner radius the panel should be painted with: with smooth corners on, it has to match the
        /// radius Windows clips the window to, whatever the roundness slider says.
        /// </summary>
        protected static double PaintedCornerRadius(double requestedRadius)
        {
            switch (DwmCornerFor(requestedRadius))
            {
                case Native.CornerRound:
                    return DwmRadius;
                case Native.CornerRoundSmall:
                    return DwmRadiusSmall;
                case Native.CornerSquare:
                    return 0;
                default:
                    return requestedRadius;
            }
        }

        #endregion

        #region Context menu actions shared by both bars

        protected void OpenSettings()
        {
            App.Instance.ShowSettings();
        }

        protected void ExitApp()
        {
            App.Instance.ExitGracefully();
        }

        #endregion

        /// <summary>
        /// Invisible app bar that only reserves space along the screen edge. Its pixels are fully transparent,
        /// so clicks pass straight through it to whatever is underneath.
        /// </summary>
        private sealed class ReserveBar : AppBarWindow
        {
            public ReserveBar(ShellManager shell, AppBarEdge edge, double height, AppBarScreen screen)
                : base(shell.AppBarManager, shell.ExplorerHelper, shell.FullScreenHelper, screen, edge, AppBarMode.Normal, height)
            {
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowActivated = false;
                IsHitTestVisible = false;
                Focusable = false;
            }

            public void SetHeight(double height)
            {
                if (Math.Abs(DesiredHeight - height) < 0.01)
                {
                    return;
                }

                DesiredHeight = height;
                if (Handle != IntPtr.Zero && !IsClosing)
                {
                    UpdatePosition();
                }
            }

            public void CloseBar()
            {
                AllowClose = true;
                Close();
            }
        }
    }
}
