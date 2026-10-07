using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BIMaestro.Tutorials
{
    internal static class DemoBoosterExercise
    {
        private static Document _document;
        private static HashSet<long> _valves;
        private static HashSet<long> _rotated;
        private static HashSet<long> _copied;
        private static Window _card;
        private static TextBlock _text;
        private static Button _finish;
        private static int _step;
        internal static void Begin(UIApplication app)
        {
            _card?.Close();
            _document = app.ActiveUIDocument.Document;
            _valves = new HashSet<long>(new FilteredElementCollector(_document).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(instance => instance.Symbol?.Family?.Name == "Vanne papillon - 50-300 mm" && instance.Category?.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeAccessory &&
                    (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "").StartsWith(DemoProjectBuilder.DemoPrefix + "BOOSTER_"))
                .Select(instance => (long)instance.Id.GetIdValue()));
            if (_valves.Count != 6) throw new InvalidOperationException("La scène MEP Booster doit contenir six vannes raccordées.");
            _rotated = new HashSet<long>();
            _copied = new HashSet<long>();
            MepBooster.MepBoosterService.TutorialActivation = Activation;
            MepBooster.MepBoosterService.TutorialProgress = Report;
            MepBooster.MepBoosterService.TutorialCopy = ReportCopy;
            _step = MepBooster.MepBoosterService.IsEnabled ? 1 : 0;
            _card = new Window { Title = "BIMaestro — Guide MEP Booster", Width = 430, SizeToContent = SizeToContent.Height, MinHeight = 290,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Brushes.White,
                Left = SystemParameters.WorkArea.Right - 460, Top = SystemParameters.WorkArea.Bottom - 320 };
            new WindowInteropHelper(_card).Owner = app.MainWindowHandle;
            var panel = new StackPanel { Margin = new Thickness(18) };
            _card.Content = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2), Child = panel };
            panel.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(), Width = 34, Height = 34,
                HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(new TextBlock { Text = "Découvrir MEP Booster", FontSize = 18, Foreground = DemoTourPalette.Accent,
                Margin = new Thickness(0, 8, 0, 8) });
            _text = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 95 };
            panel.Children.Add(_text);
            var quit = new Button { Content = "Quitter le guide", Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
            Window card = _card;
            _finish = quit;
            quit.Click += (_, __) => {
                bool finished = _step == 6;
                card.Close();
                if (finished) DemoTourCompletion.Show(app.MainWindowHandle, "mep-booster", "Tu as terminé le tutoriel MEP Booster : rotations et copie d’orientation ont été appliquées.");
            };
            card.Closed += (_, __) => { if (ReferenceEquals(_card, card)) { _card = null; _document = null; MepBooster.MepBoosterService.TutorialActivation = null; MepBooster.MepBoosterService.TutorialProgress = null; MepBooster.MepBoosterService.TutorialCopy = null; Couleur.AppearanceOnboarding.ConsumeTourClick("mep-booster"); } };
            _card.SizeChanged += (_, __) => { if (_card != null) _card.Top = Math.Max(SystemParameters.WorkArea.Top + 16, SystemParameters.WorkArea.Bottom - _card.ActualHeight - 24); };
            UpdateText();
            if (_step > 0) _card.Show();
            if (_step == 0) Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "mep-booster");
        }
        internal static void Activation(bool enabled)
        {
            if (_card == null) return;
            Couleur.AppearanceOnboarding.ConsumeTourClick("mep-booster");
            if (enabled) { _step = Math.Max(_step, 1); _card.Show(); }
            else _text.Text = "MEP Booster est OFF. Clique à nouveau sur le bouton pour l’activer, puis sélectionne une vanne dans la vue 09.";
            if (enabled) UpdateText();
        }
        internal static void Report(Document document, IEnumerable<ElementId> ids, string action, double angle = 0)
        {
            if (_card == null || !Equals(document, _document) || ids == null) return;
            var selected = ids.Select(id => id.GetIdValue()).ToList();
            if (selected.Count == 0 || !selected.All(id => _valves.Contains(id))) return;
            if (action == "pill" && _step == 1) _step = 2;
            if (action == "preview" && _step == 2 && Math.Abs(angle) > 0.01) _step = 3;
            if (action == "applied" && _step >= 3 && Math.Abs(angle % 360) > 0.01)
            {
                foreach (long id in selected) _rotated.Add(id);
                if (_step < 5) _step = _rotated.Count >= 2 ? 5 : 4;
            }
            UpdateText();
        }
        internal static void ReportCopy(Document document, ElementId reference, IEnumerable<ElementId> targets)
        {
            if (_card == null || !Equals(document, _document) || _step != 5 || targets == null) return;
            var copied = targets.Select(id => (long)id.GetIdValue()).Distinct().ToList();
            bool validReference = _rotated.Contains(reference.GetIdValue());
            if (validReference)
                foreach (long id in copied.Where(id => _valves.Contains(id) && !_rotated.Contains(id))) _copied.Add(id);
            foreach (long id in copied.Where(id => _valves.Contains(id))) _rotated.Add(id);
            bool valid = validReference && _copied.Count >= 2;
            if (valid) _step = 6;
            UpdateText();
            if (!valid) _text.Text += "\nPour valider l’essai, utilise une vanne tournée comme référence et copie sur deux autres vannes, ensemble ou en plusieurs copies. " + _copied.Count + " vanne(s) validée(s) sur 2.";
        }
        private static void UpdateText()
        {
            if (_text == null) return;
            if (_finish != null) _finish.Content = _step == 6 ? "Terminer le tutoriel" : "Quitter le guide";
            _text.Text = new[] {
                "1/7 · Active MEP Booster dans l’onglet BIMaestro. Le bouton passe de OFF à ON. La vue 09 contient six vannes, deux filtres à tamis en Y, deux compteurs d’eau et quatre coudes. Commence l’exercice sur les vannes.",
                "2/7 · Clique sur une seule vanne. Après 0,2 seconde de sélection stable, une pastille MEP apparaît près de la sélection. Tu peux zoomer pour mieux voir sa poignée.",
                "3/7 · Survole la pastille puis un angle, par exemple 90°. Observe l’aperçu de rotation avant de cliquer. Les angles incompatibles avec les raccordements restent désactivés.",
                "4/7 · Clique sur un angle disponible pour appliquer la rotation. Observe la poignée de la vanne : la rotation se fait autour de la canalisation, en conservant les raccordements.",
                "5/7 · Première rotation appliquée ! Clique ailleurs que sur la rosace pour la fermer, puis sélectionne une autre vanne et applique une rotation disponible. Tu peux aussi sélectionner plusieurs vannes compatibles.",
                "6/7 · Copier l’orientation : clique ailleurs pour fermer la rosace, puis sélectionne une seule vanne que tu viens de tourner. Dans sa rosace, clique sur le bouton « Copier l’orientation ». Sélectionne au moins deux autres vannes encore intactes, clique sur Terminer, observe l’aperçu vert puis confirme la copie.",
                "7/7 · Copie validée ! Au moins deux vannes encore intactes ont reçu l’orientation et le sens d’une vanne modifiée. Compare leurs poignées avec la référence. Clique hors de la rosace pour la fermer ; le bouton ON/OFF permet de désactiver MEP Booster."
            }[_step];
        }
    }
}
