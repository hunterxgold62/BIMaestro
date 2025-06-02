using System.Windows;

namespace IA
{
    public partial class ProfileSelectionWindow : Window
    {
        public string SelectedProfile { get; private set; }

        public ProfileSelectionWindow()
        {
            InitializeComponent();
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (BasiqueRadio.IsChecked == true)
                SelectedProfile = "Basique";
            else if (PersonnelleRevitRadio.IsChecked == true)
                SelectedProfile = "Personnelle Revit";
            else if (BIMManagerRadio.IsChecked == true)
                SelectedProfile = "BIM Manager";

            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
