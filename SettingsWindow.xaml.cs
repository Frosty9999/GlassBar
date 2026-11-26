using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GlassBar
{
    /// <summary>
    /// The options window. Every control is bound two-way to <see cref="Settings"/>, and the bars listen to
    /// the same object, so changes show up while you drag a slider. To add an option: add the property to
    /// Settings.cs, then one Add... line to the matching Build... method below.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static Settings S => Settings.Instance;

        public SettingsWindow()
        {
            InitializeComponent();

            BuildAppearance(AppearancePanel);
            BuildDock(DockPanelHost);
            BuildTopBar(TopBarPanel);
            BuildGeneral(GeneralPanel);

            PinnedList.ItemsSource = S.PinnedApps;

            SourceInitialized += (s, e) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle, 0x1C, 0x1C, 0x1E);
        }

        #region Tabs

        private void BuildAppearance(Panel p)
        {
            AddHeader(p, "Theme presets");
            AddNote(p, "Sets the glass, text and accent colors on both bars. Fine-tune each bar on its own tab.", false);
            WrapPanel presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (string name in new[] { "Dark", "Light", "Midnight", "Frost", "Clear" })
            {
                string preset = name;
                Button button = new Button { Content = preset, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 8) };
                button.Click += (s, e) => S.ApplyPreset(preset);
                presets.Children.Add(button);
            }
            p.Children.Add(presets);

            AddHeader(p, "Text and accents (both bars)");
            AddColor(p, "Text color", nameof(Settings.TextColor));
            AddColor(p, "Accent color", nameof(Settings.AccentColor));
            AddText(p, "Font", nameof(Settings.FontFamilyName));
            AddNote(p, "Colors are hex values such as #1C1C1E. Press Enter to apply.");
        }

        private void BuildDock(Panel p)
        {
            AddHeader(p, "Dock");
            AddCheck(p, "Show the dock", nameof(Settings.DockEnabled));
            AddCheck(p, "Show a dock on every monitor (off: main monitor only)", nameof(Settings.DockOnAllScreens));
            AddCheck(p, "Keep maximized windows above the dock (off: apps use the full screen and the dock floats over them)", nameof(Settings.DockReserveSpace));
            AddCheck(p, "Don't let windows be dropped behind the dock (a window moved or resized onto it is nudged up)", nameof(Settings.DockKeepWindowsClear));
            AddCheck(p, "Hide automatically (slides off-screen until the mouse touches the bottom edge)", nameof(Settings.DockAutoHide));
            AddCheck(p, "Show the Start button", nameof(Settings.DockShowStartButton));
            AddCheck(p, "Show a dot under running apps", nameof(Settings.DockShowIndicators));
            AddCheck(p, "Show window previews when the mouse rests on a running app", nameof(Settings.DockShowPreviews));
            AddCheck(p, "Show app badges (unread counts and other status icons apps set)", nameof(Settings.DockShowBadges));
            AddCheck(p, "Show download and copy progress under app icons", nameof(Settings.DockShowProgress));
            AddCheck(p, "Show the Recycle Bin at the end of the dock (drop files on it to delete them)", nameof(Settings.DockShowRecycleBin));
            AddCheck(p, "List an app's recently opened files when you right-click it", nameof(Settings.DockShowRecentFiles));
            AddCheck(p, "Win+1 to Win+9 open or switch to the dock's apps (Shift adds a new window)", nameof(Settings.DockWinNumberKeys));
            AddNote(p, "To add a folder stack (Downloads, Documents...), drag a folder onto the dock or right-click the dock. Stacks sit after the divider.");

            AddGlass(p, "Dock");

            AddHeader(p, "Size and shape");
            AddSlider(p, "Icon size", nameof(Settings.DockIconSize), 28, 72, 1, "{0:0}");
            AddSlider(p, "Space between icons", nameof(Settings.DockSpacing), 0, 24, 1, "{0:0}");
            AddSlider(p, "Padding inside the dock", nameof(Settings.DockPadding), 4, 20, 1, "{0:0}");
            AddSlider(p, "Distance from screen edge", nameof(Settings.DockMargin), 0, 40, 1, "{0:0}");
            AddSlider(p, "Corner roundness", nameof(Settings.DockCornerRadius), 0, 40, 1, "{0:0}");
            AddSlider(p, "Hover zoom", nameof(Settings.DockHoverZoom), 1, 1.25, 0.01, "{0:0.00}x");
        }

        private void BuildTopBar(Panel p)
        {
            AddHeader(p, "Top bar");
            AddCheck(p, "Show the top bar", nameof(Settings.TopBarEnabled));
            AddCheck(p, "Show a top bar on every monitor (each shows the title only when the active window is on it)", nameof(Settings.TopBarOnAllScreens));
            AddCheck(p, "Keep maximized windows below the top bar", nameof(Settings.TopBarReserveSpace));
            AddCheck(p, "Don't let windows be dropped behind the top bar (a window moved or resized under it is nudged down)", nameof(Settings.TopBarKeepWindowsClear));
            AddCheck(p, "Hide automatically (slides off-screen until the mouse touches the top edge)", nameof(Settings.TopBarAutoHide));

            AddGlass(p, "TopBar");
            Button match = new Button { Content = "Match the dock's glass", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            match.Click += (s, e) => S.CopyDockGlassToTopBar();
            p.Children.Add(match);

            AddHeader(p, "Size and shape");
            AddSlider(p, "Height", nameof(Settings.TopBarHeight), 22, 48, 1, "{0:0}");
            AddSlider(p, "Float (distance from screen edges)", nameof(Settings.TopBarMargin), 0, 24, 1, "{0:0}");
            AddSlider(p, "Corner roundness", nameof(Settings.TopBarCornerRadius), 0, 24, 1, "{0:0}");
            AddSlider(p, "Text size", nameof(Settings.TopBarFontSize), 10, 18, 0.5, "{0:0.0}");
            AddNote(p, "Tip: set Float to about 6 and Corner roundness to about 10 for a floating, pill-shaped bar.");

            AddHeader(p, "What it shows");
            AddCheck(p, "Start button", nameof(Settings.ShowStartInTopBar));
            AddCheck(p, "Title of the window in front", nameof(Settings.ShowWindowTitle));
            AddCheck(p, "System tray icons", nameof(Settings.ShowTray));
            AddCheck(p, "Quick settings button (volume, network)", nameof(Settings.ShowQuickSettings));
            AddCheck(p, "CPU usage", nameof(Settings.ShowCpu));
            AddCheck(p, "Memory usage", nameof(Settings.ShowRam));
            AddCheck(p, "GPU usage (the busiest part of the graphics card, as in Task Manager)", nameof(Settings.ShowGpu));
            AddCheck(p, "Battery (laptops only)", nameof(Settings.ShowBattery));
            AddCheck(p, "Network speed (download and upload)", nameof(Settings.ShowNetSpeed));
            AddCheck(p, "Wi-Fi (signal and network name; click for network settings)", nameof(Settings.ShowWifi));
            AddCheck(p, "Volume (scroll over it to change, click for app volumes and output device, middle-click to mute)", nameof(Settings.ShowVolume));
            AddCheck(p, "Clock", nameof(Settings.ShowClock));
            AddCheck(p, "Clicking the clock opens a calendar (off: Windows' notification center)", nameof(Settings.ClockOpensCalendar));
            AddText(p, "Clock format", nameof(Settings.ClockFormat));
            AddNote(p, "Clock format examples:  HH:mm   |   h:mm tt   |   ddd d MMM  HH:mm:ss   |   dddd, MMMM d");
        }

        private void BuildGeneral(Panel p)
        {
            AddHeader(p, "Windows");
            AddCheck(p, "Hide the Windows taskbar while GlassBar is running", nameof(Settings.HideWindowsTaskbar));
            AddCheck(p, "Start GlassBar when I sign in", nameof(Settings.RunAtStartup));

            AddHeader(p, "Performance");
            AddCheck(p, "Draw with the graphics card (slightly smoother animations, but uses a lot more memory)", nameof(Settings.UseGpuRendering));
            AddNote(p, "Takes effect the next time GlassBar starts. The glass blur itself is drawn by Windows either way.");

            AddHeader(p, "Hot corners");
            AddNote(p, "Rest the mouse in a corner of any screen for a moment to run its action. Ignored while a mouse button is held and during full-screen games and videos.", false);
            Array actions = Enum.GetValues(typeof(HotCornerAction));
            AddChoice(p, "Top left", nameof(Settings.HotCornerTopLeft), actions);
            AddChoice(p, "Top right", nameof(Settings.HotCornerTopRight), actions);
            AddChoice(p, "Bottom left", nameof(Settings.HotCornerBottomLeft), actions);
            AddChoice(p, "Bottom right", nameof(Settings.HotCornerBottomRight), actions);

            AddHeader(p, "Backup");
            AddNote(p, "Save all settings and dock apps to a file, or load them back (also how to move GlassBar to another PC).", false);
            AddButtons(p, ("Export settings...", ExportSettings), ("Import settings...", ImportSettings));

            AddHeader(p, "Corners");
            AddCheck(p, "Smooth corners (Windows 11 rounds the bars itself; the roundness sliders then only choose square, small or normal)", nameof(Settings.SmoothCorners));
            AddNote(p, "Right-click the dock or the top bar to reopen this window or to quit. Quitting brings the Windows taskbar back.");
            AddNote(p, "Settings file: " + Settings.FilePath);
        }

        /// <summary>The glass controls. Both bars have the same set; prefix is "Dock" or "TopBar".</summary>
        private static void AddGlass(Panel p, string prefix)
        {
            AddHeader(p, "Glass");
            AddChoice(p, "Background effect", prefix + "BlurMode", Enum.GetValues(typeof(BlurMode)));
            AddColor(p, "Tint color", prefix + "TintColor");
            AddSlider(p, "Tint strength", prefix + "TintOpacity", 0, 1, 0.01, "{0:0%}");
            AddColor(p, "Border color", prefix + "BorderColor");
            AddSlider(p, "Border strength", prefix + "BorderOpacity", 0, 1, 0.01, "{0:0%}");
            AddNote(p, "Acrylic is the frosted-glass blur, Blur is a lighter blur, None shows only the tint. Lower the tint strength to see more of what's behind.");
        }

        #endregion

        #region Row builders

        private const double LabelWidth = 210;

        private static void AddHeader(Panel p, string text)
        {
            p.Children.Add(new TextBlock
            {
                Tag = HeaderTag,
                Text = text,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, p.Children.Count == 0 ? 0 : 20, 0, 8)
            });
        }

        private static readonly Brush NoteBrush = MakeFrozen(Color.FromRgb(0x8E, 0x8E, 0x93));

        private static Brush MakeFrozen(Color c)
        {
            SolidColorBrush brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        private static void AddNote(Panel p, string text, bool spaced = true)
        {
            p.Children.Add(new TextBlock
            {
                Tag = NoteTag,
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = NoteBrush,
                Margin = new Thickness(0, spaced ? 8 : 0, 0, 0)
            });
        }

        private static Binding TwoWay(string property)
        {
            return new Binding(property) { Source = S, Mode = BindingMode.TwoWay };
        }

        private static Grid NewRow(string label)
        {
            Grid row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            TextBlock text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 0);
            row.Children.Add(text);
            return row;
        }

        private static void AddCheck(Panel p, string label, string property)
        {
            CheckBox box = new CheckBox
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 5, 0, 5)
            };
            box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, TwoWay(property));
            p.Children.Add(box);
        }

        private static void AddSlider(Panel p, string label, string property, double min, double max, double step, string format)
        {
            Grid row = NewRow(label);

            Slider slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                SmallChange = step,
                LargeChange = step * 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, TwoWay(property));
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);

            TextBlock value = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            value.SetBinding(TextBlock.TextProperty, new Binding(property) { Source = S, Mode = BindingMode.OneWay, StringFormat = format });
            Grid.SetColumn(value, 2);
            row.Children.Add(value);

            p.Children.Add(row);
        }

        private static void AddChoice(Panel p, string label, string property, Array values)
        {
            Grid row = NewRow(label);

            ComboBox combo = new ComboBox { ItemsSource = values, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
            FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding { Converter = SpacedNameConverter.Instance });
            combo.ItemTemplate = new DataTemplate { VisualTree = text };
            combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, TwoWay(property));
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            p.Children.Add(row);
        }

        private static TextBox AddText(Panel p, string label, string property)
        {
            Grid row = NewRow(label);

            TextBox box = new TextBox { Padding = new Thickness(4, 3, 4, 3), VerticalAlignment = VerticalAlignment.Center };
            box.SetBinding(TextBox.TextProperty, TwoWay(property)); // applies when focus leaves the box...
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) // ...or on Enter
                {
                    box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                }
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            p.Children.Add(row);
            return box;
        }

        private static void AddColor(Panel p, string label, string property)
        {
            TextBox box = AddText(p, label, property);
            Grid row = (Grid)box.Parent;

            // Preview swatch. An unfinished hex value simply leaves the swatch empty.
            Border swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                BorderBrush = MakeFrozen(Color.FromRgb(0x5A, 0x5A, 0x5E)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            swatch.SetBinding(Border.BackgroundProperty, new Binding(property) { Source = S, Mode = BindingMode.OneWay });
            Grid.SetColumn(swatch, 2);
            row.Children.Add(swatch);
        }

        private static void AddButtons(Panel p, params (string Label, Action OnClick)[] buttons)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            foreach ((string label, Action onClick) in buttons)
            {
                Button button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
                button.Click += (s, e) => onClick();
                row.Children.Add(button);
            }

            p.Children.Add(row);
        }

        #endregion

        #region Search

        private const string HeaderTag = "header";
        private const string NoteTag = "note";

        /// <summary>
        /// Shows only the options whose text contains what was typed (a matching section title shows its whole
        /// section), hides tabs with nothing left, and moves to the first tab that has a match.
        /// </summary>
        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            TabItem firstMatch = null;
            foreach ((TabItem tab, Panel panel) in new (TabItem, Panel)[]
            {
                (TabAppearance, AppearancePanel), (TabDock, DockPanelHost), (TabTopBar, TopBarPanel), (TabGeneral, GeneralPanel)
            })
            {
                bool any = FilterPanel(panel, query);
                tab.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                if (any && firstMatch == null)
                {
                    firstMatch = tab;
                }
            }

            bool pinsMatch = query.Length == 0 || Contains("dock apps pinned pin keep in dock order", query);
            TabDockApps.Visibility = pinsMatch ? Visibility.Visible : Visibility.Collapsed;
            if (pinsMatch && firstMatch == null)
            {
                firstMatch = TabDockApps;
            }

            if (query.Length > 0 && firstMatch != null && (Tabs.SelectedItem as TabItem)?.Visibility != Visibility.Visible)
            {
                Tabs.SelectedItem = firstMatch;
            }
        }

        /// <summary>Returns true if anything on the panel matches (always true for an empty search).</summary>
        private static bool FilterPanel(Panel panel, string query)
        {
            bool any = false;
            UIElement header = null;
            bool headerMatches = false;
            bool sectionHasMatch = false;

            void CloseSection()
            {
                if (header != null)
                {
                    header.Visibility = query.Length == 0 || headerMatches || sectionHasMatch ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is TextBlock title && HeaderTag.Equals(title.Tag))
                {
                    CloseSection();
                    header = child;
                    headerMatches = query.Length > 0 && Contains(title.Text, query);
                    sectionHasMatch = false;
                    any |= headerMatches;
                    continue;
                }

                string text = SearchTextOf(child);
                bool isNote = child is TextBlock note && NoteTag.Equals(note.Tag);
                bool match = query.Length == 0 || headerMatches || (!isNote && text != null && Contains(text, query));

                child.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match && query.Length > 0)
                {
                    sectionHasMatch = true;
                    any = true;
                }
            }

            CloseSection();
            return any || query.Length == 0;
        }

        /// <summary>The label of an option row: a check box's text, or the text in front of a slider/box/list.</summary>
        private static string SearchTextOf(UIElement element)
        {
            switch (element)
            {
                case CheckBox box:
                    return (box.Content as TextBlock)?.Text ?? box.Content as string;
                case Grid row:
                    foreach (UIElement cell in row.Children)
                    {
                        if (cell is TextBlock label)
                        {
                            return label.Text;
                        }
                    }

                    return null;
                case TextBlock block:
                    return block.Text;
                default:
                    return null;
            }
        }

        private static bool Contains(string text, string query)
        {
            return text != null && text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        #endregion

        #region Backup

        private void ExportSettings()
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "Export GlassBar settings",
                FileName = "GlassBar settings " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json",
                Filter = "GlassBar settings (*.json)|*.json"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                S.ExportTo(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the settings:\n" + ex.Message, "GlassBar");
            }
        }

        private void ImportSettings()
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Import GlassBar settings",
                Filter = "GlassBar settings (*.json)|*.json"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            if (MessageBox.Show(this, "Replace all of GlassBar's settings and dock apps with the ones in this file?",
                    "GlassBar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                S.ImportFrom(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "That file could not be loaded as GlassBar settings:\n" + ex.Message, "GlassBar");
            }
        }

        #endregion

        #region Dock apps tab

        private void AddApp_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Choose an app to keep in the dock",
                Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
                DereferenceLinks = true
            };

            if (dialog.ShowDialog(this) == true && !DockManager.PinPath(dialog.FileName))
            {
                MessageBox.Show(this, "That item is not a program GlassBar can pin.", "GlassBar");
            }
        }

        private void RemoveApp_Click(object sender, RoutedEventArgs e)
        {
            if (PinnedList.SelectedItem is PinnedApp app)
            {
                S.PinnedApps.Remove(app);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private void MoveSelected(int offset)
        {
            int index = PinnedList.SelectedIndex;
            int target = index + offset;
            if (index < 0 || target < 0 || target >= S.PinnedApps.Count)
            {
                return;
            }

            S.PinnedApps.Move(index, target);
            PinnedList.SelectedIndex = target;
        }

        #endregion

        #region Footer buttons

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "Reset all appearance and layout options to their defaults? Your dock apps are kept.",
                "GlassBar", MessageBoxButton.OKCancel);

            if (answer == MessageBoxResult.OK)
            {
                S.ResetToDefaults();
            }
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            App.Instance.ExitGracefully();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion
    }

    /// <summary>Shows enum values as words: "ShowDesktop" becomes "Show desktop".</summary>
    internal sealed class SpacedNameConverter : IValueConverter
    {
        public static readonly SpacedNameConverter Instance = new SpacedNameConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = value?.ToString() ?? "";
            string spaced = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
            return spaced.Length > 1 ? spaced.Substring(0, 1) + spaced.Substring(1).ToLowerInvariant() : spaced;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
