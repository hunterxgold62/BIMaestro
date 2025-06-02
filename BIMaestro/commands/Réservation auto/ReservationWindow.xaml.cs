using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;

namespace RevitAddinReservationExample
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

        // Propriétés pour récupérer le choix final
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

            comboFamily.ItemsSource = reservationFamilies;
            if (reservationFamilies.Any())
                comboFamily.SelectedIndex = 0;
        }

        // Dès que l'utilisateur change type ou famille, on active/désactive "Multi"
        private void OnCriteriaChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool isCanal = (comboObjectType.SelectedItem as string) == "Canalisation";
            var fam = comboFamily.SelectedItem as FamilySymbol;
            bool isRect = fam != null &&
                          (fam.Name.IndexOf("rect", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || fam.Family.Name.IndexOf("rect", System.StringComparison.OrdinalIgnoreCase) >= 0);

            chkMulti.IsEnabled = isCanal && isRect;
            if (!chkMulti.IsEnabled)
                chkMulti.IsChecked = false;
        }

        private void OnOkClicked(object sender, RoutedEventArgs e)
        {
            NormeEnabled = chkNorme.IsChecked == true;
            DynamoAutoEnabled = chkDynamoAuto.IsChecked == true;
            AutomatiqueEnabled = chkAutomatique.IsChecked == true;
            MultiEnabled = chkMulti.IsChecked == true;

            // Type d'objet choisi
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
