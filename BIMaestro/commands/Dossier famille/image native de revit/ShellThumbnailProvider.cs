using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Famille
{
    /// <summary>
    /// Récupère la vignette Windows d'un fichier via le Shell (COM).
    /// Ne lance pas Revit, très rapide. Nécessite que le handler Revit soit installé sur la machine.
    /// </summary>
    public static class ShellThumbnailProvider
    {
        [Flags]
        private enum SIIGBF : uint
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [ComImport]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        public static bool TryGetThumbnail(string path, int size, out BitmapSource bmp)
        {
            BitmapSource localBmp = null;
            Exception error = null;
            var done = new AutoResetEvent(false);

            var t = new Thread(() =>
            {
                try
                {
                    var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
                    SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);

                    var sz = new SIZE { cx = size, cy = size };
                    factory.GetImage(sz,
                        SIIGBF.BIGGERSIZEOK | SIIGBF.RESIZETOFIT | SIIGBF.THUMBNAILONLY,
                        out var hbm);

                    if (hbm != IntPtr.Zero)
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(
                            hbm, IntPtr.Zero, Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(size, size));

                        DeleteObject(hbm);

                        if (src != null)
                        {
                            src.Freeze();
                            localBmp = src;
                        }
                    }
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            done.WaitOne();

            bmp = localBmp;
            return bmp != null && error == null;
        }
    }
}
