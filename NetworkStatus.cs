using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBar
{
    /// <summary>Connection type, Wi-Fi name and signal, and total network throughput.</summary>
    internal static class NetworkStatus
    {
        public enum Kind { None, Ethernet, WiFi }

        /// <summary>The current connection. For Wi-Fi, <paramref name="ssid"/> and <paramref name="signal"/> (0-100) are filled in.</summary>
        public static Kind GetConnection(out string ssid, out int signal)
        {
            ssid = null;
            signal = 0;

            if (TryGetWifi(out ssid, out signal))
            {
                return Kind.WiFi;
            }

            try
            {
                bool wired = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) &&
                    !IsVirtual(n) &&
                    n.GetIPProperties().GatewayAddresses.Count > 0);
                return wired ? Kind.Ethernet : Kind.None;
            }
            catch (Exception)
            {
                return Kind.None;
            }
        }

        /// <summary>Hyper-V, VPN and VirtualBox adapters look like Ethernet but aren't the real connection.</summary>
        private static bool IsVirtual(NetworkInterface n)
        {
            string text = (n.Description + " " + n.Name).ToLowerInvariant();
            return text.Contains("virtual") || text.Contains("hyper-v") || text.Contains("vpn") || text.Contains("vethernet") ||
                   text.Contains("tap-") || text.Contains("wintun") || text.Contains("loopback");
        }

        #region Throughput

        private static long _lastReceived = -1;
        private static long _lastSent = -1;
        private static DateTime _lastSample;

        /// <summary>Bytes per second down and up since the previous call, over all real adapters that are up.</summary>
        public static void SampleSpeed(out double down, out double up)
        {
            down = 0;
            up = 0;
            long received = 0, sent = 0;

            try
            {
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        n.NetworkInterfaceType == NetworkInterfaceType.Tunnel || IsVirtual(n))
                    {
                        continue;
                    }

                    IPInterfaceStatistics stats = n.GetIPStatistics();
                    received += stats.BytesReceived;
                    sent += stats.BytesSent;
                }
            }
            catch (Exception)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            double seconds = (now - _lastSample).TotalSeconds;
            if (_lastReceived >= 0 && seconds > 0.2)
            {
                down = Math.Max(0, (received - _lastReceived) / seconds);
                up = Math.Max(0, (sent - _lastSent) / seconds);
            }

            _lastReceived = received;
            _lastSent = sent;
            _lastSample = now;
        }

        /// <summary>Always in megabytes per second (Frosty's choice), one decimal below 100 so small speeds still show.</summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            double megabytes = bytesPerSecond / (1024 * 1024);
            return megabytes.ToString(megabytes >= 100 ? "0" : "0.0") + " MB/s";
        }

        #endregion

        #region Wi-Fi (Native Wifi API)

        private static IntPtr _wlan = IntPtr.Zero;
        private static bool _wlanUnavailable;

        private static bool TryGetWifi(out string ssid, out int signal)
        {
            ssid = null;
            signal = 0;

            if (_wlanUnavailable)
            {
                return false;
            }

            IntPtr list = IntPtr.Zero;
            try
            {
                if (_wlan == IntPtr.Zero && WlanOpenHandle(2, IntPtr.Zero, out _, out _wlan) != 0)
                {
                    _wlanUnavailable = true; // no WLAN service (typical on desktops without Wi-Fi)
                    return false;
                }

                if (WlanEnumInterfaces(_wlan, IntPtr.Zero, out list) != 0)
                {
                    return false;
                }

                int count = Marshal.ReadInt32(list);
                int infoSize = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
                for (int i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO_LIST: two DWORDs, then the array
                    WLAN_INTERFACE_INFO info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * infoSize);
                    if (info.isState != WLAN_INTERFACE_STATE_CONNECTED)
                    {
                        continue;
                    }

                    Guid id = info.InterfaceGuid;
                    if (WlanQueryInterface(_wlan, ref id, WLAN_INTF_OPCODE_CURRENT_CONNECTION, IntPtr.Zero, out _, out IntPtr data, out _) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        WLAN_CONNECTION_ATTRIBUTES attributes = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                        DOT11_SSID raw = attributes.wlanAssociationAttributes.dot11Ssid;
                        ssid = Encoding.UTF8.GetString(raw.ucSSID, 0, (int)Math.Min(raw.uSSIDLength, 32));
                        signal = (int)attributes.wlanAssociationAttributes.wlanSignalQuality;
                        return true;
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }

                return false;
            }
            catch (Exception)
            {
                _wlanUnavailable = true;
                return false;
            }
            finally
            {
                if (list != IntPtr.Zero)
                {
                    WlanFreeMemory(list);
                }
            }
        }

        private const int WLAN_INTERFACE_STATE_CONNECTED = 1;
        private const int WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
            public int isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public int dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
            public int dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_ATTRIBUTES
        {
            public int isState;
            public int wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            // wlanSecurityAttributes follows; not needed
        }

        [DllImport("wlanapi.dll")]
        private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr handle);

        [DllImport("wlanapi.dll")]
        private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll")]
        private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
            out int dataSize, out IntPtr data, out int opcodeValueType);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        #endregion
    }
}
