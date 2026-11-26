using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;

namespace GlassBar
{
    /// <summary>
    /// Opens from the top bar's volume icon: the master volume, a volume for each app playing sound, and the
    /// playback device to use. Changes apply live. Closes when you click anywhere else.
    /// </summary>
    internal sealed class VolumePopup : GlassPopup
    {
        private const double PopupWidth = 320;

        private readonly StackPanel _devices;
        private readonly StackPanel _apps;
        private readonly TextBlock _appsHeader;
        private readonly StackPanel _bluetooth;
        private readonly TextBlock _bluetoothHeader;
        private readonly Slider _master;
        private readonly Button _masterMute;
        private readonly TextBlock _masterValue;
        private readonly DispatcherTimer _refresh;

        private List<AudioVolume.AppSession> _sessions = new List<AudioVolume.AppSession>();
        private bool _updating; // true while the code (not the user) moves the sliders

        public VolumePopup()
            : base(closeWhenDeactivated: true)
        {
            Panel.Padding = new Thickness(12, 10, 12, 10);

            _master = MakeSlider();
            _master.ValueChanged += (s, e) =>
            {
                if (!_updating)
                {
                    AudioVolume.Set((float)(_master.Value / 100), _master.Value > 0 ? false : (bool?)null);
                    UpdateMasterLabels();
                    SystemStats.Instance.UpdateVolume();
                }
            };

            _masterMute = MakeIconButton("Mute");
            _masterMute.Click += (s, e) =>
            {
                AudioVolume.ToggleMute();
                ReadMaster();
                SystemStats.Instance.UpdateVolume();
            };

            _masterValue = new TextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };

            _devices = new StackPanel();
            _appsHeader = Header("Apps");
            _apps = new StackPanel();
            _bluetoothHeader = Header("Bluetooth batteries");
            _bluetooth = new StackPanel();

