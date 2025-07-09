// UI/CleanupWindow.xaml.cs
using System.Windows;

namespace MyRevitAddin.UI
{
    public partial class CleanupWindow : Window
    {
        public bool DeleteViews => ViewsCheckbox.IsChecked == true;
        public bool DeleteFamilies => FamiliesCheckbox.IsChecked == true;
        public bool DeleteSchedules => SchedulesCheckbox.IsChecked == true;
        public bool DeleteHardFamilies => HardFamiliesCheckbox.IsChecked == true;

        public CleanupWindow()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
