using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed partial class PropertiesAppearanceWindow
    {
        private readonly HashSet<string> _collapsedGroups = new HashSet<string>();
        private Border _resetConfirmation;
        private bool IsCollapsed(PropertyPreviewRow row) => string.IsNullOrWhiteSpace(_search.Text) && _collapsedGroups.Contains(row.GroupKey ?? "");
        private void ClickRow(PropertyPreviewRow row)
        {
            if (DateTime.UtcNow < _ignoreRowClickUntil) return;
            if (row.Header)
            {
                string key = row.GroupKey ?? "";
                if (!_collapsedGroups.Remove(key)) _collapsedGroups.Add(key);
            }
            Select(row);
        }
        private UIElement BuildResetConfirmation()
        {
            var content = new StackPanel();
            content.Children.Add(Label("Retrouver l’affichage Revit d’origine ?", 15, true));
            content.Children.Add(Label("Cela retire tous vos ordres et toutes vos couleurs, pour toutes les catégories, tous les types et les deux thèmes. Les paramètres du modèle restent inchangés.", 12));
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(MakeButton("Oui, tout réinitialiser", ResetEverything, true));
            actions.Children.Add(MakeButton("Conserver mes réglages", () => _resetConfirmation.Visibility = Visibility.Collapsed)); content.Children.Add(actions);
            _resetConfirmation = Card(content, 14); _resetConfirmation.Visibility = Visibility.Collapsed; _resetConfirmation.Margin = new Thickness(0, 12, 0, 0); return _resetConfirmation;
        }
        private void ResetEverything()
        {
            _previewTimer.Stop();
            PropertiesAppearanceCommand.Request(app =>
            {
                PropertiesAppearanceService.ResetAll(); PropertiesAppearanceService.ProcessIdling(app);
                if (_closed) return;
                _loading = true; _enabled.IsChecked = false; _live.IsChecked = false; _loading = false;
                _drafts.Clear(); _drafts["all"] = new AppearanceProfile(); _dirty.Clear(); _revision++;
                _collapsedGroups.Clear(); _search.Text = ""; _context = AppearanceContext.ForEditor(app);
                ReloadContext(_context.CategoryKey ?? "all"); _resetConfirmation.Visibility = Visibility.Collapsed;
                Notify("Tout a été réinitialisé : ordre, couleurs et activation. " + PropertiesAppearanceService.Status);
            });
        }
    }
}
