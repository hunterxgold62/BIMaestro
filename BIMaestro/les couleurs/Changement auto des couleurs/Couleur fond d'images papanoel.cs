using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MyRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class PapanoelCommand : IExternalCommand
    {
        private static Random _rnd = new Random();
        private static bool _isRunning = false; // Contrôle d'exécution
        private static Task _colorChangeTask;
        private static CancellationTokenSource _cts;
        private static int _clickCount = 0;
        private static readonly int DoubleClickThreshold = 300; // Temps pour le double-clic en ms
        private static DateTime _lastClickTime = DateTime.MinValue;

        private static readonly List<string> _targetKeywords = new List<string>
        {
            "Outils de Visualisation",
            "Modification",
            "Outils IA",
            "Couleur du projet",
            "Panneaux réservés au test",
            "Analyse",
            "Spécifique aux familles"
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            IntPtr mainWindowHandle = uiapp.MainWindowHandle;

            HwndSource hwndSource = HwndSource.FromHwnd(mainWindowHandle);
            Window mainWindow = hwndSource?.RootVisual as Window;
            if (mainWindow == null)
                return Result.Failed;

            // Gérer clic simple ou double-clic
            DateTime currentTime = DateTime.Now;
            _clickCount++;

            if ((currentTime - _lastClickTime).TotalMilliseconds <= DoubleClickThreshold && _clickCount >= 2)
            {
                // Double-clic détecté : on arrête la boucle et on réinitialise
                _clickCount = 0;
                _isRunning = false;
                _cts?.Cancel();
                ResetTargetedBorders(mainWindow);
                return Result.Succeeded;
            }

            _lastClickTime = currentTime;

            // Clic simple : démarrer/arrêter les couleurs
            Task.Delay(DoubleClickThreshold).ContinueWith(_ =>
            {
                if (_clickCount == 1)
                {
                    _clickCount = 0; // Réinitialiser

                    _isRunning = !_isRunning;

                    if (_isRunning)
                    {
                        // Démarrer le changement des couleurs avec annulation
                        _cts = new CancellationTokenSource();
                        _colorChangeTask = Task.Run(() => ChangeColorsLoop(mainWindow, _cts.Token));
                    }
                    else
                    {
                        // Arrêt demandé
                        _cts?.Cancel();
                    }
                }
            });

            return Result.Succeeded;
        }

        private void ChangeColorsLoop(Window mainWindow, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(500); // Pause de 0.5 seconde

                mainWindow.Dispatcher.Invoke(() =>
                {
                    UpdatePanelColors(mainWindow);
                });
            }
        }

        private void UpdatePanelColors(Window mainWindow)
        {
            var borders = FindChildrenByType<Border>(mainWindow);

            var panelsWithColors = new Dictionary<string, SolidColorBrush>();
            foreach (var keyword in _targetKeywords)
            {
                panelsWithColors[keyword] = GenerateRandomPastelBrush();
            }

            foreach (var border in borders)
            {
                if (border.DataContext != null)
                {
                    var dc = border.DataContext;
                    var cookieProp = dc.GetType().GetProperty("Cookie", BindingFlags.Public | BindingFlags.Instance);
                    if (cookieProp != null)
                    {
                        var cookieValue = cookieProp.GetValue(dc);
                        if (cookieValue != null)
                        {
                            string cookieStr = cookieValue.ToString();
                            foreach (var kvp in panelsWithColors)
                            {
                                if (cookieStr.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    border.ClearValue(Border.BackgroundProperty);
                                    border.Background = kvp.Value;

                                    // Définir BorderBrush légèrement plus sombre
                                    border.BorderBrush = GenerateDarkerBrush(kvp.Value);
                                    border.BorderThickness = new Thickness(1); // Définir l'épaisseur
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ResetTargetedBorders(Window mainWindow)
        {
            // S'assurer que la boucle de changement de couleur est arrêtée
            _isRunning = false;
            _cts?.Cancel();
            var borders = FindChildrenByType<Border>(mainWindow);

            foreach (var border in borders)
            {
                if (border.DataContext != null)
                {
                    var dc = border.DataContext;
                    var cookieProp = dc.GetType().GetProperty("Cookie", BindingFlags.Public | BindingFlags.Instance);
                    if (cookieProp != null)
                    {
                        var cookieValue = cookieProp.GetValue(dc);
                        if (cookieValue != null)
                        {
                            string cookieStr = cookieValue.ToString();

                            foreach (var keyword in _targetKeywords)
                            {
                                if (cookieStr.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    // Réinitialiser Background et BorderBrush
                                    border.ClearValue(Border.BackgroundProperty);
                                    border.ClearValue(Border.BorderBrushProperty);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private SolidColorBrush GenerateRandomPastelBrush()
        {
            byte r = (byte)(200 + _rnd.Next(56));
            byte g = (byte)(200 + _rnd.Next(56));
            byte b = (byte)(200 + _rnd.Next(56));
            System.Windows.Media.Color color = System.Windows.Media.Color.FromRgb(r, g, b);
            return new SolidColorBrush(color);
        }

        private SolidColorBrush GenerateDarkerBrush(SolidColorBrush baseBrush)
        {
            System.Windows.Media.Color color = baseBrush.Color;
            byte darkenFactor = 20;

            byte r = (byte)Math.Max(color.R - darkenFactor, 0);
            byte g = (byte)Math.Max(color.G - darkenFactor, 0);
            byte b = (byte)Math.Max(color.B - darkenFactor, 0);

            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        }

        private List<T> FindChildrenByType<T>(DependencyObject parent) where T : DependencyObject
        {
            var found = new List<T>();
            if (parent == null) return found;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    found.Add(typedChild);
                }
                found.AddRange(FindChildrenByType<T>(child));
            }

            return found;
        }
    }
}
