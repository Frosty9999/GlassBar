using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using TrayIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace GlassBar
{
    /// <summary>The menu-bar style strip at the top of the screen: Start, window title, tray, stats and clock.</summary>
    public partial class TopBarWindow : BarWindow
    {
        public TopBarWindow(ShellManager shell, string screenDeviceName)
            : base(shell, AppBarEdge.Top, screenDeviceName)
        {
            InitializeComponent();

            TrayPanel.DataContext = shell.NotificationArea;
            UseOwnTrayLists();
            StatsPanel.DataContext = SystemStats.Instance;

            InitBar(Pill);
            ApplyLayout();

            shell.TasksService.WindowActivated += TasksService_WindowActivated;
            shell.TasksService.DesktopActivated += TasksService_DesktopActivated;
            ShowCurrentWindowTitle();

            // Apps place their tray flyouts next to the main screen's bar (or the bar whose tray was just clicked).
            SourceInitialized += (s, e) =>
            {
                // Clicking the bar (the app name, the clock...) must not take focus from the app in front:
                // the app stays active, and its name stays in the bar.
                Native.SetNoActivate(Hwnd);
                if (IsOnPrimaryScreen)
                {
                    SetTrayHost();
                }
            };
            LocationChanged += (s, e) => { if (IsOnPrimaryScreen) SetTrayHost(); };
            SizeChanged += (s, e) => { if (IsOnPrimaryScreen) SetTrayHost(); };
        }

        #region Layout

        protected override double PillCornerRadius => S.TopBarCornerRadius;

        protected override BlurMode PillBlurMode => S.TopBarBlurMode;

        protected override double EdgeMargin => S.TopBarMargin;

        protected override bool StretchAcross => true;

        private void ApplyLayout()
        {
            double margin = Math.Max(0, S.TopBarMargin);

            Pill.Height = S.TopBarHeight;
            Pill.CornerRadius = new CornerRadius(PaintedCornerRadius(Math.Max(0, Math.Min(S.TopBarCornerRadius, S.TopBarHeight / 2))));

            // Attached to the screen edge: only a hairline along the bottom. Floating: border all round.
            Pill.BorderThickness = margin > 0 ? new Thickness(1) : new Thickness(0, 0, 0, 1);

            SetReservedHeight(S.TopBarReserveSpace ? S.TopBarHeight + margin : 0);
            SetAutoHide(S.TopBarAutoHide);
        }

        protected override void OnSettingChanged(string name)
        {
            if (name != null && (name.StartsWith("TopBar", StringComparison.Ordinal) || name == nameof(Settings.SmoothCorners)))
            {
                ApplyLayout();
            }

            if (name == nameof(Settings.ShowVolume))
            {
                _pinnedTray?.Refresh();
                _unpinnedTray?.Refresh();
            }
        }

        protected override void OnBarClosing()
        {
            _calendar?.Close();
            _volume?.Close();
            Shell.TasksService.WindowActivated -= TasksService_WindowActivated;
            Shell.TasksService.DesktopActivated -= TasksService_DesktopActivated;
        }

        #endregion

        #region Window title

        private void TasksService_WindowActivated(object sender, WindowEventArgs e)
        {
            ApplicationWindow window = e.Window;
            Dispatcher.BeginInvoke(new Action(() => ShowActiveWindow(OnThisScreen(window) ? window : null)));
        }

        private void TasksService_DesktopActivated(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Focus moved to one of GlassBar's own windows (the app menu, a popup, settings): that is not
                // "no app in front", so keep showing the app the user was in.
                if (Native.ForegroundIsGlassBar())
                {
                    return;
                }

                ShowActiveWindow(null);
            }));
        }

        private void ShowCurrentWindowTitle()
        {
            foreach (object entry in Shell.Tasks.GroupedWindows)
            {
                if (entry is ApplicationWindow window && window.State == ApplicationWindow.WindowState.Active)
                {
                    ShowActiveWindow(OnThisScreen(window) ? window : null);
                    return;
                }
            }
        }

        #region App menu

        private ApplicationWindow _activeWindow;

        /// <summary>Shows the app in front (bold) and its window title; null clears it (desktop in front).</summary>
        private void ShowActiveWindow(ApplicationWindow window)
        {
            if (_activeWindow != null)
            {
                _activeWindow.PropertyChanged -= ActiveWindow_PropertyChanged;
            }

            _activeWindow = window;
            TitleButton.DataContext = window;
            AppNameText.Text = window == null ? "" : AppNameFor(window);
            TitleButton.IsHitTestVisible = window != null;

            if (window != null)
            {
                window.PropertyChanged += ActiveWindow_PropertyChanged;
            }

            UpdateTitleVisibility();
        }

        private void ActiveWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ApplicationWindow.Title))
            {
                Dispatcher.BeginInvoke(new Action(UpdateTitleVisibility));
            }
        }

        /// <summary>"Spotify  Spotify" says nothing twice: hide the window title when it is just the app's name.</summary>
        private void UpdateTitleVisibility()
        {
            string title = _activeWindow?.Title?.Trim() ?? "";
            bool same = title.Length == 0 || string.Equals(title, AppNameText.Text, StringComparison.CurrentCultureIgnoreCase);
            TitleText.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string AppNameFor(ApplicationWindow window)
        {
            string name = App.Instance.DockItemFor(window)?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    name = FileVersionInfo.GetVersionInfo(window.WinFileName).FileDescription;
                }
                catch (Exception)
                {
                    // fall through to the title
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = window.Title ?? "";
            }

            name = name.Trim();
            return name.Length > 40 ? name.Substring(0, 37) + "..." : name;
        }

        /// <summary>Like the app menu on a Mac: new window, hide, hide others, close, quit, show the program file.</summary>
        private void Title_Click(object sender, RoutedEventArgs e)
        {
            ApplicationWindow window = _activeWindow;
            if (window == null)
            {
                return;
            }

            DockItem item = App.Instance.DockItemFor(window);
            string name = AppNameFor(window);
            List<ApplicationWindow> appWindows = item != null && item.Windows.Count > 0 ? item.Windows.ToList() : new List<ApplicationWindow> { window };

            ContextMenu menu = new ContextMenu { PlacementTarget = TitleButton, Placement = PlacementMode.Bottom, VerticalOffset = 4 };

            menu.Items.Add(new MenuItem
            {
                Style = (Style)FindResource("PlainMenuItem"),
                Header = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) }
            });

            if (item != null && item.CanLaunch)
            {
                AddMenuItem(menu, "New window", () => item.Launch());
            }

            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Hide " + name, () =>
            {
                foreach (ApplicationWindow w in appWindows)
                {
                    w.Minimize();
                }
            });
            AddMenuItem(menu, "Hide others", () =>
            {
                foreach (object entry in Shell.Tasks.GroupedWindows)
                {
                    if (entry is ApplicationWindow w && !appWindows.Contains(w))
                    {
                        w.Minimize();
                    }
                }
            });

            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Close window", () => window.Close());
            AddMenuItem(menu, appWindows.Count > 1 ? "Quit " + name + " (close all " + appWindows.Count + " windows)" : "Quit " + name, () =>
            {
                foreach (ApplicationWindow w in appWindows)
                {
                    w.Close();
                }
            });

            string exe = item?.Path;
            if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
            {
                exe = window.WinFileName;
            }

            if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
            {
                menu.Items.Add(new Separator());
                AddMenuItem(menu, "Show program in File Explorer", () =>
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + exe + "\"") { UseShellExecute = true })?.Dispose());
            }

            menu.IsOpen = true;
        }

        private static void AddMenuItem(ContextMenu menu, string header, Action action)
        {
            MenuItem item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        #endregion

        /// <summary>With a bar on each monitor, only the bar on the active window's monitor shows its title.</summary>
        private bool OnThisScreen(ApplicationWindow window)
        {
            try
            {
                return System.Windows.Forms.Screen.FromHandle(window.Handle).DeviceName == ScreenDeviceName;
            }
            catch (Exception)
            {
                return IsOnPrimaryScreen;
            }
        }

        #endregion

        #region System tray

        /// <summary>The Windows volume icon's ID in the tray (ManagedShell's NotificationArea.VOLUME_GUID).</summary>
        private static readonly Guid VolumeIconGuid = new Guid(NotificationArea.VOLUME_GUID);

        private ListCollectionView _pinnedTray;
        private ListCollectionView _unpinnedTray;

        /// <summary>
        /// The same pinned/overflow split as ManagedShell's own tray lists, but leaving out the Windows volume icon
        /// while the top bar shows its own volume control, so there aren't two speakers side by side.
        /// These are this bar's own views, so ManagedShell's shared lists stay untouched.
        /// </summary>
        private void UseOwnTrayLists()
        {
            if (Shell.NotificationArea?.TrayIcons == null)
            {
                return; // keep the XAML bindings to ManagedShell's lists
            }

            _pinnedTray = MakeTrayView(icon => icon.IsPinned);
            _pinnedTray.SortDescriptions.Add(new SortDescription(nameof(TrayIcon.PinOrder), ListSortDirection.Ascending));
            _pinnedTray.IsLiveSorting = true;
            _pinnedTray.LiveSortingProperties.Add(nameof(TrayIcon.PinOrder));
            _unpinnedTray = MakeTrayView(icon => !icon.IsPinned);

            PinnedTray.ItemsSource = _pinnedTray;
            UnpinnedTray.ItemsSource = _unpinnedTray;
        }

        private ListCollectionView MakeTrayView(Predicate<TrayIcon> section)
        {
            ListCollectionView view = new ListCollectionView(Shell.NotificationArea.TrayIcons)
            {
                Filter = item => item is TrayIcon icon && section(icon) && !icon.IsHidden && !IsDuplicateVolume(icon),
                IsLiveFiltering = true
            };
            view.LiveFilteringProperties.Add(nameof(TrayIcon.IsHidden));
            view.LiveFilteringProperties.Add(nameof(TrayIcon.IsPinned));
            return view;
        }

        private static bool IsDuplicateVolume(TrayIcon icon)
        {
            return S.ShowVolume && icon.GUID == VolumeIconGuid;
        }

        /// <summary>Tells ManagedShell where the tray is, so apps position their flyouts next to our bar.</summary>
        private void SetTrayHost()
        {
            if (Hwnd == IntPtr.Zero || Shell.NotificationArea == null)
            {
                return;
            }

            Shell.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = NativeMethods.ABEdge.ABE_TOP,
                rc = ScreenRect
            });
        }

        private void TrayToggle_Click(object sender, RoutedEventArgs e)
        {
            bool show = UnpinnedTray.Visibility != Visibility.Visible;
            UnpinnedTray.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TrayToggle.Content = show ? "" : "";
            TrayToggle.ToolTip = show ? "Hide extra icons" : "Show hidden icons";
        }

        private static TrayIcon IconOf(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as TrayIcon;
        }

        // The click sequence below mirrors what the Windows tray sends, so apps react as they normally would:
        // right/middle buttons report "down" on press; the left button reports down and up together on release.

        private void TrayIcon_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SetTrayHost();

            if (e.ChangedButton != MouseButton.Left)
            {
                IconOf(sender)?.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), Native.DoubleClickTime);
            }
        }

        private void TrayIcon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            TrayIcon icon = IconOf(sender);
            if (icon == null)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), Native.DoubleClickTime);
            }

            icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), Native.DoubleClickTime);
        }

        private void TrayIcon_MouseEnter(object sender, MouseEventArgs e)
        {
            e.Handled = true;
            TrayIcon icon = IconOf(sender);
            if (icon == null || !(sender is FrameworkElement element))
            {
                return;
            }

            // Record where the icon is on screen; apps ask for this to place their popups.
            // (Right/Bottom carry width/height here: that is the layout ManagedShell expects.)
            Point location = element.PointToScreen(new Point(0, 0));
            double scale = Scale;
            icon.Placement = new NativeMethods.Rect
            {
                Top = (int)location.Y,
                Left = (int)location.X,
                Bottom = (int)(element.ActualHeight * scale),
                Right = (int)(element.ActualWidth * scale)
            };

            icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
        }

        private void TrayIcon_MouseLeave(object sender, MouseEventArgs e)
        {
            e.Handled = true;
            IconOf(sender)?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        }

        private void TrayIcon_MouseMove(object sender, MouseEventArgs e)
        {
            e.Handled = true;
            IconOf(sender)?.IconMouseMove(MouseHelper.GetCursorPositionParam());
        }

        #endregion

        #region Buttons and menu

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            ShellHelper.ShowStartMenu();
        }

        private void QuickSettings_Click(object sender, RoutedEventArgs e)
        {
            // Win+A: Quick Settings on Windows 11, Action Center on Windows 10
            ShellHelper.ShowActionCenter();
        }

        private CalendarPopup _calendar;
        private DateTime _calendarClosedAt;

        private void Clock_Click(object sender, RoutedEventArgs e)
        {
            if (S.ClockOpensCalendar)
            {
                // Clicking the clock while the calendar is open closes it. (The click itself already closed it,
                // by taking focus away, so check how recently that happened.)
                if ((DateTime.Now - _calendarClosedAt).TotalMilliseconds < 300)
                {
                    return;
                }

                if (_calendar == null)
                {
                    _calendar = new CalendarPopup();
                    _calendar.IsVisibleChanged += (s, a) =>
                    {
                        if (!_calendar.IsVisible)
                        {
                            _calendarClosedAt = DateTime.Now;
                        }
                    };
                }

                Point topLeft = ClockButton.PointToScreen(new Point(0, 0));
                Point bottomRight = ClockButton.PointToScreen(new Point(ClockButton.ActualWidth, ClockButton.ActualHeight));
                _calendar.ShowBelow(new Rect(topLeft, bottomRight));
                return;
            }

            if (EnvironmentHelper.IsWindows11OrBetter)
            {
                ShellHelper.ShowNotificationCenter(); // Win+N: notifications and calendar
            }
            else
            {
                ShellHelper.ShowActionCenter();
            }
        }

        private void Network_Click(object sender, RoutedEventArgs e)
        {
            // Quick Settings has the Wi-Fi and network pickers.
            ShellHelper.ShowActionCenter();
        }

        private VolumePopup _volume;
        private DateTime _volumeClosedAt;

        /// <summary>Click: the sound popup (volume, per-app volume, output device).</summary>
        private void Volume_Click(object sender, RoutedEventArgs e)
        {
            // A click on the icon while the popup is open closes it (the click already did, by taking focus).
            if ((DateTime.Now - _volumeClosedAt).TotalMilliseconds < 300)
            {
                return;
            }

            if (_volume == null)
            {
                _volume = new VolumePopup();
                _volume.IsVisibleChanged += (s, a) =>
                {
                    if (!_volume.IsVisible)
                    {
                        _volumeClosedAt = DateTime.Now;
                    }
                };
            }

            Point topLeft = VolumeButton.PointToScreen(new Point(0, 0));
            Point bottomRight = VolumeButton.PointToScreen(new Point(VolumeButton.ActualWidth, VolumeButton.ActualHeight));
            _volume.ShowBelow(new Rect(topLeft, bottomRight));
        }

        /// <summary>Middle-click: mute or unmute.</summary>
        private void Volume_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                AudioVolume.ToggleMute();
                SystemStats.Instance.UpdateVolume();
                e.Handled = true;
            }
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // One notch = 2%, like the Windows volume flyout.
            AudioVolume.Change(e.Delta > 0 ? 0.02f : -0.02f);
            SystemStats.Instance.UpdateVolume();
            e.Handled = true;
        }

        private void SettingsMenu_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings();
        }

        private void TaskManagerMenu_Click(object sender, RoutedEventArgs e)
        {
            ShellHelper.StartTaskManager();
        }

        private void ExitMenu_Click(object sender, RoutedEventArgs e)
        {
            ExitApp();
        }

        #endregion
    }
}
