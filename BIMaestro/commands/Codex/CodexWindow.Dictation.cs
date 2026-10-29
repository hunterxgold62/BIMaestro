using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using BIMaestro.Localization;

namespace BIMaestro.Codex
{
    internal sealed partial class CodexWindow
    {
        private void StartDictation()
        {
            if (closed || !dictate.IsEnabled) return;
            Activate();
            input.Focus();
            // Return focus to the editor before Windows receives its shortcut.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (closed || !IsActive || !dictate.IsEnabled || !input.IsKeyboardFocused) return;
                if (Keyboard.Modifiers != ModifierKeys.None)
                {
                    status.Text = UiLanguage.T("Relâchez les touches du clavier, puis cliquez sur Micro ou appuyez sur Win+H dans le champ du message.", "Release the keyboard keys, then click Mic or press Win+H in the message field.");
                    return;
                }
                try
                {
                    var keys = new[] { DictationKey(0x5B, 0), DictationKey(0x48, 0), DictationKey(0x48, 2), DictationKey(0x5B, 2) };
                    uint inserted = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(DictationInput)));
                    if (inserted != keys.Length)
                    {
                        // Release keys even if Windows accepted only part of the sequence.
                        var release = new[] { DictationKey(0x48, 2), DictationKey(0x5B, 2) };
                        SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(DictationInput)));
                        throw new Win32Exception(UiLanguage.T("Impossible d'ouvrir la dictée. Appuyez sur Win+H dans le champ du message.", "Could not open dictation. Press Win+H in the message field."));
                    }
                    status.Text = UiLanguage.T("Dictée Windows : utilisez son bouton micro pour démarrer ou arrêter. Relisez le texte, puis cliquez sur Envoyer. Si nécessaire, autorisez le micro et la reconnaissance vocale dans Windows.", "Windows dictation: use its microphone button to start or stop. Review the text, then click Send. If needed, allow microphone access and speech recognition in Windows.");
                }
                catch (Exception ex) { Error(ex); }
            }));
        }

        private static DictationInput DictationKey(ushort key, uint flags) => new DictationInput
        {
            Type = 1,
            Data = new DictationInputUnion { Keyboard = new DictationKeyboard { VirtualKey = key, Flags = flags } }
        };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, DictationInput[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct DictationInput { public uint Type; public DictationInputUnion Data; }

        // Include MOUSEINPUT so the union has the native INPUT size on both architectures.
        [StructLayout(LayoutKind.Explicit)]
        private struct DictationInputUnion
        {
            [FieldOffset(0)] public DictationKeyboard Keyboard;
            [FieldOffset(0)] public DictationMouse Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DictationKeyboard
        {
            public ushort VirtualKey, ScanCode;
            public uint Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DictationMouse
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
    }
}
