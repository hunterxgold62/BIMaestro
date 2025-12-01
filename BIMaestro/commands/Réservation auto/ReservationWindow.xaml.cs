using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;  // <-- pour MouseButtonEventArgs
using Autodesk.Revit.DB;

namespace Modification
{
    public partial class ExtendedReservationWindow : Window
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

        public ObjectType SelectedObjectType { get; private set; }
        public HostTarget SelectedHostTarget { get; private set; }
        public FamilySymbol SelectedReservationSymbol { get; private set; }
        public bool NormeEnabled { get; private set; }
        public bool DynamoAutoEnabled { get; private set; }
        public bool AutomatiqueEnabled { get; private set; }
        public bool MultiEnabled { get; private set; }

        private readonly List<FamilySymbol> _allReservationFamilies;

        private readonly List<ReservationSymbolItem> _reservationItems;

        public ExtendedReservationWindow(List<FamilySymbol> reservationFamilies)
        {
            InitializeComponent();

            // 1) Initialiser d'abord la source
            _allReservationFamilies = reservationFamilies ?? new List<FamilySymbol>();
            _reservationItems = _allReservationFamilies
                .Where(fs => fs != null)
                .Select(fs => new ReservationSymbolItem(fs, BuildDisplayName(fs)))
                .ToList();

            // 2) Config host type
            comboHostType.ItemsSource = new List<string>
    {
        "Mur",
        "Sol"
    };
            comboHostType.SelectedIndex = 0;  // déclenche OnHostTypeChanged -> UpdateFamilyFilter,
                                              // mais maintenant _allReservationFamilies n'est plus null

            // 3) Config object type
            comboObjectType.ItemsSource = new List<string>
    {
        "Canalisation",
        "Gaine",
        "Porte",
        "Fenêtre",
        "Autre"
    };
            comboObjectType.SelectedIndex = 0; // déclenche OnCriteriaChanged, OK aussi



            Loaded += (_, __) => comboObjectType.Focus();
        }


        // Clic gauche : toggle du popup
        private void BtnOptions_Click(object sender, RoutedEventArgs e)
        {
            popupOptions.IsOpen = !popupOptions.IsOpen;
        }

        // Clic droit : toggle du popup + on consomme l'événement
        private void BtnOptions_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            popupOptions.IsOpen = !popupOptions.IsOpen;
        }

        // Logique existante
        private void OnCriteriaChanged(object sender, SelectionChangedEventArgs e)
        {
            var typeSel = comboObjectType.SelectedItem as string;
            bool isCanal = typeSel == "Canalisation";
            bool isAutre = typeSel == "Autre";

            var famItem = comboFamily.SelectedItem as ReservationSymbolItem;
            bool isRect = famItem != null && famItem.IsRectangular;

            chkMulti.IsEnabled = (isCanal || isAutre) && isRect;
            if (!chkMulti.IsEnabled)
                chkMulti.IsChecked = false;

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
                case "Canalisation": SelectedObjectType = ObjectType.Canalisation; break;
                case "Gaine": SelectedObjectType = ObjectType.Gaine; break;
                case "Porte": SelectedObjectType = ObjectType.Porte; break;
                case "Fenêtre": SelectedObjectType = ObjectType.Fenetre; break;
                default: SelectedObjectType = ObjectType.Autre; break;
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

        private void OnHostTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFamilyFilter();
        }

        private void UpdateFamilyFilter()
        {
            if (comboFamily == null || _reservationItems == null)
            {
                // Rien à filtrer pour le moment
                comboFamily.ItemsSource = null;
                comboFamily.SelectedIndex = -1;
                return;
            }

            var previousSelection = comboFamily.SelectedItem as ReservationSymbolItem;
            bool isSol = (comboHostType?.SelectedItem as string) == "Sol";

            var filtered = _reservationItems
                .Where(item => item?.Symbol?.Family?.Name != null)
                .Where(item => isSol
                    ? item.Symbol.Family.Name.IndexOf("sol", System.StringComparison.OrdinalIgnoreCase) >= 0
                    : item.Symbol.Family.Name.IndexOf("mur", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(item => item.DisplayName)
                .ToList();

            comboFamily.ItemsSource = filtered;

            if (filtered.Any())
            {
                var keepSelection = previousSelection != null
                    ? filtered.FirstOrDefault(fs => fs.Symbol.Id == previousSelection.Symbol.Id)
                    : null;

                comboFamily.SelectedItem = keepSelection ?? filtered.First();
            }
            else
                comboFamily.SelectedIndex = -1;
        }

        private static string BuildDisplayName(FamilySymbol fs)
        {
            return fs?.Family?.Name ?? string.Empty;
        }

        private static bool IsCircular(FamilySymbol fs)
        {
            string name = ($"{fs?.Name} {fs?.Family?.Name}").ToLowerInvariant();
            return name.Contains("circ") || name.Contains("ø") || name.Contains("diam");
        }

        private static bool IsRectangular(FamilySymbol fs)
        {
            string name = ($"{fs?.Name} {fs?.Family?.Name}").ToLowerInvariant();
            return name.Contains("rect") || name.Contains("rectangle");
        }

        private class ReservationSymbolItem
        {
            public ReservationSymbolItem(FamilySymbol symbol, string displayName)
            {
                Symbol = symbol;
                DisplayName = displayName;
                IsCircular = IsCircular(symbol);
                IsRectangular = IsRectangular(symbol);
            }

            public FamilySymbol Symbol { get; }
            public string DisplayName { get; }
            public bool IsCircular { get; }
            public bool IsRectangular { get; }
        }

    }
}