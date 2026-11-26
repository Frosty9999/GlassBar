using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.UWPInterop;
using ManagedShell.WindowsTasks;

namespace GlassBar
{
    /// <summary>One icon in the dock: an app, with all of its open windows grouped under it.</summary>
    public class DockItem : INotifyPropertyChanged
    {
        public string Key { get; }
        public string Path { get; }
        public string AppUserModelId { get; }

        public bool IsUwp => !string.IsNullOrEmpty(AppUserModelId);

        /// <summary>The Recycle Bin at the end of the dock (not a pin; its icon shows empty or full).</summary>
        public bool IsRecycleBin => Key == DockManager.RecycleBinKey;

        /// <summary>For items whose icon GlassBar draws itself (the Recycle Bin).</summary>
        internal void SetIcon(ImageSource icon)
        {
            if (!ReferenceEquals(_icon, icon))
            {
                _icon = icon;
                Raise(nameof(Icon));
            }
        }

        /// <summary>A pinned folder ("stack"): clicking it shows the folder's newest files.</summary>
        public bool IsFolder => !IsUwp && !string.IsNullOrEmpty(Path) && Directory.Exists(Path);

        public bool CanLaunch => IsUwp || (!string.IsNullOrEmpty(Path) && (File.Exists(Path) || Directory.Exists(Path)));

        public List<ApplicationWindow> Windows { get; private set; } = new List<ApplicationWindow>();

        private ApplicationWindow _lastActive;
        private ApplicationWindow _activeOnPress;

        private string _name;
        public string Name { get => _name; set => Set(ref _name, value); }

        private ImageSource _icon;
        public ImageSource Icon => _icon ?? Windows.FirstOrDefault()?.Icon;

        private bool _isPinned;
        public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }

        private bool _isActive;
        public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

        /// <summary>Raised when the app's set of open windows changes (window previews rebuild on it).</summary>
        public event EventHandler WindowsChanged;

        #region Badge and progress (sent by apps through the taskbar's ITaskbarList3 interface)

        /// <summary>The small status icon an app put on its taskbar button, e.g. an unread count.</summary>
        public ImageSource Badge => Windows.Select(w => w.OverlayIcon).FirstOrDefault(i => i != null);

        public string BadgeDescription => Windows.FirstOrDefault(w => w.OverlayIcon != null)?.OverlayIconDescription;

        /// <summary>Apps report progress in 0..65534 (Explorer's own scale for ITaskbarList3::SetProgressValue).</summary>
        private const double ProgressMax = 65534;

        private ApplicationWindow ProgressWindow
        {
            get
            {
                // Errors and pauses matter most, then normal progress, then "busy" (indeterminate).
                return Windows.FirstOrDefault(w => w.ProgressState == NativeMethods.TBPFLAG.TBPF_ERROR)
                    ?? Windows.FirstOrDefault(w => w.ProgressState == NativeMethods.TBPFLAG.TBPF_PAUSED)
                    ?? Windows.FirstOrDefault(w => w.ProgressState == NativeMethods.TBPFLAG.TBPF_NORMAL)
                    ?? Windows.FirstOrDefault(w => w.ProgressState == NativeMethods.TBPFLAG.TBPF_INDETERMINATE);
            }
        }

        public bool HasProgress => ProgressWindow != null;

        /// <summary>0 to 1. "Busy" without a known amount shows as a full, faded bar.</summary>
        public double ProgressFraction
        {
            get
            {
                ApplicationWindow w = ProgressWindow;
                if (w == null)
                {
                    return 0;
                }

                if (w.ProgressState == NativeMethods.TBPFLAG.TBPF_INDETERMINATE)
                {
                    return 1;
                }

                return Math.Max(0, Math.Min(1, w.ProgressValue / ProgressMax));
            }
        }

