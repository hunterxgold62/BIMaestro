using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BIMaestro.ViewHover
{
    // Owns only the extra row. Never replaces Revit's tab templates or content.
    internal sealed class ViewDeckHost : IDisposable
    {
        private const string Marker = "BIMaestroViewDeck";
        private readonly Grid _grid;
        private readonly FrameworkElement _strip;
        private readonly RowDefinition _row = new RowDefinition { Height = GridLength.Auto };
        private readonly RowDefinition _implicitRow;
        private readonly Dictionary<UIElement, int> _originalRows = new Dictionary<UIElement, int>();
        private readonly Dictionary<UIElement, object> _localRows = new Dictionary<UIElement, object>();
        private bool _disposed;

        internal Grid Grid => _grid;
        internal bool IsAttached => !_disposed && _grid.Children.Contains(_strip) && _grid.RowDefinitions.Contains(_row);

        internal static ViewDeckHost Attach(Grid grid, FrameworkElement strip)
        {
            if (grid == null || strip == null || strip.Parent != null ||
                grid.Children.OfType<FrameworkElement>().Any(child => child.Name == Marker))
                return null;

            // A bound row belongs to the host application: don't override it.
            if (grid.Children.Cast<UIElement>().Any(child =>
                BindingOperations.IsDataBound(child, Grid.RowProperty))) return null;
            return new ViewDeckHost(grid, strip);
        }

        private ViewDeckHost(Grid grid, FrameworkElement strip)
        {
            _grid = grid;
            _strip = strip;
            foreach (UIElement child in grid.Children)
            {
                _originalRows.Add(child, Grid.GetRow(child));
                _localRows.Add(child, child.ReadLocalValue(Grid.RowProperty));
            }

            if (grid.RowDefinitions.Count == 0)
                _implicitRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            try
            {
                if (_implicitRow != null) grid.RowDefinitions.Add(_implicitRow);
                grid.RowDefinitions.Insert(0, _row);
                foreach (var entry in _originalRows) Grid.SetRow(entry.Key, entry.Value + 1);
                strip.Name = Marker;
                Grid.SetRow(strip, 0);
                Grid.SetColumn(strip, 0);
                Grid.SetColumnSpan(strip, Math.Max(1, grid.ColumnDefinitions.Count));
                grid.Children.Add(strip);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _grid.Children.Remove(_strip);
            bool hasRow = _grid.RowDefinitions.Contains(_row);
            foreach (UIElement child in _grid.Children)
            {
                if (_originalRows.TryGetValue(child, out int original))
                {
                    // Preserve changes made by Revit or another add-in since attachment.
                    if (Grid.GetRow(child) != original + 1) continue;
                    object local = _localRows[child];
                    if (local == DependencyProperty.UnsetValue) child.ClearValue(Grid.RowProperty);
                    else child.SetValue(Grid.RowProperty, local);
                }
                else if (hasRow && Grid.GetRow(child) > 0 &&
                         !BindingOperations.IsDataBound(child, Grid.RowProperty))
                    Grid.SetRow(child, Grid.GetRow(child) - 1);
            }
            if (hasRow) _grid.RowDefinitions.Remove(_row);
            if (_implicitRow != null) _grid.RowDefinitions.Remove(_implicitRow);
            _originalRows.Clear();
            _localRows.Clear();
        }
    }
}
