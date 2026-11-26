using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    /// <summary>An app the user keeps in the dock.</summary>
    public class PinnedApp
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string AppUserModelId { get; set; } = "";

        [JsonIgnore]
        public string Key => !string.IsNullOrEmpty(AppUserModelId) ? "appx:" + AppUserModelId : AppPaths.Canonical(Path);
    }

    /// <summary>
    /// Every user-facing option. Saved as JSON in %AppData%\GlassBar\settings.json and applied live:
    /// the bars bind to this object, so changing a property updates them immediately.
    /// </summary>
    public class Settings : INotifyPropertyChanged
    {
        #region Singleton, load and save

        private static readonly string Folder =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassBar");

        public static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static Settings _instance;
        public static Settings Instance => _instance ??= Load();

        private DispatcherTimer _saveTimer;

        private static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Settings loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                    if (loaded != null)
                    {
                        return loaded;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable settings file: fall back to defaults rather than failing to start.
            }

            Settings fresh = new Settings();
            fresh.AddDefaultPins();
            return fresh;
        }

        private void AddDefaultPins()
        {
            string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (File.Exists(explorer))
            {
                PinnedApps.Add(new PinnedApp { Name = "File Explorer", Path = explorer });
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception)
            {
                // Saving is best-effort; the bars keep running with the in-memory values.
            }
        }

        private void QueueSave()
        {
            // Only the live instance saves (not objects being deserialized or the defaults template).
            if (!ReferenceEquals(this, _instance))
            {
                return;
            }

            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    Save();
                };
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Copies the settings file somewhere else (a backup, or to move GlassBar to another PC).</summary>
        public void ExportTo(string path)
        {
            Save();
            File.Copy(FilePath, path, true);
        }

        /// <summary>
        /// Loads every option and the pinned apps from a settings file made by <see cref="ExportTo"/>. Applies live.
        /// Throws if the file is not a GlassBar settings file.
        /// </summary>
        public void ImportFrom(string path)
        {
            Settings loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions)
                              ?? throw new InvalidDataException("The file is empty.");

            foreach (PropertyInfo p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || !p.CanRead || p.Name == nameof(PinnedApps) || p.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                {
                    continue;
                }

                p.SetValue(this, p.GetValue(loaded));
            }

            PinnedApps.Clear();
            foreach (PinnedApp pin in loaded.PinnedApps)
            {
                PinnedApps.Add(pin);
            }

            Save();
        }

        /// <summary>Restores every option except the pinned apps.</summary>
        public void ResetToDefaults()
        {
            Settings defaults = new Settings();
            foreach (PropertyInfo p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || !p.CanRead || p.Name == nameof(PinnedApps) || p.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                {
                    continue;
                }

                p.SetValue(this, p.GetValue(defaults));
            }
        }

        public void ApplyPreset(string name)
        {
            switch (name)
            {
                case "Light":
                    SetGlass("#F5F5F7", 0.55, "#FFFFFF", 0.55);
                    TextColor = "#1C1C1E"; AccentColor = "#007AFF";
                    break;
                case "Midnight":
                    SetGlass("#0B1020", 0.6, "#7AA2FF", 0.25);
                    TextColor = "#E6ECFF"; AccentColor = "#7AA2FF";
                    break;
                case "Frost":
                    SetGlass("#DDF3FF", 0.28, "#FFFFFF", 0.6);
                    TextColor = "#FFFFFF"; AccentColor = "#5AC8FA";
                    break;
                case "Clear":
                    SetGlass("#FFFFFF", 0.08, "#FFFFFF", 0.35);
                    TextColor = "#FFFFFF"; AccentColor = "#FFFFFF";
                    break;
                default: // "Dark"
                    SetGlass("#1C1C1E", 0.55, "#FFFFFF", 0.18);
                    TextColor = "#FFFFFF"; AccentColor = "#0A84FF";
                    break;
            }
        }

        private void SetGlass(string tint, double tintOpacity, string border, double borderOpacity)
        {
            DockTintColor = tint; DockTintOpacity = tintOpacity; DockBorderColor = border; DockBorderOpacity = borderOpacity;
            TopBarTintColor = tint; TopBarTintOpacity = tintOpacity; TopBarBorderColor = border; TopBarBorderOpacity = borderOpacity;
        }

        /// <summary>Gives the top bar the same glass as the dock.</summary>
        public void CopyDockGlassToTopBar()
        {
            TopBarBlurMode = DockBlurMode;
            TopBarTintColor = DockTintColor;
            TopBarTintOpacity = DockTintOpacity;
            TopBarBorderColor = DockBorderColor;
            TopBarBorderOpacity = DockBorderOpacity;
        }

        #endregion

        #region Appearance

        private string _textColor = "#FFFFFF";
        public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

        private string _accentColor = "#0A84FF";
        public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

        private string _fontFamilyName = "Segoe UI Variable Text";
        public string FontFamilyName { get => _fontFamilyName; set => Set(ref _fontFamilyName, value); }

        #endregion

        #region Dock

        private BlurMode _dockBlurMode = BlurMode.Acrylic;
        public BlurMode DockBlurMode { get => _dockBlurMode; set => Set(ref _dockBlurMode, value); }

        private string _dockTintColor = "#1C1C1E";
        public string DockTintColor { get => _dockTintColor; set => Set(ref _dockTintColor, value); }

        private double _dockTintOpacity = 0.55;
        public double DockTintOpacity { get => _dockTintOpacity; set => Set(ref _dockTintOpacity, value); }

        private string _dockBorderColor = "#FFFFFF";
        public string DockBorderColor { get => _dockBorderColor; set => Set(ref _dockBorderColor, value); }

        private double _dockBorderOpacity = 0.18;
        public double DockBorderOpacity { get => _dockBorderOpacity; set => Set(ref _dockBorderOpacity, value); }

        private bool _dockEnabled = true;
        public bool DockEnabled { get => _dockEnabled; set => Set(ref _dockEnabled, value); }

        private double _dockIconSize = 44;
        public double DockIconSize { get => _dockIconSize; set => Set(ref _dockIconSize, value); }

        private double _dockSpacing = 8;
        public double DockSpacing { get => _dockSpacing; set => Set(ref _dockSpacing, value); }

        private double _dockPadding = 8;
        public double DockPadding { get => _dockPadding; set => Set(ref _dockPadding, value); }

        private double _dockMargin = 8;
        /// <summary>Gap between the dock and the bottom of the screen.</summary>
        public double DockMargin { get => _dockMargin; set => Set(ref _dockMargin, value); }

        private double _dockCornerRadius = 20;
        public double DockCornerRadius { get => _dockCornerRadius; set => Set(ref _dockCornerRadius, value); }

        private bool _dockReserveSpace;
        /// <summary>Keep maximized windows above the dock. Off: windows use the full screen height and the dock floats over them.</summary>
        public bool DockReserveSpace { get => _dockReserveSpace; set => Set(ref _dockReserveSpace, value); }

        private bool _dockKeepWindowsClear = true;
        /// <summary>When a window is moved or resized so that it ends up behind the dock, nudge it up above the dock.</summary>
        public bool DockKeepWindowsClear { get => _dockKeepWindowsClear; set => Set(ref _dockKeepWindowsClear, value); }

        private bool _dockAutoHide;
        public bool DockAutoHide { get => _dockAutoHide; set => Set(ref _dockAutoHide, value); }

        private double _dockHoverZoom = 1.12;
        /// <summary>Icon scale while the mouse is over it. 1 turns the effect off.</summary>
        public double DockHoverZoom { get => _dockHoverZoom; set => Set(ref _dockHoverZoom, value); }

        private bool _dockShowIndicators = true;
        public bool DockShowIndicators { get => _dockShowIndicators; set => Set(ref _dockShowIndicators, value); }

        private bool _dockShowStartButton = true;
        public bool DockShowStartButton { get => _dockShowStartButton; set => Set(ref _dockShowStartButton, value); }

        private bool _dockShowPreviews = true;
        /// <summary>Show live window thumbnails when the mouse rests on a running app.</summary>
        public bool DockShowPreviews { get => _dockShowPreviews; set => Set(ref _dockShowPreviews, value); }

        private bool _dockShowBadges = true;
        /// <summary>Show the small status icon apps put on their taskbar button (unread counts and the like).</summary>
        public bool DockShowBadges { get => _dockShowBadges; set => Set(ref _dockShowBadges, value); }

        private bool _dockShowProgress = true;
        /// <summary>Show download/copy progress under the icon.</summary>
        public bool DockShowProgress { get => _dockShowProgress; set => Set(ref _dockShowProgress, value); }

        private bool _dockShowRecycleBin = true;
        public bool DockShowRecycleBin { get => _dockShowRecycleBin; set => Set(ref _dockShowRecycleBin, value); }

        private bool _dockShowRecentFiles = true;
        /// <summary>An app's right-click menu lists its recently opened files (its jump list).</summary>
        public bool DockShowRecentFiles { get => _dockShowRecentFiles; set => Set(ref _dockShowRecentFiles, value); }

        private bool _dockOnAllScreens;
        /// <summary>A dock on every monitor (off: the main monitor only).</summary>
        public bool DockOnAllScreens { get => _dockOnAllScreens; set => Set(ref _dockOnAllScreens, value); }

        private bool _dockWinNumberKeys = true;
        /// <summary>Win+1..9 (and 0) open or switch to the dock's apps, like the Windows taskbar.</summary>
        public bool DockWinNumberKeys { get => _dockWinNumberKeys; set => Set(ref _dockWinNumberKeys, value); }

        private ObservableCollection<PinnedApp> _pinnedApps;
        public ObservableCollection<PinnedApp> PinnedApps
        {
            get => _pinnedApps;
            set
            {
                if (_pinnedApps != null)
                {
                    _pinnedApps.CollectionChanged -= PinnedApps_CollectionChanged;
                }

                _pinnedApps = value ?? new ObservableCollection<PinnedApp>();
                _pinnedApps.CollectionChanged += PinnedApps_CollectionChanged;
                OnChanged(nameof(PinnedApps));
            }
        }

        /// <summary>Raised when an app is pinned, unpinned or reordered.</summary>
        public event EventHandler PinnedAppsChanged;

        private void PinnedApps_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            PinnedAppsChanged?.Invoke(this, EventArgs.Empty);
            QueueSave();
        }

        #endregion

        #region Top bar

        private bool _topBarEnabled = true;
        public bool TopBarEnabled { get => _topBarEnabled; set => Set(ref _topBarEnabled, value); }

        private bool _topBarOnAllScreens = true;
        /// <summary>A top bar on every monitor (off: the main monitor only).</summary>
        public bool TopBarOnAllScreens { get => _topBarOnAllScreens; set => Set(ref _topBarOnAllScreens, value); }

        private bool _topBarReserveSpace = true;
        /// <summary>Keep maximized windows below the top bar.</summary>
        public bool TopBarReserveSpace { get => _topBarReserveSpace; set => Set(ref _topBarReserveSpace, value); }

        private bool _topBarKeepWindowsClear = true;
        /// <summary>A window moved or resized under the top bar is nudged down below it.</summary>
        public bool TopBarKeepWindowsClear { get => _topBarKeepWindowsClear; set => Set(ref _topBarKeepWindowsClear, value); }

        private bool _topBarAutoHide;
        public bool TopBarAutoHide { get => _topBarAutoHide; set => Set(ref _topBarAutoHide, value); }

        private BlurMode _topBarBlurMode = BlurMode.Acrylic;
        public BlurMode TopBarBlurMode { get => _topBarBlurMode; set => Set(ref _topBarBlurMode, value); }

        private string _topBarTintColor = "#1C1C1E";
        public string TopBarTintColor { get => _topBarTintColor; set => Set(ref _topBarTintColor, value); }

        private double _topBarTintOpacity = 0.55;
        public double TopBarTintOpacity { get => _topBarTintOpacity; set => Set(ref _topBarTintOpacity, value); }

        private string _topBarBorderColor = "#FFFFFF";
        public string TopBarBorderColor { get => _topBarBorderColor; set => Set(ref _topBarBorderColor, value); }

        private double _topBarBorderOpacity = 0.18;
        public double TopBarBorderOpacity { get => _topBarBorderOpacity; set => Set(ref _topBarBorderOpacity, value); }

        private double _topBarHeight = 30;
        public double TopBarHeight { get => _topBarHeight; set => Set(ref _topBarHeight, value); }

        private double _topBarMargin;
        /// <summary>0 = full-width bar attached to the screen edge; above 0 it floats.</summary>
        public double TopBarMargin { get => _topBarMargin; set => Set(ref _topBarMargin, value); }

        private double _topBarCornerRadius;
        public double TopBarCornerRadius { get => _topBarCornerRadius; set => Set(ref _topBarCornerRadius, value); }

        private double _topBarFontSize = 13;
        public double TopBarFontSize { get => _topBarFontSize; set => Set(ref _topBarFontSize, value); }

        private bool _showStartInTopBar = true;
        public bool ShowStartInTopBar { get => _showStartInTopBar; set => Set(ref _showStartInTopBar, value); }

        private bool _showWindowTitle = true;
        public bool ShowWindowTitle { get => _showWindowTitle; set => Set(ref _showWindowTitle, value); }

        private bool _showTray = true;
        public bool ShowTray { get => _showTray; set => Set(ref _showTray, value); }

        private bool _showQuickSettings = true;
        public bool ShowQuickSettings { get => _showQuickSettings; set => Set(ref _showQuickSettings, value); }

        private bool _showCpu = true;
        public bool ShowCpu { get => _showCpu; set => Set(ref _showCpu, value); }

        private bool _showRam = true;
        public bool ShowRam { get => _showRam; set => Set(ref _showRam, value); }

        private bool _showGpu = true;
        public bool ShowGpu { get => _showGpu; set => Set(ref _showGpu, value); }

        private bool _showBattery = true;
        public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }

        private bool _showClock = true;
        public bool ShowClock { get => _showClock; set => Set(ref _showClock, value); }

        private string _clockFormat = "ddd MMM d   h:mm tt";
        /// <summary>.NET date format string, e.g. "HH:mm" or "ddd d MMM  h:mm tt".</summary>
        public string ClockFormat { get => _clockFormat; set => Set(ref _clockFormat, value); }

        private bool _showWifi = true;
        public bool ShowWifi { get => _showWifi; set => Set(ref _showWifi, value); }

        private bool _showVolume = true;
        public bool ShowVolume { get => _showVolume; set => Set(ref _showVolume, value); }

        private bool _showNetSpeed;
        public bool ShowNetSpeed { get => _showNetSpeed; set => Set(ref _showNetSpeed, value); }

        private bool _clockOpensCalendar = true;
        /// <summary>Clicking the clock opens GlassBar's own calendar; off: Windows' notification center.</summary>
        public bool ClockOpensCalendar { get => _clockOpensCalendar; set => Set(ref _clockOpensCalendar, value); }

        #endregion

        #region General

        private bool _hideWindowsTaskbar = true;
        public bool HideWindowsTaskbar { get => _hideWindowsTaskbar; set => Set(ref _hideWindowsTaskbar, value); }

        private bool _runAtStartup;
        public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }

        private bool _useGpuRendering;
        /// <summary>Off: draw the bars on the CPU, which saves the large graphics-driver memory WPF otherwise loads. Applies on restart.</summary>
        public bool UseGpuRendering { get => _useGpuRendering; set => Set(ref _useGpuRendering, value); }

        private HotCornerAction _hotCornerTopLeft;
        public HotCornerAction HotCornerTopLeft { get => _hotCornerTopLeft; set => Set(ref _hotCornerTopLeft, value); }

        private HotCornerAction _hotCornerTopRight;
        public HotCornerAction HotCornerTopRight { get => _hotCornerTopRight; set => Set(ref _hotCornerTopRight, value); }

        private HotCornerAction _hotCornerBottomLeft;
        public HotCornerAction HotCornerBottomLeft { get => _hotCornerBottomLeft; set => Set(ref _hotCornerBottomLeft, value); }

        private HotCornerAction _hotCornerBottomRight;
        public HotCornerAction HotCornerBottomRight { get => _hotCornerBottomRight; set => Set(ref _hotCornerBottomRight, value); }

        private bool _smoothCorners = true;
        /// <summary>Let Windows 11 round the bars' corners (anti-aliased, fixed size) instead of a custom-radius clip.</summary>
        public bool SmoothCorners { get => _smoothCorners; set => Set(ref _smoothCorners, value); }

        #endregion

        #region Derived values the XAML binds to

        // The tint never goes fully transparent: a zero-alpha pixel would let clicks fall through the bar.
        [JsonIgnore]
        public Brush DockPanelBrush => MakeBrush(DockTintColor, Math.Max(DockTintOpacity, 1.0 / 255), Color.FromRgb(0x1C, 0x1C, 0x1E));

        [JsonIgnore]
        public Brush DockEdgeBrush => MakeBrush(DockBorderColor, DockBorderOpacity, Colors.White);

        [JsonIgnore]
        public Brush TopBarPanelBrush => MakeBrush(TopBarTintColor, Math.Max(TopBarTintOpacity, 1.0 / 255), Color.FromRgb(0x1C, 0x1C, 0x1E));

        [JsonIgnore]
        public Brush TopBarEdgeBrush => MakeBrush(TopBarBorderColor, TopBarBorderOpacity, Colors.White);

        [JsonIgnore]
        public Brush TextBrush => MakeBrush(TextColor, 1, Colors.White);

        [JsonIgnore]
        public Brush TextDimBrush => MakeBrush(TextColor, 0.65, Colors.White);

        [JsonIgnore]
        public Brush HoverBrush => MakeBrush(TextColor, 0.14, Colors.White);

        [JsonIgnore]
        public Brush AccentBrush => MakeBrush(AccentColor, 1, Color.FromRgb(0x0A, 0x84, 0xFF));

        [JsonIgnore]
        public FontFamily FontFamilyValue
        {
            get
            {
                try
                {
                    // Second name is the fallback when the chosen font is not installed.
                    return new FontFamily((string.IsNullOrWhiteSpace(FontFamilyName) ? "Segoe UI" : FontFamilyName) + ", Segoe UI");
                }
                catch (Exception)
                {
                    return new FontFamily("Segoe UI");
                }
            }
        }

        /// <summary>Width of one dock slot: the icon plus the gap between icons.</summary>
        [JsonIgnore]
        public double DockItemWidth => DockIconSize + DockSpacing;

        /// <summary>Height of one dock slot: the icon plus room for the running indicator.</summary>
        [JsonIgnore]
        public double DockItemHeight => DockIconSize + DockIndicatorSpace;

        public const double DockIndicatorSpace = 7;

        private static Brush MakeBrush(string hex, double opacity, Color fallback)
        {
            Color c = fallback;
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color parsed)
                {
                    c = parsed;
                }
            }
            catch (Exception)
            {
                // Not a valid color string: keep the fallback.
            }

            c.A = (byte)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 255);
            SolidColorBrush brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        #endregion

        public Settings()
        {
            PinnedApps = new ObservableCollection<PinnedApp>();
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            OnChanged(name);
        }

        private void OnChanged(string name)
        {
            Raise(name);

            switch (name)
            {
                case nameof(DockTintColor):
                case nameof(DockTintOpacity):
                    Raise(nameof(DockPanelBrush));
                    break;
                case nameof(DockBorderColor):
                case nameof(DockBorderOpacity):
                    Raise(nameof(DockEdgeBrush));
                    break;
                case nameof(TopBarTintColor):
                case nameof(TopBarTintOpacity):
                    Raise(nameof(TopBarPanelBrush));
                    break;
                case nameof(TopBarBorderColor):
                case nameof(TopBarBorderOpacity):
                    Raise(nameof(TopBarEdgeBrush));
                    break;
                case nameof(TextColor):
                    Raise(nameof(TextBrush));
                    Raise(nameof(TextDimBrush));
                    Raise(nameof(HoverBrush));
                    break;
                case nameof(AccentColor):
                    Raise(nameof(AccentBrush));
                    break;
                case nameof(FontFamilyName):
                    Raise(nameof(FontFamilyValue));
                    break;
                case nameof(DockIconSize):
                case nameof(DockSpacing):
                    Raise(nameof(DockItemWidth));
                    Raise(nameof(DockItemHeight));
                    break;
            }

            QueueSave();
        }

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        #endregion
    }
}
