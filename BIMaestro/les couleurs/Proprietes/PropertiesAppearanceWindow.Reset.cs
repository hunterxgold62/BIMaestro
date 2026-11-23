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
            content.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.Reset.RestoreTheOriginalRevitDisplay"), 15, true));
            content.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.Reset.ThisRemovesCustomColorsOrderAndGroupsFor"), 12));
            var actions = new WrapPanel();
            actions.Children.Add(MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.Reset.BackUpThenReset"), BackupAndReset, true));
            actions.Children.Add(MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.Reset.ResetWithoutBackingUp"), ResetEverything));
            actions.Children.Add(MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.Reset.Cancel"), () => _resetConfirmation.Visibility = Visibility.Collapsed)); content.Children.Add(actions);
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
                    var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLanguage.Get("PropertiesAppearanceWindow.Reset.BackUpMyPropertiesBeforeReset"),
                        Filter = UiLanguage.Get("PropertiesAppearanceWindow.Reset.BIMaestroPackBimaestroStyleJson"), FileName = UiLanguage.Get("PropertiesAppearanceWindow.Reset.MyPropertiesBeforeResetBimaestroStyleJson"), AddExtension = true };
                    var owner = Window.GetWindow(this);
                    saved = (owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
                    if (saved) Couleur.AppearancePackFile.Export(dialog.FileName, new Couleur.AppearancePack {
                        IncludesRibbon = false, IncludesBrowser = false, IncludesProperties = true, Properties = CapturePreferences()
                    });
                }
                if (saved) ResetEverything();
            }
            catch (Exception ex) { Notify(UiLanguage.Get("PropertiesAppearanceWindow.Reset.UnableToBackUp") + ex.Message + UiLanguage.Get("PropertiesAppearanceWindow.Reset.PropertiesWereNotReset")); }
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
                Notify(UiLanguage.Get("PropertiesAppearanceWindow.Reset.PropertiesResetColorsOrderCustomGroupsAndActivation") + PropertiesAppearanceService.Status);
            });
        }
    }
}
