using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed partial class PropertiesAppearanceEditor
    {
        private readonly HashSet<string> _collapsedGroups = new HashSet<string>();
        private Border _resetConfirmation;
        internal Func<bool> SaveBackupRequested;
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
            content.Children.Add(Label("Cela retire les couleurs, l’ordre et les groupes personnalisés, pour toutes les catégories, tous les types et les deux thèmes. Sauvegardez vos réglages pour pouvoir les restaurer depuis « Partager mes réglages ».", 12));
            var actions = new WrapPanel();
            actions.Children.Add(MakeButton("Sauvegarder puis réinitialiser", BackupAndReset, true));
            actions.Children.Add(MakeButton("Réinitialiser sans sauvegarder", ResetEverything));
            actions.Children.Add(MakeButton("Annuler", () => _resetConfirmation.Visibility = Visibility.Collapsed)); content.Children.Add(actions);
            _resetConfirmation = Card(content, 14); _resetConfirmation.Visibility = Visibility.Collapsed; _resetConfirmation.Margin = new Thickness(0, 12, 0, 0); return _resetConfirmation;
        }
        internal void ShowResetConfirmation() => _resetConfirmation.Visibility = Visibility.Visible;
        private void BackupAndReset()
        {
            try
            {
                bool saved;
                if (SaveBackupRequested != null) saved = SaveBackupRequested();
                else
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Sauvegarder mes propriétés avant réinitialisation",
                        Filter = "Pack BIMaestro|*.bimaestro-style.json", FileName = "Mes propriétés avant réinitialisation.bimaestro-style.json", AddExtension = true };
                    var owner = Window.GetWindow(this);
                    saved = (owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
                    if (saved) Couleur.AppearancePackFile.Export(dialog.FileName, new Couleur.AppearancePack {
                        IncludesRibbon = false, IncludesBrowser = false, IncludesProperties = true, Properties = CapturePreferences()
                    });
                }
                if (saved) ResetEverything();
            }
            catch (Exception ex) { Notify("Sauvegarde impossible : " + ex.Message + " Les propriétés n’ont pas été réinitialisées."); }
        }
        private void ResetEverything()
        {
            _previewTimer.Stop();
            PropertiesAppearanceCommand.Request(app =>
            {
                if (_closed) return;
                PropertiesAppearanceService.ResetAll(); PropertiesAppearanceService.ProcessIdling(app);
                if (_closed) return;
                _loading = true; _enabled.IsChecked = false; _live.IsChecked = false; _loading = false;
                _drafts.Clear(); _drafts["all"] = new AppearanceProfile(); _dirty.Clear(); _revision++;
                _collapsedGroups.Clear(); _search.Text = ""; _context = AppearanceContext.ForEditor(app);
                ReloadContext(_context.CategoryKey ?? "all"); _resetConfirmation.Visibility = Visibility.Collapsed;
                Notify("Propriétés réinitialisées : couleurs, ordre, groupes personnalisés et activation. " + PropertiesAppearanceService.Status);
            });
        }
    }
}
