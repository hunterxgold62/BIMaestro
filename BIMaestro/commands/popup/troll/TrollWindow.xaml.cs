using System.Windows;
using System.Windows.Media;

namespace MyRevitTroll
{
    public partial class TrollWindow : Window
    {
        // Constructeur avec paramètres de message, couleur de fond, couleur de bord,
        // et taille (width/height) personnalisables
        public TrollWindow(string message, Brush background, Brush borderBrush, double width = 100, double height = 100)
        {
            InitializeComponent();

            // Mise à jour du texte
            MessageTextBlock.Text = message;

            // Mise à jour des couleurs
            MainBorder.Background = background;
            MainBorder.BorderBrush = borderBrush;

            // Ajustement automatique de la couleur du texte si on veut
            if (background == Brushes.Yellow)
                MessageTextBlock.Foreground = Brushes.DarkOrange;
            else if (background == Brushes.LightBlue)
                MessageTextBlock.Foreground = Brushes.Blue;
            else if (background == Brushes.LightGreen)
                MessageTextBlock.Foreground = Brushes.Green;
            else if (background == Brushes.White)
                MessageTextBlock.Foreground = Brushes.Black;
            else if (background == Brushes.LightGray)
                MessageTextBlock.Foreground = Brushes.Gray;
            else if (background == Brushes.LightCyan)
                MessageTextBlock.Foreground = Brushes.DarkCyan;
            // etc. Sinon, par défaut rouge

            // Ajustement de la taille de la fenêtre
            this.Width = width;
            this.Height = height;
        }
    }
}
