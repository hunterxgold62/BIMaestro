using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;

namespace BIMaestro.Tutorials
{
    internal sealed class DemoStep
    {
        internal readonly string Title, Text, Target;
        internal DemoStep(string title, string text, string target)
        { Title = title; Text = text; Target = target; }
    }

    // New tours only add metadata and a window hook. The controls keep their native handlers.
    internal static class DemoTourCatalog
    {
        internal static readonly IReadOnlyDictionary<string, string> Views = new Dictionary<string, string>
        {
            ["reservation"] = "BIMaestro - 01 Auto réservation",
            ["history"] = "BIMaestro - 02 Qui a fait ça",
            ["colors"] = "BIMaestro - 03 Couleurs et vues"
        };

        internal static readonly IReadOnlyDictionary<string, DemoStep[]> Steps = new Dictionary<string, DemoStep[]>
        {
            ["reservation"] = new[]
            {
                new DemoStep("Choisir le support", "Choisis « Mur » : tu vas placer toi-même une réservation dans le mur de la vue 01.", "hostMurCard"),
                new DemoStep("Choisir la forme", "Choisis « Rectangulaire ». La famille murale est déjà chargée dans la maquette de formation.", "shapeRectCard"),
                new DemoStep("Choisir le réseau", "Choisis « Canalisation ». La source est déjà « Maquette » et le mode automatique est désactivé.", "objPipeCard"),
                new DemoStep("Placer ta réservation", "Clique sur « Lancer », puis sélectionne dans la vue la canalisation et le mur. Le guide vérifiera qu'une réservation a bien été créée.", "DemoRunReservationButton")
            },
            ["history"] = new[]
            {
                new DemoStep("Voir les suppressions", "Deux objets de la scène ont été supprimés au démarrage du parcours. Dans Action, filtre sur « Suppressions ».", "ActionFilterCombo"),
                new DemoStep("Choisir un objet", "Sélectionne une carte de mobilier supprimé. Le troisième objet resté en place sert de repère dans la vue 02.", "VisualCardsList"),
                new DemoStep("Examiner le contexte", "Ouvre Détails pour retrouver l'auteur, la date et les informations enregistrées avant la suppression.", "DetailsButton"),
                new DemoStep("Faire réapparaître l'objet", "Clique sur « Restaurer les éléments » et confirme. Pikachu attendra que la restauration réussisse dans la maquette.", "RestoreDeletedButton")
            }
        };
    }

    internal static class DemoHistoryScene
    {
        internal static int Reset(Document doc, out int removedReservations)
        {
            removedReservations = 0;
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ouvre d'abord une maquette BIMaestro_Apprentissage pour recommencer les exercices.");

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            FamilyInstance witness = furniture.FirstOrDefault(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                == DemoProjectBuilder.HistoryFurnitureMark(0));
            if (witness == null)
                throw new InvalidOperationException("Cette maquette n'a pas la scène de mobilier. Crée une nouvelle maquette de formation.");
            Level level = doc.GetElement(witness.LevelId) as Level;
            if (level == null) throw new InvalidOperationException("Le niveau du mobilier de démonstration est introuvable.");

            var reservations = furniture.Where(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                == DemoProjectBuilder.DemoPrefix + "RESERVATION_CREEE")
                .Select(instance => instance.Id).ToList();
            int restoredFurniture = 0;
            using (var tx = new Transaction(doc, "BIMaestro - Recommencer les exercices"))
            {
                tx.Start();
                if (reservations.Count > 0) doc.Delete(reservations);
                removedReservations = reservations.Count;

                for (int index = 1; index <= 2; index++)
                {
                    XYZ target = DemoProjectBuilder.HistoryFurniturePosition(index);
                    string mark = DemoProjectBuilder.HistoryFurnitureMark(index);
                    FamilyInstance instance = furniture.FirstOrDefault(candidate =>
                        candidate.Id != witness.Id && !reservations.Contains(candidate.Id) &&
                        (candidate.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "") == mark);
                    if (instance == null)
                        instance = furniture.FirstOrDefault(candidate =>
                            candidate.Id != witness.Id && !reservations.Contains(candidate.Id) &&
                            candidate.GetTypeId().Equals(witness.GetTypeId()) &&
                            (candidate.Location as LocationPoint)?.Point.DistanceTo(target) <
                                UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters));
                    if (instance == null)
                    {
                        instance = doc.Create.NewFamilyInstance(target, witness.Symbol, level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        restoredFurniture++;
                    }
                    else if (instance.Location is LocationPoint point)
                        ElementTransformUtils.MoveElement(doc, instance.Id, target - point.Point);

                    Parameter parameter = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (parameter != null && !parameter.IsReadOnly) parameter.Set(mark);
                }
                tx.Commit();
            }
            doc.Save();
            return restoredFurniture;
        }

