
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using BIMaestro.Localization;

namespace Visualisation
{
    public partial class FamilySelectionWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/visualisation?outil=selection-elements";
        /// <summary>
        /// Noms de familles parent cochées. Ex. ["Simple (T)", "BarreAP", ...]
        /// </summary>
        public List<string> SelectedParentFamilies { get; private set; }

        /// <summary>
        /// Sous-familles cochées malgré parent décoché.
        /// (ex. ["Simple (T) : 0.83m x 2.04m", ...])
        /// </summary>
        public List<string> SelectedSubFamilies { get; private set; }

        /// <summary>
        /// Sous-familles décochées malgré parent coché.
        /// (ex. ["Simple (T) : 0.93m x 2.04m", ...])
        /// </summary>
        public List<string> ExcludedSubFamilies { get; private set; }

        /// <summary>
        /// True = appliquer à toute la maquette (ignore la vue active).
        /// False = se limiter à la vue active (comportement historique).
        /// </summary>
        public bool ScopeEntireModel { get; private set; } = false;

        private readonly List<string> _viewFamilies;
        private readonly List<string> _entireModelFamilies;
        private List<FamilyItem> AllFamilyItems { get; set; }

        public FamilySelectionWindow(IEnumerable<string> viewFamilies, IEnumerable<string> entireModelFamilies)
        {
            InitializeComponent();

            SelectedParentFamilies = new List<string>();
            SelectedSubFamilies = new List<string>();
            ExcludedSubFamilies = new List<string>();

            _viewFamilies = NormalizeFamilies(viewFamilies);
            _entireModelFamilies = NormalizeFamilies(entireModelFamilies);

            BuildTree(_viewFamilies);
        }

        private void FinishButton_Click(object sender, RoutedEventArgs e)
        {
            // Récupérer la portée
            ScopeEntireModel = (EntireModelCheckBox.IsChecked == true);

            // Vider les listes
            SelectedParentFamilies.Clear();
            SelectedSubFamilies.Clear();
            ExcludedSubFamilies.Clear();

            if (AllFamilyItems == null)
            {
                DialogResult = true;
                Close();
                return;
            }

            // Parcourir chaque "famille parent"
            foreach (var familyItem in AllFamilyItems)
            {
                if (familyItem.IsSelected)
                {
                    // Parent coché
                    SelectedParentFamilies.Add(familyItem.Name);

                    // Sous-familles décochées ⇒ Exclusion
                    foreach (var child in familyItem.SubFamilies)
                    {
                        if (!child.IsSelected)
                        {
                            string exclName = $"{familyItem.Name} : {child.Name}";
                            ExcludedSubFamilies.Add(exclName);
                        }
                    }
                }
                else
                {
                    // Parent non coché
                    // => regarder si certaines sous-familles sont cochées
                    foreach (var child in familyItem.SubFamilies)
                    {
                        if (child.IsSelected)
                        {
                            // Sous-famille cochée malgré parent décoché
                            string selectedName = $"{familyItem.Name} : {child.Name}";
                            SelectedSubFamilies.Add(selectedName);
                        }
                    }
                }
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Bouton "Tout sélectionner"
        /// </summary>
        private void SelectAllViewsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AllFamilyItems == null) return;

            foreach (var family in AllFamilyItems)
            {
                family.IsSelected = true;
                foreach (var child in family.SubFamilies)
                    child.IsSelected = true;
            }

            FamiliesTreeView.Items.Refresh();
        }

        /// <summary>
        /// Bouton "Tout désélectionner"
        /// </summary>
        private void DeselectAllViewsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AllFamilyItems == null) return;

            foreach (var family in AllFamilyItems)
            {
                family.IsSelected = false;
                foreach (var child in family.SubFamilies)
                    child.IsSelected = false;
            }

            FamiliesTreeView.Items.Refresh();
        }

        private void EntireModelScopeChanged(object sender, RoutedEventArgs e)
        {
            var families = (EntireModelCheckBox.IsChecked == true)
                ? _entireModelFamilies
                : _viewFamilies;

            BuildTree(families);
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show(UiLanguage.T("Impossible d’ouvrir la page d’aide.", "Unable to Open the Help Page."), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BuildTree(IEnumerable<string> families)
        {
            AllFamilyItems = CreateFamilyItems(families);

            FamiliesTreeView.ItemsSource = null;
            FamiliesTreeView.ItemsSource = AllFamilyItems;
            FamiliesTreeView.Items.Refresh();
        }

        private static List<string> NormalizeFamilies(IEnumerable<string> families)
        {
            if (families == null)
                return new List<string>();

            return families
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<FamilyItem> CreateFamilyItems(IEnumerable<string> families)
        {
            var result = new List<FamilyItem>();
            if (families == null)
                return result;

            var lookup = new Dictionary<string, FamilyItem>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var entry in families)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                string family = entry.Trim();
                var parts = family.Split(new[] { ':' }, 2);

                if (parts.Length == 2)
                {
                    string parentName = parts[0].Trim();
                    string childName = parts[1].Trim();

                    if (!lookup.TryGetValue(parentName, out var parent))
                    {
                        parent = new FamilyItem { Name = parentName };
                        lookup[parentName] = parent;
                        result.Add(parent);
                    }

                    parent.SubFamilies.Add(new FamilyItem { Name = childName });
                }
                else
                {
                    if (!lookup.TryGetValue(family, out var parent))
                    {
                        parent = new FamilyItem { Name = family };
                        lookup[family] = parent;
                        result.Add(parent);
                    }
                }
            }

            result = result
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var parent in result)
            {
                var orderedChildren = parent.SubFamilies
                    .OrderBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                parent.SubFamilies = new ObservableCollection<FamilyItem>(orderedChildren);
                parent.IsSelected = true; // réinitialise la sélection + propage aux enfants
            }

            return result;
        }
    }
}
