using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;

namespace Modification
{
    public partial class ExtendedReservationWindowV2 : Window
    {
        public enum HostTarget
        {
            Mur,
            Sol
        }

        public enum ObjectType
        {
            Canalisation,
            Gaine,
            Porte,
            Fenetre,
            Autre
        }

        public enum PipeSource
        {
            Maquette,
            LienIFC,
            LienRVT
        }

        public ObjectType SelectedObjectType { get; private set; }
        public HostTarget SelectedHostTarget { get; private set; }
        public FamilySymbol SelectedReservationSymbol { get; private set; }
        public bool NormeEnabled { get; private set; }
        public bool DynamoAutoEnabled { get; private set; }
        public bool AutomatiqueEnabled { get; private set; }
        public bool MultiEnabled { get; private set; }

        public PipeSource SelectedPipeSource
        {
            get
            {
                if (comboPipeSource == null)
                    return PipeSource.Maquette;

                string src = null;

                if (comboPipeSource.SelectedItem is ComboBoxItem item)
                    src = item.Content as string;
                else if (comboPipeSource.SelectedItem is string s)
                    src = s;

                return src switch
                {
                    "Lien IFC" => PipeSource.LienIFC,
                    "Lien RVT" => PipeSource.LienRVT,
                    _ => PipeSource.Maquette
                };
            }
        }

        private readonly List<FamilySymbol> _allFamilies;
        private readonly List<ReservationSymbolItem> _items;

        public ExtendedReservationWindowV2(List<FamilySymbol> familiesV2)
        {
            InitializeComponent();

            _allFamilies = familiesV2 ?? new List<FamilySymbol>();
            _items = _allFamilies
                .Where(fs => fs != null && fs.Family != null)
                .Select(fs => new ReservationSymbolItem(fs, BuildDisplayName(fs)))
                .OrderBy(i => i.DisplayName)
                .ToList();

            comboHostType.ItemsSource = new List<string> { "Mur", "Sol" };
            comboHostType.SelectedIndex = 0;

            comboObjectType.ItemsSource = new List<string>
            {
                "Canalisation",
                "Gaine",
                "Porte",
                "Fenêtre",
                "Autre"
            };
            comboObjectType.SelectedIndex = 0;

            if (comboPipeSource != null && comboPipeSource.Items.Count > 0)
                comboPipeSource.SelectedIndex = 0;

            Loaded += (_, __) =>
            {
                UpdateFamilyFilter();
                comboObjectType.Focus();
            };
        }

        // Clic gauche : toggle popup
        private void BtnOptions_Click(object sender, RoutedEventArgs e)
        {
            popupOptions.IsOpen = !popupOptions.IsOpen;
        }

        // Clic droit : toggle popup + consume
        private void BtnOptions_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            popupOptions.IsOpen = !popupOptions.IsOpen;
        }

        private void OnCriteriaChanged(object sender, SelectionChangedEventArgs e)
        {
            var typeSel = comboObjectType.SelectedItem as string;
            bool isCanal = typeSel == "Canalisation";
            bool isAutre = typeSel == "Autre";

            if (comboPipeSource != null)
            {
                comboPipeSource.IsEnabled = isCanal;
                if (!isCanal)
                    comboPipeSource.SelectedIndex = 0;
            }

            // V2 : familles rectangulaires uniquement => multi sur cana/autre comme V1
            chkMulti.IsEnabled = (isCanal || isAutre);
            if (!chkMulti.IsEnabled)
                chkMulti.IsChecked = false;

            UpdateFamilyFilter();
        }

        private void OnHostTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFamilyFilter();
        }

        private void OnOkClicked(object sender, RoutedEventArgs e)
        {
            NormeEnabled = chkNorme.IsChecked == true;
            DynamoAutoEnabled = chkDynamoAuto.IsChecked == true;
            AutomatiqueEnabled = chkAutomatique.IsChecked == true;
            MultiEnabled = chkMulti.IsChecked == true;

            SelectedHostTarget = (comboHostType.SelectedItem as string) == "Sol"
                ? HostTarget.Sol
                : HostTarget.Mur;

            switch (comboObjectType.SelectedItem as string)
            {
                case "Canalisation":
                    SelectedObjectType = ObjectType.Canalisation;
                    break;
                case "Gaine":
                    SelectedObjectType = ObjectType.Gaine;
                    break;
                case "Porte":
                    SelectedObjectType = ObjectType.Porte;
                    break;
                case "Fenêtre":
                    SelectedObjectType = ObjectType.Fenetre;
                    break;
                default:
                    SelectedObjectType = ObjectType.Autre;
                    break;
            }

            SelectedReservationSymbol = (comboFamily.SelectedItem as ReservationSymbolItem)?.Symbol;

            DialogResult = true;
            Close();
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void UpdateFamilyFilter()
        {
            if (comboFamily == null || _items == null)
            {
                comboFamily.ItemsSource = null;
                comboFamily.SelectedIndex = -1;
                return;
            }

            var previousSelection = comboFamily.SelectedItem as ReservationSymbolItem;
            bool isSol = (comboHostType?.SelectedItem as string) == "Sol";

            var filtered = _items
                .Where(item =>
                {
                    string fam = item?.Symbol?.Family?.Name ?? "";
                    if (isSol)
                        return fam.IndexOf("horizontale", StringComparison.OrdinalIgnoreCase) >= 0;
                    return fam.IndexOf("verticale", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .OrderBy(item => item.DisplayName)
                .ToList();

            comboFamily.ItemsSource = filtered;

            if (filtered.Any())
            {
                var keep = previousSelection != null
                    ? filtered.FirstOrDefault(x => x.Symbol.Id == previousSelection.Symbol.Id)
                    : null;

                comboFamily.SelectedItem = keep ?? filtered.First();
            }
            else
            {
                comboFamily.SelectedIndex = -1;
            }
        }

        private static string BuildDisplayName(FamilySymbol fs)
        {
            // Affichage simple (comme V1)
            return fs?.Family?.Name ?? string.Empty;
        }

        private class ReservationSymbolItem
        {
            public ReservationSymbolItem(FamilySymbol symbol, string displayName)
            {
                Symbol = symbol;
                DisplayName = displayName;
            }

            public FamilySymbol Symbol { get; }
            public string DisplayName { get; }
        }
    }
}
