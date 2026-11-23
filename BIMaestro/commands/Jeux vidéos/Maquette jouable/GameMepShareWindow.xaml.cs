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
        private readonly GameMepPublishClient _publishClient = new GameMepPublishClient(() => global::BIMaestroApp.LicenseJwt);
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
                ? UiLanguage.Get("GameMepShareWindow.xaml.NoBusinessParameters")
                : names.Length + UiLanguage.Get("GameMepShareWindow.xaml.SelectedParameters");
            AuthorizedParametersText.ToolTip = names.Length == 0
                ? UiLanguage.Get("GameMepShareWindow.xaml.OnlyTheMinimumTechnicalGraphInformationWillBe")
                : string.Join(", ", names);
            EstimatedSizeText.Text = UiLanguage.Get("GameMepShareWindow.xaml.CalculatingCompressedSize");
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
                    EstimatedSizeText.Text = UiLanguage.Get("GameMepShareWindow.xaml.AnalysisUnavailable");
                    OptimizationText.Text = UiLanguage.Get("GameMepShareWindow.xaml.TheOriginalExportRemainsAvailable") + exception.Message;
                }
            }
            finally { if (!token.IsCancellationRequested) { _analyzing = false; SetBusy(_busy); } }
        }

        private void RefreshAnalysis()
        {
            EstimatedSizeText.Text = UiLanguage.Get("GameMepShareWindow.xaml.CompressedSize") + FormatBytes(_useLighter ? _lighterSize : _originalSize) + UiLanguage.Get("GameMepShareWindow.xaml.50MB");
            long gain = Math.Max(0, _originalSize - _lighterSize);
            OptimizationText.Text = gain > 0
                ? FormatBytes(_originalSize) + " → " + FormatBytes(_lighterSize) + UiLanguage.Get("GameMepShareWindow.xaml.CalculatedReduction") + FormatBytes(gain) + " (" + (100.0 * gain / _originalSize).ToString("0.0") + " %)"
                : UiLanguage.Get("GameMepShareWindow.xaml.NoUsefulSizeReductionDetectedForDoorsAnd");
            LightenButton.Content = _useLighter ? UiLanguage.Get("GameMepShareWindow.xaml.RestoreOriginalDetails") : UiLanguage.Get("GameMepShareWindow.xaml.SimplifyDetails");
            EstimatedSizeDetailsText.Text = UiLanguage.Get("GameMepShareWindow.xaml.3DGeometry") + FormatBytes(ExportScene.WebTiles.Sum(x => x.Size)) +
                " (" + ExportScene.WebTiles.Count + UiLanguage.Get("GameMepShareWindow.xaml.Files") + BuildGeometryDetails();
        }

        private void LightenButton_Click(object sender, RoutedEventArgs e)
        {
            _useLighter = !_useLighter;
            RefreshAnalysis();
            StatusText.Text = _useLighter ? UiLanguage.Get("GameMepShareWindow.xaml.SimplifiedDetailsForTheNextPublication") : UiLanguage.Get("GameMepShareWindow.xaml.OriginalGeometryForTheNextPublication");
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
                    ? UiLanguage.Get("GameMepShareWindow.xaml.Uncategorized")
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
            return UiLanguage.Get("GameMepShareWindow.xaml.ORIGINALEXPORTDIAGNOSTICSEstimatedSizePerElementMeasure") +
                categoryDetails + UiLanguage.Get("GameMepShareWindow.xaml.LARGESTTYPES") + typeDetails + UiLanguage.Get("GameMepShareWindow.xaml.LARGESTELEMENTS") + elementDetails;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + UiLanguage.Get("GameMepShareWindow.xaml.MB");
            return Math.Max(1, bytes / 1024).ToString() + UiLanguage.Get("GameMepShareWindow.xaml.KB");
        }

        private async void LocalExportButton_Click(object sender, RoutedEventArgs e)
        {
            string name = PublicationNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = UiLanguage.Get("GameMepShareWindow.xaml.MEPModel");
            string safeName = new string(name.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = UiLanguage.Get("GameMepShareWindow.xaml.SaveGLBTilesLocally"),
                Filter = "Archive ZIP (*.zip)|*.zip", DefaultExt = ".zip",
                AddExtension = true, FileName = safeName + " - GLB.zip"
            };
            if (dialog.ShowDialog(this) != true) return;
            var scene = ExportScene;
            string temporaryPath = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            SetBusy(true);
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.CreatingLocalZIP");
            PublishProgressBar.Value = 0;
            try
            {
                await Task.Run(() => GameMepWebPackage.WriteLocalTilesZip(scene, temporaryPath, name, token), token);
                token.ThrowIfCancellationRequested();
                if (File.Exists(dialog.FileName)) File.Replace(temporaryPath, dialog.FileName, null);
                else File.Move(temporaryPath, dialog.FileName);
                PublishProgressBar.Value = 100;
                StatusText.Text = scene.WebTiles.Count + UiLanguage.Get("GameMepShareWindow.xaml.GLBTilesSaved") + dialog.FileName;
            }
            catch (OperationCanceledException) { StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.LocalExportCanceled"); }
            catch (Exception exception)
            {
                Debug.WriteLine("Export GLB local impossible : " + exception);
                StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.UnableToExportLocally") + exception.Message;
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
                StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.EnterAPublicationName");
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
                _state = await _publishClient.PublishAsync(
                    ExportScene, PublicationNameTextBox.Text.Trim(), progress,
                    _cancellation.Token);
                ShowState();
                StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.Revision") + _state.Revision +
                    UiLanguage.Get("GameMepShareWindow.xaml.PublishedTheLinksRemainValid");
            }
            catch (OperationCanceledException) { StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.PublicationCanceled"); }
            catch (Exception exception)
            {
                Debug.WriteLine("Publication MEP impossible : " + exception);
                _state = GameMepPublishClient.Load(_scene.MepGraph);
                ShowState();
                StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.UnableToPublish") + exception.Message;
            }
            finally { SetBusy(false); }
        }

        private async void ExtendButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            try
            {
                await _publishClient.ExtendAsync(
                    _scene.MepGraph, _state, 30, CancellationToken.None);
                ShowState();
                StatusText.Text = UiLanguage.Get("GameMepShareWindow.xaml.SharingHasBeenExtendedBy30Days");
            }
            catch (Exception exception) { StatusText.Text = exception.Message; }
            finally { SetBusy(false); }
        }

        private async void RevokeButton_Click(object sender, RoutedEventArgs e)
        {
            string scope = (DeletionScopeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(scope)) return;
            string confirmation = scope == "data"
                ? UiLanguage.Get("GameMepShareWindow.xaml.PermanentlyDeleteOpeningsAnnotationsAndBatchesFromThe")
                : scope == "model"
                    ? UiLanguage.Get("GameMepShareWindow.xaml.DeleteThePublished3DModelAndDisableBoth")
                    : UiLanguage.Get("GameMepShareWindow.xaml.PermanentlyDeleteThePublishedModelANDItsOpenings");
            if (MessageBox.Show(this,
                    confirmation,
                    UiLanguage.Get("GameMepShareWindow.xaml.DeleteSharedData"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true);
            try
            {
                await _publishClient.DeleteAsync(
                    _scene.MepGraph, _state, scope, CancellationToken.None);
                ShowState();
                PublishProgressBar.Value = 0;
                StatusText.Text = scope == "data" ? UiLanguage.Get("GameMepShareWindow.xaml.OpeningsAndAnnotationsDeletedThePublishedModelHas")
                    : scope == "model" ? UiLanguage.Get("GameMepShareWindow.xaml.3DModelDeletedOpeningsAndAnnotationsHaveBeen")
                    : UiLanguage.Get("GameMepShareWindow.xaml.PublishedModelOpeningsAndAnnotationsDeleted");
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
            PublishButton.Content = available ? UiLanguage.Get("GameMepShareWindow.xaml.PublishANewRevision") : UiLanguage.Get("GameMepShareWindow.xaml.ShareOnTheWeb");
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
            Copy(_state.ViewerUrl, UiLanguage.Get("GameMepShareWindow.xaml.ViewerLinkCopied"));
        private void CopyEditorButton_Click(object sender, RoutedEventArgs e) =>
            Copy(_state.EditorUrl, UiLanguage.Get("GameMepShareWindow.xaml.EditorLinkCopied"));
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
