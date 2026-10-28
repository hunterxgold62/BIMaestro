using BIMaestro.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
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
                ? UiLanguage.T("Aucun paramètre métier", "No business parameters")
                : names.Length + UiLanguage.T(" paramètres sélectionnés", " selected parameters");
            AuthorizedParametersText.ToolTip = names.Length == 0
                ? UiLanguage.T("Seules les informations techniques minimales du graphe seront publiées.", "Only the minimum technical graph information will be published.")
                : string.Join(", ", names);
            EstimatedSizeText.Text = UiLanguage.T("Calcul de la taille compressée…", "Calculating compressed size…");
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
                    EstimatedSizeText.Text = UiLanguage.T("Analyse indisponible", "Analysis unavailable");
                    OptimizationText.Text = UiLanguage.T("L’export original reste disponible. ", "The original export remains available. ") + exception.Message;
                }
            }
            finally { if (!token.IsCancellationRequested) { _analyzing = false; SetBusy(_busy); } }
        }

        private void RefreshAnalysis()
        {
            EstimatedSizeText.Text = UiLanguage.T("Taille compressée : ", "Compressed size: ") + FormatBytes(_useLighter ? _lighterSize : _originalSize) + UiLanguage.T(" / 50 Mo", " / 50 MB");
            long gain = Math.Max(0, _originalSize - _lighterSize);
            OptimizationText.Text = gain > 0
                ? FormatBytes(_originalSize) + " → " + FormatBytes(_lighterSize) + UiLanguage.T("\nGain calculé : ", "\nCalculated reduction: ") + FormatBytes(gain) + " (" + (100.0 * gain / _originalSize).ToString("0.0") + " %)"
                : UiLanguage.T("Aucun gain utile détecté pour les portes et garde-corps de cette vue.", "No useful size reduction detected for doors and railings in this view.");
            LightenButton.Content = _useLighter ? UiLanguage.T("Revenir aux détails d’origine", "Restore original details") : UiLanguage.T("Alléger les détails", "Simplify details");
            EstimatedSizeDetailsText.Text = UiLanguage.T("Géométrie 3D : ", "3D geometry: ") + FormatBytes(ExportScene.WebTiles.Sum(x => x.Size)) +
                " (" + ExportScene.WebTiles.Count + UiLanguage.T(" fichiers)\n", " files)\n") + BuildGeometryDetails();
        }

        private void LightenButton_Click(object sender, RoutedEventArgs e)
        {
            _useLighter = !_useLighter;
            RefreshAnalysis();
            StatusText.Text = _useLighter ? UiLanguage.T("Détails allégés pour la prochaine publication.", "Simplified details for the next publication.") : UiLanguage.T("Géométrie d’origine pour la prochaine publication.", "Original geometry for the next publication.");
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
                    ? UiLanguage.T("Sans catégorie", "Uncategorized")
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
            return UiLanguage.T("\nDIAGNOSTIC DE L’EXPORT D’ORIGINE\nPoids par élément estimé ; nombre de triangles mesuré.\n\nCATÉGORIES LES PLUS LOURDES\n", "\nORIGINAL EXPORT DIAGNOSTICS\nEstimated size per element; measured triangle count.\n\nLARGEST CATEGORIES\n") +
                categoryDetails + UiLanguage.T("\n\nTYPES LES PLUS LOURDS\n", "\n\nLARGEST TYPES\n") + typeDetails + UiLanguage.T("\n\nÉLÉMENTS LES PLUS LOURDS\n", "\n\nLARGEST ELEMENTS\n") + elementDetails;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + UiLanguage.T(" Mo", " MB");
            return Math.Max(1, bytes / 1024).ToString() + UiLanguage.T(" Ko", " KB");
        }

        private async void LocalExportButton_Click(object sender, RoutedEventArgs e)
        {
            string name = PublicationNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = UiLanguage.T("Maquette MEP", "MEP model");
            string safeName = new string(name.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = UiLanguage.T("Enregistrer les tuiles GLB en local", "Save GLB tiles locally"),
                Filter = "Archive ZIP (*.zip)|*.zip", DefaultExt = ".zip",
                AddExtension = true, FileName = safeName + " - GLB.zip"
            };
            if (dialog.ShowDialog(this) != true) return;
            var scene = ExportScene;
            string temporaryPath = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            SetBusy(true);
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            StatusText.Text = UiLanguage.T("Création du ZIP local…", "Creating local ZIP…");
            PublishProgressBar.Value = 0;
            try
            {
                await Task.Run(() => GameMepWebPackage.WriteLocalTilesZip(scene, temporaryPath, name, token), token);
                token.ThrowIfCancellationRequested();
                if (File.Exists(dialog.FileName)) File.Replace(temporaryPath, dialog.FileName, null);
                else File.Move(temporaryPath, dialog.FileName);
                PublishProgressBar.Value = 100;
                StatusText.Text = scene.WebTiles.Count + UiLanguage.T(" tuiles GLB enregistrées : ", " GLB tiles saved: ") + dialog.FileName;
            }
            catch (OperationCanceledException) { StatusText.Text = UiLanguage.T("Export local annulé.", "Local export canceled."); }
            catch (Exception exception)
            {
                Debug.WriteLine("Export GLB local impossible : " + exception);
                StatusText.Text = UiLanguage.T("Export local impossible : ", "Unable to export locally: ") + exception.Message;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception exception) { Debug.WriteLine(exception); }
                SetBusy(false);
            }
        }

        private async void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PublicationNameTextBox.Text))
            {
                StatusText.Text = UiLanguage.T("Donnez un nom à la publication.", "Enter a publication name.");
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
                StatusText.Text = UiLanguage.T("Révision ", "Revision ") + _state.Revision +
                    UiLanguage.T(" publiée. Les liens restent valables.", " published. The links remain valid.");
            }
            catch (OperationCanceledException) { StatusText.Text = UiLanguage.T("Publication annulée.", "Publication canceled."); }
            catch (Exception exception)
            {
                Debug.WriteLine("Publication MEP impossible : " + exception);
                _state = GameMepPublishClient.Load(_scene.MepGraph);
                ShowState();
                StatusText.Text = UiLanguage.T("Publication impossible : ", "Unable to publish: ") + exception.Message;
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
                StatusText.Text = UiLanguage.T("Le partage est prolongé de 30 jours.", "Sharing has been extended by 30 days.");
            }
            catch (Exception exception) { StatusText.Text = exception.Message; }
            finally { SetBusy(false); }
        }

        private async void RevokeButton_Click(object sender, RoutedEventArgs e)
        {
            string scope = (DeletionScopeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(scope)) return;
            string confirmation = scope == "data"
                ? UiLanguage.T("Supprimer définitivement les résas, annotations et lots du partage web ? La maquette 3D et les liens sont conservés. Le fichier Revit reste intact.", "Permanently delete openings, annotations and batches from the web publication? The 3D model and links will be kept. The Revit file remains untouched.")
                : scope == "model"
                    ? UiLanguage.T("Supprimer la maquette 3D publiée et désactiver les deux liens ? Les résas et annotations sont conservées pendant 5 mois et seront retrouvées lors d'un nouvel export.", "Delete the published 3D model and disable both links? Openings and annotations will be kept for 5 months and restored on the next export.")
                    : UiLanguage.T("Supprimer définitivement la maquette publiée ET ses résas, annotations et lots, puis désactiver les deux liens ? Le fichier Revit reste intact.", "Permanently delete the published model AND its openings, annotations and batches, then disable both links? The Revit file remains untouched.");
            if (MessageBox.Show(this,
                    confirmation,
                    UiLanguage.T("Supprimer les données du partage", "Delete shared data"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true);
            try
            {
                await GameMepPublishClient.DeleteAsync(
                    _scene.MepGraph, _state, scope, CancellationToken.None);
                ShowState();
                PublishProgressBar.Value = 0;
                StatusText.Text = scope == "data" ? UiLanguage.T("Résas et annotations supprimées. La maquette publiée est conservée.", "Openings and annotations deleted. The published model has been kept.")
                    : scope == "model" ? UiLanguage.T("Maquette 3D supprimée. Résas et annotations conservées ; publiez une nouvelle révision pour les retrouver.", "3D model deleted. Openings and annotations have been kept; publish a new revision to restore them.")
                    : UiLanguage.T("Maquette publiée, résas et annotations supprimées.", "Published model, openings and annotations deleted.");
            }
            catch (Exception exception) { StatusText.Text = exception.Message; }
            finally { ShowState(); SetBusy(false); }
        }

        private void ShowState()
        {
            bool available = !string.IsNullOrWhiteSpace(_state.PublicationId) &&
                !string.IsNullOrWhiteSpace(_state.ViewerUrl) &&
                !string.IsNullOrWhiteSpace(_state.EditorUrl);
            LinksPanel.Visibility = !string.IsNullOrWhiteSpace(_state.PublicationId) ? Visibility.Visible : Visibility.Collapsed;
            ViewerUrlTextBox.Text = _state.ViewerUrl;
            EditorUrlTextBox.Text = _state.EditorUrl;
            PublishButton.Content = available ? UiLanguage.T("Publier une nouvelle révision", "Publish a new revision") : UiLanguage.T("Partager sur le web", "Share on the web");
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            LinksPanel.IsEnabled = !busy;
            LightenButton.IsEnabled = !busy && !_analyzing && _lighterSize < _originalSize;
            PublishButton.IsEnabled = !busy;
            LocalExportButton.IsEnabled = !busy;
            PublicationNameTextBox.IsEnabled = !busy;
        }

        private void CopyViewerButton_Click(object sender, RoutedEventArgs e) =>
            Copy(_state.ViewerUrl, UiLanguage.T("Lien de consultation copié.", "Viewer link copied."));
        private void CopyEditorButton_Click(object sender, RoutedEventArgs e) =>
            Copy(_state.EditorUrl, UiLanguage.T("Lien d’édition copié.", "Editor link copied."));
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
