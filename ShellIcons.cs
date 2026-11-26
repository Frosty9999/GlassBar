using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBar
{
    /// <summary>
    /// Loads the real icon of a program or shortcut through the Windows shell (IShellItemImageFactory),
    /// the same way File Explorer gets them. Must run on an STA thread (ManagedShell's IconScheduler is one).
    /// Returns null if no icon could be read, so the caller can fall back to the window's own icon.
    /// </summary>
    internal static class ShellIcons
    {
        public static ImageSource Load(string path, int size)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory) != 0 || factory == null)
                {
                    return null;
                }

                try
                {
                    if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hBitmap) != 0)
                    {
                        return null;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }

                return ToBitmapSource(hBitmap);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
            }
        }

        /// <summary>Copies a 32-bit shell bitmap into a WPF image, keeping its transparency.</summary>
        private static BitmapSource ToBitmapSource(IntPtr hBitmap)
        {
            if (GetObject(hBitmap, Marshal.SizeOf(typeof(BITMAP)), out BITMAP info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0)
            {
                return null;
            }

            int width = info.bmWidth;
            int height = info.bmHeight;
            byte[] pixels = new byte[width * height * 4];

            BITMAPINFOHEADER header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth = width,
                biHeight = -height, // negative = rows top to bottom
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0   // BI_RGB
            };

            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, hBitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                {
                    return null;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }

            // Old 24-bit icons come back with every alpha byte at zero: treat those as fully opaque.
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    anyAlpha = true;
                    break;
                }
            }

            if (!anyAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }

        #region Interop

        private const int SIIGBF_BIGGERSIZEOK = 0x01;
        private const int SIIGBF_ICONONLY = 0x04;

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path, IntPtr bindContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hObject, int size, out BITMAP bitmap);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr dc, IntPtr hBitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        #endregion
    }
}
