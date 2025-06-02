using System;
using System.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace MyRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleCombinedColoringCommand : IExternalCommand
    {
        private const int DoubleClickThresholdMs = 300;
        private static bool _waitingForDoubleClick = false;
        private static Timer _singleClickTimer = null;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ColoringStateManager.LoadState();

                if (!_waitingForDoubleClick)
                {
                    // 1er clic => on attend un possible second clic
                    _waitingForDoubleClick = true;

                    _singleClickTimer = new Timer(SingleClickAction, commandData, DoubleClickThresholdMs, Timeout.Infinite);
                }
                else
                {
                    // 2e clic => double clic => switch mode
                    _waitingForDoubleClick = false;

                    if (_singleClickTimer != null)
                    {
                        _singleClickTimer.Dispose();
                        _singleClickTimer = null;
                    }

                    DoDoubleClick(commandData);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Se déclenche après DoubleClickThresholdMs si aucun second clic n'a eu lieu
        /// => on fait l'action simple clic (toggle on/off).
        /// </summary>
        private void SingleClickAction(object state)
        {
            _waitingForDoubleClick = false;

            if (state is ExternalCommandData cdata)
            {
                DoSingleClick(cdata);
            }
        }

        private void DoSingleClick(ExternalCommandData commandData)
        {
            try
            {
                // Toggle on/off
                ColoringStateManager.ToggleColoring();

                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                CombinedColoringApplication.ResetColorings(mainWindowHandle);
                PartialColoringHelper.ResetPartialColoring(mainWindowHandle);

                if (ColoringStateManager.IsColoringActive)
                {
                    CombinedColoringApplication.ApplyTabItemColoring(mainWindowHandle);
                    if (ColoringStateManager.IsFullMode)
                    {
                        CombinedColoringApplication.ApplyPapanoelColoring(mainWindowHandle);
                    }
                    else
                    {
                        PartialColoringHelper.ApplyPartialColoring(mainWindowHandle);
                    }
                }
            }
            catch
            {
                // Éviter de faire planter Revit
            }
        }

        private void DoDoubleClick(ExternalCommandData commandData)
        {
            try
            {
                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                // Switch de mode
                ColoringStateManager.SwitchMode();

                CombinedColoringApplication.ResetColorings(mainWindowHandle);
                PartialColoringHelper.ResetPartialColoring(mainWindowHandle);

                if (ColoringStateManager.IsColoringActive)
                {
                    CombinedColoringApplication.ApplyTabItemColoring(mainWindowHandle);
                    if (ColoringStateManager.IsFullMode)
                    {
                        CombinedColoringApplication.ApplyPapanoelColoring(mainWindowHandle);
                    }
                    else
                    {
                        PartialColoringHelper.ApplyPartialColoring(mainWindowHandle);
                    }
                }
            }
            catch
            {
                // Éviter de faire planter Revit
            }
        }
    }

    // -----------------------------------------------------------------------
    //   1) ColoringStateManager : on/off + full/partial
    // -----------------------------------------------------------------------
    public static class ColoringStateManager
    {
        private static readonly string persistenceFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "RevitLogs", "SauvegardePréférence", "resetColor.txt");

        public static bool IsColoringActive { get; private set; }
        public static bool IsFullMode { get; private set; }

        public static void LoadState()
        {
            try
            {
                EnsureDirectoryExists();

                if (File.Exists(persistenceFilePath))
                {
                    string state = File.ReadAllText(persistenceFilePath).Trim();
                    var parts = state.Split('-');
                    if (parts.Length == 2)
                    {
                        IsColoringActive = parts[0].Equals("Active", StringComparison.OrdinalIgnoreCase);
                        IsFullMode = parts[1].Equals("Full", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        // Valeurs par défaut si le fichier n'a pas le bon format
                        IsColoringActive = true;
                        IsFullMode = true;
                        SaveState();
                    }
                }
                else
                {
                    // Valeurs par défaut si le fichier n'existe pas
                    IsColoringActive = true;
                    IsFullMode = true;
                    SaveState();
                }
            }
            catch (Exception ex)
            {
                // Affiche une erreur et applique des valeurs par défaut
                TaskDialog.Show("Erreur de Chargement",
                    $"Impossible de charger l'état de coloration : {ex.Message}");
                IsColoringActive = true;
                IsFullMode = true;
            }
        }

        public static void SaveState()
        {
            try
            {
                EnsureDirectoryExists();

                string activePart = IsColoringActive ? "Active" : "Inactive";
                string modePart = IsFullMode ? "Full" : "Partial";
                File.WriteAllText(persistenceFilePath, $"{activePart}-{modePart}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur de Sauvegarde",
                    $"Impossible de sauvegarder l'état de coloration : {ex.Message}");
            }
        }

        public static void ToggleColoring()
        {
            IsColoringActive = !IsColoringActive;
            SaveState();
        }

        public static void SwitchMode()
        {
            IsFullMode = !IsFullMode;
            SaveState();
        }

        private static void EnsureDirectoryExists()
        {
            string directory = Path.GetDirectoryName(persistenceFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    // -----------------------------------------------------------------------
    //   2) Coloration Complète : TabItems + "Papanoel"
    // -----------------------------------------------------------------------
    public static class CombinedColoringApplication
    {
        // Dictionnaire pour stocker les couleurs "flash" attribuées aux projets
        private static Dictionary<string, SolidColorBrush> _projectTabColors = new Dictionary<string, SolidColorBrush>();
        private static readonly Random _random = new Random();

        private static readonly Dictionary<string, SolidColorBrush> predefinedKeywordColors =
            new Dictionary<string, SolidColorBrush>
            {
                { "Outils de Visualisation",   new SolidColorBrush(Color.FromRgb(255, 230, 230)) },
                { "Modification",             new SolidColorBrush(Color.FromRgb(230, 255, 230)) },
                { "Outils IA",                new SolidColorBrush(Color.FromRgb(230, 230, 255)) },
                { "Couleur du projet",        new SolidColorBrush(Color.FromRgb(230, 230, 230)) },
                { "Panneaux réservée au test",new SolidColorBrush(Color.FromRgb(255, 255, 230)) },
                { "Analyse",                  new SolidColorBrush(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles",  new SolidColorBrush(Color.FromRgb(255, 230, 255)) }
            };

        // Liste de mots-clés dans l'ordre où on les teste;
        // On place "Analyse" en dernier pour qu'elle soit appliquée après tout le reste
        private static readonly List<string> _targetKeywords = new List<string>
        {
            "Outils de Visualisation",
            "Modification",
            "Outils IA",
            "Couleur du projet",
            "Panneaux réservée au test",
            "Spécifique aux familles",
            "Analyse"
        };

        /// <summary>
        /// Réinitialise le dictionnaire des couleurs de projets.
        /// Ainsi, lors du prochain appel d'ApplyTabItemColoring, une nouvelle couleur aléatoire sera générée.
        /// </summary>
        public static void ResetRandomColors()
        {
            _projectTabColors.Clear();
        }

        public static void ApplyColorings(IntPtr mainWindowHandle)
        {
            if (ColoringStateManager.IsColoringActive) return;
            ApplyTabItemColoring(mainWindowHandle);
            ApplyPapanoelColoring(mainWindowHandle);
        }

        public static void ResetColorings(IntPtr mainWindowHandle)
        {
            ResetTabItemColoring(mainWindowHandle);
            ResetPapanoelColoring(mainWindowHandle);
        }

        /// <summary>
        /// Coloration flashy des onglets (TabItems) + texte en noir
        /// </summary>
        public static void ApplyTabItemColoring(IntPtr mainWindowHandle)
        {
            var mainWindow = GetMainWindow(mainWindowHandle);
            if (mainWindow == null) return;

            var tabItems = FindChildrenByType<TabItem>(mainWindow);
            foreach (var tabItem in tabItems)
            {
                var toolTip = tabItem.ToolTip as string;
                if (string.IsNullOrEmpty(toolTip)) continue;

                string projectName = ExtractProjectName(toolTip);
                if (string.IsNullOrEmpty(projectName)) continue;

                SolidColorBrush borderBrush;
                SolidColorBrush projectBrush = GetFlashyProjectColor(projectName, out borderBrush);

                tabItem.Background = projectBrush;
                tabItem.BorderBrush = borderBrush;

                // Mettre le texte en noir
                ColorTextBlocks(tabItem, Brushes.Black);
            }
        }

        private static SolidColorBrush GetFlashyProjectColor(string projectName, out SolidColorBrush borderBrush)
        {
            if (_projectTabColors.TryGetValue(projectName, out SolidColorBrush existingBrush))
            {
                borderBrush = DarkenColor(existingBrush.Color, 0.7);
                return existingBrush;
            }

            Color randomColor = GenerateRandomFlashyColor();
            var newBrush = new SolidColorBrush(randomColor);
            _projectTabColors[projectName] = newBrush;

            borderBrush = DarkenColor(randomColor, 0.7);
            return newBrush;
        }

        /// <summary>
        /// Couleur flashy = R, G, B dans [180..256].
        /// </summary>
        private static Color GenerateRandomFlashyColor()
        {
            byte r = (byte)(_random.Next(180, 256));
            byte g = (byte)(_random.Next(180, 256));
            byte b = (byte)(_random.Next(180, 256));
            return Color.FromRgb(r, g, b);
        }

        /// <summary>
        /// Reset tab item + clear la couleur du texte
        /// </summary>
        private static void ResetTabItemColoring(IntPtr mainWindowHandle)
        {
            var mainWindow = GetMainWindow(mainWindowHandle);
            if (mainWindow == null) return;

            var tabItems = FindChildrenByType<TabItem>(mainWindow);
            foreach (var tabItem in tabItems)
            {
                tabItem.ClearValue(TabItem.BackgroundProperty);
                tabItem.ClearValue(TabItem.BorderBrushProperty);

                // Rétablir la couleur d'origine du texte
                ClearTextBlocks(tabItem);
            }
        }

        /// <summary>
        /// Coloration "Papanoel" => pastel + texte en noir
        /// (Boucle originale avec break; => on s’arrête au premier mot-clé trouvé)
        /// </summary>
        public static void ApplyPapanoelColoring(IntPtr mainWindowHandle)
        {
            var mainWindow = GetMainWindow(mainWindowHandle);
            if (mainWindow == null) return;

            var borders = FindChildrenByType<Border>(mainWindow);
            foreach (var border in borders)
            {
                var dc = border.DataContext;
                if (dc == null) continue;

                var cookieProp = dc.GetType().GetProperty("Cookie");
                if (cookieProp == null) continue;

                var cookieValue = cookieProp.GetValue(dc);
                if (cookieValue == null) continue;

                string cookieStr = cookieValue.ToString();

                foreach (var keyword in _targetKeywords)
                {
                    if (cookieStr.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (predefinedKeywordColors.TryGetValue(keyword, out SolidColorBrush brush))
                        {
                            border.Background = brush;
                            var darkerBrush = DarkenColor(brush.Color, 0.7);
                            border.BorderBrush = darkerBrush;
                            border.BorderThickness = new Thickness(1);

                            // Texte en noir
                            ColorTextBlocks(border, Brushes.Black);
                        }

                        // On quitte la boucle dès qu'on a trouvé un mot-clé
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Reset papanoel => ClearValue + clear text color
        /// </summary>
        private static void ResetPapanoelColoring(IntPtr mainWindowHandle)
        {
            var mainWindow = GetMainWindow(mainWindowHandle);
            if (mainWindow == null) return;

            var borders = FindChildrenByType<Border>(mainWindow);
            foreach (var border in borders)
            {
                border.ClearValue(Border.BackgroundProperty);
                border.ClearValue(Border.BorderBrushProperty);
                border.ClearValue(Border.BorderThicknessProperty);

                // Restaurer la couleur d'origine du texte
                ClearTextBlocks(border);
            }
        }

        // --------------------------------------------------------------------
        // Outils communs
        // --------------------------------------------------------------------
        private static Window GetMainWindow(IntPtr mainWindowHandle)
        {
            HwndSource hwndSource = HwndSource.FromHwnd(mainWindowHandle);
            return hwndSource?.RootVisual as Window;
        }

        private static SolidColorBrush DarkenColor(Color color, double factor)
        {
            byte r = (byte)Math.Max(color.R * factor, 0);
            byte g = (byte)Math.Max(color.G * factor, 0);
            byte b = (byte)Math.Max(color.B * factor, 0);
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private static string ExtractProjectName(string toolTipText)
        {
            int index = toolTipText.IndexOf(" - ");
            if (index > 0)
            {
                return toolTipText.Substring(0, index).Trim();
            }
            return toolTipText;
        }

        private static List<T> FindChildrenByType<T>(DependencyObject parent) where T : DependencyObject
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

        // Méthode pour colorer tous les TextBlocks
        private static void ColorTextBlocks(DependencyObject parent, Brush color)
        {
            var textBlocks = FindChildrenByType<TextBlock>(parent);
            foreach (var tb in textBlocks)
            {
                tb.Foreground = color;
            }
        }

        // Méthode pour restaurer la couleur d'origine des TextBlocks
        private static void ClearTextBlocks(DependencyObject parent)
        {
            var textBlocks = FindChildrenByType<TextBlock>(parent);
            foreach (var tb in textBlocks)
            {
                tb.ClearValue(TextBlock.ForegroundProperty);
            }
        }
    }

    // -----------------------------------------------------------------------
    //   3) Coloration Partielle : Recherche "PanelTitleBar" + Titre
    // -----------------------------------------------------------------------
    public static class PartialColoringHelper
    {
        private static readonly Dictionary<string, SolidColorBrush> partialTitles
            = new Dictionary<string, SolidColorBrush>
            {
                { "Outils de Visualisation",   new SolidColorBrush(Color.FromRgb(255, 230, 230)) },
                { "Modification",             new SolidColorBrush(Color.FromRgb(230, 255, 230)) },
                { "Outils IA",                new SolidColorBrush(Color.FromRgb(230, 230, 255)) },
                { "Analyse",                  new SolidColorBrush(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles",  new SolidColorBrush(Color.FromRgb(255, 230, 255)) },
                { "Panneaux réservée au test",new SolidColorBrush(Color.FromRgb(255, 255, 230)) },
                { "Couleur du projet",        new SolidColorBrush(Color.FromRgb(230, 230, 230)) }
            };

        public static void ApplyPartialColoring(IntPtr mainWindowHandle)
        {
            if (!ColoringStateManager.IsColoringActive) return;
            if (ColoringStateManager.IsFullMode) return;

            Window mainWindow = GetMainWindow(mainWindowHandle);
            if (mainWindow == null) return;

            var allPanelTitleBars = FindVisualByTypeName(mainWindow, "PanelTitleBar");

            foreach (var ptb in allPanelTitleBars)
            {
                var titleProp = ptb.GetType().GetProperty("Title");
                if (titleProp == null) continue;

                var titleValue = titleProp.GetValue(ptb);
                if (titleValue == null) continue;

                string titleStr = titleValue.ToString();

                if (partialTitles.TryGetValue(titleStr, out SolidColorBrush colorBrush))
                {
                    Border borderToColor = null;
                    var allBorders = FindChildrenByType<Border>(ptb);
                    if (allBorders.Count > 0)
                    {
                        borderToColor = allBorders[0];
                    }
                    if (borderToColor == null && ptb is Border borderSelf)
                    {
                        borderToColor = borderSelf;
                    }

                    if (borderToColor != null)
                    {
                        borderToColor.Background = colorBrush;
                        var darkerBrush = DarkenColor(colorBrush.Color, 0.7);
                        borderToColor.BorderBrush = darkerBrush;
                        borderToColor.BorderThickness = new Thickness(1);

                        // Texte en noir
                        ColorTextBlocks(borderToColor, Brushes.Black);
                    }
                }
            }
        }

        public static void ResetPartialColoring(IntPtr mainWindowHandle)
        {
            Window mainWindow = GetMainWindow(mainWindowHandle);
            if (mainWindow == null) return;

            var allPanelTitleBars = FindVisualByTypeName(mainWindow, "PanelTitleBar");
            foreach (var ptb in allPanelTitleBars)
            {
                var titleProp = ptb.GetType().GetProperty("Title");
                if (titleProp == null) continue;

                var titleValue = titleProp.GetValue(ptb);
                if (titleValue == null) continue;

                string titleStr = titleValue.ToString();
                if (partialTitles.ContainsKey(titleStr))
                {
                    var allBorders = FindChildrenByType<Border>(ptb);
                    Border borderToReset = allBorders.Count > 0 ? allBorders[0] : null;

                    if (borderToReset == null && ptb is Border borderSelf)
                    {
                        borderToReset = borderSelf;
                    }

                    if (borderToReset != null)
                    {
                        borderToReset.ClearValue(Border.BackgroundProperty);
                        borderToReset.ClearValue(Border.BorderBrushProperty);
                        borderToReset.ClearValue(Border.BorderThicknessProperty);

                        // ClearValue => restaurer la couleur d'origine
                        ClearTextBlocks(borderToReset);
                    }
                }
            }
        }

        // --------------------------------------------------------------------
        // Outils communs
        // --------------------------------------------------------------------
        private static Window GetMainWindow(IntPtr mainWindowHandle)
        {
            HwndSource hwndSource = HwndSource.FromHwnd(mainWindowHandle);
            return hwndSource?.RootVisual as Window;
        }

        private static List<FrameworkElement> FindVisualByTypeName(DependencyObject parent, string typeName)
        {
            var result = new List<FrameworkElement>();
            if (parent == null) return result;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null)
                {
                    if (child.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                        && child is FrameworkElement fe)
                    {
                        result.Add(fe);
                    }
                    result.AddRange(FindVisualByTypeName(child, typeName));
                }
            }
            return result;
        }

        private static List<T> FindChildrenByType<T>(DependencyObject parent) where T : DependencyObject
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

        private static void ColorTextBlocks(DependencyObject parent, Brush color)
        {
            var textBlocks = FindChildrenByType<TextBlock>(parent);
            foreach (var tb in textBlocks)
            {
                tb.Foreground = color;
            }
        }

        private static void ClearTextBlocks(DependencyObject parent)
        {
            var textBlocks = FindChildrenByType<TextBlock>(parent);
            foreach (var tb in textBlocks)
            {
                tb.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        private static SolidColorBrush DarkenColor(Color color, double factor)
        {
            byte r = (byte)(color.R * factor);
            byte g = (byte)(color.G * factor);
            byte b = (byte)(color.B * factor);
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }
}
