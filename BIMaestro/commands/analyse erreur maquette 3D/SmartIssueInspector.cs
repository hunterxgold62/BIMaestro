using Autodesk.Revit.DB;
using Grid = System.Windows.Controls.Grid;
using WpfVisibility = System.Windows.Visibility;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMaestro.Localization;

namespace Analyse
{
    public sealed class SmartIssueInspector : Window
    {
        private readonly ModelIssue _issue;
        private readonly TextBlock _stale = new TextBlock { Text = UiLanguage.T("La maquette a changé. Relancez l'analyse pour actualiser les résultats.", "The model has changed. Run the analysis again to refresh the results."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, Visibility = WpfVisibility.Collapsed, Margin = new Thickness(0, 4, 0, 8) };
        private readonly Image _preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 240, Margin = new Thickness(0, 12, 0, 12) };
        private readonly StackPanel _visual = new StackPanel();
        private readonly TextBlock _analysisNotice = Text(UiLanguage.T("Analyse en cours : les détails sont consultables. Le cadrage et les captures Revit seront disponibles à la fin.", "Analysis in progress: details can be viewed. Framing and Revit captures will be available when it finishes."), 12);
        private Button _focusButton, _captureButton;
        private Button _contextButton, _reservationButton;
        private readonly TextBlock _actionMessage = Text("", 12);
        private SmartVisualScene _displayedScene;
        private bool? _displayedPending;
        private string _displayedCapture;
        public SmartIssueInspector(ModelIssue issue, Action<ModelIssue> focus, Func<ModelIssue, string, string, bool> save,
            Action<ModelIssue> capture, Action<ModelIssue, int> navigate, Action toggleContext = null, Action<ModelIssue> reserve = null)
        {
            _issue = issue; Title = UiLanguage.T("Clash 3D · inspection", "Clash 3D · inspection"); Width = 650; Height = 760; MinWidth = 480; MinHeight = 500;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/" + typeof(SmartIssueInspector).Assembly.GetName().Name
                + ";component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "App.Background");
            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel();
            header.Children.Add(Text(issue.Category, 22, true));
            header.Children.Add(Text(issue.ContextText, 12));
            header.Children.Add(_stale);
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
            _focusButton = Button(UiLanguage.T("Voir en 3D", "Show in 3D"), () => focus(issue), true); actions.Children.Add(_focusButton);
            _contextButton = Button(UiLanguage.T("Voir autour", "Show surroundings"), () => toggleContext?.Invoke()); _contextButton.Visibility = WpfVisibility.Collapsed;
            _contextButton.ToolTip = UiLanguage.T("Retirer la boîte de coupe, puis revenir au conflit avec le même bouton.", "Remove the section box, then return to the clash with the same button."); actions.Children.Add(_contextButton);
            actions.Children.Add(Button(UiLanguage.T("Précédent", "Previous"), () => navigate(issue, -1)));
            actions.Children.Add(Button(UiLanguage.T("Suivant", "Next"), () => navigate(issue, 1)));
            header.Children.Add(actions); header.Children.Add(_analysisNotice); root.Children.Add(header);
            _reservationButton = Button(UiLanguage.T("Créer la réservation", "Create opening"), () => reserve?.Invoke(issue));
            _reservationButton.Visibility = issue.CanCreateReservation && !issue.IsApproximate && reserve != null ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            _reservationButton.ToolTip = UiLanguage.T("Créer une réservation avec la famille, la forme et les paramètres configurés dans Autoréservation.", "Create an opening using the family, shape and parameters configured in Auto-Opening.");
            header.Children.Add(_reservationButton); header.Children.Add(_actionMessage);
            var body = new StackPanel();
            body.Children.Add(Text(issue.ConfidenceText, 15, true));
            body.Children.Add(Text(issue.Message, 13));
            body.Children.Add(_visual);
            var screen = new Expander { Header = UiLanguage.T("Capture Revit (facultative)", "Revit capture (optional)"), Margin = new Thickness(0, 10, 0, 10) };
            var screenBody = new StackPanel(); _captureButton = Button(UiLanguage.T("Capturer un aperçu", "Capture a preview"), () => capture(issue)); screenBody.Children.Add(_captureButton);
            screenBody.Children.Add(_preview); screen.Content = screenBody; body.Children.Add(screen);
            body.Children.Add(ObjectCard(UiLanguage.T("Objet contrôlé · orange", "Checked element · orange"), issue.ElementLabel, issue.ElementTypeName, "#E15924"));
            body.Children.Add(ObjectCard(UiLanguage.T("Obstacle · bleu", "Obstacle · blue"), issue.ObstacleLabel ?? UiLanguage.T("Aucun second objet", "No second element"), issue.RelatedTypeName, "#246DC4"));
            if (issue.LinkedElementId != Autodesk.Revit.DB.ElementId.InvalidElementId)
                body.Children.Add(Text(UiLanguage.T("Identifiant de l'obstacle dans le lien : ", "Obstacle ID in the link: ") + issue.LinkedElementId.GetIdLongValue(), 12));
            if (issue.IntersectionVolumeMm3 > 0)
                body.Children.Add(Text(UiLanguage.T("Volume témoin : ", "Reference volume: ") + issue.IntersectionVolumeMm3.ToString("N1") + UiLanguage.T(" mm³ (plus grande intersection de solides détectée).", " mm³ (largest solid intersection detected)."), 12));
            body.Children.Add(Text(UiLanguage.T("Suite conseillée", "Recommended next step"), 15, true)); body.Children.Add(Text(issue.AdviceText, 13));
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            var decision = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            decision.Children.Add(Text(UiLanguage.T("Décision", "Decision"), 15, true));
            var choices = new[] { ModelIssue.StatusActive, ModelIssue.StatusToFix, ModelIssue.StatusReview, ModelIssue.StatusFixed, ModelIssue.StatusIgnored }
                .Select(s => new SmartDisplayChoice(s)).ToList();
            var status = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(c => c.Value == issue.StatusText),
                Margin = new Thickness(0, 4, 0, 10), Height = 34 };
            decision.Children.Add(status);
            var comment = new TextBox { Text = issue.StatusComment ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8), ToolTip = UiLanguage.T("Commentaire sur votre décision.", "Comment on your decision.") };
            decision.Children.Add(comment);
            var saved = Text(issue.StatusUpdatedText, 12);
            var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(Button(UiLanguage.T("Enregistrer la décision", "Save decision"), () =>
            {
                if (save(issue, (status.SelectedItem as SmartDisplayChoice)?.Value ?? ModelIssue.StatusActive, comment.Text)) saved.Text = UiLanguage.T("Décision enregistrée · ", "Decision saved · ") + issue.StatusUpdatedText;
                else saved.Text = UiLanguage.T("Décision non sauvegardée. Consultez le message de la fenêtre principale.", "Decision not saved. See the message in the main window.");
            }, true));
            footer.Children.Add(saved);
            decision.Children.Add(Text(UiLanguage.T("« Traité » et « À ignorer » sont des décisions manuelles. Si la géométrie du conflit change, la décision repasse à « À revoir » lors de l'analyse.", "\"Resolved\" and \"Ignore\" are manual decisions. If the clash geometry changes, the decision reverts to \"To review\" at the next analysis."), 12));
            body.Children.Add(decision);
            Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
            RefreshPreview();
            SetScanning(false);
        }
        private static TextBlock Text(string value, double size, bool bold = false)
        {
            var text = new TextBlock { Text = value ?? "", FontSize = size, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 4, 0, 6) };
            text.SetResourceReference(TextBlock.ForegroundProperty, size < 14 ? "Text.Secondary" : "Text.Primary"); return text;
        }
        private static Border ObjectCard(string title, string label, string type, string color)
        {
            var content = new StackPanel(); content.Children.Add(Text(title, 12, true)); content.Children.Add(Text(label, 14, true));
            if (!string.IsNullOrWhiteSpace(type)) content.Children.Add(Text(type, 12));
            var card = new Border { Child = content, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 8, 0, 8),
                BorderBrush = (Brush)new BrushConverter().ConvertFromString(color), BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(6) };
            card.SetResourceReference(Border.BackgroundProperty, "Surface"); return card;
        }
        private static Button Button(string label, Action action, bool primary = false)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 4, 8, 4), MinHeight = 32,
                MinWidth = 0, Padding = new Thickness(12, 0, 12, 0) };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SecondaryButton"); button.Click += (s, e) => action(); return button;
        }
        public void RefreshPreview()
        {
            if (!ReferenceEquals(_displayedScene, _issue.VisualScene) || _displayedPending != _issue.VisualPending)
            {
                _displayedScene = _issue.VisualScene; _displayedPending = _issue.VisualPending;
                _visual.Children.Clear();
                if (_displayedScene?.HasGeometry == true)
                {
                    _visual.Children.Add(new SmartClashViewer(_displayedScene));
                    _visual.Children.Add(Text(UiLanguage.T("Glissez pour tourner · Molette pour zoomer · Repère : centre de la zone détectée", "Drag to orbit · Scroll to zoom · Marker: center of the detected zone"), 12));
                    _visual.Children.Add(Text(UiLanguage.T("Vue locale des formes réelles, coupées au bord du cadrage. Le repère ne représente pas le volume exact de l'intersection.", "Local view of the actual shapes, clipped at the framing edge. The marker does not represent the exact intersection volume."), 12));
                }
                else _visual.Children.Add(Text(_issue.VisualPending ? UiLanguage.T("Aperçu en préparation… Les objets et le résultat sont déjà consultables.", "Preparing preview… The elements and result can already be viewed.")
                    : UiLanguage.T("Aperçu indisponible. Utilisez « Voir en 3D » pour retrouver les objets dans Revit.", "Preview unavailable. Use \"Show in 3D\" to locate the elements in Revit."), 13));
                if (!string.IsNullOrWhiteSpace(_displayedScene?.Notice)) _visual.Children.Add(Text(_displayedScene.Notice, 12));
            }
            if (_displayedCapture == _issue.ThumbnailPath) return;
            _displayedCapture = _issue.ThumbnailPath;
            _preview.Visibility = WpfVisibility.Collapsed;
            if (string.IsNullOrWhiteSpace(_issue.ThumbnailPath) || !File.Exists(_issue.ThumbnailPath)) return;
            try
            {
                using (var stream = File.OpenRead(_issue.ThumbnailPath))
                { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); _preview.Source = image; }
                _preview.Visibility = WpfVisibility.Visible;
            }
            catch (IOException) { }
        }
        public void SetScanning(bool scanning)
        {
            _focusButton.IsEnabled = _captureButton.IsEnabled = !scanning;
            _analysisNotice.Visibility = scanning ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            if (scanning) _contextButton.IsEnabled = _reservationButton.IsEnabled = false;
        }
        public void SetActions(ModelIssue displayed, bool context, bool busy, bool stale)
        {
            _contextButton.Visibility = ReferenceEquals(displayed, _issue) ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            _contextButton.Content = context ? UiLanguage.T("Isoler le conflit", "Isolate clash") : UiLanguage.T("Voir autour", "Show surroundings");
            _contextButton.IsEnabled = !busy;
            _focusButton.IsEnabled = _captureButton.IsEnabled = !busy;
            _reservationButton.IsEnabled = !busy && !stale;
        }
        public void SetActionMessage(string message) => _actionMessage.Text = message;
        public void MarkStale() { _stale.Visibility = WpfVisibility.Visible; _reservationButton.IsEnabled = false; }
    }
}
