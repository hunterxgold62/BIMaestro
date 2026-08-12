using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Analyse
{
    /// <summary>
    /// Affiche une seule phrase issue du dernier événement connu. Aucun scan de
    /// fichier ni calcul géométrique n'est effectué au moment de l'affichage.
    /// </summary>
    internal static class ElementHistoryHoverInfoService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr windowHandle);

        private static Popup _popup;
        private static TextBlock _text;
        private static DispatcherTimer _hideTimer;
        private static NativePoint _cursorWhenShown;
        private static DateTime _hideAtUtc;
        private static bool _isListeningForActivity;

        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int WmMouseMove = 0x0200;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmRightButtonDown = 0x0204;
        private const int WmMiddleButtonDown = 0x0207;
        private const int WmMouseWheel = 0x020A;
        private const int WmXButtonDown = 0x020B;
        private const int WmMouseHorizontalWheel = 0x020E;

        /// <summary>
        /// Cette option reste volontairement désactivée à chaque démarrage de
        /// Revit. L'utilisateur l'active uniquement pour la session courante.
        /// </summary>
        internal static bool IsEnabled { get; private set; }

        internal static bool Toggle(Document document)
        {
            IsEnabled = !IsEnabled;
            if (IsEnabled)
            {
                LatestElementHistoryIndex.ScheduleBackgroundLoad(document);
            }
            else
            {
                Hide();
            }

            return IsEnabled;
        }

        internal static void OnDocumentOpened(Document document)
        {
            if (IsEnabled)
                LatestElementHistoryIndex.ScheduleBackgroundLoad(document);
        }

        internal static void OnSelectionChanged(
            Document document,
            ICollection<ElementId> selectedIds)
        {
            Hide();
            if (!IsEnabled || document == null || selectedIds == null ||
                selectedIds.Count != 1)
            {
                return;
            }

            ElementId id = null;
            foreach (ElementId selectedId in selectedIds)
            {
                id = selectedId;
                break;
            }

            Element element = id == null ? null : document.GetElement(id);
            if (element == null ||
                !LatestElementHistoryIndex.TryGetLatest(
                    document,
                    element,
                    out ElementHistoryEvent historyEvent))
            {
                return;
            }

            string currentRevitUser = string.Empty;
            try
            {
                currentRevitUser = document.Application.Username;
            }
            catch { }

            string phrase = FormatPhrase(
                historyEvent,
                currentRevitUser,
                Environment.UserName,
                DateTime.Now);
            if (string.IsNullOrWhiteSpace(phrase)) return;

            Show(phrase);
        }

        internal static string FormatPhrase(
            ElementHistoryEvent historyEvent,
            string currentRevitUser,
            string currentWindowsUser,
            DateTime nowLocal)
        {
            if (historyEvent == null) return string.Empty;

            string actor = historyEvent.User ?? string.Empty;
            bool isCurrentUser = SameUser(actor, currentRevitUser) ||
                                 SameUser(actor, currentWindowsUser);
            string subject = isCurrentUser
                ? "Vous avez"
                : FriendlyUserName(actor) + " a";
            if (string.IsNullOrWhiteSpace(actor) && !isCurrentUser)
                subject = "Un utilisateur a";

            string action;
            switch ((historyEvent.Action ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant())
            {
                case "move":
                    action = "déplacé cet élément";
                    break;
                case "create":
                    action = "créé cet élément";
                    break;
                case "type_change":
                    action = "changé le type de cet élément";
                    break;
                default:
                    action = "modifié cet élément";
                    break;
            }

            DateTime localTimestamp = historyEvent.Ts.Kind == DateTimeKind.Utc
                ? historyEvent.Ts.ToLocalTime()
                : historyEvent.Ts;
            return subject + " " + action + " " +
                   FormatRelativeDate(localTimestamp, nowLocal) + ".";
        }

        internal static void Hide()
        {
            StopListeningForActivity();
            if (_hideTimer != null)
            {
                _hideTimer.Stop();
                _hideTimer = null;
            }

            if (_popup != null)
                _popup.IsOpen = false;
        }

        private static void Show(string phrase)
        {
            EnsurePopup();
            _text.Text = phrase;

            NativePoint cursor;
            if (!GetCursorPos(out cursor))
                cursor = new NativePoint { X = 80, Y = 80 };
            _cursorWhenShown = cursor;

            double dpiScale = GetCursorDpiScale(cursor);
            _popup.HorizontalOffset = cursor.X / dpiScale + 18;
            _popup.VerticalOffset = cursor.Y / dpiScale + 18;
            _popup.IsOpen = true;

            _hideTimer?.Stop();
            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _hideAtUtc = DateTime.UtcNow.AddSeconds(4);
            _hideTimer.Tick += (_, __) => CheckForMovementOrTimeout();
            _hideTimer.Start();
            StartListeningForActivity();
        }

        private static void StartListeningForActivity()
        {
            if (_isListeningForActivity) return;
            ComponentDispatcher.ThreadPreprocessMessage +=
                OnThreadPreprocessMessage;
            _isListeningForActivity = true;
        }

        private static void StopListeningForActivity()
        {
            if (!_isListeningForActivity) return;
            ComponentDispatcher.ThreadPreprocessMessage -=
                OnThreadPreprocessMessage;
            _isListeningForActivity = false;
        }

        private static void OnThreadPreprocessMessage(
            ref MSG message,
            ref bool handled)
        {
            int messageId = message.message;
            if (messageId == WmKeyDown ||
                messageId == WmSysKeyDown ||
                messageId == WmMouseMove ||
                messageId == WmLeftButtonDown ||
                messageId == WmRightButtonDown ||
                messageId == WmMiddleButtonDown ||
                messageId == WmMouseWheel ||
                messageId == WmXButtonDown ||
                messageId == WmMouseHorizontalWheel)
            {
                Hide();
            }
        }

        private static void CheckForMovementOrTimeout()
        {
            if (DateTime.UtcNow >= _hideAtUtc)
            {
                Hide();
                return;
            }

            if (!GetCursorPos(out NativePoint cursor)) return;
            int deltaX = cursor.X - _cursorWhenShown.X;
            int deltaY = cursor.Y - _cursorWhenShown.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) >= 16)
                Hide();
        }

        private static double GetCursorDpiScale(NativePoint cursor)
        {
            try
            {
                IntPtr window = WindowFromPoint(cursor);
                uint dpi = window == IntPtr.Zero
                    ? 96u
                    : GetDpiForWindow(window);
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            catch
            {
                // GetDpiForWindow n'existe pas sur les anciennes versions
                // de Windows. Revit 2024/2025 reste alors en coordonnées 100 %.
                return 1.0;
            }
        }

        private static void EnsurePopup()
        {
            if (_popup != null) return;

            _text = new TextBlock
            {
                Foreground = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(32, 32, 32)),
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380
            };
            var border = new Border
            {
                Background = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(250, 250, 250)),
                BorderBrush = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(176, 176, 176)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(9, 6, 9, 6),
                Child = _text,
                IsHitTestVisible = false
            };
            _popup = new Popup
            {
                Placement = PlacementMode.AbsolutePoint,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = border
            };
        }

        private static bool SameUser(string left, string right)
        {
            string a = NormalizeUser(left);
            string b = NormalizeUser(right);
            return a.Length > 0 && b.Length > 0 &&
                   string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUser(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            int separator = normalized.LastIndexOf('\\');
            if (separator >= 0 && separator + 1 < normalized.Length)
                normalized = normalized.Substring(separator + 1);
            return normalized;
        }

        private static string FriendlyUserName(string value)
        {
            string normalized = NormalizeUser(value);
            return string.IsNullOrWhiteSpace(normalized)
                ? "Un utilisateur"
                : normalized;
        }

        private static string FormatRelativeDate(
            DateTime timestamp,
            DateTime now)
        {
            string time = timestamp.ToString("HH 'h' mm", CultureInfo.GetCultureInfo("fr-FR"));
            DateTime day = timestamp.Date;
            DateTime today = now.Date;
            if (day == today)
                return "aujourd’hui à " + time;
            if (day == today.AddDays(-1))
                return "hier à " + time;

            double days = (today - day).TotalDays;
            if (days > 1 && days < 7)
            {
                return timestamp.ToString(
                           "dddd 'à' HH 'h' mm",
                           CultureInfo.GetCultureInfo("fr-FR"));
            }

            return timestamp.ToString(
                "'le' d MMMM yyyy 'à' HH 'h' mm",
                CultureInfo.GetCultureInfo("fr-FR"));
        }
    }
}
