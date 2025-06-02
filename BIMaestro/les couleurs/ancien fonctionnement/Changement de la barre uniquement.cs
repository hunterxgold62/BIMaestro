using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;

namespace MyRevitPlugin
{
    // Appliquez l'attribut Transaction avec le mode ReadOnly
    [Transaction(TransactionMode.ReadOnly)]
    public class UIHelper : IExternalCommand
    {
        /// <summary>
        /// Dictionnaire associant les noms des panneaux à leurs couleurs de fond et chemins respectifs.
        /// </summary>
        private static readonly Dictionary<string, (SolidColorBrush Color, string Path)> PanelConfigurations = new Dictionary<string, (SolidColorBrush, string)>
        {
            { "Outils de Visualisation", (new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 230, 230)), "Border\\Grid\\Border\\AdornerDecorator\\ContentPresenter\\Grid\\Grid\\StackPanel\\Border\\RevitRibbonControl\\AdornerDecorator\\DockPanel\\Decorator\\ItemsControl\\Border\\ItemsPresenter\\PanelSetListView\\ContentPresenter19\\PanelListScrollViewer\\DockPanel\\ScrollContentPresenter\\RibbonPanelList\\Border\\ItemsPresenter\\PanelListView\\ContentPresenter1\\RibbonPanelControl\\Grid\\Border1\\Grid\\PanelTitleBar\\Border") },
            { "Modification", (new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 255, 230)), "Border\\Grid\\Border\\AdornerDecorator\\ContentPresenter\\Grid\\Grid\\StackPanel\\Border\\RevitRibbonControl\\AdornerDecorator\\DockPanel\\Decorator\\ItemsControl\\Border\\ItemsPresenter\\PanelSetListView\\ContentPresenter19\\PanelListScrollViewer\\DockPanel\\ScrollContentPresenter\\RibbonPanelList\\Border\\ItemsPresenter\\PanelListView\\ContentPresenter2\\RibbonPanelControl\\Grid\\Border1\\Grid\\PanelTitleBar\\Border") },
            { "Outils IA", (new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 255)), "Border\\Grid\\Border\\AdornerDecorator\\ContentPresenter\\Grid\\Grid\\StackPanel\\Border\\RevitRibbonControl\\AdornerDecorator\\DockPanel\\Decorator\\ItemsControl\\Border\\ItemsPresenter\\PanelSetListView\\ContentPresenter19\\PanelListScrollViewer\\DockPanel\\ScrollContentPresenter\\RibbonPanelList\\Border\\ItemsPresenter\\PanelListView\\ContentPresenter3\\RibbonPanelControl\\Grid\\Border1\\Grid\\PanelTitleBar\\Border") },
            { "Panneaux réservée au test", (new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 230)), "Border\\Grid\\Border\\AdornerDecorator\\ContentPresenter\\Grid\\Grid\\StackPanel\\Border\\RevitRibbonControl\\AdornerDecorator\\DockPanel\\Decorator\\ItemsControl\\Border\\ItemsPresenter\\PanelSetListView\\ContentPresenter19\\PanelListScrollViewer\\DockPanel\\ScrollContentPresenter\\RibbonPanelList\\Border\\ItemsPresenter\\PanelListView\\ContentPresenter4\\RibbonPanelControl\\Grid\\Border1\\Grid\\PanelTitleBar\\Border") },
            { "Analyse", (new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 255, 255)), "Border\\Grid\\Border\\AdornerDecorator\\ContentPresenter\\Grid\\Grid\\StackPanel\\Border\\RevitRibbonControl\\AdornerDecorator\\DockPanel\\Decorator\\ItemsControl\\Border\\ItemsPresenter\\PanelSetListView\\ContentPresenter19\\PanelListScrollViewer\\DockPanel\\ScrollContentPresenter\\RibbonPanelList\\Border\\ItemsPresenter\\PanelListView\\ContentPresenter5\\RibbonPanelControl\\Grid\\Border1\\Grid\\PanelTitleBar\\Border") },
            { "Spécifique aux familles", (new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 230, 255)), "Border\\Grid\\Border\\AdornerDecorator\\ContentPresenter\\Grid\\Grid\\StackPanel\\Border\\RevitRibbonControl\\AdornerDecorator\\DockPanel\\Decorator\\ItemsControl\\Border\\ItemsPresenter\\PanelSetListView\\ContentPresenter19\\PanelListScrollViewer\\DockPanel\\ScrollContentPresenter\\RibbonPanelList\\Border\\ItemsPresenter\\PanelListView\\ContentPresenter6\\RibbonPanelControl\\Grid\\Border1\\Grid\\PanelTitleBar\\Border") }
        };

        /// <summary>
        /// Point d'entrée de la commande externe.
        /// </summary>
        /// <param name="commandData">Données de la commande externe.</param>
        /// <param name="message">Message d'erreur en cas d'échec.</param>
        /// <param name="elements">Éléments affectés par la commande.</param>
        /// <returns>Résultat de l'exécution de la commande.</returns>
        public Result Execute(
          ExternalCommandData commandData,
          ref string message,
          ElementSet elements)
        {
            try
            {
                // Obtenir le handle de la fenêtre principale de Revit
                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;

                // Appeler la méthode d'application des modifications
                ApplyModifications(mainWindowHandle);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
             
                return Result.Failed;
            }
        }

        /// <summary>
        /// Applique les modifications aux panneaux et textes spécifiés.
        /// </summary>
        /// <param name="mainWindowHandle">Handle de la fenêtre principale de Revit.</param>
        public static void ApplyModifications(IntPtr mainWindowHandle)
        {
            // Obtenir la source de fenêtre à partir du handle
            HwndSource hwndSource = HwndSource.FromHwnd(mainWindowHandle);
            Window mainWindow = hwndSource?.RootVisual as Window;

            if (mainWindow == null)
            {
              
                return;
            }

            foreach (var panel in PanelConfigurations)
            {
                string panelName = panel.Key;
                SolidColorBrush panelColor = panel.Value.Color;
                string panelPath = panel.Value.Path;

                // Traverser l'arborescence visuelle pour trouver le Border spécifique
                DependencyObject targetElement = TraverseVisualTree(mainWindow, panelPath);

                if (targetElement is Border panelBorder)
                {
                    // Appliquer les modifications sur le thread UI
                    mainWindow.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            // Changer le Background
                            panelBorder.Background = panelColor;

                            // Changer le BorderBrush en une couleur légèrement plus foncée
                            SolidColorBrush darkerBrush = DarkenColor(panelColor.Color, 0.7); // Assombrit de 70%
                            panelBorder.BorderBrush = darkerBrush;

                            // Définir l'épaisseur de la bordure
                            panelBorder.BorderThickness = new Thickness(1);

                            // Modifier la couleur des TextBlocks enfants en rouge
                            ModifyTextBlocks(panelBorder, Brushes.Black);
                        }
                        catch (Exception ex)
                        {
                          
                        }
                    });

                }
                else
                {
                   
                }
            }
        }

        /// <summary>
        /// Modifie la couleur de texte de tous les TextBlocks enfants en rouge.
        /// </summary>
        /// <param name="parent">Élément parent contenant les TextBlocks.</param>
        /// <param name="color">Nouvelle couleur de texte.</param>
        private static void ModifyTextBlocks(DependencyObject parent, SolidColorBrush color)
        {
            foreach (var textBlock in FindChildrenByType<TextBlock>(parent))
            {
                textBlock.Foreground = color;
            }
        }

        /// <summary>
        /// Traverse l'arborescence visuelle en suivant un chemin spécifique pour trouver un élément.
        /// </summary>
        /// <param name="root">Élément racine pour commencer la recherche.</param>
        /// <param name="path">Chemin des éléments à traverser.</param>
        /// <returns>L'élément trouvé ou null si non trouvé.</returns>
        private static DependencyObject TraverseVisualTree(DependencyObject root, string path)
        {
            string[] steps = path.Split('\\');

            DependencyObject current = root;
            foreach (string step in steps)
            {
                if (current == null)
                    return null;

                // Extraire le type et l'index (par exemple, "ContentPresenter19" => type="ContentPresenter", index=19)
                string typeName = step;
                int index = 1; // Par défaut, premier enfant

                // Vérifier si le pas contient un index, par exemple "ContentPresenter19"
                int numIndex = typeName.Length;
                for (int i = typeName.Length - 1; i >= 0; i--)
                {
                    if (!char.IsDigit(typeName[i]))
                    {
                        numIndex = i + 1;
                        break;
                    }
                }

                string typePart = typeName.Substring(0, numIndex);
                string indexPart = typeName.Substring(numIndex);

                if (!int.TryParse(indexPart, out index))
                {
                    index = 1; // Par défaut à 1 si aucun index
                }

                // Trouver le nième enfant de type spécifié
                int count = VisualTreeHelper.GetChildrenCount(current);
                int matched = 0;
                DependencyObject next = null;

                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(current, i);
                    if (child.GetType().Name.Equals(typePart, StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        if (matched == index)
                        {
                            next = child;
                            break;
                        }
                    }
                }

                current = next;
            }

            return current;
        }

        /// <summary>
        /// Trouve tous les enfants d'un type spécifique dans l'arborescence visuelle.
        /// </summary>
        /// <typeparam name="T">Type de l'élément à trouver.</typeparam>
        /// <param name="parent">Élément parent à partir duquel commencer la recherche.</param>
        /// <returns>Liste des éléments trouvés.</returns>
        private static List<T> FindChildrenByType<T>(DependencyObject parent) where T : DependencyObject
        {
            List<T> children = new List<T>();
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    children.Add(typedChild);
                }
                children.AddRange(FindChildrenByType<T>(child));
            }
            return children;
        }

        /// <summary>
        /// Assombrit une couleur d'un certain pourcentage.
        /// </summary>
        /// <param name="color">Couleur originale.</param>
        /// <param name="percentage">Pourcentage d'assombrissement (0.0 à 1.0).</param>
        /// <returns>Nouvelle couleur assombrie.</returns>
        private static SolidColorBrush DarkenColor(System.Windows.Media.Color color, double percentage)
        {
            byte r = (byte)(color.R * percentage);
            byte g = (byte)(color.G * percentage);
            byte b = (byte)(color.B * percentage);
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        }

        /// <summary>
        /// Enregistre des messages de débogage dans un fichier log sur le bureau.
        /// </summary>
        /// <param name="message">Message à enregistrer.</param>
        
    }
}
