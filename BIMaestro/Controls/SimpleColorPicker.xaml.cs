using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using BIMaestro.Localization;

namespace BIMaestro.UI
{
    /// <summary>
    /// Sélecteur de couleur WPF autonome, sans dépendance à un toolkit tiers.
    /// </summary>
    public partial class SimpleColorPicker : UserControl
    {
        private static readonly string[] PaletteColors =
        {
            "#FFFFFFFF", "#FFF2F2F2", "#FFD1D5DB", "#FF9CA3AF", "#FF4B5563", "#FF111827",
            "#FFFFE4E6", "#FFFECACA", "#FFFCA5A5", "#FFEF4444", "#FFB91C1C", "#FF7F1D1D",
            "#FFFFF7D6", "#FFFDE68A", "#FFFBBF24", "#FFF59E0B", "#FFB45309", "#FF78350F",
            "#FFDCFCE7", "#FF86EFAC", "#FF22C55E", "#FF16A34A", "#FF15803D", "#FF14532D",
            "#FFDBEAFE", "#FF93C5FD", "#FF3B82F6", "#FF2563EB", "#FF1D4ED8", "#FF1E3A8A",
            "#FFF3E8FF", "#FFD8B4FE", "#FFA855F7", "#FF9333EA", "#FF7E22CE", "#FF581C87"
        };

        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(
                nameof(SelectedColor),
                typeof(Color?),
                typeof(SimpleColorPicker),
                new FrameworkPropertyMetadata(
                    Colors.Transparent,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnSelectedColorChanged));

        public SimpleColorPicker()
        {
            InitializeComponent();
            BuildPalette();
            UpdateVisuals(SelectedColor);
        }

        public Color? SelectedColor
        {
            get => (Color?)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        public event RoutedPropertyChangedEventHandler<Color?>? SelectedColorChanged;

        private static void OnSelectedColorChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var picker = (SimpleColorPicker)dependencyObject;
            var oldColor = (Color?)e.OldValue;
            var newColor = (Color?)e.NewValue;

            picker.UpdateVisuals(newColor);
            picker.SelectedColorChanged?.Invoke(
                picker,
                new RoutedPropertyChangedEventArgs<Color?>(oldColor, newColor));
        }

        private void BuildPalette()
        {
            foreach (string colorText in PaletteColors)
            {
                Color color = ParseColor(colorText);
                var button = new Button
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                    BorderThickness = new Thickness(1),
                    Tag = color,
                    ToolTip = ColorToText(color)
                };

                button.Click += PaletteButton_Click;
                PalettePanel.Children.Add(button);
            }
        }

        private void PaletteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Color color)
                SelectColor(color);
        }

        private void ColorPopup_Opened(object sender, EventArgs e)
        {
            HexTextBox.Text = SelectedColor.HasValue
                ? ColorToText(SelectedColor.Value)
                : string.Empty;
            HexTextBox.SelectAll();
            HexTextBox.Focus();
        }

        private void ApplyHexButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyHexColor();
        }

        private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            ApplyHexColor();
            e.Handled = true;
        }

        private void ApplyHexColor()
        {
            try
            {
                SelectColor(ParseColor(HexTextBox.Text));
            }
            catch (FormatException)
            {
                MessageBox.Show(
                    "Saisissez une couleur au format #RRGGBB ou #AARRGGBB.",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                HexTextBox.SelectAll();
                HexTextBox.Focus();
            }
        }

        private void CustomColorButton_Click(object sender, RoutedEventArgs e)
        {
            Color current = SelectedColor ?? Colors.White;

            using (var dialog = new Forms.ColorDialog
            {
                AllowFullOpen = true,
                FullOpen = true,
                Color = DrawingColor.FromArgb(current.R, current.G, current.B)
            })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK)
                    return;

                DrawingColor selected = dialog.Color;
                SelectColor(Color.FromArgb(255, selected.R, selected.G, selected.B));
            }
        }

        private void TransparentButton_Click(object sender, RoutedEventArgs e)
        {
            SelectColor(Colors.Transparent);
        }

        private void SelectColor(Color color)
        {
            SelectedColor = color;
            DropDownButton.IsChecked = false;
        }

        private void UpdateVisuals(Color? color)
        {
            if (ColorSwatch == null || ColorText == null)
                return;

            if (!color.HasValue)
            {
                ColorSwatch.Fill = Brushes.Transparent;
                ColorText.Text = UiLanguage.T("Aucune", "None");
                return;
            }

            ColorSwatch.Fill = new SolidColorBrush(color.Value);
            ColorText.Text = ColorToText(color.Value);
        }

        private static Color ParseColor(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
                return Colors.Transparent;

            object converted = ColorConverter.ConvertFromString(normalized);
            if (converted is Color color)
                return color;

            throw new FormatException("Couleur invalide.");
        }

        private static string ColorToText(Color color)
        {
            return color.A == 0
                ? "Transparent"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }
}
