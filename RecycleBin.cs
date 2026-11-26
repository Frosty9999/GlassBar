using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBar
{
    /// <summary>The Windows Recycle Bin: whether it has anything in it, its icons, opening, emptying, and recycling files.</summary>
    internal static class RecycleBin
    {
        /// <summary>True when any drive's Recycle Bin holds something.</summary>
        public static bool HasItems
        {
            get
            {
                SHQUERYRBINFO info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                return SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
            }
        }

        private static ImageSource _emptyIcon;
        private static ImageSource _fullIcon;

        /// <summary>Windows' own empty or full bin icon.</summary>
        public static ImageSource Icon(bool full)
        {
            if (full)
            {
                return _fullIcon ??= StockIcon(SIID_RECYCLERFULL);
            }

            return _emptyIcon ??= StockIcon(SIID_RECYCLER);
        }

        public static void Open()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do.
            }
        }

        /// <summary>Empties the bin. Windows asks for confirmation first and shows its usual progress.</summary>
        public static void Empty()
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, 0);
        }

        /// <summary>Moves files and folders to the Recycle Bin (they can be restored from there).</summary>
        public static void Recycle(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            SHFILEOPSTRUCT operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", paths) + "\0\0", // the list ends with two nulls
                fFlags = FOF_ALLOWUNDO
            };
            SHFileOperation(ref operation);
        }

        private static ImageSource StockIcon(uint id)
        {
            SHSTOCKICONINFO info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            if (SHGetStockIconInfo(id, SHGSI_ICONLOCATION, ref info) != 0)
            {
                return null;
            }

            // Pull a large version of the icon from its file, so it stays sharp at dock sizes.
            const int size = 96;
            if (SHDefExtractIcon(info.szPath, info.iIcon, 0, out IntPtr large, out IntPtr small, (uint)(size | (16 << 16))) != 0)
            {
                return null;
            }

            try
            {
                if (large == IntPtr.Zero)
                {
                    return null;
                }

                BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                if (small != IntPtr.Zero)
                {
                    DestroyIcon(small);
                }
            }
        }

        #region Interop

        private const uint SIID_RECYCLER = 31;
        private const uint SIID_RECYCLERFULL = 32;
        private const uint SHGSI_ICONLOCATION = 0;
        private const uint FO_DELETE = 3;
        private const ushort FOF_ALLOWUNDO = 0x40;

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

        [DllImport("shell32.dll")]
        private static extern int SHGetStockIconInfo(uint id, uint flags, ref SHSTOCKICONINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        #endregion
    }
}
