using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GlassBar
{
    /// <summary>
    /// GPU usage the way Task Manager shows it. Windows reports a "GPU Engine" counter for every app on every
    /// engine (3D, copy, video decode...); the usage of an engine is the sum over apps, and the GPU's usage is its
    /// busiest engine. Read through PDH (the performance counter API) with a wildcard, so apps starting and
    /// stopping are picked up automatically. Only queried while the GPU readout is on.
    /// </summary>
    internal sealed class GpuUsage : IDisposable
    {
        private IntPtr _query;
        private IntPtr _counter;
        private bool _failed;

        /// <summary>0-100, or null when it can't be read (no counters, or the first sample).</summary>
        public double? Sample()
        {
            if (_failed)
            {
                return null;
            }

            if (_query == IntPtr.Zero && !Open())
            {
                _failed = true;
                return null;
            }

            if (PdhCollectQueryData(_query) != 0)
            {
                return null;
            }

            // Ask for the buffer size first, then read every instance.
            int size = 0;
            int status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out int count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size <= 0)
            {
                return null; // e.g. the very first collection: utilization needs two samples
            }

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0)
                {
                    return null;
                }

                Dictionary<string, double> perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                int itemSize = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM_DOUBLE));
                for (int i = 0; i < count; i++)
                {
                    PDH_FMT_COUNTERVALUE_ITEM_DOUBLE item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(buffer + i * itemSize);
                    if (item.FmtValue.CStatus != 0)
                    {
                        continue;
                    }

                    string engine = EngineOf(Marshal.PtrToStringUni(item.szName));
                    perEngine.TryGetValue(engine, out double sum);
                    perEngine[engine] = sum + item.FmtValue.doubleValue;
                }

                double busiest = 0;
                foreach (double value in perEngine.Values)
                {
                    busiest = Math.Max(busiest, value);
                }

                return Math.Min(100, busiest);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// "pid_1234_luid_0x0_0xd377_phys_0_eng_0_engtype_3D" -> "luid_0x0_0xd377_phys_0_eng_0":
        /// the engine without the app, so all apps on one engine add up.
        /// </summary>
        private static string EngineOf(string instance)
        {
            if (string.IsNullOrEmpty(instance))
            {
                return "";
            }

            int start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int end = instance.IndexOf("_engtype", StringComparison.OrdinalIgnoreCase);
            return start >= 0 && end > start ? instance.Substring(start, end - start) : instance;
        }

        private bool Open()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            {
                _query = IntPtr.Zero;
                return false;
            }

            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                return false;
            }

            PdhCollectQueryData(_query); // first sample; values come from the next one
            return true;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }

            _failed = false;
        }

        #region Interop

        private const int PDH_FMT_DOUBLE = 0x00000200;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public int CStatus;
            public double doubleValue; // union; aligned to 8, so 4 bytes of padding come before it
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArray(IntPtr counter, int format, ref int bufferSize, out int itemCount, IntPtr items);

        [DllImport("pdh.dll")]
        private static extern int PdhCloseQuery(IntPtr query);

        #endregion
    }
}
