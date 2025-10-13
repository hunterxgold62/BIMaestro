using System.Windows;
using System.Windows.Controls;

namespace BIMaestro.UI
{
    public partial class AnimatedDeleteIcon : UserControl
    {
        public AnimatedDeleteIcon()
        {
            InitializeComponent();
        }

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(AnimatedDeleteIcon),
                new PropertyMetadata(28.0));
    }
}
