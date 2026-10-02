using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Couleur
{
    public partial class ColorPreferencesWindow
    {
        private int _appearanceGuideStep = -1;
        private bool _appearanceGuideActionsConnected;
        private bool _appearanceGuideStepCompleted;
        private Color _appearanceBackgroundInitialColor;
        private readonly HashSet<BrowserIconRule> _appearanceTouchedIcons = new HashSet<BrowserIconRule>();
        private const int AppearanceGuideStepCount = 15;
        private AdornerLayer _appearanceGuideLayer;
        private AppearanceGuideAdorner _appearanceGuideHighlight;

        // The color command calls this after opening the actual preferences window.
        internal void StartAppearanceTutorial()
        {
            if (!IsLoaded)
            {
                Loaded += StartAppearanceTutorialWhenLoaded;
                return;
            }
            _appearanceGuideStep = 0;
            _appearanceTouchedIcons.Clear();
            ConnectAppearanceGuideActions();
            AppearanceGuideCard.Visibility = Visibility.Visible;
            AppearanceGuideSprite.Source = RibbonPanelColorScheme.CreateCompanionImage();
            RenderOptions.SetBitmapScalingMode(AppearanceGuideSprite, BitmapScalingMode.NearestNeighbor);
            Closed -= AppearanceTutorialClosed;
            Closed += AppearanceTutorialClosed;
            ShowAppearanceGuideStep();
        }

        private void StartAppearanceTutorialWhenLoaded(object sender, RoutedEventArgs args)
        {
            Loaded -= StartAppearanceTutorialWhenLoaded;
            StartAppearanceTutorial();
        }

        private void RestartAppearanceTutorial_Click(object sender, RoutedEventArgs args) => StartAppearanceTutorial();
        private void AppearanceGuidePrevious_Click(object sender, RoutedEventArgs args)
        {
            if (_appearanceGuideStep > 0) { _appearanceGuideStep--; ShowAppearanceGuideStep(); }
        }

        private void AppearanceGuideNext_Click(object sender, RoutedEventArgs args)
        {
            if (!_appearanceGuideStepCompleted) return;
            if (_appearanceGuideStep >= AppearanceGuideStepCount - 1) { StopAppearanceTutorial(); return; }
            _appearanceGuideStep++;
            ShowAppearanceGuideStep();
        }

        private void AppearanceGuideQuit_Click(object sender, RoutedEventArgs args) => StopAppearanceTutorial();
        private void GoToBrowserIconsButton_Click(object sender, RoutedEventArgs args) =>
            BrowserTabs.SelectedItem = BrowserIconsTab;
        private void AppearanceTutorialClosed(object sender, EventArgs args) => RemoveAppearanceGuideHighlight();

        private void StopAppearanceTutorial()
        {
            _appearanceGuideStep = -1;
            AppearanceGuideCard.Visibility = Visibility.Collapsed;
            RemoveAppearanceGuideHighlight();
        }

        private void ConnectAppearanceGuideActions()
        {
            if (_appearanceGuideActionsConnected) return;
            _appearanceGuideActionsConnected = true;
            TutorialEnablePanels.Checked += (_, __) => CompleteAppearanceGuideStep(0);
            TutorialPresetChoice.SelectionChanged += (_, __) => CompleteAppearanceGuideStep(1);
            TutorialApplyPreset.Click += (_, __) => CompleteAppearanceGuideStep(2);
            ViewsAndFoldersTab.PreviewMouseLeftButtonUp += (_, __) => CompleteAppearanceGuideStep(3);
            AppearanceTabs.SelectionChanged += (_, args) =>
            {
                if (args.Source == AppearanceTabs && ViewsAndFoldersTab.IsSelected)
                    CompleteAppearanceGuideStep(3);
            };
            TutorialEnableBrowser.Checked += (_, __) => CompleteAppearanceGuideStep(4);
            BrowserTabs.SelectionChanged += (_, args) =>
            {
                if (args.Source != BrowserTabs) return;
                if (BrowserBackgroundTab.IsSelected) CompleteAppearanceGuideStep(5);
                if (BrowserFoldersTab.IsSelected) CompleteAppearanceGuideStep(7);
                if (BrowserIconsTab.IsSelected) CompleteAppearanceGuideStep(8);
            };
            TutorialGoToIconsFromFolders.Click += (_, __) => CompleteAppearanceGuideStep(8);
            TutorialBackgroundColor.SelectedColorChanged += (_, __) =>
            {
                if (_appearanceGuideStep == 6 && BrowserPreferences.BackgroundColor != _appearanceBackgroundInitialColor)
                    CompleteAppearanceGuideStep(6);
            };
            TutorialEnableIcons.Checked += (_, __) => CompleteAppearanceGuideStep(9);
            TutorialAddIconRule.Click += (_, __) =>
            {
                if (_appearanceGuideStep == 10 || _appearanceGuideStep == 12)
                    CompleteAppearanceGuideStep(_appearanceGuideStep);
            };
            TutorialIconRules.AddHandler(TextBox.TextChangedEvent,
                new TextChangedEventHandler((_, __) =>
                    Dispatcher.BeginInvoke(new Action(CheckAppearanceIconRule), DispatcherPriority.Background)), true);
            TutorialIconRules.AddHandler(Selector.SelectionChangedEvent,
                new SelectionChangedEventHandler((_, args) =>
                {
                    if (args.OriginalSource is ComboBox combo && combo.DataContext is BrowserIconRule rule)
                        _appearanceTouchedIcons.Add(rule);
                    Dispatcher.BeginInvoke(new Action(CheckAppearanceIconRule), DispatcherPriority.Background);
                }), true);
        }

        private void CheckAppearanceIconRule()
        {
            if (_appearanceGuideStep != 11 && _appearanceGuideStep != 13) return;
            BrowserIconRule rule = FindAppearanceIconRule(_appearanceGuideStep == 11 ? "Plans d'étage" : "Vues 3D");
            if (rule != null && _appearanceTouchedIcons.Contains(rule) &&
                BrowserIconAssets.Any(asset => asset.Id == rule.IconId))
                CompleteAppearanceGuideStep(_appearanceGuideStep);
        }

        private BrowserIconRule FindAppearanceIconRule(string name) =>
            BrowserIcons.Rules.FirstOrDefault(rule => string.Equals(
                rule.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));

        private void CompleteAppearanceGuideStep(int expectedStep)
        {
            if (_appearanceGuideStep != expectedStep || _appearanceGuideStepCompleted) return;
            _appearanceGuideStepCompleted = true;
            if (expectedStep == 7)
            {
                AppearanceGuideNext.IsEnabled = true;
                return;
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_appearanceGuideStep != expectedStep || !IsVisible) return;
                _appearanceGuideStep++;
                ShowAppearanceGuideStep();
            }), DispatcherPriority.Background);
        }

        private void ShowAppearanceGuideStep()
        {
            RemoveAppearanceGuideHighlight();
            if (_appearanceGuideStep < 0) return;
            _appearanceGuideStepCompleted = false;
            string title, description;
            FrameworkElement target;
            switch (_appearanceGuideStep)
            {
                case 0:
                    AppearanceTabs.SelectedItem = MyPanelsTab;
                    target = TutorialEnablePanels;
                    title = "1/15 · Activer les couleurs";
                    description = "Coche « Afficher les couleurs de mes panneaux » pour voir les styles sur les rubans BIMaestro. Tu peux aussi choisir de colorer tout le panneau.";
                    break;
                case 1:
                    AppearanceTabs.SelectedItem = MyPanelsTab;
                    target = TutorialPresetChoice;
                    title = "2/15 · Choisir un style";
                    description = "Ouvre cette liste et choisis un style de départ. Tu y retrouveras les effets animés, dont Pokémon pixel. Cette sélection ne modifie pas encore les lignes.";
                    break;
                case 2:
                    AppearanceTabs.SelectedItem = MyPanelsTab;
                    target = TutorialApplyPreset;
                    title = "3/15 · Appliquer le style";
                    description = "Clique sur « Utiliser ce style » pour remplir les réglages des panneaux. Tu peux ensuite ajuster les couleurs ligne par ligne avant d’enregistrer.";
                    break;
                case 3:
                    target = ViewsAndFoldersTab;
                    title = "4/15 · Passer aux vues";
                    description = "Ouvre « Vues et dossiers ». Nous allons changer le fond, puis placer des icônes dans l’arborescence.";
                    break;
                case 4:
                    AppearanceTabs.SelectedItem = ViewsAndFoldersTab;
                    target = TutorialEnableBrowser;
                    title = "5/15 · Activer l’arborescence";
                    description = "Coche « Personnalisation active » pour que le fond et les icônes puissent être appliqués dans Revit.";
                    break;
                case 5:
                    target = BrowserBackgroundTab;
                    title = "6/15 · Ouvrir Fond et aperçu";
                    description = "Ouvre « Fond et aperçu ». L’aperçu te montre immédiatement l’effet de tes choix.";
                    break;
                case 6:
                    _appearanceBackgroundInitialColor = BrowserPreferences.BackgroundColor;
                    BrowserTabs.SelectedItem = BrowserBackgroundTab;
                    target = TutorialBackgroundColor;
                    title = "7/15 · Changer la couleur du fond";
                    description = "Choisis une autre couleur dans « Fond principal » et regarde l’aperçu de l’arborescence. Le guide attend que la couleur change.";
                    break;
                case 7:
                    target = BrowserFoldersTab;
                    title = "8/15 · Repérer les dossiers";
                    description = "Ouvre « Dossiers ». Cet onglet sert à colorer les dossiers existants. Nous allons ensuite placer une icône sur « Plans d'étage ». Clique sur Suivant après avoir regardé.";
                    break;
                case 8:
                    target = TutorialGoToIconsFromFolders;
                    title = "9/15 · Passer aux icônes";
                    description = "Clique sur « Ajouter une icône à un dossier… ». Nous allons ajouter une image devant « Plans d'étage » et « Vues 3D », sans créer de type de vue.";
                    break;
                case 9:
                    BrowserTabs.SelectedItem = BrowserIconsTab;
                    target = TutorialEnableIcons;
                    title = "10/15 · Afficher les icônes";
                    description = "Coche « Afficher mes icônes ». Ce réglage fonctionne même si tu changes ensuite les couleurs.";
                    break;
                case 10:
                    target = TutorialAddIconRule;
                    title = "11/15 · Ajouter Plans d’étage";
                    description = "Clique sur « + Ajouter un nom » pour créer une règle pour le dossier « Plans d’étage ». Si elle existe déjà, clique sur Suivant.";
                    break;
                case 11:
                    target = TutorialIconRules;
                    title = "12/15 · Choisir son icône";
                    description = "Dans la nouvelle ligne, saisis exactement « Plans d'étage », puis choisis une image dans la liste. Le nom doit correspondre à celui du dossier Revit.";
                    break;
                case 12:
                    target = TutorialAddIconRule;
                    title = "13/15 · Ajouter Vues 3D";
                    description = "Ajoute une seconde règle avec « + Ajouter un nom ». Elle servira au dossier « Vues 3D » ; tu ne crées aucun type de vue.";
                    break;
                case 13:
                    target = TutorialIconRules;
                    title = "14/15 · Choisir l’icône 3D";
                    description = "Saisis exactement « Vues 3D » dans la seconde ligne, puis choisis son icône. La règle s’appliquera au nom du dossier existant.";
                    break;
                default:
                    target = TutorialSave;
                    title = "15/15 · Enregistrer";
                    description = "Clique sur « Enregistrer mes réglages » pour appliquer le nouveau fond et les deux icônes. Vérifie ensuite l’arborescence dans Revit.";
                    break;
            }
            AppearanceGuideTitle.Text = title;
            AppearanceGuideText.Text = description;
            AppearanceGuidePrevious.IsEnabled = _appearanceGuideStep > 0;
            AppearanceGuideNext.Content = "Suivant";
            _appearanceGuideStepCompleted = (_appearanceGuideStep == 0 && TutorialEnablePanels.IsChecked == true)
                || (_appearanceGuideStep == 3 && ViewsAndFoldersTab.IsSelected)
                || (_appearanceGuideStep == 4 && TutorialEnableBrowser.IsChecked == true)
                || (_appearanceGuideStep == 5 && BrowserBackgroundTab.IsSelected)
                || (_appearanceGuideStep == 7 && BrowserFoldersTab.IsSelected)
                || (_appearanceGuideStep == 8 && BrowserIconsTab.IsSelected)
                || (_appearanceGuideStep == 9 && TutorialEnableIcons.IsChecked == true)
                || (_appearanceGuideStep == 10 && FindAppearanceIconRule("Plans d'étage") != null)
                || (_appearanceGuideStep == 11 && FindAppearanceIconRule("Plans d'étage") != null)
                || (_appearanceGuideStep == 12 && FindAppearanceIconRule("Vues 3D") != null)
                || (_appearanceGuideStep == 13 && FindAppearanceIconRule("Vues 3D") != null);
            AppearanceGuideNext.IsEnabled = _appearanceGuideStep < AppearanceGuideStepCount - 1 && _appearanceGuideStepCompleted;
            // A tab change materializes its content on the next layout pass.
            int expectedStep = _appearanceGuideStep;
            Dispatcher.BeginInvoke(new Action(() => HighlightAppearanceGuideTarget(target, expectedStep)), DispatcherPriority.Loaded);
        }

        private void HighlightAppearanceGuideTarget(FrameworkElement target, int expectedStep)
        {
            if (_appearanceGuideStep != expectedStep || !target.IsVisible || !IsVisible) return;
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return;
            _appearanceGuideLayer = layer;
            _appearanceGuideHighlight = new AppearanceGuideAdorner(target);
            layer.Add(_appearanceGuideHighlight);
        }

        private void RemoveAppearanceGuideHighlight()
        {
            if (_appearanceGuideHighlight != null) _appearanceGuideLayer?.Remove(_appearanceGuideHighlight);
            _appearanceGuideHighlight = null;
            _appearanceGuideLayer = null;
        }

        private sealed class AppearanceGuideAdorner : Adorner
        {
            internal AppearanceGuideAdorner(UIElement adorned) : base(adorned)
            {
                IsHitTestVisible = false;
                Focusable = false;
            }

            protected override void OnRender(DrawingContext drawing)
            {
                Rect bounds = new Rect(AdornedElement.RenderSize);
                if (bounds.Width < 1 || bounds.Height < 1) return;
                drawing.DrawRoundedRectangle(null, new Pen(Brushes.DarkOrange, 3),
                    new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
            }
        }
    }
}
