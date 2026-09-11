using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BIMaestro.VideoGames
{
    public partial class GameMepShareWindow : Window
    {
        private readonly GameSceneData _scene;
        private GameMepShareState _state;
        private GameSceneData? _lighterScene;
        private GameMepWebPackage.ExportAnalysis? _analysis;
        private bool _useLighter, _busy, _analyzing = true;
        private long _originalSize, _lighterSize;
        private GameSceneData ExportScene => _useLighter && _lighterScene != null ? _lighterScene : _scene;
        private CancellationTokenSource? _cancellation;
        private readonly CancellationTokenSource _sizeCalculationCancellation =
            new CancellationTokenSource();

        internal GameMepShareWindow(GameSceneData scene)
        {
            InitializeComponent();
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));
            PublicationNameTextBox.Text = string.IsNullOrWhiteSpace(scene.ViewName)
                ? scene.MepGraph.DocumentTitle
                : scene.MepGraph.DocumentTitle + " · " + scene.ViewName;
            _state = GameMepPublishClient.Load(scene.MepGraph);
            string[] names = scene.Elements.SelectMany(item => item.WebProperties.Keys)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(item => item).ToArray();
            AuthorizedParametersText.Text = names.Length == 0
                ? "Aucun paramètre métier"
                : names.Length + " paramètres sélectionnés";
            AuthorizedParametersText.ToolTip = names.Length == 0
                ? "Seules les informations techniques minimales du graphe seront publiées."
                : string.Join(", ", names);
            EstimatedSizeText.Text = "Calcul de la taille compressée…";
            ShowState();
            AnalyzeAsync();
        }

        private async void AnalyzeAsync()
        {
            var token = _sizeCalculationCancellation.Token;
            string name = PublicationNameTextBox.Text.Trim();
            try
            {
                var analysis = await Task.Run(() => GameMepWebPackage.AnalyzeExport(_scene, token), token);
                var sizes = await Task.Run(() => {
                    token.ThrowIfCancellationRequested();
                    var original = GameMepWebPackage.Build(_scene, name);
                    var lighter = GameMepWebPackage.Build(analysis.LighterScene, name);
                    return new[] { original.Bytes.LongLength + original.Assets.Sum(x => x.Size), lighter.Bytes.LongLength + lighter.Assets.Sum(x => x.Size) };
                }, token);
                if (token.IsCancellationRequested) return;
                _analysis = analysis; _lighterScene = analysis.LighterScene;
                _originalSize = sizes[0]; _lighterSize = sizes[1];
                RefreshAnalysis();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                if (!token.IsCancellationRequested) {
                    EstimatedSizeText.Text = "Analyse indisponible";
                    OptimizationText.Text = "L’export original reste disponible. " + exception.Message;
                }
            }
            finally { if (!token.IsCancellationRequested) { _analyzing = false; SetBusy(_busy); } }
        }

        private void RefreshAnalysis()
        {
            EstimatedSizeText.Text = "Taille compressée : " + FormatBytes(_useLighter ? _lighterSize : _originalSize) + " / 512 Mo";
            long gain = Math.Max(0, _originalSize - _lighterSize);
            OptimizationText.Text = gain > 0
                ? FormatBytes(_originalSize) + " → " + FormatBytes(_lighterSize) + "\nGain calculé : " + FormatBytes(gain) + " (" + (100.0 * gain / _originalSize).ToString("0.0") + " %)"
                : "Aucun gain utile détecté pour les portes et garde-corps de cette vue.";
            LightenButton.Content = _useLighter ? "Revenir aux détails d’origine" : "Alléger les détails";
            EstimatedSizeDetailsText.Text = "Géométrie 3D : " + FormatBytes(ExportScene.WebTiles.Sum(x => x.Size)) +
                " (" + ExportScene.WebTiles.Count + " fichiers)\n" + BuildGeometryDetails();
        }

        private void LightenButton_Click(object sender, RoutedEventArgs e)
        {
            _useLighter = !_useLighter;
            RefreshAnalysis();
            StatusText.Text = _useLighter ? "Détails allégés pour la prochaine publication." : "Géométrie d’origine pour la prochaine publication.";
        }

        private string BuildGeometryDetails()
        {
            if (_analysis == null) return string.Empty;
            var bytesByElementIndex = _analysis.EstimatedBytes;
            var elementsByIndex = _scene.Elements
                .Where(element => element.WebElementIndex >= 0)
                .ToDictionary(element => element.WebElementIndex);
            var rankedElements = bytesByElementIndex
                .Where(pair => elementsByIndex.ContainsKey(pair.Key))
                .Select(pair => new
                {
                    Element = elementsByIndex[pair.Key],
                    Bytes = pair.Value,
                    Triangles = _analysis.Triangles.TryGetValue(pair.Key, out long triangles) ? triangles : 0
                })
                .OrderByDescending(item => item.Bytes)
                .ToArray();
            var categories = rankedElements
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Element.Category)
                    ? "Sans catégorie"
                    : item.Element.Category)
                .Select(group => new { Name = group.Key, Bytes = group.Sum(item => item.Bytes) })
                .OrderByDescending(item => item.Bytes)
                .Take(5);
            string categoryDetails = string.Join("\n", categories.Select((item, index) =>
                (index + 1) + ". " + item.Name + " ≈ " + FormatBytes(item.Bytes)));
            string elementDetails = string.Join("\n", rankedElements.Take(10).Select((item, index) =>
                (index + 1) + ". #" + item.Element.ElementId + " · " +
                (string.IsNullOrWhiteSpace(item.Element.Name) ? item.Element.TypeName : item.Element.Name) +
                " · " + item.Element.DocumentTitle + " ≈ " + FormatBytes(item.Bytes) +
                " · " + item.Triangles.ToString("N0") + " triangles"));
            string typeDetails = string.Join("\n", rankedElements
                .GroupBy(item => item.Element.Category + " · " + item.Element.TypeName)
                .Select(group => new { Name = group.Key, Bytes = group.Sum(item => item.Bytes), Count = group.Count() })
                .OrderByDescending(item => item.Bytes).Take(5)
                .Select(item => item.Name + " (" + item.Count + ") ≈ " + FormatBytes(item.Bytes)));
            return "\nDIAGNOSTIC DE L’EXPORT D’ORIGINE\nPoids par élément estimé ; nombre de triangles mesuré.\n\nCATÉGORIES LES PLUS LOURDES\n" +
                categoryDetails + "\n\nTYPES LES PLUS LOURDS\n" + typeDetails + "\n\nÉLÉMENTS LES PLUS LOURDS\n" + elementDetails;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + " Mo";
            return Math.Max(1, bytes / 1024).ToString() + " Ko";
        }

        private async void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PublicationNameTextBox.Text))
            {
                StatusText.Text = "Donnez un nom à la publication.";
                return;
            }
            SetBusy(true);
            _cancellation = new CancellationTokenSource();
            var progress = new Progress<GameMepPublishProgress>(value =>
            {
                StatusText.Text = value.Message;
                PublishProgressBar.Value = value.Percentage * 100.0;
            });
            try
            {
                _state = await GameMepPublishClient.PublishAsync(
                    ExportScene, PublicationNameTextBox.Text.Trim(), progress,
                    _cancellation.Token);
                ShowState();
                StatusText.Text = "Révision " + _state.Revision +
                    " publiée. Les liens restent valables.";
            }
            catch (OperationCanceledException) { StatusText.Text = "Publication annulée."; }
            catch (Exception exception)
            {
                Debug.WriteLine("Publication MEP impossible : " + exception);
                StatusText.Text = "Publication impossible : " + exception.Message;
            }
            finally { SetBusy(false); }
        }

        private async void ExtendButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            try
            {
                await GameMepPublishClient.ExtendAsync(
                    _scene.MepGraph, _state, 30, CancellationToken.None);
                ShowState();
                StatusText.Text = "Le partage est prolongé de 30 jours.";
            }
            catch (Exception exception) { StatusText.Text = exception.Message; }
            finally { SetBusy(false); }
        }

        private async void RevokeButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this,
                    "Révoquer immédiatement les deux liens ?",
                    "Révoquer le partage", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true);
            try
            {
                await GameMepPublishClient.RevokeAsync(
                    _scene.MepGraph, _state, CancellationToken.None);
                _state = new GameMepShareState();
                ShowState();
                StatusText.Text = "Le partage a été révoqué.";
            }
            catch (Exception exception) { StatusText.Text = exception.Message; }
            finally { SetBusy(false); }
        }

        private void ShowState()
        {
            bool available = !string.IsNullOrWhiteSpace(_state.PublicationId) &&
                !string.IsNullOrWhiteSpace(_state.ViewerUrl) &&
                !string.IsNullOrWhiteSpace(_state.EditorUrl);
            LinksPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            ViewerUrlTextBox.Text = _state.ViewerUrl;
            EditorUrlTextBox.Text = _state.EditorUrl;
            PublishButton.Content = available ? "Publier une nouvelle révision" : "Partager sur le web";
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            LightenButton.IsEnabled = !busy && !_analyzing && _lighterSize < _originalSize;
            PublishButton.IsEnabled = !busy;
            PublicationNameTextBox.IsEnabled = !busy;
        }

        private void CopyViewerButton_Click(object sender, RoutedEventArgs e) =>
            Copy(_state.ViewerUrl, "Lien de consultation copié.");
        private void CopyEditorButton_Click(object sender, RoutedEventArgs e) =>
            Copy(_state.EditorUrl, "Lien d’édition copié.");
        private void Copy(string value, string message)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Clipboard.SetText(value); StatusText.Text = message;
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _sizeCalculationCancellation.Cancel();
            _sizeCalculationCancellation.Dispose();
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            base.OnClosed(e);
        }
    }
}