            Button soundSettings = new Button
            {
                Style = BarButtonStyle,
                Content = "Sound settings",
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            soundSettings.Click += (s, e) =>
            {
                HidePopup();
                ShellHelper.StartProcess("ms-settings:sound");
            };

            StackPanel body = new StackPanel { Width = PopupWidth };
            body.Children.Add(Header("Sound"));
            body.Children.Add(Row(_masterMute, _master, _masterValue));
            body.Children.Add(Header("Output"));
            body.Children.Add(_devices);
            body.Children.Add(_appsHeader);
            body.Children.Add(_apps);
            body.Children.Add(_bluetoothHeader);
            body.Children.Add(_bluetooth);
            body.Children.Add(soundSettings);
            Panel.Child = body;

            // While open, follow changes made elsewhere (keyboard volume keys, apps starting or stopping sound).
            _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _refresh.Tick += (s, e) => Refresh(rebuildIfChanged: true);

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    HidePopup();
                }
            };
        }

        private static Style BarButtonStyle => (Style)Application.Current.FindResource("BarButton");

        public void ShowBelow(Rect anchor)
        {
            BuildDevices();
            BuildApps(AudioVolume.GetSessions());
            BuildBluetooth();
            ReadMaster();
            ShowAt(anchor, above: false);
            _refresh.Start();
        }

        public override void HidePopup()
        {
            _refresh.Stop();
            ReleaseSessions();
            base.HidePopup();
        }

        private void ReleaseSessions()
        {
            foreach (AudioVolume.AppSession session in _sessions)
            {
                session.Release();
            }

            _sessions = new List<AudioVolume.AppSession>();
        }

        private void Refresh(bool rebuildIfChanged)
        {
            ReadMaster();

            List<AudioVolume.AppSession> fresh = AudioVolume.GetSessions();
            bool sameApps = fresh.Count == _sessions.Count &&
                            fresh.Select(a => a.ExePath ?? a.Name).SequenceEqual(_sessions.Select(a => a.ExePath ?? a.Name));

            if (!sameApps && rebuildIfChanged)
            {
                BuildApps(fresh);
            }
            else
            {
                // Same apps: just move the sliders to their current values.
                foreach (AudioVolume.AppSession session in fresh)
                {
                    session.Release();
                }
            }
        }

        #region Master volume

        private void ReadMaster()
        {
            if (!AudioVolume.TryGet(out float level, out bool muted))
            {
                _master.IsEnabled = false;
                _masterValue.Text = "";
                return;
            }

            _master.IsEnabled = true;
            _updating = true;
            _master.Value = Math.Round(level * 100);
            _updating = false;
            _masterMute.Content = Glyph(muted ? 0 : level * 100);
            _masterMute.ToolTip = muted ? "Unmute" : "Mute";
            _master.Opacity = muted ? 0.5 : 1;
            UpdateMasterLabels();
        }

        private void UpdateMasterLabels()
        {
            _masterValue.Text = Math.Round(_master.Value).ToString();
        }

        #endregion

        #region Output devices

        private void BuildDevices()
        {
            _devices.Children.Clear();
            foreach (AudioVolume.Device device in AudioVolume.GetDevices())
            {
                AudioVolume.Device target = device;

                TextBlock check = new TextBlock
                {
                    Text = device.IsDefault ? "" : "",
                    FontFamily = IconFont,
                    FontSize = 12,
                    Width = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = S.AccentBrush
                };
                TextBlock name = new TextBlock
                {
                    Text = device.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = device.IsDefault ? FontWeights.SemiBold : FontWeights.Normal
                };

                DockPanel row = new DockPanel();
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);
                row.Children.Add(name);

                Border tile = MakeHoverTile(row, new Thickness(4, 5, 6, 5));
                tile.ToolTip = device.IsDefault ? "Playing through this device" : "Play sound through this device";
                tile.MouseLeftButtonUp += (s, e) =>
                {
                    if (!target.IsDefault && AudioVolume.SetDefaultDevice(target.Id))
                    {
                        BuildDevices();
                        BuildApps(AudioVolume.GetSessions());
                        ReadMaster();
                        SystemStats.Instance.UpdateVolume();
                    }
                };
                _devices.Children.Add(tile);
            }

            if (_devices.Children.Count == 0)
            {
                _devices.Children.Add(new TextBlock { Text = "No playback devices", Opacity = 0.6, Margin = new Thickness(4, 2, 0, 4) });
            }
        }

        #endregion

        #region Apps

        private void BuildApps(List<AudioVolume.AppSession> sessions)
        {
            ReleaseSessions();
            _sessions = sessions;
            _apps.Children.Clear();

            _appsHeader.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (AudioVolume.AppSession session in sessions)
            {
                AudioVolume.AppSession app = session;

                Image icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                if (app.ExePath != null)
                {
                    LoadIcon(app.ExePath, icon);
                }

                Button mute = new Button
                {
                    Style = BarButtonStyle,
                    Padding = new Thickness(3),
                    Content = icon,
                    ToolTip = app.Name + (app.Muted ? " (muted, click to unmute)" : " (click to mute)"),
                    Opacity = app.Muted ? 0.45 : 1
                };
                if (app.IsSystemSounds)
                {
                    mute.Content = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 15, Width = 20, TextAlignment = TextAlignment.Center };
                }

                Slider slider = MakeSlider();
                slider.Value = Math.Round(app.Volume * 100);
                slider.ToolTip = app.Name;
                slider.Opacity = app.Muted ? 0.5 : 1;

                TextBlock value = new TextBlock
                {
                    Width = 34,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.75,
                    Text = slider.Value.ToString()
                };

                slider.ValueChanged += (s, e) =>
                {
                    app.SetVolume((float)(slider.Value / 100));
                    if (app.Muted && slider.Value > 0)
                    {
                        app.SetMute(false);
                        mute.Opacity = 1;
                        slider.Opacity = 1;
                    }

                    value.Text = Math.Round(slider.Value).ToString();
                };

                mute.Click += (s, e) =>
                {
                    app.SetMute(!app.Muted);
                    mute.Opacity = app.Muted ? 0.45 : 1;
                    slider.Opacity = app.Muted ? 0.5 : 1;
                    mute.ToolTip = app.Name + (app.Muted ? " (muted, click to unmute)" : " (click to mute)");
                };

                _apps.Children.Add(Row(mute, slider, value));
            }
        }

        private static void LoadIcon(string path, Image target)
        {
            Task.Factory.StartNew(() => ShellIcons.Load(path, 48), CancellationToken.None, TaskCreationOptions.None, IconHelper.IconScheduler)
                .ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    {
                        target.Source = t.Result;
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        #endregion

        #region Bluetooth batteries

        /// <summary>Headphones, controllers and other Bluetooth devices that report a battery level.</summary>
        private void BuildBluetooth()
        {
            _bluetooth.Children.Clear();
            List<BluetoothBattery.Device> devices = BluetoothBattery.Read();
            _bluetoothHeader.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (BluetoothBattery.Device device in devices)
            {
                // Segoe Fluent battery glyphs: E850 (empty) to E859 (90%), E83F full.
                int step = device.Percent / 10;
                string glyph = step >= 10 ? "\uE83F" : ((char)(0xE850 + step)).ToString();

                TextBlock icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = IconFont,
                    FontSize = 15,
                    Width = 28,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = device.Percent <= 20 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : null
                };
                if (icon.Foreground == null)
                {
                    icon.ClearValue(TextBlock.ForegroundProperty);
                }

                TextBlock percent = new TextBlock
                {
                    Text = device.Percent + "%",
                    Width = 40,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.75
                };
                TextBlock name = new TextBlock
                {
                    Text = device.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                DockPanel row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), ToolTip = "Last level the device reported to Windows" };
                DockPanel.SetDock(icon, Dock.Left);
                DockPanel.SetDock(percent, Dock.Right);
                row.Children.Add(icon);
                row.Children.Add(percent);
                row.Children.Add(name);
                _bluetooth.Children.Add(row);
            }
        }

        #endregion

        #region Building blocks

        private static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");

        private static string Glyph(double percent)
        {
            return percent <= 0 ? "" : percent < 34 ? "" : percent < 67 ? "" : "";
        }

        private static Slider MakeSlider()
        {
            return new Slider
            {
                Style = (Style)Application.Current.FindResource("GlassSlider"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };
        }

        private static Button MakeIconButton(string tip)
        {
            return new Button
            {
                Style = BarButtonStyle,
                FontFamily = IconFont,
                FontSize = 15,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                ToolTip = tip
            };
        }

        private static TextBlock Header(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(4, 8, 0, 4) };
        }

        /// <summary>[button] [slider fills] [value]</summary>
        private static DockPanel Row(FrameworkElement left, Slider slider, FrameworkElement right)
        {
            DockPanel row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(left, Dock.Left);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(left);
            row.Children.Add(right);
            row.Children.Add(slider);
            return row;
        }

        #endregion
    }
}
