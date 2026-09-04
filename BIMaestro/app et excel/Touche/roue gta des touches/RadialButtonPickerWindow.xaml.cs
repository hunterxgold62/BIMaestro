using BIMaestro.RibbonLayout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BIMaestro.UI
{
    public partial class RadialButtonPickerWindow : Window
    {
        private readonly List<PickerItem> _items;
        public RibbonButtonInfo SelectedButton { get; private set; }

        public RadialButtonPickerWindow(IEnumerable<RibbonButtonInfo> buttons)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _items = (buttons ?? Array.Empty<RibbonButtonInfo>())
                .Where(b => b != null && !string.Equals(b.Id, "RadialMenuButtonsCommand", StringComparison.OrdinalIgnoreCase))
                .Select(b => new PickerItem
                {
                    Button = b,
                    Label = Normalize(b.DisplayName),
                    ImagePath = RibbonButtonImageCache.GetOrCreate(b.ImageResourceName)
                })
                .OrderBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ApplyFilter();
            Loaded += (_, __) => SearchBox.Focus();
        }

        private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            if (ButtonsList == null) return;
            string query = SearchBox?.Text?.Trim() ?? string.Empty;
            ButtonsList.ItemsSource = string.IsNullOrEmpty(query)
                ? _items
                : _items.Where(item => item.Label.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        }

        private void Choose_Click(object sender, RoutedEventArgs e) => AcceptSelection();
        private void ButtonsList_DoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void AcceptSelection()
        {
            if (ButtonsList.SelectedItem is not PickerItem item) return;
            SelectedButton = item.Button;
            DialogResult = true;
            Close();
        }

        private static string Normalize(string value) => string.Join(" ", (value ?? string.Empty)
            .Replace("\r", " ").Replace("\n", " ")
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        private sealed class PickerItem
        {
            public RibbonButtonInfo Button { get; set; }
            public string Label { get; set; }
            public string ImagePath { get; set; }
        }
    }
}
