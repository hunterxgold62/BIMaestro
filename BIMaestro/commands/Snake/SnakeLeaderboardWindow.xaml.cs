using System.Windows;

namespace BIMaestro.Bonus
{
    public partial class SnakeLeaderboardWindow : Window
    {
        public SnakeLeaderboardWindow(SnakeLeaderboardData data)
        {
            InitializeComponent();
            DataContext = new SnakeLeaderboardViewModel(data);
        }
    }
}