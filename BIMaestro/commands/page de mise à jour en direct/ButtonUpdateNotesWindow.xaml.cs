using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Page
{
    public partial class ButtonUpdateNotesWindow : Window
    {
        public ButtonUpdateNotesWindow(IReadOnlyList<ButtonUpdateNote> notes, string previousVersion, string version)
        {
            InitializeComponent();

            var list = (notes ?? new List<ButtonUpdateNote>()).ToList();
            NotesItems.ItemsSource = list;

            var title = list.Count == 1 && !string.IsNullOrWhiteSpace(list[0].Title)
                ? $"Nouveautés - {list[0].Title}"
                : "Nouveautés BIMaestro";

            Title = title;
            HeaderTitleText.Text = title;
            HeaderSubtitleText.Text = string.IsNullOrWhiteSpace(previousVersion)
                ? $"Version {version} - première utilisation après mise à jour"
                : $"Passage {previousVersion} → {version} - première utilisation après mise à jour";
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