        public double ProgressOpacity => ProgressWindow?.ProgressState == NativeMethods.TBPFLAG.TBPF_INDETERMINATE ? 0.45 : 1;

        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
        private static readonly Brush PausedBrush = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));

        public Brush ProgressBrush
        {
            get
            {
                switch (ProgressWindow?.ProgressState)
                {
                    case NativeMethods.TBPFLAG.TBPF_ERROR:
                        return ErrorBrush;
                    case NativeMethods.TBPFLAG.TBPF_PAUSED:
                        return PausedBrush;
                    default:
                        return Settings.Instance.AccentBrush;
                }
            }
        }

        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void Window_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "OverlayIcon":
                case "OverlayIconDescription":
                    Raise(nameof(Badge));
                    Raise(nameof(BadgeDescription));
                    break;
                case "ProgressState":
                case "ProgressValue":
                    RaiseProgress();
                    break;
            }
        }

        private void RaiseProgress()
        {
            Raise(nameof(HasProgress));
            Raise(nameof(ProgressFraction));
            Raise(nameof(ProgressOpacity));
            Raise(nameof(ProgressBrush));
        }

        #endregion

        public DockItem(string key, string path, string appUserModelId, string name)
        {
            Key = key;
            Path = path ?? "";
            AppUserModelId = appUserModelId ?? "";
            _name = name;
            LoadIcon();
        }

        private void LoadIcon()
        {
            string path = Path;
            string aumid = AppUserModelId;

            // Use ManagedShell's icon thread so shell icon calls are serialized with its own.
            Task.Factory.StartNew(() =>
            {
                ImageSource image = null;
                try
                {
                    if (!string.IsNullOrEmpty(aumid))
                    {
                        image = StoreAppHelper.AppList.GetAppByAumid(aumid)?.GetIconImageSource(IconSize.ExtraLarge);
                    }
                    else if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    {
                        // Programs and folder stacks alike.
                        // The program's own icon, read the way Explorer reads it. (ManagedShell's file-icon
                        // lookup returns the generic file-type icon for some programs: a blank white page.)
                        image = ShellIcons.Load(path, 96);
                    }
                }
                catch (Exception)
                {
                    // Fall back to the window's own icon.
                }

                if (image != null)
                {
                    if (image.CanFreeze && !image.IsFrozen)
                    {
                        image.Freeze();
                    }

                    _icon = image;
                    Raise(nameof(Icon));
                }
            }, CancellationToken.None, TaskCreationOptions.None, IconHelper.IconScheduler);
        }

        internal void SetWindows(List<ApplicationWindow> windows)
        {
            List<ApplicationWindow> old = Windows;
            bool changed = old.Count != windows.Count || !old.SequenceEqual(windows);

            foreach (ApplicationWindow w in old)
            {
                w.PropertyChanged -= Window_PropertyChanged;
            }

            foreach (ApplicationWindow w in windows)
            {
                w.PropertyChanged += Window_PropertyChanged;
            }

            Windows = windows;
            IsRunning = windows.Count > 0;

            if (changed)
            {
                Raise(nameof(Badge));
                Raise(nameof(BadgeDescription));
                RaiseProgress();
                WindowsChanged?.Invoke(this, EventArgs.Empty);
            }

            ApplicationWindow active = windows.FirstOrDefault(w => w.State == ApplicationWindow.WindowState.Active);
            if (active != null)
            {
                _lastActive = active;
            }

            IsActive = active != null;

            if (_icon == null)
            {
                // Still showing the window's own icon, which may have loaded since last time.
                Raise(nameof(Icon));
            }
        }

        /// <summary>
        /// Call on mouse-down. Clicking the dock takes focus away from the app, so we have to remember
        /// whether this app was in front before the click to decide between "bring forward" and "minimize".
        /// </summary>
        public void CapturePressState()
        {
            _activeOnPress = Windows.FirstOrDefault(w => w.State == ApplicationWindow.WindowState.Active);
        }

        /// <summary>Left-click behavior: launch, bring forward, cycle windows, or minimize.</summary>
        public void Activate()
        {
            ApplicationWindow wasActive = _activeOnPress;
            _activeOnPress = null;

            if (Windows.Count == 0)
            {
                Launch();
                return;
            }

            if (wasActive != null && Windows.Contains(wasActive))
            {
                if (Windows.Count == 1)
                {
                    wasActive.Minimize();
                }
                else
                {
                    int next = (Windows.IndexOf(wasActive) + 1) % Windows.Count;
                    Windows[next].BringToFront();
                }

                return;
            }

            ApplicationWindow target = _lastActive != null && Windows.Contains(_lastActive) ? _lastActive : Windows[0];
            target.BringToFront();
        }

        public void Launch()
        {
            if (IsFolder)
            {
                ShellHelper.StartProcess(Path); // opens the folder in File Explorer
            }
            else if (IsUwp)
            {
                ShellHelper.StartProcess("appx:" + AppUserModelId);
            }
            else if (!string.IsNullOrEmpty(Path))
            {
                ShellHelper.StartProcess(Path);
            }
        }

        /// <summary>The window snapping acts on: the one used last, else the first.</summary>
        public ApplicationWindow PrimaryWindow =>
            _lastActive != null && Windows.Contains(_lastActive) ? _lastActive : Windows.FirstOrDefault();

        /// <summary>Desktop programs can be handed files to open (Store apps can't be started with file arguments).</summary>
        public bool CanOpenFiles => !IsUwp && !IsFolder && !IsRecycleBin &&
                                    Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path);

        /// <summary>Starts the program with the files as arguments, the way "Open with" does.</summary>
        public void OpenFiles(IEnumerable<string> files)
        {
            if (!CanOpenFiles)
            {
                return;
            }

            try
            {
                ProcessStartInfo start = new ProcessStartInfo(Path)
                {
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(Path) ?? ""
                };
                foreach (string file in files)
                {
                    start.ArgumentList.Add(file);
                }

                Process.Start(start)?.Dispose();
            }
            catch (Exception)
            {
                // The program refused to start; nothing more to do.
            }
        }

        public void CloseAll()
        {
            foreach (ApplicationWindow window in Windows.ToList())
            {
                window.Close();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                Raise(name);
            }
        }

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Builds the dock's contents: pinned apps first (in the user's order), then any other running apps.
    /// Windows come from ManagedShell's task service and are grouped per application.
    /// </summary>
    public class DockManager
    {
        /// <summary>Apps: pinned, then running. Drag-reorderable.</summary>
        public ObservableCollection<DockItem> Items { get; } = new ObservableCollection<DockItem>();

        /// <summary>After the divider: folder stacks, then the Recycle Bin.</summary>
        public ObservableCollection<DockItem> Stacks { get; } = new ObservableCollection<DockItem>();

        internal const string RecycleBinKey = "::recyclebin";
        private readonly DockItem _recycleBin = new DockItem(RecycleBinKey, "", "", "Recycle Bin");
        private readonly DispatcherTimer _recycleBinTimer;
        private bool? _recycleBinFull;

        private readonly ICollectionView _windows;
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<string, DockItem> _cache = new Dictionary<string, DockItem>(StringComparer.OrdinalIgnoreCase);
        private bool _refreshQueued;

        // Drag reordering: while a drag is in progress the list is only changed by the drag itself.
        private bool _dragActive;
        // The user's chosen order for running apps that are not pinned (keys), for this session.
        private List<string> _unpinnedOrder = new List<string>();

        public DockManager(ShellManager shell)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _windows = shell.Tasks.GroupedWindows;

            _windows.CollectionChanged += (s, e) => QueueRefresh();
            shell.TasksService.WindowActivated += (s, e) => QueueRefresh();
            shell.TasksService.DesktopActivated += (s, e) => QueueRefresh();
            Settings.Instance.PinnedAppsChanged += (s, e) => QueueRefresh();
            Settings.Instance.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Settings.DockShowRecycleBin))
                {
                    QueueRefresh();
                }
            };

            // Empty or full: checked every few seconds (cheap), and right after files are dropped on it.
            _recycleBinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _recycleBinTimer.Tick += (s, e) => UpdateRecycleBin();
            _recycleBinTimer.Start();
            UpdateRecycleBin();

            Refresh();
        }

        public void UpdateRecycleBin()
        {
            if (!Settings.Instance.DockShowRecycleBin)
            {
                return;
            }

            bool full = RecycleBin.HasItems;
            if (full != _recycleBinFull)
            {
                _recycleBinFull = full;
                _recycleBin.SetIcon(RecycleBin.Icon(full));
                _recycleBin.Name = full ? "Recycle Bin (has items)" : "Recycle Bin (empty)";
            }
        }

        public void QueueRefresh()
        {
            if (_refreshQueued)
            {
                return;
            }

            _refreshQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
        }

        private static string KeyFor(ApplicationWindow window)
        {
            if (window.IsUWP && !string.IsNullOrEmpty(window.AppUserModelID))
            {
                return "appx:" + window.AppUserModelID;
            }

            string file = AppPaths.Canonical(window.WinFileName);
            return !string.IsNullOrEmpty(file) ? file : "hwnd:" + window.Handle;
        }

        private void Refresh()
        {
            _refreshQueued = false;

            if (_dragActive)
            {
                return; // EndDrag refreshes once the icon is dropped
            }

            // Group the open windows by application.
            Dictionary<string, List<ApplicationWindow>> groups = new Dictionary<string, List<ApplicationWindow>>(StringComparer.OrdinalIgnoreCase);
            List<string> runningOrder = new List<string>();

            foreach (object entry in _windows)
            {
                if (!(entry is ApplicationWindow window))
                {
                    continue;
                }

                string key = KeyFor(window);
                if (!groups.TryGetValue(key, out List<ApplicationWindow> list))
                {
                    list = new List<ApplicationWindow>();
                    groups[key] = list;
                    runningOrder.Add(key);
                }

                list.Add(window);
            }

            // Pinned apps first, then running apps that are not pinned. Folders go after the divider.
            List<DockItem> desired = new List<DockItem>();
            List<DockItem> stacks = new List<DockItem>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PinnedApp pin in Settings.Instance.PinnedApps)
            {
                string key = pin.Key;
                if (string.IsNullOrEmpty(key) || used.Contains(key))
                {
                    continue;
                }

                if (!_cache.TryGetValue(key, out DockItem item))
                {
                    // A pin saved from an older path (Steam's web helper, an old app-* folder) gets the name
                    // of the program it now opens.
                    bool isUwp = !string.IsNullOrEmpty(pin.AppUserModelId);
                    string path = isUwp ? pin.Path : key;
                    string name = !isUwp && !string.Equals(path, pin.Path, StringComparison.OrdinalIgnoreCase)
                        ? NameForFile(path)
                        : pin.Name;
                    item = new DockItem(key, path, pin.AppUserModelId, string.IsNullOrWhiteSpace(name) ? pin.Name : name);
                    _cache[key] = item;
                }

                // The program was uninstalled or moved: leave it out rather than show an empty slot.
                // The pin stays in the settings, so it comes back if the program does.
                if (!item.CanLaunch)
                {
                    continue;
                }

                used.Add(key);
                item.IsPinned = true;
                (item.IsFolder ? stacks : desired).Add(item);
            }

            // Running apps that are not pinned keep the order the user dragged them into; new ones go last.
            List<string> savedOrder = _unpinnedOrder;
            runningOrder = runningOrder
                .OrderBy(k =>
                {
                    int index = savedOrder.FindIndex(o => string.Equals(o, k, StringComparison.OrdinalIgnoreCase));
                    return index >= 0 ? index : int.MaxValue;
                })
                .ToList();

            foreach (string key in runningOrder)
            {
                if (!used.Add(key))
                {
                    continue;
                }

                if (!_cache.TryGetValue(key, out DockItem item))
                {
                    ApplicationWindow first = groups[key][0];
                    bool isUwp = key.StartsWith("appx:", StringComparison.OrdinalIgnoreCase);
                    item = new DockItem(key, isUwp ? "" : key, isUwp ? first.AppUserModelID : "", NameFor(first, isUwp));
                    _cache[key] = item;
                }

                item.IsPinned = false;
                desired.Add(item);
            }

            foreach (DockItem item in desired)
            {
                item.SetWindows(groups.TryGetValue(item.Key, out List<ApplicationWindow> list) ? list : new List<ApplicationWindow>());
            }

            if (Settings.Instance.DockShowRecycleBin)
            {
                stacks.Add(_recycleBin);
            }

            Sync(Items, desired);
            Sync(Stacks, stacks);

            // Forget apps that are neither pinned nor running.
            foreach (string stale in _cache.Keys.Where(k => !used.Contains(k)).ToList())
            {
                _cache.Remove(stale);
            }
        }

        /// <summary>The app a window belongs to (running apps and pinned apps alike), or null.</summary>
        public DockItem FindItem(ApplicationWindow window)
        {
            if (window == null)
            {
                return null;
            }

            DockItem item = Items.FirstOrDefault(i => i.Windows.Contains(window));
            if (item != null)
            {
                return item;
            }

            _cache.TryGetValue(KeyFor(window), out item);
            return item;
        }

        /// <summary>Brings a visible collection in line with the desired list, moving items rather than rebuilding.</summary>
        private static void Sync(ObservableCollection<DockItem> target, List<DockItem> desired)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                {
                    continue;
                }

                int existing = target.IndexOf(desired[i]);
                if (existing >= 0)
                {
                    target.Move(existing, i);
                }
                else
                {
                    target.Insert(i, desired[i]);
                }
            }

            while (target.Count > desired.Count)
            {
                target.RemoveAt(target.Count - 1);
            }
        }

        private static string NameFor(ApplicationWindow window, bool isUwp)
        {
            if (!isUwp)
            {
                string fromFile = NameForFile(AppPaths.Canonical(window.WinFileName));
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    return fromFile;
                }
            }

            // Store apps: the window title is normally the app name.
            return window.Title ?? "";
        }

        private static string NameForFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "";
            }

            try
            {
                string description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch (Exception)
            {
                // No version resource: use the file name.
            }

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        #region Drag reordering

        public void BeginDrag()
        {
            _dragActive = true;
        }

        /// <summary>Moves the dragged icon to a slot, live, while the mouse button is still held.</summary>
        public void DragTo(DockItem item, int index)
        {
            int current = Items.IndexOf(item);
            if (current < 0 || Items.Count == 0)
            {
                return;
            }

            index = Math.Max(0, Math.Min(Items.Count - 1, index));
            if (index != current)
            {
                Items.Move(current, index);
            }
        }

        /// <summary>
        /// Saves the new order. A running app dropped in among the pinned apps becomes pinned there;
        /// a pinned app dropped after the running apps stays pinned, at the end of the pinned apps.
        /// </summary>
        public void EndDrag(DockItem item)
        {
            _dragActive = false;

            int position = Items.IndexOf(item);
            if (position >= 0)
            {
                bool pinnedAfter = Items.Skip(position + 1).Any(i => i.IsPinned);
                bool pinNow = !item.IsPinned && pinnedAfter && item.CanLaunch;

                // New pinned order, as it now appears in the dock.
                List<PinnedApp> order = new List<PinnedApp>();
                foreach (DockItem d in Items)
                {
                    if (d.IsPinned)
                    {
                        PinnedApp pin = Settings.Instance.PinnedApps
                            .FirstOrDefault(p => string.Equals(p.Key, d.Key, StringComparison.OrdinalIgnoreCase));
                        if (pin != null && !order.Contains(pin))
                        {
                            order.Add(pin);
                        }
                    }
                    else if (pinNow && ReferenceEquals(d, item))
                    {
                        order.Add(new PinnedApp { Name = item.Name ?? "", Path = item.Path, AppUserModelId = item.AppUserModelId });
                    }
                }

                ObservableCollection<PinnedApp> pins = Settings.Instance.PinnedApps;
                for (int i = 0; i < order.Count; i++)
                {
                    int current = pins.IndexOf(order[i]);
                    if (current < 0)
                    {
                        pins.Insert(i, order[i]);
                    }
                    else if (current != i)
                    {
                        pins.Move(current, i);
                    }
                }

                _unpinnedOrder = Items
                    .Where(d => !d.IsPinned && !(pinNow && ReferenceEquals(d, item)))
                    .Select(d => d.Key)
                    .ToList();
            }

            Refresh();
        }

        #endregion

        #region Pinning

        public void Pin(DockItem item)
        {
            if (item == null || item.IsPinned || !item.CanLaunch)
            {
                return;
            }

            Settings.Instance.PinnedApps.Add(new PinnedApp
            {
                Name = item.Name ?? "",
                Path = item.Path,
                AppUserModelId = item.AppUserModelId
            });
        }

        public void Unpin(DockItem item)
        {
            if (item == null)
            {
                return;
            }

            // Remove every pin for this app: older saves can hold several paths that now lead to the same program.
            List<PinnedApp> pins = Settings.Instance.PinnedApps
                .Where(p => string.Equals(p.Key, item.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (PinnedApp pin in pins)
            {
                Settings.Instance.PinnedApps.Remove(pin);
            }
        }

        /// <summary>Pins a program by file path. Shortcuts (.lnk) are resolved to their target.</summary>
        public static bool PinPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                path = ResolveShortcut(path);
            }

            bool folder = !string.IsNullOrEmpty(path) && Directory.Exists(path);
            if (!folder && (string.IsNullOrEmpty(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            path = AppPaths.Canonical(path);
            if (Settings.Instance.PinnedApps.Any(p => string.Equals(p.Key, path, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string name = folder ? FolderName(path) : NameForFile(path);
            Settings.Instance.PinnedApps.Add(new PinnedApp { Name = name, Path = path });
            return true;
        }

        private static string FolderName(string path)
        {
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private static string ResolveShortcut(string shortcutPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return "";
                }

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                string target = shortcut.TargetPath;
                return target ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        #endregion
    }
}
