using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BIMaestro.PropertiesAppearance
{
    // Detects an active text edit in Revit (ribbon, palette, dialog) so that
    // background work does not interrupt the user's typing.
    internal static class TextInputFocus
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            internal uint Size, Flags;
            internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            internal int Left, Top, Right, Bottom;
        }
        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        private static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;

        internal static bool IsActive()
        {
            var foreground = GetForegroundWindow();
            uint thread = GetWindowThreadProcessId(foreground, out var process);
            if (foreground == IntPtr.Zero || process != ProcessId) return false;
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info)) return true;
            if (info.Caret != IntPtr.Zero) return true;
            var name = new System.Text.StringBuilder(256);
            GetClassName(info.Focus, name, name.Capacity);
            if (name.ToString().IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            return focused is System.Windows.Controls.Primitives.TextBoxBase
                || focused is System.Windows.Controls.PasswordBox
                || (focused is System.Windows.Controls.ComboBox combo && combo.IsEditable);
        }
    }
}
