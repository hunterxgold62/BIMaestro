using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
            CalculateCompressedSizeAsync();
        }

        private async void CalculateCompressedSizeAsync()
        {
            try
            {
                string publicationName = PublicationNameTextBox.Text.Trim();
                GameMepWebPackageResult package = await Task.Run(
                    () => GameMepWebPackage.Build(_scene, publicationName),
                    _sizeCalculationCancellation.Token);
                if (!_sizeCalculationCancellation.IsCancellationRequested)
                {
                    EstimatedSizeText.Text = "Taille réelle compressée : " +
                        FormatBytes(package.Bytes.LongLength) + " / 50 Mo";
                    using var stream = new MemoryStream(package.Bytes, writable: false);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    EstimatedSizeDetailsText.Text = string.Join("\n",
                        archive.Entries
                            .OrderByDescending(entry => entry.CompressedLength)
                            .Select(entry => FriendlyFileName(entry.FullName) + " : " +
                                FormatBytes(entry.CompressedLength)));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.WriteLine("Calcul de taille du partage MEP impossible : " + exception);
                if (!_sizeCalculationCancellation.IsCancellationRequested)
                {
                    EstimatedSizeText.Text = "Taille compressée indisponible";
                    EstimatedSizeDetailsText.Text = string.Empty;
                }
            }
        }

        private static string FriendlyFileName(string fileName)
        {
            switch (fileName.ToLowerInvariant())
            {
                case "model.glb": return "Géométrie 3D";
                case "properties.json": return "Propriétés des éléments";
                case "mep.json": return "Graphe et flux MEP";
                case "viewer.json": return "Navigation et portes";
                case "manifest.json": return "Informations du paquet";
                case "thumbnail.webp": return "Miniature";
                default: return fileName;
            }
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
                    _scene, PublicationNameTextBox.Text.Trim(), progress,
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
