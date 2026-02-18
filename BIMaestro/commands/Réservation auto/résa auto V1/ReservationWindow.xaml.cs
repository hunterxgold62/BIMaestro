using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        /// <summary>
        /// Source des canalisations, calculée directement à partir du ComboBox.
        /// (Pas de setter : on lit l'état réel de la fenêtre au moment où la commande le demande.)
        /// </summary>
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

        private readonly List<FamilySymbol> _allReservationFamilies;
        private readonly List<ReservationSymbolItem> _reservationItems;

        public ExtendedReservationWindow(List<FamilySymbol> reservationFamilies)
        {
            InitializeComponent();

            // 1) On stocke toutes les familles et on prépare les items pour le ComboBox
            _allReservationFamilies = reservationFamilies ?? new List<FamilySymbol>();
            _reservationItems = _allReservationFamilies
                .Where(fs => fs != null)
                .Select(fs => new ReservationSymbolItem(fs, BuildDisplayName(fs)))
                .ToList();

            // 2) Support : mur / sol
            comboHostType.ItemsSource = new List<string>
            {
                "Mur",
                "Sol"
            };
            comboHostType.SelectedIndex = 0;

            // 3) Type d'objet
            comboObjectType.ItemsSource = new List<string>
            {
                "Canalisation",
                "Gaine",
                "Porte",
                "Fenêtre",
                "Autre"
            };
            comboObjectType.SelectedIndex = 0;

            // 4) Source des canalisations (items définis dans le XAML)
            if (comboPipeSource != null && comboPipeSource.Items.Count > 0)
                comboPipeSource.SelectedIndex = 0; // Maquette par défaut

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

        private void OnCriteriaChanged(object sender, SelectionChangedEventArgs e)
        {
            var typeSel = comboObjectType.SelectedItem as string;
            bool isCanal = typeSel == "Canalisation";
            bool isAutre = typeSel == "Autre";

            // Le choix de source n'a de sens que pour les canalisations
            if (comboPipeSource != null)
            {
                comboPipeSource.IsEnabled = isCanal;
                if (!isCanal)
                    comboPipeSource.SelectedIndex = 0; // On revient à "Maquette"
            }

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

            // Support mur / sol
            SelectedHostTarget = (comboHostType.SelectedItem as string) == "Sol"
                ? HostTarget.Sol
                : HostTarget.Mur;

            // Type d'objet
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

            // Famille de réservation
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
                comboFamily.ItemsSource = null;
                comboFamily.SelectedIndex = -1;
                return;
            }

            var previousSelection = comboFamily.SelectedItem as ReservationSymbolItem;
            bool isSol = (comboHostType?.SelectedItem as string) == "Sol";

            var filtered = _reservationItems
                .Where(item => item?.Symbol?.Family?.Name != null)
                .Where(item => isSol
                    ? item.Symbol.Family.Name.IndexOf("sol", StringComparison.OrdinalIgnoreCase) >= 0
                    : item.Symbol.Family.Name.IndexOf("mur", StringComparison.OrdinalIgnoreCase) >= 0)
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
            {
                comboFamily.SelectedIndex = -1;
            }
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
