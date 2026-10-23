using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed partial class PropertiesAppearanceWindow
    {
        private Point _dragStart;
        private DateTime _ignoreRowClickUntil;
        private PropertyPreviewRow _dragCandidate;
        private Button _dropIndicator;
        private Brush _previousDropBrush;
        private Thickness _previousDropThickness;
        private ScrollViewer _previewScroll;
        private DateTime _lastDragScroll;
        private readonly Dictionary<PropertyPreviewRow,string> _originNumbers = new Dictionary<PropertyPreviewRow,string>();
        private const string DragFormat = "BIMaestro.PropertiesAppearance.Row";
        private AppearanceLayout DraftLayout => AppearanceOrdering.Resolve(new AppearancePreferences { Profiles = _drafts.Values.ToList() }, _context.CategoryKey, _context.TypeKey);

        private void RenderComparison()
        {
            _rows.Children.Clear(); if (_context == null) return;
            // Origin references are assigned before filtering, collapse and drag ordering.
            _originNumbers.Clear(); int groupNumber=0, parameterNumber=0;
            foreach(var row in _context.PreviewRows)
            {
                if(row.Header){groupNumber++;parameterNumber=0;}else parameterNumber++;
                _originNumbers[row]=row.Header?groupNumber.ToString():groupNumber+"."+parameterNumber;
            }
            string query = AppearancePreferences.Normalize(_search.Text);
            Func<IList<PropertyPreviewRow>, List<PropertyPreviewRow>> filter = rows =>
            {
                var matching = rows.Where(r => query.Length == 0 || AppearancePreferences.Normalize(r.Label + " " + r.Value).Contains(query)).ToList();
                var groups = new HashSet<string>(matching.Where(r => !r.Header).Select(r => r.GroupKey));
                var matchedTitles = new HashSet<string>(matching.Where(r => r.Header).Select(r => r.GroupKey));
                return rows.Where(r => (matching.Contains(r) || r.Header && groups.Contains(r.GroupKey) || query.Length > 0 && matchedTitles.Contains(r.GroupKey)) && (r.Header || !IsCollapsed(r))).ToList();
            };
            var before = filter(_context.PreviewRows); var after = filter(AppearanceOrdering.Arrange(_context.PreviewRows, DraftLayout));
            for (int i = 0; i < Math.Max(before.Count, after.Count); i++)
            {
                UIElement left = i < before.Count ? PreviewCell(before[i], false) : new Border();
                UIElement right = i < after.Count ? PreviewCell(after[i], true) : new Border();
                var pair = Pair(left, right); pair.Margin = new Thickness(0, 0, 3, 3); _rows.Children.Add(pair);
            }
            if (before.Count == 0) _rows.Children.Add(Label(_context.PreviewRows.Count == 0 ? "Ouvrez un projet et affichez la palette Propriétés, puis cliquez sur « Changer de sélection »." : "Aucune propriété ne correspond à cette recherche.", 14, false, true));
        }
        private void EnableDragging(Button button, PropertyPreviewRow row)
        {
            button.AllowDrop = true;
            button.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragCandidate = row; };
            button.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate != row) return;
                Point point = e.GetPosition(this);
                if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragCandidate = null; _ignoreRowClickUntil = DateTime.UtcNow.AddHours(1);
                try { DragDrop.DoDragDrop(button, new DataObject(DragFormat, row), DragDropEffects.Move); }
                finally { _ignoreRowClickUntil = DateTime.UtcNow.AddMilliseconds(250); ClearDropIndicator(); RenderRows(); }
            };
            button.DragOver += (_, e) =>
            {
                var source = e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as PropertyPreviewRow : null;
                bool valid = CanMove(source, row);
                e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
                ClearDropIndicator();
                if (!valid) { if (source != null && !source.Header && source.GroupKey != row.GroupKey) Notify("Pour cet essai, les paramètres restent dans leur groupe Revit. Les groupes entiers peuvent être déplacés librement."); return; }
                bool after = e.GetPosition(button).Y > button.ActualHeight / 2;
                _dropIndicator = button; _previousDropBrush = button.BorderBrush; _previousDropThickness = button.BorderThickness;
                button.BorderBrush = Brush("#19A36E"); button.BorderThickness = after ? new Thickness(1, 1, 1, 4) : new Thickness(1, 4, 1, 1);
                if (_previewScroll != null && DateTime.UtcNow >= _lastDragScroll)
                {
                    _lastDragScroll = DateTime.UtcNow.AddMilliseconds(75);
                    double y = e.GetPosition(_previewScroll).Y;
                    if (y < 36) _previewScroll.ScrollToVerticalOffset(_previewScroll.VerticalOffset - 22);
                    else if (y > _previewScroll.ActualHeight - 36) _previewScroll.ScrollToVerticalOffset(_previewScroll.VerticalOffset + 22);
                }
            };
            button.DragLeave += (_, __) => { if (_dropIndicator == button) ClearDropIndicator(); };
            button.Drop += (_, e) =>
            {
                var source = e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as PropertyPreviewRow : null;
                bool after = e.GetPosition(button).Y > button.ActualHeight / 2; ClearDropIndicator();
                if (CanMove(source, row)) MoveRow(source, row, after); e.Handled = true;
            };
        }
        private void ClearDropIndicator()
        {
            if (_dropIndicator == null) return;
            _dropIndicator.BorderBrush = _previousDropBrush; _dropIndicator.BorderThickness = _previousDropThickness; _dropIndicator = null;
        }
        private bool CanMove(PropertyPreviewRow source, PropertyPreviewRow target)
        {
            if (source == null || target == null || ReferenceEquals(source, target) || !_context.PreviewRows.Contains(source) || _profile == null) return false;
            if (source.Header) return target.Header && source.GroupKey != null && target.GroupKey != null;
            if (source.GroupKey == null || source.GroupKey != target.GroupKey) return false;
            return _context.PreviewRows.Count(r => r.OrderKey == source.OrderKey) == 1 && _context.PreviewRows.Count(r => r.OrderKey == target.OrderKey) == 1;
        }
        private void MoveRow(PropertyPreviewRow source, PropertyPreviewRow target, bool after)
        {
            if (!CanMove(source, target)) return;
            var arranged = AppearanceOrdering.Arrange(_context.PreviewRows, DraftLayout);
            var keys = source.Header ? arranged.Where(r => r.Header).Select(r => r.GroupKey).ToList() : arranged.Where(r => !r.Header && r.GroupKey == source.GroupKey).Select(r => r.OrderKey).ToList();
            string sourceKey = source.Header ? source.GroupKey : source.OrderKey;
            string targetKey = source.Header ? target.GroupKey : target.OrderKey;
            keys.Remove(sourceKey); int index = target.Header && !source.Header ? 0 : keys.IndexOf(targetKey) + (after ? 1 : 0);
            if (index < 0) return; keys.Insert(Math.Min(index, keys.Count), sourceKey);
            if (source.Header) _profile.Layout.Groups = keys; else _profile.Layout.Parameters[source.GroupKey] = keys;
            _loading = true; _enabled.IsChecked = true; _loading = false; _selected = source; Changed(); LoadColors();
            Notify((source.Header ? "Groupe déplacé avec ses paramètres." : "Paramètre déplacé dans son groupe.") + " Enregistrez pour conserver cet ordre.");
        }
        private void ResetOrder()
        {
            if (_profile == null) return; _profile.Layout = new AppearanceLayout(); Changed();
            Notify("Ordre personnalisé retiré pour cette portée. Un ordre plus général peut rester applicable.");
        }
    }
}