        internal static int Prepare(Document doc)
        {
            // Only touch the generated learning model, and only its marked furniture.
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ouvre d'abord une maquette BIMaestro_Apprentissage pour préparer cet exercice.");

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            if (!furniture.Any(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    == DemoProjectBuilder.DemoPrefix + "HISTORIQUE_TEMOIN"))
                throw new InvalidOperationException("Cette maquette utilise l'ancien scénario. Crée une nouvelle maquette de formation pour l'exercice de restauration.");
            var toRemove = furniture
                .Where(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(DemoProjectBuilder.DemoPrefix + "HISTORIQUE_A_RESTAURER_", StringComparison.Ordinal))
                .Select(instance => instance.Id).ToList();
            if (toRemove.Count == 0) return 0;

            Analyse.ElementHistoryTracker.PrimeDocument(doc);
            using (var tx = new Transaction(doc, "BIMaestro - Exercice historique : supprimer le mobilier"))
            {
                tx.Start();
                doc.Delete(toRemove);
                tx.Commit();
            }
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            doc.Save();
            return toRemove.Count;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class DemoToursCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "DemoTours";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var choice = new DemoChoiceWindow();
            new WindowInteropHelper(choice).Owner = data.Application.MainWindowHandle;
            choice.ShowDialog();
            string selectedChoice = choice.Choice;
            if (selectedChoice == "overview")
            {
                var overview = new DemoDiscoveryWindow();
                new WindowInteropHelper(overview).Owner = data.Application.MainWindowHandle;
                overview.ShowDialog();
                selectedChoice = overview.SelectedTour;
            }
            if (selectedChoice == "create")
            {
                try
                {
                    string path = DemoProjectBuilder.Create(data.Application);
                    TaskDialog.Show("BIMaestro - Formation", "Maquette créée et ouverte :\n" + path);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    TaskDialog.Show("BIMaestro - Formation", "Création impossible : " + ex.Message);
                    return Result.Failed;
                }
            }
            else if (!string.IsNullOrEmpty(selectedChoice))
            {
                UIDocument activeDocument = data.Application.ActiveUIDocument;
                if (activeDocument == null)
                {
                    TaskDialog.Show("BIMaestro - Formation", "Ouvre d'abord la maquette de formation ou un projet Revit.");
                    return Result.Cancelled;
                }
                if (selectedChoice == "reset")
                {
                    try
                    {
                        int restored = DemoHistoryScene.Reset(activeDocument.Document, out int removed);
                        DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                        TaskDialog.Show("BIMaestro - Formation",
                            "Exercices prêts à recommencer : " + restored + " meuble(s) remis en place et " +
                            removed + " réservation(s) du parcours retirée(s). Les couleurs personnelles restent inchangées.");
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        TaskDialog.Show("BIMaestro - Formation", "Réinitialisation impossible : " + ex.Message);
                        return Result.Failed;
                    }
                }
                DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                if (DemoTourCatalog.Views.TryGetValue(selectedChoice, out string viewName))
                {
                    View tourView = new FilteredElementCollector(activeDocument.Document)
                        .OfClass(typeof(View3D)).Cast<View3D>()
                        .FirstOrDefault(view => view.Name == viewName);
                    if (tourView != null && activeDocument.ActiveView.Id != tourView.Id)
                        activeDocument.ActiveView = tourView;
                }
                if (selectedChoice == "history")
                {
                    try
                    {
                        int removed = DemoHistoryScene.Prepare(activeDocument.Document);
                        TaskDialog.Show("BIMaestro - Formation", removed > 0
                            ? removed + " objets de la scène ont été supprimés et enregistrés dans l'historique. Le troisième reste visible. Suis Pikachu pour les restaurer."
                            : "La scène est déjà préparée. Suis Pikachu pour retrouver les suppressions dans l'historique.");
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        TaskDialog.Show("BIMaestro - Formation", "Préparation de l'exercice impossible : " + ex.Message);
                        return Result.Failed;
                    }
                }
                Couleur.AppearanceOnboarding.StartIntro(data.Application.MainWindowHandle, selectedChoice);
            }
            return Result.Succeeded;
        }
    }

