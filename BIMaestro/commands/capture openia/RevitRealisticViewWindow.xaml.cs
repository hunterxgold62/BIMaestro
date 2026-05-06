using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace IA
{
    public partial class RevitRealisticViewWindow : Window
    {
        public ElementId SelectedViewId { get; private set; }

        public RevitRealisticViewWindow(IReadOnlyCollection<View> views)
        {
            InitializeComponent();
            ViewsList.ItemsSource = views.Select(v => new ViewItem(v)).ToList();
            if (ViewsList.Items.Count > 0) ViewsList.SelectedIndex = 0;
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (ViewsList.SelectedItem is ViewItem item)
            {
                SelectedViewId = item.Id;
                DialogResult = true;
                Close();
                return;
            }

            MessageBox.Show("Sélectionnez une vue.");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private sealed class ViewItem
        {
            public ElementId Id { get; }
            public string DisplayName { get; }

            public ViewItem(View view)
            {
                Id = view.Id;
                DisplayName = $"[{view.ViewType}] {view.Name}";
            }
        }
    }
}
