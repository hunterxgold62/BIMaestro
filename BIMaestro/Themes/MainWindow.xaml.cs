using System.Windows;

namespace BIMaestro.Demo
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void SwitchButton_Click(object sender, RoutedEventArgs e)
        {
            string newTheme = App.CurrentTheme == "Light" ? "Dark" : "Light";
            App.ApplyTheme(newTheme);
        }
    }
}
