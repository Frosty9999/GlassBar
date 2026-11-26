using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Threading;
using System.Runtime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using Microsoft.Win32;

namespace GlassBar
{
    public partial class App : Application
    {
        private const string StartupValueName = "GlassBar";
        private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static App Instance => (App)Current;

        private Mutex _singleInstance;
        private ShellManager _shell;
        private DockManager _dockManager;
        // One bar per monitor, keyed by Screen.DeviceName.
        private readonly Dictionary<string, DockWindow> _docks = new Dictionary<string, DockWindow>();
        private readonly Dictionary<string, TopBarWindow> _topBars = new Dictionary<string, TopBarWindow>();
        private string _primaryScreen;
        private DispatcherTimer _displayChangeTimer;
        private WindowGuard _windowGuard;
        private HotCorners _hotCorners;
        private SettingsWindow _settingsWindow;
        private bool _cleanedUp;
        private int _errorCount;
        private DispatcherTimer _trimTimer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Only one copy may run: two would fight over the taskbar and the system tray.
            _singleInstance = new Mutex(true, SingleInstanceName, out bool isFirst);
            if (!isFirst)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown();
                return;
            }

            ListenForQuitSignal();
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // GPU rendering loads the Direct3D driver into the process, which costs far more memory than
            // two small bars need. Must be decided before the first window is created.
            if (!Settings.Instance.UseGpuRendering)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }
            SessionEnding += (s, a) => CleanUp();

            bool firstRun = !File.Exists(Settings.FilePath);

            // Records the taskbar's state and starts the watchdog that restores it if GlassBar crashes.
            CrashGuard.Begin();

            try
            {
                // True only when GlassBar replaces Explorer as the Windows shell, which is not the normal setup.
                EnvironmentHelper.IsAppRunningAsShell = NativeMethods.GetShellWindow() == IntPtr.Zero;

                ShellConfig config = ShellManager.DefaultShellConfig;
                config.TaskIconSize = IconSize.Large;
                _shell = new ShellManager(config);

                _dockManager = new DockManager(_shell);

                _shell.ExplorerHelper.HideExplorerTaskbar = Settings.Instance.HideWindowsTaskbar;

                // The installer can add or remove the startup entry, so the checkbox follows the registry.
                Settings.Instance.RunAtStartup = RunAtStartupEntryExists();
                Settings.Instance.PropertyChanged += Settings_PropertyChanged;
                SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

                SyncBars();

                // Nudges windows out from under the top bar and the dock after they are moved or resized.
                _windowGuard = new WindowGuard();
                _hotCorners = new HotCorners(_shell);
            }
            catch (Exception ex)
            {
                // A failed start must not leave an invisible process holding the taskbar hidden.
                LogError(ex);
                MessageBox.Show("GlassBar could not start:\n" + ex.Message + "\n\nDetails: " + ErrorLogPath, "GlassBar");
                ExitGracefully();
                return;
            }

            // Startup allocates a lot that is never needed again (icon loading, app lists, layout).
            QueueMemoryTrim(TimeSpan.FromSeconds(15));
            _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _trimTimer.Tick += (s, a) => TrimMemory();
            _trimTimer.Start();

            if (firstRun)
            {
                // Show the options once so it is clear where they live (right-click either bar to reopen).
                Settings.Instance.Save();
                ShowSettings();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CleanUp();
            base.OnExit(e);
        }

        #region Bars

        /// <summary>Opens or closes bars on each monitor so that they match the settings and the monitors present.</summary>
        private void SyncBars()
        {
            if (_shell == null || _cleanedUp)
            {
                return;
            }

            Settings s = Settings.Instance;
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            string primary = System.Windows.Forms.Screen.PrimaryScreen.DeviceName;

            // The main monitor changed: start over, so the keyboard shortcuts and tray follow the new main bars.
            if (_primaryScreen != null && _primaryScreen != primary)
            {
                CloseAll(_docks);
                CloseAll(_topBars);
            }

            _primaryScreen = primary;

            IEnumerable<string> Wanted(bool enabled, bool everywhere) =>
                !enabled ? Enumerable.Empty<string>()
                : everywhere ? screens.Select(x => x.DeviceName)
                : new[] { primary };

            SyncSet(_topBars, Wanted(s.TopBarEnabled, s.TopBarOnAllScreens), name => new TopBarWindow(_shell, name));
            SyncSet(_docks, Wanted(s.DockEnabled, s.DockOnAllScreens), name => new DockWindow(_shell, _dockManager, name));
        }

        private static void SyncSet<T>(Dictionary<string, T> bars, IEnumerable<string> wanted, Func<string, T> create) where T : BarWindow
        {
            HashSet<string> want = new HashSet<string>(wanted);

            foreach (string name in bars.Keys.Where(k => !want.Contains(k)).ToList())
            {
                bars[name].CloseBar();
                bars.Remove(name);
            }

            foreach (string name in want.Where(n => !bars.ContainsKey(n)))
            {
                T bar = create(name);
                bars[name] = bar;
                bar.Show();
            }
        }

        private static void CloseAll<T>(Dictionary<string, T> bars) where T : BarWindow
        {
            foreach (T bar in bars.Values)
            {
                bar.CloseBar();
            }

            bars.Clear();
        }

        /// <summary>Monitors plugged in, unplugged or rearranged. Windows sends several events in a row, so wait for quiet.</summary>
        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_displayChangeTimer == null)
                {
                    _displayChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _displayChangeTimer.Tick += (s, a) =>
                    {
                        _displayChangeTimer.Stop();
                        SyncBars();
                    };
                }

                _displayChangeTimer.Stop();
                _displayChangeTimer.Start();
            }));
        }

        /// <summary>
        /// What windows on a monitor should stay clear of (physical pixels): the bottom of the top bar and the top
        /// of the dock, each only while that bar is showing and its "don't let windows go behind it" setting is on.
        /// Used when windows are moved (WindowGuard) and snapped (WindowTiling).
        /// </summary>
        /// <summary>The dock entry (app) a window belongs to, even when the dock itself is turned off.</summary>
        internal DockItem DockItemFor(ManagedShell.WindowsTasks.ApplicationWindow window)
        {
            return _dockManager?.FindItem(window);
        }

        internal BarLimits BarLimitsOn(string screenDeviceName)
        {
            Settings s = Settings.Instance;
            BarLimits limits = new BarLimits();

            if (s.TopBarKeepWindowsClear && _topBars.TryGetValue(screenDeviceName, out TopBarWindow bar) && bar.IsFullyShown)
            {
                limits.TopBarBottom = bar.BarScreenRect.Bottom;
            }

            if (s.DockKeepWindowsClear && _docks.TryGetValue(screenDeviceName, out DockWindow dock) && dock.IsFullyShown)
            {
                ManagedShell.Interop.NativeMethods.Rect r = dock.BarScreenRect;
                limits.DockTop = r.Top - (int)Math.Round(6 * dock.BarScale); // small gap above the dock
                limits.DockLeft = r.Left;
                limits.DockRight = r.Right;
            }

            return limits;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Settings.DockEnabled):
                case nameof(Settings.TopBarEnabled):
                case nameof(Settings.DockOnAllScreens):
                case nameof(Settings.TopBarOnAllScreens):
                    SyncBars();
                    break;
                case nameof(Settings.HideWindowsTaskbar):
                    _shell.ExplorerHelper.HideExplorerTaskbar = Settings.Instance.HideWindowsTaskbar;
                    break;
                case nameof(Settings.RunAtStartup):
                    ApplyRunAtStartup(Settings.Instance.RunAtStartup);
                    break;
            }
        }

        #endregion

        #region Settings window, startup entry, exit

        public void ShowSettings()
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (s, e) =>
                {
                    _settingsWindow = null;
                    QueueMemoryTrim(TimeSpan.FromSeconds(2));
                };
                _settingsWindow.Show();
            }

            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
        }

        private static bool RunAtStartupEntryExists()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, false))
                {
                    return key?.GetValue(StartupValueName) != null;
                }
            }
            catch (Exception)
            {
                return Settings.Instance.RunAtStartup;
            }
        }

        private static void ApplyRunAtStartup(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (enable)
                    {
                        string exe = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exe))
                        {
                            key.SetValue(StartupValueName, "\"" + exe + "\"");
                        }
                    }
                    else
                    {
                        key.DeleteValue(StartupValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the startup entry:\n" + ex.Message, "GlassBar");
            }
        }

        // The installer and uninstaller set this event (by name, from Inno Setup's script) so a running GlassBar
        // exits cleanly and gives the taskbar back before its files are replaced or removed. "GlassBar.exe --quit"
        // sets it too. Keep the name in sync with installer\GlassBar.iss.
        private const string QuitEventName = "GlassBar_Quit";
        private const string SingleInstanceName = "GlassBar_SingleInstance";

        private void ListenForQuitSignal()
        {
            EventWaitHandle quit;
            try
            {
                quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            }
            catch (Exception ex)
            {
                LogError(ex);
                return;
            }

            Thread waiter = new Thread(() =>
            {
                quit.WaitOne();
                Dispatcher.BeginInvoke(new Action(ExitGracefully));
            })
            {
                IsBackground = true,
                Name = "GlassBar quit signal"
            };
            waiter.Start();
        }

        /// <summary>
        /// For "GlassBar.exe --quit": asks the running copy to exit and waits up to 15 seconds for it.
        /// Returns false if none was running or it did not exit in time.
        /// </summary>
        public static bool SignalRunningCopyToQuit()
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle quit))
            {
                return false;
            }

            using (quit)
            {
                quit.Set();
            }

            for (int i = 0; i < 150; i++)
            {
                if (!Mutex.TryOpenExisting(SingleInstanceName, out Mutex running))
                {
                    return true;
                }

                running.Dispose();
                Thread.Sleep(100);
            }

            return false;
        }

        /// <summary>Closes the bars, gives the Windows taskbar back and exits.</summary>
        public void ExitGracefully()
        {
            CleanUp();
            Shutdown();
        }

        private void CleanUp()
        {
            if (_cleanedUp)
            {
                return;
            }

            _cleanedUp = true;
            _trimTimer?.Stop();

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displayChangeTimer?.Stop();
            Settings.Instance.Save();

            try
            {
                _settingsWindow?.Close();

                if (_shell != null)
                {
                    _windowGuard?.Dispose();
                    _windowGuard = null;
                    _hotCorners?.Dispose();
                    _hotCorners = null;
                    _shell.AppBarManager.SignalGracefulShutdown();
                    CloseAll(_topBars);
                    CloseAll(_docks);

                    _shell.ExplorerHelper.HideExplorerTaskbar = false;
                    _shell.Dispose();
                    _shell = null;
                }

                CrashGuard.End();
            }
            catch (Exception ex)
            {
                LogError(ex);
            }

            _singleInstance?.Dispose();
            _singleInstance = null;
        }

        #endregion

        #region Memory

        private void QueueMemoryTrim(TimeSpan delay)
        {
            DispatcherTimer once = new DispatcherTimer { Interval = delay };
            once.Tick += (s, e) =>
            {
                once.Stop();
                TrimMemory();
            };
            once.Start();
        }

        /// <summary>Frees unused .NET memory and returns idle pages to Windows.</summary>
        private static void TrimMemory()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            Native.TrimWorkingSet();
        }

        #endregion

        #region Errors

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogError(e.Exception);
            _errorCount++;

            // Keep running through an isolated failure, but stop (and restore the taskbar) if errors keep coming.
            if (_errorCount <= 5)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            MessageBox.Show(
                "GlassBar hit repeated errors and will close. The Windows taskbar will be restored.\n\n" +
                "Details were written to:\n" + ErrorLogPath + "\n\n" + e.Exception.Message,
                "GlassBar");
            ExitGracefully();
        }

        public static string ErrorLogPath =>
            Path.Combine(Path.GetDirectoryName(Settings.FilePath) ?? "", "error.log");

        private static void LogError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath) ?? "");
                File.AppendAllText(ErrorLogPath, DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception)
            {
                // Nothing more we can do if the log itself cannot be written.
            }
        }

        #endregion
    }
}
