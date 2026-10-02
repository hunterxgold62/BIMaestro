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
        private static HwndSource _messageWindow;
        private static ExternalEvent _showEvent;
        private static bool _registered;
        private static bool _pending;

        public static void Initialize()
        {
            if (_messageWindow != null) return;
            _showEvent = ExternalEvent.Create(new ShowRadialHandler());
            // A native message-only window receives WM_HOTKEY even when Revit
            // is dispatching messages outside the WPF message loop.
            _messageWindow = new HwndSource(new HwndSourceParameters("BIMaestro.RadialHotkey")
            {
                ParentWindow = new IntPtr(-3),
                WindowStyle = 0,
                Width = 0,
                Height = 0
            });
            _messageWindow.AddHook(OnWindowMessage);
            ApplySavedPreference(out _);
        }

        public static void Shutdown()
        {
            if (_registered) UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            _registered = false;
            _pending = false;
            _messageWindow?.RemoveHook(OnWindowMessage);
            _messageWindow?.Dispose();
            _messageWindow = null;
            _showEvent?.Dispose();
            _showEvent = null;
        }

        public static bool ApplySavedPreference(out string error) =>
            TryRegister(RadialButtonsPreferencesManager.Load().Hotkey, out error);

        public static bool TryRegister(RadialHotkeyPreference hotkey, out string error)
        {
            error = null;
            _pending = false;
            if (_messageWindow == null) Initialize();
            if (_registered) UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            _registered = false;
            if (hotkey == null) return true;
            if (hotkey.Modifiers == 0 || hotkey.VirtualKey <= 0)
            {
                error = "Le raccourci doit contenir Ctrl, Alt, Maj ou Windows.";
                return false;
            }
            _registered = RegisterHotKey(_messageWindow.Handle, HotkeyId,
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
            if (foreground != main && !IsChild(main, foreground) && GetAncestor(foreground, 3) != main) return;
            RadialButtonsService.Show(uiApplication);
        }

        private static IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;
            // Check focus at keypress time so a shortcut pressed in another app
            // cannot open the rosace after the user switches back to Revit.
            IntPtr main;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                main = process.MainWindowHandle;
            IntPtr foreground = GetForegroundWindow();
            if (main != IntPtr.Zero && (foreground == main || IsChild(main, foreground) || GetAncestor(foreground, 3) == main))
            {
                _pending = true;
                _showEvent?.Raise();
            }
            handled = true;
            return IntPtr.Zero;
        }

        private sealed class ShowRadialHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app) => ProcessPending(app);
            public string GetName() => "BIMaestro - Raccourci Rosace Boutons";
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    }
}
