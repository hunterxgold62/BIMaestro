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
        public enum ObjectType
        {
            Canalisation,
            Gaine,
            Porte,
            Fenetre,
            Autre
        }

        public ObjectType SelectedObjectType { get; private set; }
        public FamilySymbol SelectedReservationSymbol { get; private set; }
        public bool NormeEnabled { get; private set; }
        public bool DynamoAutoEnabled { get; private set; }
        public bool AutomatiqueEnabled { get; private set; }
        public bool MultiEnabled { get; private set; }

        public ExtendedReservationWindow(List<FamilySymbol> reservationFamilies)
        {
            InitializeComponent();

            comboObjectType.ItemsSource = new List<string>
            {
                "Canalisation",
                "Gaine",
                "Porte",
                "Fenêtre",
                "Autre"
            };
            comboObjectType.SelectedIndex = 0;

            reservationFamilies ??= new List<FamilySymbol>();
            comboFamily.ItemsSource = reservationFamilies.OrderBy(fs => fs?.Name).ToList();
            if (reservationFamilies.Any())
                comboFamily.SelectedIndex = 0;

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

            var fam = comboFamily.SelectedItem as FamilySymbol;
            bool isRect = fam != null &&
                          ((fam.Name?.IndexOf("rect", System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                           || (fam.Family?.Name?.IndexOf("rect", System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

            chkMulti.IsEnabled = (isCanal || isAutre) && isRect;
            if (!chkMulti.IsEnabled)
                chkMulti.IsChecked = false;
        }

        private void OnOkClicked(object sender, RoutedEventArgs e)
        {
            NormeEnabled = chkNorme.IsChecked == true;
            DynamoAutoEnabled = chkDynamoAuto.IsChecked == true;
            AutomatiqueEnabled = chkAutomatique.IsChecked == true;
            MultiEnabled = chkMulti.IsChecked == true;

            switch (comboObjectType.SelectedItem as string)
            {
                case "Canalisation": SelectedObjectType = ObjectType.Canalisation; break;
                case "Gaine": SelectedObjectType = ObjectType.Gaine; break;
                case "Porte": SelectedObjectType = ObjectType.Porte; break;
                case "Fenêtre": SelectedObjectType = ObjectType.Fenetre; break;
                default: SelectedObjectType = ObjectType.Autre; break;
            }

            SelectedReservationSymbol = comboFamily.SelectedItem as FamilySymbol;

            DialogResult = true;
            Close();
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}