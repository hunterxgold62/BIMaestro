using System.Windows;
using System.Windows.Input;

namespace BIMaestro.Bonus
{
    public partial class SnakeLeaderboardWindow : Window
    {
        public SnakeLeaderboardWindow(SnakeLeaderboardData data)
        {
            InitializeComponent();
            DataContext = new SnakeLeaderboardViewModel(data);

            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
