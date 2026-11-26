using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace GlassBar
{
    /// <summary>The floating dock at the bottom of the screen.</summary>
    public partial class DockWindow : BarWindow
    {
        /// <summary>Breathing room between the dock and maximized windows above it.</summary>
        private const double TopGap = 6;

        private readonly DockManager _manager;

        private readonly DockHotkeys _hotkeys;

        // Window previews and folder stacks
        private PreviewWindow _preview;
        private StackWindow _stack;
        private readonly DispatcherTimer _previewShowTimer;
        private readonly DispatcherTimer _previewHideTimer;
        private DockItem _hoverItem;
        private FrameworkElement _hoverElement;

        // Icon dragging
        private DockItem _pressItem;
        private Point _pressPoint;
        private bool _dragging;

        public DockWindow(ShellManager shell, DockManager manager, string screenDeviceName)
            : base(shell, AppBarEdge.Bottom, screenDeviceName)
        {
            _manager = manager;

            InitializeComponent();
            DataContext = manager;

            InitBar(Pill);
            ApplyLayout();


            manager.Items.CollectionChanged += (s, e) => UpdateDivider();
            manager.Stacks.CollectionChanged += (s, e) => UpdateDivider();
            UpdateDivider();

            // One keyboard hook for all docks: the main screen's dock owns it.
            _hotkeys = IsOnPrimaryScreen ? new DockHotkeys(ActivateByNumber) : null;

            _previewShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _previewShowTimer.Tick += (s, e) =>
            {
                _previewShowTimer.Stop();
                ShowPreview();
            };

            _previewHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewHideTimer.Tick += (s, e) =>
            {
                if (_preview != null && _preview.IsMouseOver)
                {
                    return; // the mouse moved onto the previews: keep them
                }

                _previewHideTimer.Stop();
                HidePreview();
            };
        }




        protected override void OnBarClosing()
        {
            _hotkeys?.Dispose();
            _previewShowTimer.Stop();
            _previewHideTimer.Stop();
            _preview?.Close();
            _stack?.Close();
        }

        #region Layout

        private static double BottomPadding => S.DockPadding / 2;

        // icon + indicator row + padding above and below + 1px border on each side
        private static double PillHeight => S.DockItemHeight + S.DockPadding + BottomPadding + 2;

        protected override double PillCornerRadius => S.DockCornerRadius;

        protected override double EdgeMargin => S.DockMargin;

        protected override BlurMode PillBlurMode => S.DockBlurMode;

        private void ApplyLayout()
        {
            Pill.Padding = new Thickness(S.DockPadding, S.DockPadding, S.DockPadding, BottomPadding);
            Pill.CornerRadius = new CornerRadius(PaintedCornerRadius(Math.Max(0, Math.Min(S.DockCornerRadius, PillHeight / 2))));

            // Zero means no reservation: apps and the desktop use the whole screen and the dock floats over them.
            SetReservedHeight(S.DockReserveSpace ? PillHeight + S.DockMargin + TopGap : 0);
            SetAutoHide(S.DockAutoHide);
        }

        protected override void OnSettingChanged(string name)
        {
            if (name != null && (name.StartsWith("Dock", StringComparison.Ordinal) || name == nameof(Settings.SmoothCorners)))
            {
                ApplyLayout();
            }
        }

        #endregion

        #region App icons

        private static DockItem ItemOf(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as DockItem;
        }

        private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DockItem item = ItemOf(sender);
            item?.CapturePressState();
            HidePreview();

            _pressItem = item;
            _pressPoint = e.GetPosition(ItemsHost);
            _dragging = false;
        }

        private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            DockItem item = _pressItem;
            _pressItem = null;

            if (item != null && ReferenceEquals(item, ItemOf(sender)))
            {
                if (item.IsRecycleBin)
                {
                    RecycleBin.Open();
                }
                else if (item.IsFolder)
                {
                    ShowStack(item, sender as FrameworkElement);
                }
                else
                {
                    item.Activate();
                }
            }
        }

        #endregion

        #region Drag to reorder

        private void ItemsHost_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pressItem == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point position = e.GetPosition(ItemsHost);

            if (!_dragging)
            {
                if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2)
                {
                    return;
                }

                // Start dragging. Capturing the mouse on the list keeps the drag going (and the icon inside
                // the dock) even when the pointer leaves the dock.
                _dragging = true;
                HidePreview();
                _manager.BeginDrag();
                ItemsHost.CaptureMouse();
                SetDragLook(_pressItem, true);
            }

            int slot = (int)Math.Floor(position.X / Math.Max(1, S.DockItemWidth));
            _manager.DragTo(_pressItem, slot);
            e.Handled = true;
        }

        private void ItemsHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                return; // an ordinary click: the icon's own handler opens the app
            }

            e.Handled = true;
            FinishDrag();
        }

        private void ItemsHost_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                FinishDrag();
            }
        }

        private void FinishDrag()
        {
            DockItem item = _pressItem;
            _dragging = false;
            _pressItem = null;

            if (ItemsHost.IsMouseCaptured)
            {
                ItemsHost.ReleaseMouseCapture();
            }

            if (item != null)
            {
                SetDragLook(item, false);
                _manager.EndDrag(item);
            }
        }

        private void SetDragLook(DockItem item, bool dragging)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
            {
                container.Opacity = dragging ? 0.6 : 1.0;

                // The container's first child is the icon panel (the DataTemplate's Grid).
                if (VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is Panel panel)
                {
                    AnimateIcon(panel, 1);
                }
            }
        }

        private void Item_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle-click opens another window of the app.
            if (e.ChangedButton == MouseButton.Middle)
            {
                ItemOf(sender)?.Launch();
                e.Handled = true;
            }
        }

        private void Item_MouseEnter(object sender, MouseEventArgs e)
        {
            AnimateIcon(sender, Math.Max(1, S.DockHoverZoom));

            _hoverItem = ItemOf(sender);
            _hoverElement = sender as FrameworkElement;
            _previewHideTimer.Stop();

            if (_preview != null && _preview.IsVisible)
            {
                ShowPreview(); // previews already open: follow the mouse to the next icon right away
            }
            else if (!_dragging && S.DockShowPreviews && _hoverItem != null && _hoverItem.Windows.Count > 0)
            {
                _previewShowTimer.Stop();
                _previewShowTimer.Start();
            }

            // Running apps show previews (with each window's title) instead of the name tooltip.
            if (S.DockShowPreviews && _hoverItem != null && _hoverItem.Windows.Count > 0)
            {
                SetIconTooltip(_hoverElement, false);
            }
        }

        private void Item_MouseLeave(object sender, MouseEventArgs e)
        {
            AnimateIcon(sender, 1);

            _previewShowTimer.Stop();
            SetIconTooltip(null, true);
            _hoverItem = null;
            _hoverElement = null;
            if (_preview != null && _preview.IsVisible)
            {
                _previewHideTimer.Start();
            }
        }

        #endregion

        #region Window previews and folder stacks

        /// <summary>
        /// Where a popup for this icon goes: its horizontal span, and the top of the dock (so the popup floats a
        /// fixed distance above the dock, not above the icon). Physical pixels.
        /// </summary>
        private Rect PopupAnchorFor(FrameworkElement element)
        {
            Point topLeft = element.PointToScreen(new Point(0, 0));
            Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            double dockTop = ScreenRect.Top;
            return new Rect(topLeft.X, dockTop, Math.Max(1, bottomRight.X - topLeft.X), Math.Max(1, bottomRight.Y - dockTop));
        }

        /// <summary>The icon's name tooltip would cover the previews, so it is switched off while they show.</summary>
        private FrameworkElement _tooltipOff;

        private void SetIconTooltip(FrameworkElement element, bool enabled)
        {
            if (!enabled && element != null)
            {
                ToolTipService.SetIsEnabled(element, false);
                if (element.ToolTip is ToolTip tip)
                {
                    tip.IsOpen = false;
                }

                _tooltipOff = element;
            }
            else if (enabled && _tooltipOff != null)
            {
                ToolTipService.SetIsEnabled(_tooltipOff, true);
                _tooltipOff = null;
            }
        }

        private void ShowPreview()
        {
            if (_hoverItem == null || _hoverElement == null || _dragging || !S.DockShowPreviews ||
                _hoverItem.Windows.Count == 0 || !IsFullyShown || !_hoverElement.IsLoaded)
            {
                HidePreview();
                return;
            }

            if (_preview == null)
            {
                _preview = new PreviewWindow();
                _preview.MouseEnter += (s, e) => _previewHideTimer.Stop();
                _preview.MouseLeave += (s, e) => _previewHideTimer.Start();
            }

            _preview.ShowFor(_hoverItem, PopupAnchorFor(_hoverElement));
        }

        private void HidePreview()
        {
            _previewShowTimer.Stop();
            _previewHideTimer.Stop();
            _preview?.HidePopup();
        }

        private void ShowStack(DockItem item, FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            if (_stack != null && _stack.IsVisible)
            {
                _stack.HidePopup(); // a second click on the folder closes it
                return;
            }

            _stack ??= new StackWindow();
            _stack.ShowFor(item, PopupAnchorFor(element));
        }

        private void AddStack_Click(object sender, RoutedEventArgs e)
        {
            string folder = null;
            switch ((sender as FrameworkElement)?.Tag as string)
            {
                case "Downloads":
                    folder = Native.DownloadsFolder;
                    break;
                case "Documents":
                    folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    break;
                default:
                    using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = "Choose a folder to add to the dock",
                        UseDescriptionForTitle = true
                    })
                    {
                        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            folder = dialog.SelectedPath;
                        }
                    }

                    break;
            }

            if (!string.IsNullOrEmpty(folder))
            {
                DockManager.PinPath(folder);
            }
        }

        private static void AnimateIcon(object sender, double scaleTo)
        {
            if (!(sender is Panel panel) || panel.Children.Count == 0 || !(panel.Children[0] is Image image))
            {
                return;
            }

            // Transforms declared in a template are frozen and cannot be animated, so create one per icon.
            ScaleTransform scale = image.RenderTransform as ScaleTransform;
            if (scale == null || scale.IsFrozen)
            {
                scale = new ScaleTransform(1, 1);
                image.RenderTransformOrigin = new Point(0.5, 0.75);
                image.RenderTransform = scale;
            }

            // The badge/progress overlay (second child) zooms with the icon.
            if (panel.Children.Count > 1 && panel.Children[1] is FrameworkElement overlay && !ReferenceEquals(overlay.RenderTransform, scale))
            {
                overlay.RenderTransformOrigin = image.RenderTransformOrigin;
                overlay.RenderTransform = scale;
            }

            DoubleAnimation animation = new DoubleAnimation(scaleTo, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private void Item_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            DockItem item = ItemOf(sender);
            ContextMenu menu = (sender as FrameworkElement)?.ContextMenu;
            if (item == null || menu == null)
            {
                e.Handled = true;
                return;
            }

            menu.Items.Clear();
            HidePreview();

            if (item.IsRecycleBin)
            {
                MenuItem openBin = new MenuItem { Header = "Open" };
                openBin.Click += (s, a) => RecycleBin.Open();
                menu.Items.Add(openBin);

                MenuItem empty = new MenuItem { Header = "Empty Recycle Bin", IsEnabled = RecycleBin.HasItems };
                empty.Click += (s, a) =>
                {
                    RecycleBin.Empty(); // Windows asks for confirmation
                    _manager.UpdateRecycleBin();
                };
                menu.Items.Add(empty);

                menu.Items.Add(new Separator());
                MenuItem hide = new MenuItem { Header = "Hide Recycle Bin" };
                hide.Click += (s, a) => S.DockShowRecycleBin = false;
                menu.Items.Add(hide);
                return;
            }

            if (item.IsFolder)
            {
                MenuItem openFolder = new MenuItem { Header = "Open in File Explorer" };
                openFolder.Click += (s, a) => item.Launch();
                menu.Items.Add(openFolder);

                MenuItem remove = new MenuItem { Header = "Remove from Dock" };
                remove.Click += (s, a) => _manager.Unpin(item);
                menu.Items.Add(remove);
                return;
            }

            // Recently opened files, like the Windows taskbar's jump list
            if (S.DockShowRecentFiles && !item.IsFolder)
            {
                List<string> recent = JumpLists.Recent(item, 8);
                if (recent.Count > 0)
                {
                    menu.Items.Add(new MenuItem
                    {
                        Style = (Style)FindResource("PlainMenuItem"),
                        Header = new TextBlock { Text = "Recent", Opacity = 0.6, FontSize = 11, Margin = new Thickness(6, 0, 0, 0) }
                    });

                    foreach (string path in recent)
                    {
                        string target = path;
                        string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                        MenuItem entry = new MenuItem { Header = Shorten(string.IsNullOrEmpty(name) ? path : name), ToolTip = path };
                        entry.Click += (s, a) =>
                        {
                            if (item.CanOpenFiles)
                            {
                                item.OpenFiles(new[] { target }); // open it in this app
                            }
                            else
                            {
                                ShellHelper.StartProcess(target);
                            }
                        };
                        menu.Items.Add(entry);
                    }

                    menu.Items.Add(new Separator());
                }
            }

            // One entry per open window
            foreach (ApplicationWindow window in item.Windows)
            {
                ApplicationWindow target = window;
                MenuItem entry = new MenuItem { Header = Shorten(target.Title) };
                entry.Click += (s, a) => target.BringToFront();
                menu.Items.Add(entry);
            }

            if (item.Windows.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeSnapRow(item, menu));
                menu.Items.Add(new Separator());
            }

            if (item.CanLaunch)
            {
                MenuItem open = new MenuItem { Header = item.IsRunning ? "New window" : "Open" };
                open.Click += (s, a) => item.Launch();
                menu.Items.Add(open);

                MenuItem pin = new MenuItem { Header = item.IsPinned ? "Remove from Dock" : "Keep in Dock" };
                pin.Click += (s, a) =>
                {
                    if (item.IsPinned)
                    {
                        _manager.Unpin(item);
                    }
                    else
                    {
                        _manager.Pin(item);
                    }
                };
                menu.Items.Add(pin);
            }

            if (item.Windows.Count > 0)
            {
                MenuItem close = new MenuItem { Header = item.Windows.Count > 1 ? "Close all windows" : "Close window" };
                close.Click += (s, a) => item.CloseAll();
                menu.Items.Add(close);
            }

            if (menu.Items.Count == 0)
            {
                e.Handled = true;
            }
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "(untitled)";
            }

            return text.Length > 60 ? text.Substring(0, 57) + "..." : text;
        }

        #endregion

        #region Dock background

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            ShellHelper.ShowStartMenu();
        }

        #region Dropping files on icons

        /// <summary>Files over an app open in it; files over the Recycle Bin are deleted (moved to the bin).</summary>
        private static bool AcceptsDrop(DockItem item, DragEventArgs e)
        {
            return item != null && (item.IsRecycleBin || item.CanOpenFiles) && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void Item_DragEnter(object sender, DragEventArgs e)
        {
            if (AcceptsDrop(ItemOf(sender), e))
            {
                AnimateIcon(sender, Math.Max(1.1, S.DockHoverZoom)); // show which icon will take the files
            }
        }

        private void Item_DragLeave(object sender, DragEventArgs e)
        {
            AnimateIcon(sender, 1);
        }

        private void Item_DragOver(object sender, DragEventArgs e)
        {
            DockItem item = ItemOf(sender);
            if (AcceptsDrop(item, e))
            {
                e.Effects = item.IsRecycleBin ? DragDropEffects.Move : DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Item_Drop(object sender, DragEventArgs e)
        {
            DockItem item = ItemOf(sender);
            AnimateIcon(sender, 1);
            if (!AcceptsDrop(item, e) || !(e.Data.GetData(DataFormats.FileDrop) is string[] files))
            {
                return; // not handled here, so the dock pins what was dropped
            }

            e.Handled = true;

            // Let the drag finish first: starting a program or deleting can take a moment or show dialogs.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (item.IsRecycleBin)
                {
                    RecycleBin.Recycle(files);
                    _manager.UpdateRecycleBin();
                }
                else
                {
                    item.OpenFiles(files);
                }
            }));
        }

        #endregion

        #region Snapping windows

        /// <summary>
        /// A row of small layout pictures for the app's menu. Clicking one moves the app's window (the one used
        /// last) into that part of its screen. With several windows open, the last picture puts them side by side.
        /// </summary>
        private FrameworkElement MakeSnapRow(DockItem item, ContextMenu menu)
        {
            StackPanel pictures = new StackPanel { Orientation = Orientation.Horizontal };

            foreach (TileLayout layout in Enum.GetValues(typeof(TileLayout)))
            {
                (double left, double width) = WindowTiling.Span(layout);
                TileLayout chosen = layout;
                pictures.Children.Add(MakeLayoutPicture(new[] { (left, width) }, WindowTiling.Describe(layout), () =>
                {
                    IntPtr hwnd = item.PrimaryWindow?.Handle ?? IntPtr.Zero;
                    WindowTiling.Apply(hwnd, chosen);
                    item.PrimaryWindow?.BringToFront();
                }, menu));
            }

            if (item.Windows.Count > 1)
            {
                int count = Math.Min(item.Windows.Count, 4);
                (double, double)[] columns = Enumerable.Range(0, count).Select(i => ((double)i / count, 1.0 / count)).ToArray();
                pictures.Children.Add(MakeLayoutPicture(columns, "All " + item.Windows.Count + " windows side by side", () =>
                {
                    WindowTiling.SideBySide(item.Windows.Select(w => w.Handle).ToList());
                    foreach (ApplicationWindow w in item.Windows)
                    {
                        w.BringToFront();
                    }
                }, menu));
            }

            TextBlock label = new TextBlock
            {
                Text = "Snap window",
                Opacity = 0.6,
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 4)
            };

            StackPanel content = new StackPanel();
            content.Children.Add(label);
            content.Children.Add(pictures);

            return new MenuItem
            {
                Style = (Style)FindResource("PlainMenuItem"),
                Header = content
            };
        }

        /// <summary>A tiny screen with the target region(s) filled in.</summary>
        private FrameworkElement MakeLayoutPicture((double Left, double Width)[] regions, string tip, Action apply, ContextMenu menu)
        {
            const double width = 34, height = 20, inset = 2;

            Canvas screen = new Canvas { Width = width, Height = height };
            foreach ((double left, double w) in regions)
            {
                Border region = new Border
                {
                    Width = Math.Max(2, (width - 2 * inset) * w - (regions.Length > 1 ? 1 : 0)),
                    Height = height - 2 * inset,
                    CornerRadius = new CornerRadius(2),
                    Background = S.AccentBrush
                };
                Canvas.SetLeft(region, inset + (width - 2 * inset) * left);
                Canvas.SetTop(region, inset);
                screen.Children.Add(region);
            }

            Border frame = new Border
            {
                Child = screen,
                BorderThickness = new Thickness(1),
                BorderBrush = S.TextDimBrush,
                CornerRadius = new CornerRadius(4)
            };

            Border tile = new Border
            {
                Child = frame,
                Padding = new Thickness(4),
                Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                ToolTip = tip
            };
            tile.MouseEnter += (s, e) => tile.Background = S.HoverBrush;
            tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
            tile.MouseLeftButtonUp += (s, e) =>
            {
                menu.IsOpen = false;
                apply();
                e.Handled = true;
            };
            return tile;
        }

        #endregion

        #region Divider and keyboard shortcuts

        private void UpdateDivider()
        {
            Divider.Visibility = _manager.Items.Count > 0 && _manager.Stacks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Win+number: the nth app in the dock, counted like the Windows taskbar (Win+0 is the 10th).</summary>
        private void ActivateByNumber(int number, bool newWindow)
        {
            if (!S.DockWinNumberKeys)
            {
                return;
            }

            int index = number == 0 ? 9 : number - 1;
            if (index >= _manager.Items.Count)
            {
                return;
            }

            DockItem item = _manager.Items[index];
            if (newWindow)
            {
                item.Launch();
                return;
            }

            // Same as clicking it: open, bring forward, cycle through its windows, or minimize.
            item.CapturePressState();
            item.Activate();
        }

        #endregion

        private void Pill_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }

        private void Pill_Drop(object sender, DragEventArgs e)
        {
            // Dropping a program or shortcut on the dock pins it.
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (string file in files)
                {
                    DockManager.PinPath(file);
                }
            }

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
