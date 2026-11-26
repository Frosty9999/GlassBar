using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;

namespace GlassBar
{
    /// <summary>Clock, CPU, memory and battery readouts for the top bar. Updates once a second.</summary>
    public class SystemStats : INotifyPropertyChanged
    {
        public static SystemStats Instance { get; } = new SystemStats();

        private readonly DispatcherTimer _timer;
        private long _lastIdle;
        private long _lastTotal;
        private int _tick;

        private string _clockText = "";
        public string ClockText { get => _clockText; private set => Set(ref _clockText, value, nameof(ClockText)); }

        private string _cpuText = "CPU --";
        public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value, nameof(CpuText)); }

        private string _ramText = "RAM --";
        public string RamText { get => _ramText; private set => Set(ref _ramText, value, nameof(RamText)); }

        private string _gpuText = "GPU --";
        public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value, nameof(GpuText)); }

        private GpuUsage _gpu; // only exists while the GPU readout is on

        private string _batteryText = "";
        public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value, nameof(BatteryText)); }

        private string _netSpeedText = "";
        public string NetSpeedText { get => _netSpeedText; private set => Set(ref _netSpeedText, value, nameof(NetSpeedText)); }

        // Segoe Fluent Icons glyphs
        private const string GlyphWifi1 = "\uE872", GlyphWifi2 = "\uE873", GlyphWifi3 = "\uE874", GlyphWifi4 = "\uE701";
        private const string GlyphEthernet = "\uE839", GlyphOffline = "\uF384";
        private const string GlyphMute = "\uE74F", GlyphVol0 = "\uE992", GlyphVol1 = "\uE993", GlyphVol2 = "\uE994", GlyphVol3 = "\uE995";

        private string _networkGlyph = GlyphOffline;
        public string NetworkGlyph { get => _networkGlyph; private set => Set(ref _networkGlyph, value, nameof(NetworkGlyph)); }

        private string _networkTooltip = "";
        public string NetworkTooltip { get => _networkTooltip; private set => Set(ref _networkTooltip, value, nameof(NetworkTooltip)); }

        private string _volumeGlyph = GlyphVol3;
        public string VolumeGlyph { get => _volumeGlyph; private set => Set(ref _volumeGlyph, value, nameof(VolumeGlyph)); }

        private string _volumeTooltip = "";
        public string VolumeTooltip { get => _volumeTooltip; private set => Set(ref _volumeTooltip, value, nameof(VolumeTooltip)); }

        private bool _hasBattery;
        public bool HasBattery
        {
            get => _hasBattery;
            private set
            {
                if (_hasBattery != value)
                {
                    _hasBattery = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBattery)));
                }
            }
        }

        private SystemStats()
        {
            Native.TryGetSystemTimes(out _lastIdle, out _lastTotal);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Update();
            _timer.Start();

            Update();
            Settings.Instance.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Settings.ClockFormat))
                {
                    UpdateClock();
                }
                else if (e.PropertyName == nameof(Settings.ShowWifi) && Settings.Instance.ShowWifi)
                {
                    UpdateNetwork();
                }
            };
        }

        private void Update()
        {
            UpdateClock();

            // CPU and memory every 2 seconds, battery every 10.
            if (_tick % 2 == 0)
            {
                UpdateCpu();

                int memory = Native.GetMemoryLoad();
                RamText = memory >= 0 ? "RAM " + memory + "%" : "RAM --";

                UpdateGpu();
            }

            Settings settings = Settings.Instance;
            if (settings.ShowVolume)
            {
                UpdateVolume();
            }

            if (settings.ShowNetSpeed)
            {
                NetworkStatus.SampleSpeed(out double down, out double up);
                NetSpeedText = "\u2193 " + NetworkStatus.FormatSpeed(down) + "   \u2191 " + NetworkStatus.FormatSpeed(up);
            }

            if (settings.ShowWifi && _tick % 5 == 0)
            {
                UpdateNetwork();
            }

            if (_tick % 10 == 0)
            {
                bool present = Native.TryGetBattery(out int percent, out bool charging);
                HasBattery = present;
                BatteryText = present ? (charging ? "⚡ " : "") + percent + "%" : "";
            }

            _tick++;
        }

        /// <summary>Reads the volume now (after the user scrolled or clicked it) instead of on the next tick.</summary>
        public void UpdateVolume()
        {
            if (!AudioVolume.TryGet(out float level, out bool muted))
            {
                VolumeGlyph = GlyphMute;
                VolumeTooltip = "No playback device";
                return;
            }

            int percent = (int)Math.Round(level * 100);
            VolumeGlyph = muted || percent == 0 ? GlyphMute : percent < 34 ? GlyphVol1 : percent < 67 ? GlyphVol2 : GlyphVol3;
            VolumeTooltip = (muted ? "Muted (" + percent + "%)" : "Volume " + percent + "%") + "\nScroll to change, click for sound options, middle-click to mute";
        }

        private void UpdateNetwork()
        {
            switch (NetworkStatus.GetConnection(out string ssid, out int signal))
            {
                case NetworkStatus.Kind.WiFi:
                    NetworkGlyph = signal >= 75 ? GlyphWifi4 : signal >= 50 ? GlyphWifi3 : signal >= 25 ? GlyphWifi2 : GlyphWifi1;
                    NetworkTooltip = "Wi-Fi: " + ssid + " (" + signal + "%)";
                    break;
                case NetworkStatus.Kind.Ethernet:
                    NetworkGlyph = GlyphEthernet;
                    NetworkTooltip = "Connected (Ethernet)";
                    break;
                default:
                    NetworkGlyph = GlyphOffline;
                    NetworkTooltip = "Not connected";
                    break;
            }
        }

        private void UpdateGpu()
        {
            if (!Settings.Instance.ShowGpu)
            {
                if (_gpu != null)
                {
                    _gpu.Dispose();
                    _gpu = null;
                    GpuText = "GPU --";
                }

                return;
            }

            _gpu ??= new GpuUsage();
            double? usage = _gpu.Sample();
            if (usage.HasValue)
            {
                GpuText = "GPU " + Math.Round(usage.Value) + "%";
            }
        }

        private void UpdateClock()
        {
            string format = Settings.Instance.ClockFormat;
            try
            {
                ClockText = DateTime.Now.ToString(string.IsNullOrWhiteSpace(format) ? "t" : format, CultureInfo.CurrentCulture);
            }
            catch (FormatException)
            {
                ClockText = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
            }
        }

        private void UpdateCpu()
        {
            if (!Native.TryGetSystemTimes(out long idle, out long total))
            {
                return;
            }

            long idleDelta = idle - _lastIdle;
            long totalDelta = total - _lastTotal;
            _lastIdle = idle;
            _lastTotal = total;

            if (totalDelta > 0)
            {
                double usage = 100.0 * (1.0 - (double)idleDelta / totalDelta);
                CpuText = "CPU " + Math.Round(Math.Max(0, Math.Min(100, usage))) + "%";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set(ref string field, string value, string name)
        {
            if (field != value)
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
