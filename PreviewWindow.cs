using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;

namespace GlassBar
{
    /// <summary>
    /// Live thumbnails of an app's open windows, shown above its dock icon. The thumbnails are drawn by
    /// Windows (DWM) straight onto this window, so they cost GlassBar almost no memory and stay live.
    /// Click a thumbnail to switch to that window; the × or a middle-click closes it.
    /// </summary>
    internal sealed class PreviewWindow : GlassPopup
    {
        private const double ThumbHeight = 120;
        private const double MinThumbWidth = 110;
        private const double MaxThumbWidth = 240;
        private const int MaxWindows = 8;

        private readonly StackPanel _cards;
        private readonly List<Thumb> _thumbs = new List<Thumb>();
        private bool _rebuildQueued;

        private sealed class Thumb
        {
            public IntPtr Handle;          // DWM thumbnail handle
            public FrameworkElement Host;  // the area of the card it is drawn into
            public Size Source;            // size of the source window, in pixels
        }

        public DockItem Item { get; private set; }

        public PreviewWindow()
            : base(closeWhenDeactivated: false)
        {
            Panel.Padding = new Thickness(6);
            _cards = new StackPanel { Orientation = Orientation.Horizontal };
            Panel.Child = _cards;
        }

        public void ShowFor(DockItem item, Rect anchor)
        {
            if (!ReferenceEquals(Item, item))
            {
                if (Item != null)
                {
                    Item.WindowsChanged -= Item_WindowsChanged;
                }

                Item = item;
                Item.WindowsChanged += Item_WindowsChanged;
            }

            if (!IsVisible)
            {
                // The thumbnails need this window's handle, so make sure it exists before building.
                Opacity = 0;
                Show();
            }

            Build();
            ShowAt(anchor, above: true);
        }

        public override void HidePopup()
        {
            ClearThumbs();
            if (Item != null)
            {
                Item.WindowsChanged -= Item_WindowsChanged;
                Item = null;
            }

            base.HidePopup();
        }

        private void Item_WindowsChanged(object sender, EventArgs e)
        {
            if (_rebuildQueued || !IsVisible)
            {
                return;
            }

            _rebuildQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _rebuildQueued = false;
                if (Item == null || !IsVisible)
                {
                    return;
                }

                if (Item.Windows.Count == 0)
                {
                    HidePopup();
                }
                else
                {
                    Build();
                }
            }));
        }

        #region Cards

        private void Build()
        {
            ClearThumbs();
            _cards.Children.Clear();

            if (Item == null)
            {
                return;
            }

            foreach (ApplicationWindow window in Item.Windows.Take(MaxWindows))
            {
                _cards.Children.Add(MakeCard(window));
            }

            // Thumbnails are placed once the cards have their final positions.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PlaceThumbs));
        }

        private FrameworkElement MakeCard(ApplicationWindow window)
        {
            // Register the thumbnail first: its source size decides the card's width.
            Thumb thumb = null;
            double width = ThumbHeight * 16 / 10;

            if (Hwnd != IntPtr.Zero && !IsIconic(window.Handle) &&
                DwmRegisterThumbnail(Hwnd, window.Handle, out IntPtr handle) == 0)
            {
                DwmQueryThumbnailSourceSize(handle, out PSIZE size);
                if (size.cx > 0 && size.cy > 0)
                {
                    width = Math.Max(MinThumbWidth, Math.Min(MaxThumbWidth, ThumbHeight * size.cx / size.cy));
                    thumb = new Thumb { Handle = handle, Source = new Size(size.cx, size.cy) };
                }
                else
                {
                    DwmUnregisterThumbnail(handle);
                }
            }

            // Title row: icon, title, close button
            Image icon = new Image { Source = window.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
            TextBlock title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(window.Title) ? Item?.Name : window.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button close = new Button
            {
                Style = (Style)Application.Current.FindResource("BarButton"),
                Content = "",
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 10,
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Close window",
                Visibility = Visibility.Hidden
            };
            close.Click += (s, e) =>
            {
                window.Close();
                e.Handled = true;
            };

            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Width = width };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(icon);
            header.Children.Add(close);
            header.Children.Add(title);

            // Thumbnail area. A faded app icon sits underneath: it shows for minimized windows, which have no live image.
            Grid host = new Grid { Width = width, Height = ThumbHeight };
            host.Children.Add(new Image
            {
                Source = Item?.Icon ?? window.Icon,
                Width = 48,
                Height = 48,
                Opacity = thumb == null ? 0.8 : 0.25
            });

            if (thumb != null)
            {
                thumb.Host = host;
                _thumbs.Add(thumb);
            }

            StackPanel body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(host);

            Border card = MakeHoverTile(body, new Thickness(8));
            card.ToolTip = window.Title;
            card.MouseEnter += (s, e) => close.Visibility = Visibility.Visible;
            card.MouseLeave += (s, e) => close.Visibility = Visibility.Hidden;
            card.MouseUp += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    window.BringToFront();
                    HidePopup();
                }
                else if (e.ChangedButton == MouseButton.Middle)
                {
                    window.Close();
                }

                e.Handled = true;
            };

            return card;
        }

        #endregion

        #region DWM thumbnails

        protected override void OnPositioned()
        {
            PlaceThumbs();
        }

        /// <summary>Points each thumbnail at its card, keeping the source window's shape.</summary>
        private void PlaceThumbs()
        {
            if (Hwnd == IntPtr.Zero || !IsVisible)
            {
                return;
            }

            double scale = Scale;
            foreach (Thumb thumb in _thumbs)
            {
                if (thumb.Host == null || !thumb.Host.IsLoaded)
                {
                    continue;
                }

                Rect box;
                try
                {
                    box = thumb.Host.TransformToAncestor(this).TransformBounds(new Rect(0, 0, thumb.Host.ActualWidth, thumb.Host.ActualHeight));
                }
                catch (InvalidOperationException)
                {
                    continue; // not in the visual tree any more
                }

                // Fit the source inside the box, centered.
                double fit = Math.Min(box.Width / thumb.Source.Width, box.Height / thumb.Source.Height);
                double w = thumb.Source.Width * fit;
                double h = thumb.Source.Height * fit;
                double x = box.Left + (box.Width - w) / 2;
                double y = box.Top + (box.Height - h) / 2;

                DWM_THUMBNAIL_PROPERTIES props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECT
                    {
                        Left = (int)Math.Round(x * scale),
                        Top = (int)Math.Round(y * scale),
                        Right = (int)Math.Round((x + w) * scale),
                        Bottom = (int)Math.Round((y + h) * scale)
                    },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false
                };
                DwmUpdateThumbnailProperties(thumb.Handle, ref props);
            }
        }

        private void ClearThumbs()
        {
            foreach (Thumb thumb in _thumbs)
            {
                DwmUnregisterThumbnail(thumb.Handle);
            }

            _thumbs.Clear();
        }

        private const int DWM_TNP_RECTDESTINATION = 0x1;
        private const int DWM_TNP_OPACITY = 0x4;
        private const int DWM_TNP_VISIBLE = 0x8;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PSIZE
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out PSIZE size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        #endregion
    }
}
