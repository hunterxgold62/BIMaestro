using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace IA
{
    public static class ScreenCapture
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static Bitmap CaptureWindow(System.Drawing.Rectangle captureRegion)
        {
            Bitmap bmp = new Bitmap(captureRegion.Width, captureRegion.Height, PixelFormat.Format32bppArgb);
            using (Graphics gfx = Graphics.FromImage(bmp))
            {
                gfx.CopyFromScreen(captureRegion.Left, captureRegion.Top, 0, 0, new Size(captureRegion.Width, captureRegion.Height), CopyPixelOperation.SourceCopy);
            }

            return bmp;
        }

        public static string CaptureAndSaveImage(string savePath, System.Drawing.Rectangle captureRegion)
        {
            Bitmap bmp = null;
            try
            {
                bmp = CaptureWindow(captureRegion);
                string directoryPath = Path.GetDirectoryName(savePath);
                Directory.CreateDirectory(directoryPath); // Create directory if it doesn't exist
                bmp.Save(savePath, ImageFormat.Png);

                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    byte[] imageBytes = ms.ToArray();
                    return Convert.ToBase64String(imageBytes);
                }
            }
            finally
            {
                if (bmp != null)
                {
                    bmp.Dispose();
                }
            }
        }
    }
}