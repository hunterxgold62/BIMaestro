using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace Famille
{
    public partial class FamilySearchReviewWindow : Window
    {
        private readonly ObservableCollection<FamilySearchReviewItem> _rows;
        private CancellationTokenSource _generationCts;
        private bool _isBusy;

        public List<string> SavedPaths { get; } = new List<string>();

        public FamilySearchReviewWindow(
            IEnumerable<FamilyIndexService.Entry> entries,
            string familiesRoot,
            string imagesRoot)
        {
            InitializeComponent();

            _rows = new ObservableCollection<FamilySearchReviewItem>(
                (entries ?? Enumerable.Empty<FamilyIndexService.Entry>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Path))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new FamilySearchReviewItem(e, familiesRoot, imagesRoot)));

            ReviewGrid.ItemsSource = _rows;
            CollectionViewSource.GetDefaultView(_rows).Filter = FilterRow;
            SelectMissing();
            UpdateSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsSelected = true;
            UpdateSummary();
        }

        private void SelectMissing_Click(object sender, RoutedEventArgs e)
        {
            SelectMissing();
            UpdateSummary();
        }

        private void SelectMissing()
        {
            foreach (var row in _rows)
                row.IsSelected = !row.HasKeywords;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsSelected = false;
            UpdateSummary();
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Cochez au moins une famille à analyser.", "Mots-clés", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _generationCts = new CancellationTokenSource();
            SetBusy(true);
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.Maximum = selected.Count;
            ProgressBar.Value = 0;
            int completed = 0;
            int errors = 0;

            try
            {
                foreach (var row in selected)
                {
                    if (_generationCts.IsCancellationRequested)
                        break;

                    ProgressText.Text = $"Analyse IA {completed + 1}/{selected.Count} — {row.Name}";
                    row.Status = "Analyse en cours…";
                    try
                    {
                        var suggestion = await FamilySearchAiService.SuggestAsync(row.Name, row.Folder, row.Category);
                        row.Description = suggestion.Description;
                        row.KeywordsText = string.Join(", ", suggestion.Keywords);
                        row.Source = "ai-reviewed";
                        row.Status = "Proposition IA — à vérifier";
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        row.Status = "Échec IA : " + ShortMessage(ex.Message);
                    }

                    completed++;
                    ProgressBar.Value = completed;
                }
            }
            finally
            {
                SetBusy(false);
                ProgressText.Text = _generationCts.IsCancellationRequested
                    ? $"Analyse arrêtée après {completed} famille(s)."
                    : $"Analyse terminée : {completed - errors} proposition(s), {errors} échec(s).";
                _generationCts.Dispose();
                _generationCts = null;
                UpdateSummary(keepProgressText: true);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _generationCts?.Cancel();
            CancelButton.IsEnabled = false;
            ProgressText.Text = "Arrêt demandé…";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ReviewGrid.CommitEdit();
            ReviewGrid.CommitEdit();

            var dirty = _rows.Where(r => r.IsDirty).ToList();
            if (dirty.Count == 0)
            {
                MessageBox.Show(this, "Aucune modification à enregistrer.", "Mots-clés", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int saved = 0;
            var errors = new List<string>();
            foreach (var row in dirty)
            {
                var metadata = new FamilySearchMetadata
                {
                    Description = row.Description,
                    Keywords = FamilySearchMetadataService.ParseKeywords(row.KeywordsText),
                    Source = string.IsNullOrWhiteSpace(row.Source) ? "manual" : row.Source,
                    UpdatedBy = Environment.UserName
                };

                if (!FamilySearchMetadataService.TrySave(
                    row.Path, metadata, row.ExpectedLastWriteUtc, out string error, out DateTime? newWriteUtc))
                {
                    row.Status = "Non enregistré";
                    errors.Add(row.Name + " : " + error);
                    continue;
                }

                row.MarkSaved(metadata, newWriteUtc);
                if (!SavedPaths.Contains(row.Path, StringComparer.OrdinalIgnoreCase))
                    SavedPaths.Add(row.Path);
                saved++;
            }

            UpdateSummary();
            string message = $"{saved} famille(s) enregistrée(s).";
            if (errors.Count > 0)
            {
                message += $"\n\n{errors.Count} échec(s) :\n" + string.Join("\n", errors.Take(8));
                if (errors.Count > 8) message += $"\n… et {errors.Count - 8} autre(s).";
            }
            MessageBox.Show(this, message, "Mots-clés", MessageBoxButton.OK,
                errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => CollectionViewSource.GetDefaultView(_rows)?.Refresh();

        private bool FilterRow(object value)
        {
            if (value is not FamilySearchReviewItem row)
                return false;
            string term = (FilterBox?.Text ?? string.Empty).Trim();
            if (term.Length == 0)
                return true;
            return (row.Name?.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) ?? -1) >= 0 ||
                   (row.Description?.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) ?? -1) >= 0 ||
                   (row.KeywordsText?.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) ?? -1) >= 0;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                MessageBox.Show(this, "Arrêtez d’abord l’analyse IA en cours.", "Mots-clés", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_isBusy)
                return;

            e.Cancel = true;
            MessageBox.Show(this, "Arrêtez d’abord l’analyse IA en cours.", "Mots-clés",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            GenerateButton.IsEnabled = !busy;
            SaveButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
        }

        private void UpdateSummary(bool keepProgressText = false)
        {
            if (keepProgressText)
                return;
            int selected = _rows.Count(r => r.IsSelected);
            int enriched = _rows.Count(r => r.HasKeywords);
            int dirty = _rows.Count(r => r.IsDirty);
            ProgressText.Text = $"{_rows.Count} familles — {selected} cochée(s) — {enriched} avec mots-clés — {dirty} modification(s) non enregistrée(s).";
        }

        private static string ShortMessage(string message)
        {
            message = (message ?? "Erreur inconnue").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return message.Length <= 100 ? message : message.Substring(0, 100) + "…";
        }
    }

    public sealed class FamilySearchReviewItem : INotifyPropertyChanged
    {
        private bool _initialized;
        private bool _isSelected;
        private string _description;
        private string _keywordsText;
        private string _status;

        public FamilySearchReviewItem(FamilyIndexService.Entry entry, string familiesRoot, string imagesRoot)
        {
            Name = entry.Name;
            Path = entry.Path;
            Folder = System.IO.Path.GetDirectoryName(entry.Path);
            Category = entry.Category;
            PreviewPath = ImageResolver.Resolve(familiesRoot, imagesRoot, entry.Path);

            var metadata = FamilySearchMetadataService.Load(entry.Path);
            _description = metadata.Description ?? string.Empty;
            _keywordsText = string.Join(", ", metadata.Keywords);
            Source = metadata.Source;
            ExpectedLastWriteUtc = FamilySearchMetadataService.GetLastWriteUtc(entry.Path);
            _status = metadata.Keywords.Count > 0 ? "Déjà renseignée" : "À enrichir";
            _initialized = true;
        }

        public string Name { get; }
        public string Path { get; }
        public string Folder { get; }
        public string Category { get; }
        public string PreviewPath { get; }
        public string Source { get; set; }
        public DateTime? ExpectedLastWriteUtc { get; private set; }
        public bool IsDirty { get; private set; }
        public bool HasKeywords => FamilySearchMetadataService.ParseKeywords(KeywordsText).Count > 0;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public string Description
        {
            get => _description;
            set { if (_description != value) { _description = value ?? string.Empty; MarkDirty(); OnPropertyChanged(nameof(Description)); } }
        }

        public string KeywordsText
        {
            get => _keywordsText;
            set
            {
                if (_keywordsText == value) return;
                _keywordsText = value ?? string.Empty;
                MarkDirty();
                OnPropertyChanged(nameof(KeywordsText));
                OnPropertyChanged(nameof(HasKeywords));
            }
        }

        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); } }
        }

        public void MarkSaved(FamilySearchMetadata metadata, DateTime? newWriteUtc)
        {
            ExpectedLastWriteUtc = newWriteUtc;
            Source = metadata.Source;
            IsDirty = false;
            Status = "Enregistrée";
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(HasKeywords));
        }

        private void MarkDirty()
        {
            if (!_initialized) return;
            IsDirty = true;
            Source = "manual";
            Status = "Modifiée — non enregistrée";
            OnPropertyChanged(nameof(IsDirty));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
