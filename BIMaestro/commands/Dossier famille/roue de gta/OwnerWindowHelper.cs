using Autodesk.Revit.UI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BIMaestro
{
    internal static class OwnerWindowHelper
    {
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        public static (int X, int Y) GetCursorPosPx()
        {
            return GetCursorPos(out POINT p) ? (p.X, p.Y) : (0, 0);
        }
    }
}
