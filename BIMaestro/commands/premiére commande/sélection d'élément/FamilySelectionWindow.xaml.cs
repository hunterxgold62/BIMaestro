using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Visualisation
{
    public partial class FamilySelectionWindow : Window
    {
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

        private List<FamilyItem> AllFamilyItems { get; set; }

        public FamilySelectionWindow(List<string> families)
        {
            InitializeComponent();

            SelectedParentFamilies = new List<string>();
            SelectedSubFamilies = new List<string>();
            ExcludedSubFamilies = new List<string>();
            AllFamilyItems = new List<FamilyItem>();

            // Construire l'arborescence
            foreach (var fam in families)
            {
                // Trim() pour éviter les espaces parasites
                string family = fam.Trim();

                if (family.Contains(":"))
                {
                    var parts = family.Split(':');
                    var parentName = parts[0].Trim();
                    var childName = parts[1].Trim();

                    var parent = AllFamilyItems.FirstOrDefault(f => f.Name == parentName);
                    if (parent == null)
                    {
                        parent = new FamilyItem { Name = parentName };
                        AllFamilyItems.Add(parent);
                    }
                    parent.SubFamilies.Add(new FamilyItem { Name = childName });
                }
                else
                {
                    // Famille sans sous-familles
                    AllFamilyItems.Add(new FamilyItem { Name = family });
                }
            }

            FamiliesTreeView.ItemsSource = AllFamilyItems;
        }

        private void FinishButton_Click(object sender, RoutedEventArgs e)
        {
            // Vider les listes
            SelectedParentFamilies.Clear();
            SelectedSubFamilies.Clear();
            ExcludedSubFamilies.Clear();

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

            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        /// <summary>
        /// Bouton "Tout sélectionner"
        /// </summary>
        private void SelectAllViewsButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var family in AllFamilyItems)
            {
                family.IsSelected = true;
                foreach (var child in family.SubFamilies)
                {
                    child.IsSelected = true;
                }
            }
        }

        /// <summary>
        /// Bouton "Tout désélectionner"
        /// </summary>
        private void DeselectAllViewsButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var family in AllFamilyItems)
            {
                family.IsSelected = false;
                foreach (var child in family.SubFamilies)
                {
                    child.IsSelected = false;
                }
            }
        }
    }
}
