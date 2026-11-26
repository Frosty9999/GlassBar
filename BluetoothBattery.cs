using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBar
{
    /// <summary>
    /// Battery levels of Bluetooth devices (headphones, controllers, mice...), as shown in Windows' Bluetooth
    /// settings. Windows stores the last reported level on the device as the "Bluetooth battery" property;
    /// this reads it from every Bluetooth device the PC knows about.
    /// </summary>
    internal static class BluetoothBattery
    {
        public sealed class Device
        {
            public string Name;
            public int Percent;
        }

        public static List<Device> Read()
        {
            List<Device> result = new List<Device>();

            // Classic Bluetooth (headphones, keyboards) and Bluetooth LE (controllers, mice) devices.
            foreach (string enumerator in new[] { "BTHENUM", "BTHLE" })
            {
                foreach (string id in DeviceIds(enumerator))
                {
                    if (CM_Locate_DevNode(out uint node, id, 0) != CR_SUCCESS)
                    {
                        continue;
                    }

                    DEVPROPKEY batteryKey = DEVPKEY_Bluetooth_Battery;
                    byte[] value = GetProperty(node, ref batteryKey);
                    if (value == null || value.Length < 1 || value[0] > 100)
                    {
                        continue;
                    }

                    DEVPROPKEY nameKey = DEVPKEY_Device_FriendlyName;
                    byte[] nameBytes = GetProperty(node, ref nameKey);
                    string name = nameBytes != null ? Encoding.Unicode.GetString(nameBytes).TrimEnd('\0') : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Bluetooth device";
                    }

                    result.Add(new Device { Name = name, Percent = value[0] });
                }
            }

            // A device can appear under both enumerators; show it once.
            return result.GroupBy(d => d.Name).Select(g => g.First()).OrderBy(d => d.Name).ToList();
        }

        /// <summary>Instance IDs of the present devices created by one enumerator.</summary>
        private static IEnumerable<string> DeviceIds(string enumerator)
        {
            const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_Size(out uint size, enumerator, flags) != CR_SUCCESS || size == 0)
            {
                return Enumerable.Empty<string>();
            }

            char[] buffer = new char[size];
            if (CM_Get_Device_ID_List(enumerator, buffer, size, flags) != CR_SUCCESS)
            {
                return Enumerable.Empty<string>();
            }

            // A list of null-terminated strings, ending with an extra null.
            return new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static byte[] GetProperty(uint node, ref DEVPROPKEY key)
        {
            uint size = 0;
            int status = CM_Get_DevNode_Property(node, ref key, out _, null, ref size, 0);
            if (status != CR_BUFFER_SMALL || size == 0)
            {
                return null;
            }

            byte[] buffer = new byte[size];
            return CM_Get_DevNode_Property(node, ref key, out _, buffer, ref size, 0) == CR_SUCCESS ? buffer : null;
        }

        #region Interop

        private const int CR_SUCCESS = 0;
        private const int CR_BUFFER_SMALL = 0x1A;
        private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery = new DEVPROPKEY
        {
            fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
            pid = 2
        };

        private static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new DEVPROPKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_Size(out uint size, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNode(out uint node, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Property(uint node, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

        #endregion
    }
}