    internal static class DemoTourService
    {
        private static readonly Dictionary<Window, DemoWindowGuide> ActiveGuides = new Dictionary<Window, DemoWindowGuide>();

        internal static bool AttachIfRequested(string id, Window window)
        {
            if (!Couleur.AppearanceOnboarding.ConsumeTourClick(id)) return false;
            if (DemoTourCatalog.Steps.TryGetValue(id, out DemoStep[] steps))
                ActiveGuides[window] = new DemoWindowGuide(window, steps);
            return true;
        }

        internal static void ReportRestoration(Window window)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteRestoration();
        }

        private sealed class DemoWindowGuide
        {
            private readonly Window _window;
            private readonly DemoStep[] _steps;
            private readonly Grid _root;
            private readonly Border _card;
            private readonly TextBlock _title, _text;
            private readonly Button _previous, _next;
            private AdornerLayer _layer;
            private TargetAdorner _adorner;
            private Action _detachAction;
            private int _index;
            private bool _completed;

            internal DemoWindowGuide(Window window, DemoStep[] steps)
            {
                _window = window; _steps = steps;
                _root = window.Content as Grid;
                if (_root == null) return;
                _card = new Border
                {
                    Width = 320, Padding = new Thickness(14), Margin = new Thickness(12),
                    CornerRadius = new CornerRadius(12), Background = Brushes.White,
                    BorderBrush = Brushes.DarkOrange, BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Grid.SetRowSpan(_card, Math.Max(1, _root.RowDefinitions.Count));
                Grid.SetColumnSpan(_card, Math.Max(1, _root.ColumnDefinitions.Count));
                Panel.SetZIndex(_card, 1000);
                var stack = new StackPanel(); _card.Child = stack;
                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) });
                _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15,
                    Width = 245, TextWrapping = TextWrapping.Wrap };
                heading.Children.Add(_title); stack.Children.Add(heading);
                _text = new TextBlock { Margin = new Thickness(0, 9, 0, 12), TextWrapping = TextWrapping.Wrap };
                stack.Children.Add(_text);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right };
                stack.Children.Add(buttons);
                _previous = AddButton(buttons, "Précédent", () => { _index--; ShowStep(); });
                _next = AddButton(buttons, "Suivant", () =>
                {
                    if (!_completed) return;
                    if (_index + 1 == _steps.Length) { Close(); return; }
                    _index++; ShowStep();
                });
                AddButton(buttons, "Quitter", Close);
                _root.Children.Add(_card);
                window.Loaded += OnLoaded;
                window.Closed += (_, __) => { Close(); ActiveGuides.Remove(window); };
            }

            private static Button AddButton(Panel panel, string text, Action action)
            {
                var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(4, 0, 0, 0) };
                button.Click += (_, __) => action(); panel.Children.Add(button);
                return button;
            }

            private void OnLoaded(object sender, RoutedEventArgs args)
            {
                _window.Loaded -= OnLoaded;
                ShowStep();
            }

            private void ShowStep()
            {
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_index < 0 || _index >= _steps.Length) return;
                _completed = false;
                DemoStep step = _steps[_index];
                // The reservation launch button is in the lower-right footer.
                // Keep the guide opposite it so the learner can actually click it.
                _card.HorizontalAlignment = step.Target == "DemoRunReservationButton"
                    ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _title.Text = $"{_index + 1}/{_steps.Length} · {step.Title}";
                _text.Text = step.Text;
                _previous.IsEnabled = _index > 0;
                _next.Content = _index + 1 == _steps.Length ? "Terminer" : "Suivant";
                _next.IsEnabled = false;
                int current = _index;
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Highlight(step.Target, current);
                    WatchAction(step.Target, current);
                }), DispatcherPriority.Loaded);
            }

            private void WatchAction(string targetName, int expectedIndex)
            {
                if (_index != expectedIndex || !(_window.FindName(targetName) is FrameworkElement target)) return;
                if (targetName == "RestoreDeletedButton") return; // Wait for a committed restoration.
                if (target is ButtonBase button)
                {
                    RoutedEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    button.Click += handler;
                    _detachAction = () => button.Click -= handler;
                }
                else if (target is Selector selector)
                {
                    SelectionChangedEventHandler handler = (_, __) =>
                    {
                        if (targetName != "ActionFilterCombo" ||
                            (selector.SelectedItem?.ToString() ?? "").IndexOf("supp", StringComparison.OrdinalIgnoreCase) >= 0)
                            CompleteStep(expectedIndex);
                    };
                    selector.SelectionChanged += handler;
                    _detachAction = () => selector.SelectionChanged -= handler;
                    if (targetName == "ActionFilterCombo" &&
                        (selector.SelectedItem?.ToString() ?? "").IndexOf("supp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _text.Text += " Le filtre est déjà actif : clique sur Suivant.";
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if (targetName == "VisualCardsList" && selector.SelectedItem != null)
                    {
                        _text.Text += " Une carte est déjà sélectionnée : examine-la, puis clique sur Suivant.";
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                }
                else
                {
                    MouseButtonEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    target.AddHandler(UIElement.MouseLeftButtonUpEvent, handler, true);
                    _detachAction = () => target.RemoveHandler(UIElement.MouseLeftButtonUpEvent, handler);
                }
            }

            internal void CompleteRestoration()
            {
                if (_index == _steps.Length - 1 && _steps[_index].Target == "RestoreDeletedButton")
                    CompleteStep(_index);
            }

            private void CompleteStep(int expectedIndex)
            {
                if (_index != expectedIndex || _completed) return;
                _completed = true;
                if (_index + 1 == _steps.Length)
                {
                    _text.Text = _steps[_index].Target == "RestoreDeletedButton"
                        ? "✓ Le mobilier a réapparu. BIMaestro l'a sélectionné et cadré dans la vue : compare-le avec l'objet témoin, puis termine le parcours."
                        : "✓ Action confirmée dans la maquette. Tu peux terminer ce parcours.";
                    _next.IsEnabled = true;
                }
                else
                {
                    _index++;
                    ShowStep();
                }
            }

            private void Highlight(string targetName, int expectedIndex)
            {
                if (_index != expectedIndex || !_window.IsVisible) return;
                if (!(_window.FindName(targetName) is FrameworkElement target) || !target.IsVisible) return;
                target.BringIntoView();
                _layer = AdornerLayer.GetAdornerLayer(target);
                if (_layer == null) return;
                _adorner = new TargetAdorner(target);
                _layer.Add(_adorner);
            }

            private void RemoveHighlight()
            {
                if (_adorner != null) _layer?.Remove(_adorner);
                _adorner = null; _layer = null;
            }

            private void Close()
            {
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_root != null && _root.Children.Contains(_card)) _root.Children.Remove(_card);
            }
        }

        private sealed class TargetAdorner : Adorner
        {
            internal TargetAdorner(UIElement target) : base(target)
            { IsHitTestVisible = false; Focusable = false; }
            protected override void OnRender(DrawingContext drawing)
            {
                var bounds = new Rect(AdornedElement.RenderSize);
                if (bounds.Width > 0 && bounds.Height > 0)
                    drawing.DrawRoundedRectangle(null, new Pen(Brushes.DarkOrange, 3),
                        new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
            }
        }
    }

    internal sealed class DemoChoiceWindow : Window
    {
        internal string Choice { get; private set; }
        internal DemoChoiceWindow()
        {
            Title = "BIMaestro - Parcours guidés";
            Width = 450; Height = 420; MinHeight = 370; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(20) }; scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = "Apprendre BIMaestro avec Pikachu", FontSize = 18,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = "Découvre d'abord ce que font les trois commandes. À la fin, Pikachu te proposera d'en essayer une dans la maquette.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            Button quick = AddChoice(stack, "Commencer la découverte · 2 min", "overview");
            quick.FontWeight = FontWeights.SemiBold;
            quick.BorderBrush = Brushes.DarkOrange;
            quick.BorderThickness = new Thickness(2);
            AddChoice(stack, "Créer et ouvrir la maquette de formation", "create");
            var detailed = new Expander { Header = "Aller directement aux exercices détaillés",
                Margin = new Thickness(0, 6, 0, 12), IsExpanded = false };
            stack.Children.Add(detailed);
            var detailChoices = new StackPanel { Margin = new Thickness(8, 10, 0, 0) };
            detailed.Content = detailChoices;
            AddChoice(detailChoices, "1 · Auto réservation", "reservation");
            AddChoice(detailChoices, "2 · Qui a fait ça ?", "history");
            AddChoice(detailChoices, "3 · Couleurs et vues", "colors");
            AddChoice(stack, "Recommencer les exercices de la maquette", "reset");
        }
        private Button AddChoice(Panel panel, string label, string id)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, __) => { Choice = id; Close(); };
            panel.Children.Add(button);
            return button;
        }
    }

    internal sealed class DemoDiscoveryWindow : Window
    {
        internal string SelectedTour { get; private set; }

        internal DemoDiscoveryWindow()
        {
            Title = "BIMaestro - Découverte rapide";
            Width = 540; Height = 620; MinWidth = 480; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = "BIMaestro en 2 minutes", FontSize = 21,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(new TextBlock { Text = "Voici les trois fonctions de la maquette de formation. Cette découverte ne modifie pas le projet ; tu choisiras ensuite si tu veux pratiquer.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            AddCard(stack, "1 · Auto réservation",
                "Quand un réseau traverse un mur, BIMaestro place une famille de réservation au croisement. Dans l'exercice, tu choisis toi-même la canalisation puis le mur et tu examines le résultat.");
            AddCard(stack, "2 · Qui a fait ça ?",
                "Retrouve l'auteur et le contexte d'une modification. Dans l'exercice, deux meubles sont supprimés, puis tu les fais réapparaître à partir de l'historique.");
            AddCard(stack, "3 · Couleurs et vues",
                "Personnalise l'arborescence sans renommer ni recréer les vues. Dans l'exercice, tu changes le fond et ajoutes des icônes aux dossiers Plans d'étage et Vues 3D.");
            stack.Children.Add(new TextBlock { Text = "Envie d'essayer dans la maquette ?",
                FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) });
            stack.Children.Add(new TextBlock { Text = "Choisis un exercice détaillé, ou termine ici et reviens plus tard.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var exercises = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(exercises);
            AddExerciseButton(exercises, "Auto résa", "reservation");
            AddExerciseButton(exercises, "Qui a fait ça ?", "history");
            AddExerciseButton(exercises, "Couleurs et vues", "colors");
            var close = new Button { Content = "Terminer pour l'instant", Padding = new Thickness(12, 7, 12, 7),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            close.Click += (_, __) => Close();
            stack.Children.Add(close);
        }

        private void AddExerciseButton(Panel parent, string label, string tourId)
        {
            var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, __) => { SelectedTour = tourId; Close(); };
            parent.Children.Add(button);
        }

        private static void AddCard(Panel parent, string title, string description)
        {
            var card = new Border { Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(10), BorderBrush = Brushes.DarkOrange,
                BorderThickness = new Thickness(1), Background = Brushes.WhiteSmoke };
            parent.Children.Add(card);
            var body = new StackPanel(); card.Child = body;
            body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 10) });
        }
    }
}
