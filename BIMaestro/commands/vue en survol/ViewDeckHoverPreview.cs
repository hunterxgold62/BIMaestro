using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BIMaestro.ViewHover
{
    // UI only: hovering never calls Revit, opens a view, reads a file or exports.
    internal sealed class ViewDeckHoverPreview : IDisposable
    {
        internal const int DelayMilliseconds = 500;
        private readonly TabItem _tab;
        private readonly TextBlock _title;
        private readonly Border _added, _modified, _deleted;
        private readonly Image _image;
        private readonly TextBlock _placeholder;
        private bool _disposed;
        private readonly DispatcherTimer _delay;
        private Window _owner;
        internal ToolTip ToolTip { get; }

        internal ViewDeckHoverPreview(TabItem tab)
        {
            _tab = tab;
            _title = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _added = Badge(Color.FromRgb(232, 245, 237), Color.FromRgb(27, 112, 62));
            _modified = Badge(Color.FromRgb(255, 243, 224), Color.FromRgb(166, 79, 0));
            _deleted = Badge(Color.FromRgb(253, 236, 236), Color.FromRgb(169, 48, 48));
            var information = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            information.Children.Add(_added);
            information.Children.Add(_modified);
            information.Children.Add(_deleted);
            var heading = new Grid { Height = 24, Margin = new Thickness(0, 0, 0, 8) };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(information, 1);
            heading.Children.Add(_title);
            heading.Children.Add(information);
            _image = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            _placeholder = new TextBlock
            {
                FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var preview = new Grid { Height = 340, Background = Brushes.White };
            preview.Children.Add(_placeholder);
            preview.Children.Add(_image);
            var content = new StackPanel { Background = Brushes.White };
            content.Children.Add(heading);
            content.Children.Add(new Border
            {
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Padding = new Thickness(5), Child = preview
            });
            ToolTip = new ToolTip
            {
                Content = content, Width = 490, MaxWidth = Math.Max(180, Math.Min(490, SystemParameters.WorkArea.Width - 32)),
                Padding = new Thickness(12), Background = Brushes.White, Foreground = Brushes.Black,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Placement = PlacementMode.Bottom, PlacementTarget = tab, VerticalOffset = 5,
                Focusable = false, IsHitTestVisible = false, StaysOpen = true
            };
            tab.PreviewMouseDown += OnMouseDown;
            tab.Unloaded += OnUnloaded;
            tab.MouseEnter += OnMouseEnter;
            tab.MouseLeave += OnMouseLeave;
            tab.ToolTipOpening += OnNativeToolTipOpening;
            _delay = new DispatcherTimer(DispatcherPriority.Background, tab.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(DelayMilliseconds)
            };
            _delay.Tick += OnDelay;
        }

        private static Border Badge(Color background, Color foreground) => new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(6, 0, 0, 0), Background = new SolidColorBrush(background),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(foreground) }
        };

        private static void UpdateBadge(Border badge, string symbol, int count, bool partial)
        {
            badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)badge.Child).Text = count > 0 ? (partial ? "≈" : "") + symbol + " " + count : string.Empty;
        }

        internal void Update(string title, ImageSource image, string placeholder, ViewDeckChangeCounts changes = null)
        {
            if (_disposed) return;
            _title.Text = title ?? string.Empty;
            UpdateBadge(_added, "+", changes?.Added ?? 0, changes?.Partial ?? false);
            UpdateBadge(_modified, "~", changes?.Modified ?? 0, changes?.Partial ?? false);
            UpdateBadge(_deleted, "−", changes?.Deleted ?? 0, changes?.Partial ?? false);
            _image.Source = image;
            _image.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
            _placeholder.Text = placeholder ?? string.Empty;
            _placeholder.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnMouseEnter(object sender, MouseEventArgs args)
        {
            if (_disposed) return;
            SetOwner(Window.GetWindow(_tab));
            _delay.Stop();
            _delay.Start();
        }

        private void OnMouseLeave(object sender, MouseEventArgs args) => Close();
        private void OnNativeToolTipOpening(object sender, ToolTipEventArgs args)
        {
            if (!_disposed) args.Handled = true; // Keep the native tooltip VALUE for identification/coloring.
        }

        private void OnDelay(object sender, EventArgs args)
        {
            _delay.Stop();
            if (!_disposed && _tab.IsLoaded && _tab.IsVisible && _tab.IsMouseOver &&
                Mouse.LeftButton == MouseButtonState.Released && (_owner == null || _owner.IsActive))
                ToolTip.IsOpen = true;
        }

        private void SetOwner(Window owner)
        {
            if (_owner == owner) return;
            if (_owner != null) _owner.Deactivated -= OnOwnerDeactivated;
            _owner = owner;
            if (_owner != null) _owner.Deactivated += OnOwnerDeactivated;
        }

        private void OnOwnerDeactivated(object sender, EventArgs args) => Close();
        private void OnMouseDown(object sender, MouseButtonEventArgs args) => Close();
        private void OnUnloaded(object sender, RoutedEventArgs args) { Close(); SetOwner(null); }
        internal void Close()
        {
            _delay?.Stop();
            ToolTip.IsOpen = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _tab.PreviewMouseDown -= OnMouseDown;
            _tab.Unloaded -= OnUnloaded;
            _tab.MouseEnter -= OnMouseEnter;
            _tab.MouseLeave -= OnMouseLeave;
            _tab.ToolTipOpening -= OnNativeToolTipOpening;
            _delay.Tick -= OnDelay;
            SetOwner(null);
            ToolTip.PlacementTarget = null;
            ToolTip.Content = null;
            _image.Source = null;
        }
    }
}
