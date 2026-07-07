using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Page
{
    internal static class SecretGifShortcutManager
    {
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int VkP = 0x50;
        private const int VkL = 0x4C;
        private const int VkF = 0x46;
        private const int VkA = 0x41;
        private const int VkC = 0x43;
        private const int VkO = 0x4F;
        private const int VkM = 0x4D;
        private const int VkI = 0x49;
        private const int VkControl = 0x11;
        private const int VkShift = 0x10;

        private static readonly TimeSpan SequenceTimeout = TimeSpan.FromSeconds(2);
        private static bool _isInitialized;
        private static readonly ShortcutState CelebrationShortcut = new ShortcutState(VkP, VkL, () => ShowEffect(SecretEffectKind.Celebration));
        private static readonly ShortcutState FireworksShortcut = new ShortcutState(VkF, VkA, () => ShowEffect(SecretEffectKind.Fireworks));
        private static readonly ShortcutState ConfettiShortcut = new ShortcutState(VkC, VkO, () => ShowEffect(SecretEffectKind.Confetti));
        private static readonly ShortcutState CharacterShortcut = new ShortcutState(VkM, VkI, () => ShowEffect(SecretEffectKind.Character));
        private static SecretGifWindow _window;

        public static void Initialize()
        {
            if (_isInitialized) return;

            ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
            _isInitialized = true;
        }

        public static void Shutdown()
        {
            if (!_isInitialized) return;

            ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
            _isInitialized = false;
            ResetShortcuts();
        }

        public static void PollKeyboardState()
        {
            if (!_isInitialized) return;

            bool ctrlShiftDown = IsCtrlShiftDown();
            CelebrationShortcut.Poll(ctrlShiftDown);
            FireworksShortcut.Poll(ctrlShiftDown);
            ConfettiShortcut.Poll(ctrlShiftDown);
            CharacterShortcut.Poll(ctrlShiftDown);

            if (!ctrlShiftDown)
                ResetShortcuts();
        }

        private static void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (handled || (msg.message != WmKeyDown && msg.message != WmSysKeyDown))
                return;

            if (!IsCtrlShiftDown())
            {
                ResetShortcuts();
                return;
            }

            int key = msg.wParam.ToInt32();
            if (CelebrationShortcut.ProcessKey(key)
                || FireworksShortcut.ProcessKey(key)
                || ConfettiShortcut.ProcessKey(key)
                || CharacterShortcut.ProcessKey(key))
            {
                handled = true;
            }
        }

        private static bool IsCtrlShiftDown()
        {
            return IsKeyDown(VkControl) && IsKeyDown(VkShift);
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static void ShowSecretGif()
        {
            try
            {
                if (_window != null)
                {
                    if (!_window.IsVisible)
                        _window.Show();

                    _window.Activate();
                    _window.RestartAnimation();
                    return;
                }

                _window = new SecretGifWindow();
                _window.Closed += (s, e) => _window = null;
                _window.Show();
            }
            catch
            {
                _window = null;
            }
        }

        private static void ShowEffect(SecretEffectKind kind)
        {
            try
            {
                var effectWindow = new SecretEffectWindow(kind);
                effectWindow.Show();
            }
            catch
            {
            }
        }

        private static void ResetShortcuts()
        {
            CelebrationShortcut.Reset();
            FireworksShortcut.Reset();
            ConfettiShortcut.Reset();
            CharacterShortcut.Reset();
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private sealed class ShortcutState
        {
            private readonly int _firstKey;
            private readonly int _secondKey;
            private readonly Action _action;
            private DateTime _firstPressedAtUtc = DateTime.MinValue;
            private bool _wasChordDown;

            public ShortcutState(int firstKey, int secondKey, Action action)
            {
                _firstKey = firstKey;
                _secondKey = secondKey;
                _action = action;
            }

            public void Poll(bool ctrlShiftDown)
            {
                bool isChordDown = ctrlShiftDown && IsKeyDown(_firstKey) && IsKeyDown(_secondKey);
                if (isChordDown && !_wasChordDown)
                {
                    Reset();
                    _action();
                }

                _wasChordDown = isChordDown;
            }

            public bool ProcessKey(int key)
            {
                if (key == _firstKey)
                {
                    _firstPressedAtUtc = DateTime.UtcNow;
                    return false;
                }

                if (key == _secondKey && DateTime.UtcNow - _firstPressedAtUtc <= SequenceTimeout)
                {
                    Reset();
                    _action();
                    return true;
                }

                return false;
            }

            public void Reset()
            {
                _firstPressedAtUtc = DateTime.MinValue;
                _wasChordDown = false;
            }
        }
    }
}
