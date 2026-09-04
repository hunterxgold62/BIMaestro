using Autodesk.Revit.UI;
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BIMaestro.UI
{
    internal static class RadialGlobalHotkeyService
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x424D;
        private const uint ModNoRepeat = 0x4000;
        private static bool _listening;
        private static bool _registered;
        private static bool _pending;

        public static void Initialize()
        {
            if (_listening) return;
            ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
            _listening = true;
            ApplySavedPreference(out _);
        }

        public static void Shutdown()
        {
            if (_registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = false;
            _pending = false;
            if (_listening) ComponentDispatcher.ThreadPreprocessMessage -= OnThreadMessage;
            _listening = false;
        }

        public static bool ApplySavedPreference(out string error) =>
            TryRegister(RadialButtonsPreferencesManager.Load().Hotkey, out error);

        public static bool TryRegister(RadialHotkeyPreference hotkey, out string error)
        {
            error = null;
            if (_registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = false;
            if (hotkey == null) return true;
            if (hotkey.Modifiers == 0 || hotkey.VirtualKey <= 0)
            {
                error = "Le raccourci doit contenir Ctrl, Alt, Maj ou Windows.";
                return false;
            }
            _registered = RegisterHotKey(IntPtr.Zero, HotkeyId,
                (uint)hotkey.Modifiers | ModNoRepeat, (uint)hotkey.VirtualKey);
            if (!_registered) error = "Ce raccourci est déjà utilisé par Windows ou une autre application.";
            return _registered;
        }

        public static void ProcessPending(UIApplication uiApplication)
        {
            if (!_pending || uiApplication == null) return;
            _pending = false;
            IntPtr foreground = GetForegroundWindow();
            IntPtr main = uiApplication.MainWindowHandle;
            if (foreground != main && !IsChild(main, foreground)) return;
            RadialButtonsService.Show(uiApplication);
        }

        private static void OnThreadMessage(ref MSG msg, ref bool handled)
        {
            if (msg.message != WmHotkey || msg.wParam.ToInt32() != HotkeyId) return;
            _pending = true;
            handled = true;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    }
}
