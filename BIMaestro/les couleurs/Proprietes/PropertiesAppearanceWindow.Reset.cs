using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BIMaestro.Localization;

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
            if (row.Header && !row.ReadOnly && (row.GroupKey ?? "").StartsWith("custom:", StringComparison.Ordinal))
            {
                _groupRenameName.BringIntoView();
                _groupRenameName.Focus();
                _groupRenameName.SelectAll();
            }
        }
        private UIElement BuildResetConfirmation()
        {
            var content = new StackPanel();
            content.Children.Add(Label(UiLanguage.T("Retrouver l’affichage Revit d’origine ?", "Restore the original Revit display?"), 15, true));
            content.Children.Add(Label(UiLanguage.T("Cela retire les couleurs, l’ordre et les groupes personnalisés, pour toutes les catégories, tous les types et les deux thèmes. Sauvegardez vos réglages pour pouvoir les restaurer depuis « Partager mes réglages ».", "This removes custom colors, order and groups for all categories, all types and both themes. Back up your settings so you can restore them from “Share my settings”."), 12));
            var actions = new WrapPanel();
            actions.Children.Add(MakeButton(UiLanguage.T("Sauvegarder puis réinitialiser", "Back up, then reset"), BackupAndReset, true));
            actions.Children.Add(MakeButton(UiLanguage.T("Réinitialiser sans sauvegarder", "Reset without backing up"), ResetEverything));
            actions.Children.Add(MakeButton(UiLanguage.T("Annuler", "Cancel"), () => _resetConfirmation.Visibility = Visibility.Collapsed)); content.Children.Add(actions);
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
                    var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLanguage.T("Sauvegarder mes propriétés avant réinitialisation", "Back up my properties before reset"),
                        Filter = UiLanguage.T("Pack BIMaestro|*.bimaestro-style.json", "BIMaestro pack|*.bimaestro-style.json"), FileName = UiLanguage.T("Mes propriétés avant réinitialisation.bimaestro-style.json", "My properties before reset.bimaestro-style.json"), AddExtension = true };
                    var owner = Window.GetWindow(this);
                    saved = (owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
                    if (saved) Couleur.AppearancePackFile.Export(dialog.FileName, new Couleur.AppearancePack {
                        IncludesRibbon = false, IncludesBrowser = false, IncludesProperties = true, Properties = CapturePreferences()
                    });
                }
                if (saved) ResetEverything();
            }
            catch (Exception ex) { Notify(UiLanguage.T("Sauvegarde impossible : ", "Unable to back up:") + ex.Message + UiLanguage.T(" Les propriétés n’ont pas été réinitialisées.", " Properties were not reset.")); }
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
                Notify(UiLanguage.T("Propriétés réinitialisées : couleurs, ordre, groupes personnalisés et activation. ", "Properties reset: colors, order, custom groups and activation.") + PropertiesAppearanceService.Status);
            });
        }
    }
}
