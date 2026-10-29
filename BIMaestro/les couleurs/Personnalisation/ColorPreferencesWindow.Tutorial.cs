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
        private const int AppearanceGuideStepCount = 21;
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
            BIMaestro.Tutorials.TutorialAnalytics.Ensure("colors");
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

        private void RestartAppearanceTutorial_Click(object sender, RoutedEventArgs args)
        {
            BIMaestro.Tutorials.TutorialAnalytics.End("colors", "interrupted");
            StartAppearanceTutorial();
        }
        private void AppearanceGuidePrevious_Click(object sender, RoutedEventArgs args)
        {
            if (_appearanceGuideStep > 0) { _appearanceGuideStep--; ShowAppearanceGuideStep(movingBack: true); }
        }

        private void AppearanceGuideNext_Click(object sender, RoutedEventArgs args)
        {
            if (!_appearanceGuideStepCompleted) return;
            if (_appearanceGuideStep >= AppearanceGuideStepCount - 1) { StopAppearanceTutorial(); return; }
            _appearanceGuideStep++;
            ShowAppearanceGuideStep();
        }

        private void AppearanceGuideQuit_Click(object sender, RoutedEventArgs args)
        {
            BIMaestro.Tutorials.TutorialAnalytics.End("colors", "quit");
            StopAppearanceTutorial();
        }
        private void GoToBrowserIconsButton_Click(object sender, RoutedEventArgs args) =>
            BrowserTabs.SelectedItem = BrowserIconsTab;
        private void AppearanceTutorialClosed(object sender, EventArgs args)
        {
            if (_appearanceGuideStep >= 0)
                BIMaestro.Tutorials.TutorialAnalytics.End("colors", "interrupted");
            StopAppearanceTutorial();
        }

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
            TutorialPresetChoice.SelectionChanged += (_, __) =>
            {
                if (IsRainbowPresetSelected())
                    CompleteAppearanceGuideStep(1);
            };
            // The list lives in a popup without adorner layer: outline the item itself.
            TutorialPresetChoice.DropDownOpened += (_, __) => Dispatcher.BeginInvoke(
                new Action(HighlightRainbowPresetItem), DispatcherPriority.Loaded);
            TutorialPresetChoice.DropDownClosed += (_, __) => ClearRainbowPresetItemHighlight();
            TutorialApplyPreset.Click += (_, __) => CompleteAppearanceGuideStep(2);
            if (_propertiesEditor != null)
                _propertiesEditor.TutorialAction += action => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (action == "context-refreshed" && _propertiesEditor.HasPreviewParameter("Zone de coupe", "Section Box"))
                        CompleteAppearanceGuideStep(14);
                    else if (action == "group-created") CompleteAppearanceGuideStep(15);
                    else if (action == "parameter-grouped:Zone de coupe" || action == "parameter-grouped:Section Box")
                        CompleteAppearanceGuideStep(16);
                }));
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
                if (BrowserFoldersTab.IsSelected && _appearanceGuideStep == 7)
                    ShowAppearanceGuideStep();
                if (BrowserIconsTab.IsSelected) CompleteAppearanceGuideStep(8);
            };
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

        private bool HasValidAppearanceIconRule(string name) =>
            BrowserIcons.Rules.Any(rule => string.Equals(rule.Name?.Trim(), name,
                StringComparison.OrdinalIgnoreCase) && BrowserIconAssets.Any(asset => asset.Id == rule.IconId));

        private ComboBoxItem _appearanceRainbowItem;

        private void HighlightRainbowPresetItem()
        {
            ClearRainbowPresetItemHighlight();
            if (_appearanceGuideStep != 1 || !TutorialPresetChoice.IsDropDownOpen) return;
            object entry = TutorialPresetChoice.Items.OfType<PresetMenuEntry>().FirstOrDefault(item =>
                string.Equals(item.Name, "Arc-en-ciel", StringComparison.OrdinalIgnoreCase));
            if (!(TutorialPresetChoice.ItemContainerGenerator.ContainerFromItem(entry) is ComboBoxItem item)) return;
            _appearanceRainbowItem = item;
            item.BorderBrush = BIMaestro.Tutorials.DemoTourPalette.Accent;
            item.BorderThickness = new Thickness(2);
            item.BringIntoView();
        }

        private void ClearRainbowPresetItemHighlight()
        {
            if (_appearanceRainbowItem == null) return;
            _appearanceRainbowItem.ClearValue(Control.BorderBrushProperty);
            _appearanceRainbowItem.ClearValue(Control.BorderThicknessProperty);
            _appearanceRainbowItem = null;
        }

        private bool IsRainbowPresetSelected() => string.Equals(
            (TutorialPresetChoice.SelectedItem as PresetMenuEntry)?.Name ?? SelectedPresetName,
            "Arc-en-ciel", StringComparison.OrdinalIgnoreCase);

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

        private void ShowAppearanceGuideStep(bool movingBack = false)
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
                    title = "Activer les couleurs";
                    description = "Coche « Afficher les couleurs de mes panneaux » : ce réglage agit sur les panneaux du ruban BIMaestro. Il est indépendant des couleurs de l'arborescence. Tu peux aussi choisir de colorer tout le panneau.";
                    break;
                case 1:
                    AppearanceTabs.SelectedItem = MyPanelsTab;
                    target = TutorialPresetChoice;
                    title = "Choisir un style";
                    description = "Ouvre « Style pour tous » et choisis « Arc-en-ciel » (sans « animé »). Ce style colore les panneaux de façon différente et facilite la comparaison. Les lignes ne changent qu'après « Utiliser ce style ».";
                    break;
                case 2:
                    AppearanceTabs.SelectedItem = MyPanelsTab;
                    target = TutorialApplyPreset;
                    title = "Appliquer le style";
                    description = "Clique sur « Utiliser ce style » pour remplir les réglages des panneaux. Tu peux ensuite ajuster les couleurs ligne par ligne avant d’enregistrer.";
                    break;
                case 3:
                    target = ViewsAndFoldersTab;
                    title = "Passer aux vues";
                    description = "Ouvre « Arborescence du projet ». Nous allons changer le fond, essayer une couleur de texte sur un dossier, puis placer des icônes dans l’arborescence.";
                    break;
                case 4:
                    AppearanceTabs.SelectedItem = ViewsAndFoldersTab;
                    target = TutorialEnableBrowser;
                    title = "Activer l’arborescence";
                    description = "Coche « Personnalisation active » : sans ce commutateur, tes règles de fond, dossiers et icônes restent enregistrées mais ne s'appliquent pas à l'arborescence Revit.";
                    break;
                case 5:
                    target = BrowserBackgroundTab;
                    title = "Ouvrir Fond et aperçu";
                    description = "Ouvre « Fond et aperçu ». L’aperçu te montre immédiatement l’effet de tes choix.";
                    break;
                case 6:
                    _appearanceBackgroundInitialColor = BrowserPreferences.BackgroundColor;
                    BrowserTabs.SelectedItem = BrowserBackgroundTab;
                    target = TutorialBackgroundColor;
                    title = "Changer la couleur du fond";
                    description = "Choisis une autre couleur dans « Fond principal ». L'aperçu montre l'effet avant l'enregistrement ; tu peux donc comparer la lisibilité des noms de vues avec ton nouveau fond. Le guide attend une vraie modification.";
                    break;
                case 7:
                    if (!BrowserFoldersTab.IsSelected)
                    {
                        target = BrowserFoldersTab;
                        title = "Ouvrir Dossiers";
                        description = "Clique sur l’onglet « Dossiers ». Nous ouvrirons ensuite « Choisir un dossier… » pour essayer une couleur de texte dans l’arborescence.";
                    }
                    else
                    {
                        target = TutorialChooseFolder;
                        title = "Colorer le texte d’un dossier";
                        description = "Clique sur « Choisir un dossier… ». Bulbizarre te guidera pour sélectionner un dossier existant, choisir l’apparence « Texte », changer sa couleur puis l’appliquer à Revit. Ferme ensuite cette fenêtre pour poursuivre vers les icônes.";
                    }
                    break;
                case 8:
                    target = BrowserIconsTab;
                    title = "Passer aux icônes";
                    description = "Clique sur la bande « Icônes » en haut de cette section. Nous allons ajouter une image devant « Plans d'étage » et « Vues 3D », sans créer de type de vue.";
                    break;
                case 9:
                    BrowserTabs.SelectedItem = BrowserIconsTab;
                    target = TutorialEnableIcons;
                    title = "Afficher les icônes";
                    description = "Coche « Afficher mes icônes ». Ce réglage fonctionne même si tu changes ensuite les couleurs.";
                    break;
                case 10:
                    target = TutorialAddIconRule;
                    title = "Ajouter Plans d’étage";
                    description = "Clique sur « + Ajouter un nom » pour créer une règle pour le dossier « Plans d’étage ». Si elle existe déjà, clique sur Suivant.";
                    break;
                case 11:
                    target = TutorialIconRules;
                    title = "Choisir son icône";
                    description = "Bulbizarre se place en haut pour te laisser saisir dans la nouvelle ligne « Plans d'étage », puis choisir une image. Le nom doit correspondre au dossier Revit (accent et espace compris).";
                    break;
                case 12:
                    target = TutorialAddIconRule;
                    title = "Ajouter Vues 3D";
                    description = "Ajoute une seconde règle avec « + Ajouter un nom ». Elle servira au dossier « Vues 3D » ; tu ne crées aucun type de vue.";
                    break;
                case 13:
                    target = TutorialIconRules;
                    title = "Choisir l’icône 3D";
                    description = "Bulbizarre laisse libre la nouvelle ligne : saisis exactement « Vues 3D », puis choisis son icône. Tu personnalises le dossier existant, pas un type de vue.";
                    break;
                case 14:
                    AppearanceTabs.SelectedItem = PropertiesTab;
                    target = PropertiesEditorHost;
                    title = "Partir d’une vue 3D";
                    description = "L’onglet Propriétés règle les couleurs, l’ordre et les groupes de paramètres. Pour l’exercice, active une vue 3D dans Revit sans rien sélectionner, puis clique sur « Actualiser la sélection ». Le guide avance quand « Zone de coupe » apparaît dans l’aperçu.";
                    break;
                case 15:
                    AppearanceTabs.SelectedItem = PropertiesTab;
                    if (_propertiesEditor?.GroupExpander != null) _propertiesEditor.GroupExpander.IsExpanded = true;
                    target = (FrameworkElement)_propertiesEditor?.GroupExpander ?? PropertiesEditorHost;
                    title = "Créer ton groupe";
                    description = "Saisis le nom de ton groupe de raccourcis, par exemple « Mes raccourcis », puis clique sur « + Créer un groupe ». Il apparaît en haut de « Votre personnalisation ».";
                    break;
                case 16:
                    AppearanceTabs.SelectedItem = PropertiesTab;
                    target = PropertiesEditorHost;
                    title = "Glisser « Zone de coupe »";
                    description = "Dans « Votre personnalisation », fais glisser le paramètre « Zone de coupe » sur le bandeau de ton groupe. Tu peux le chercher dans le champ de recherche.";
                    break;
                case 17:
                    AppearanceTabs.SelectedItem = HomeTab;
                    target = HomeEditorHost;
                    title = "Choisir ton accueil Revit";
                    description = "Accueil Revit propose plusieurs présentations et l’accès aux projets et familles du journal Excel. Tu peux conserver ton choix actuel. Clique sur Suivant pour poursuivre ; les réglages seront enregistrés avec le bouton final de cette fenêtre.";
                    break;
                case 18:
                    AppearanceTabs.SelectedItem = SharingTab;
                    target = SharingTab;
                    title = "Partager tes réglages";
                    description = "Importer un pack et Exporter mes réglages permettent de choisir les parties à transférer : panneaux BIMaestro, arborescence et icônes, propriétés avec leurs profils, ordre et groupes, et accueil Revit. Les autres onglets Revit sont réglés séparément. L’import propose une sauvegarde de secours ; enregistre ensuite les réglages importés. Aucun import ni export n’est nécessaire pour ce tuto.";
                    break;
                case 19:
                    AppearanceTabs.SelectedItem = SharingTab;
                    target = TutorialApplyBrowserToProject;
                    title = "Apparence de la maquette";
                    description = "Appliquer l’arborescence personnalisée au projet en cours enregistre l’arborescence et ses icônes dans la maquette. Les collègues équipés de BIMaestro retrouvent cette apparence à l’ouverture ou après synchronisation. Ce bouton exige un projet modifiable. Pour ce tuto, lis cette possibilité puis clique sur Suivant. Si la maquette partage déjà son apparence, l’enregistrement final met aussi ce style partagé à jour.";
                    break;
                default:
                    target = TutorialSave;
                    title = "Enregistrer";
                    description = "Clique sur « Enregistrer mes réglages » pour appliquer le fond, les icônes et les éventuels changements dans Propriétés et Accueil Revit. La couleur du texte du dossier a déjà été appliquée pendant l’exercice. Vérifie ensuite le résultat dans Revit. Le guide se termine une fois l’enregistrement réussi.";
                    break;
            }
            AppearanceGuideTitle.Text = $"{_appearanceGuideStep + 1}/{AppearanceGuideStepCount} · {title}";
            BIMaestro.Tutorials.TutorialAnalytics.Step("colors", _appearanceGuideStep, title);
            AppearanceGuideText.Text = description;
            // Properties steps: keep the card off the refresh button (14) and off the preview columns (15-16).
            AppearanceGuideCard.VerticalAlignment = _appearanceGuideStep == 11 || _appearanceGuideStep == 13 ||
                _appearanceGuideStep == 15 || _appearanceGuideStep == 16 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            AppearanceGuideCard.HorizontalAlignment = _appearanceGuideStep == 14 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            AppearanceGuidePrevious.IsEnabled = _appearanceGuideStep > 0;
            AppearanceGuideNext.Content = "Suivant";
            _appearanceGuideStepCompleted = (_appearanceGuideStep == 0 && TutorialEnablePanels.IsChecked == true)
                || (_appearanceGuideStep == 1 && IsRainbowPresetSelected())
                || (_appearanceGuideStep == 3 && ViewsAndFoldersTab.IsSelected)
                || (_appearanceGuideStep == 4 && TutorialEnableBrowser.IsChecked == true)
                || (_appearanceGuideStep == 5 && BrowserBackgroundTab.IsSelected)
                || (_appearanceGuideStep == 8 && BrowserIconsTab.IsSelected)
                || (_appearanceGuideStep == 9 && TutorialEnableIcons.IsChecked == true)
                || (_appearanceGuideStep == 10 && FindAppearanceIconRule("Plans d'étage") != null)
                || (_appearanceGuideStep == 11 && HasValidAppearanceIconRule("Plans d'étage"))
                || (_appearanceGuideStep == 12 && FindAppearanceIconRule("Vues 3D") != null)
                || (_appearanceGuideStep == 13 && HasValidAppearanceIconRule("Vues 3D"))
                || (_appearanceGuideStep == 14 && _propertiesEditor?.HasPreviewParameter("Zone de coupe", "Section Box") == true)
                // Never block on an already customized palette: the real action still advances by itself.
                || _appearanceGuideStep == 15 || _appearanceGuideStep == 16
                || (_appearanceGuideStep >= 17 && _appearanceGuideStep <= 19);
            AppearanceGuideNext.IsEnabled = _appearanceGuideStep < AppearanceGuideStepCount - 1 && _appearanceGuideStepCompleted;
            // "Open this tab" steps are skipped when the tab is already open.
            if (!movingBack && _appearanceGuideStepCompleted &&
                (_appearanceGuideStep == 3 || _appearanceGuideStep == 5 || _appearanceGuideStep == 8))
            {
                int skippedStep = _appearanceGuideStep;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_appearanceGuideStep != skippedStep || !IsVisible) return;
                    _appearanceGuideStep++;
                    ShowAppearanceGuideStep();
                }), DispatcherPriority.Background);
            }
            // A tab change materializes its content on the next layout pass.
            int expectedStep = _appearanceGuideStep;
            Dispatcher.BeginInvoke(new Action(() => HighlightAppearanceGuideTarget(target, expectedStep)), DispatcherPriority.Loaded);
        }

        private void HighlightAppearanceGuideTarget(FrameworkElement target, int expectedStep)
        {
            if (_appearanceGuideStep != expectedStep || !target.IsVisible || !IsVisible) return;
            if (expectedStep == 11 || expectedStep == 13)
                FocusNewestAppearanceIconName();
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return;
            _appearanceGuideLayer = layer;
            _appearanceGuideHighlight = new AppearanceGuideAdorner(target);
            layer.Add(_appearanceGuideHighlight);
        }

        private void FocusNewestAppearanceIconName()
        {
            int index = BrowserIcons.Rules.Count - 1;
            if (index < 0 || !string.IsNullOrWhiteSpace(BrowserIcons.Rules[index].Name)) return;
            TutorialIconRules.UpdateLayout();
            DependencyObject row = TutorialIconRules.ItemContainerGenerator.ContainerFromIndex(index);
            TextBox nameBox = FindAppearanceIconTextBox(row);
            if (nameBox == null) return;
            nameBox.BringIntoView();
            nameBox.Focus();
        }

        private static TextBox FindAppearanceIconTextBox(DependencyObject root)
        {
            if (root == null) return null;
            if (root is TextBox box) return box;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                TextBox found = FindAppearanceIconTextBox(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
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
                drawing.DrawRoundedRectangle(null, new Pen(BIMaestro.Tutorials.DemoTourPalette.Accent, 3),
                    new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
            }
        }
    }
}
