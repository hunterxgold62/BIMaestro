using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Modification
{
    /// <summary>Fenêtre minimaliste : filtre live (sur Famille) + double-clic, pas de persistance disque.</summary>
    public partial class PickDefaultFlangeWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/modification?outil=bride-auto";
        private readonly List<FlangeItem> _all;   // liste complète (famille/type)
        private List<FlangeItem> _filtered;       // vue filtrée

        public PickDefaultFlangeWindow(IList<FamilySymbol> symbols)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _all = symbols.Select(fs => new FlangeItem(fs)).ToList();
            _filtered = _all;
            FlangeList.ItemsSource = _filtered;

            // Pré-sélection si déjà choisi dans la session
            if (FlangeChoiceCache.HasChoice)
            {
                var found = _all.FirstOrDefault(it =>
                    it.Family.Equals(FlangeChoiceCache.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    it.Type.Equals(FlangeChoiceCache.SymbolName, StringComparison.OrdinalIgnoreCase));
                if (found != null) FlangeList.SelectedItem = found;
            }
            if (FlangeList.SelectedItem == null && _filtered.Count > 0)
                FlangeList.SelectedIndex = 0;

            SearchBox.Focus();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public FamilySymbol SelectedSymbol =>
            (FlangeList.SelectedItem as FlangeItem)?.Symbol;

        // Filtre EN TEMPS RÉEL — sur le NOM DE LA FAMILLE uniquement
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = Normalize(SearchBox.Text);
            if (string.IsNullOrEmpty(q))
                _filtered = _all;
            else
                _filtered = _all.Where(it => Normalize(it.Family).Contains(q)).ToList();

            FlangeList.ItemsSource = _filtered;
            if (_filtered.Count > 0) FlangeList.SelectedIndex = 0;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        // Double-clic = OK
        private void FlangeList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SelectedSymbol != null) { DialogResult = true; Close(); }
        }

        // Entrée = OK, Échap = Annuler
        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && SelectedSymbol != null)
            {
                DialogResult = true; Close();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                DialogResult = false; Close();
            }
        }

        public class FlangeItem
        {
            public FamilySymbol Symbol { get; }
            public string Family { get; }
            public string Type { get; }
            public FlangeItem(FamilySymbol fs)
            {
                Symbol = fs;
                Family = fs.FamilyName ?? "";
                Type = fs.Name ?? "";
            }
            public override string ToString() => $"{Family} : {Type}";
        }
    }

    /// <summary>Bouton “Choisir la bride” : ouvre la fenêtre, enregistre le choix pour la session.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PickDefaultFlange : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument.Document;

            var symbols = new FilteredElementCollector(doc)
    .OfClass(typeof(FamilySymbol))
    .OfCategory(BuiltInCategory.OST_PipeAccessory)   // <<< le filtre manquant
    .Cast<FamilySymbol>()
    .OrderBy(fs => fs.FamilyName).ThenBy(fs => fs.Name)
    .ToList();

            if (symbols.Count == 0)
            {
                TaskDialog.Show("Choisir une bride", "Aucun type de bride trouvé dans ce projet.");
                return Result.Cancelled;
            }

            var wnd = new PickDefaultFlangeWindow(symbols);
            new WindowInteropHelper(wnd) { Owner = data.Application.MainWindowHandle };

            bool? ok = wnd.ShowDialog();
            if (ok != true) return Result.Cancelled;

            var chosen = wnd.SelectedSymbol;
            if (chosen == null) return Result.Cancelled;

            // En mémoire (session Revit uniquement)
            FlangeChoiceCache.FamilyName = chosen.FamilyName;
            FlangeChoiceCache.SymbolName = chosen.Name;

            TaskDialog.Show("Bride sélectionnée (session)",
                $"{chosen.FamilyName} : {chosen.Name}\n\n" +
                "Cette sélection sera utilisée par la commande principale tant que Revit reste ouvert.");

            return Result.Succeeded;
        }

        
        
    }
}
