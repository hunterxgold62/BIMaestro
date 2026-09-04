using Autodesk.Revit.UI;
using BIMaestro.RibbonLayout;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BIMaestro.UI
{
    internal static class RadialButtonsService
    {
        private static RadialPostCommandHandler _postHandler;
        private static ExternalEvent _postEvent;
        private static RadialMenuWindow _activeWindow;

        public static void Show(UIApplication uiApplication)
        {
            if (uiApplication == null || _activeWindow != null) return;
            EnsurePostEvent();
            var preferences = RadialButtonsPreferencesManager.Load();
            var items = BuildItems(preferences);
            var (screenX, screenY) = OwnerWindowHelper.GetCursorPosPx();
            var window = new RadialMenuWindow(items, screenX, screenY);
            _activeWindow = window;
            new WindowInteropHelper(window) { Owner = uiApplication.MainWindowHandle };
            ApplyPreferenceState(window, preferences);
            window.ConfigureButtonRequested += index => ShowPicker(uiApplication, window, index);
            window.RemovePinnedButtonRequested += index =>
            {
                if (!RadialButtonsPreferencesManager.SetPinnedButton(index, null))
                {
                    TaskDialog.Show("BIMaestro", "Impossible d’enregistrer la modification de la rosace.");
                    return;
                }
                RefreshWindow(window);
            };
            window.ResetButtonsRequested += () =>
            {
                window.BeginModalInteraction();
                TaskDialogResult answer;
                try
                {
                    var dialog = new TaskDialog("BIMaestro")
                    {
                        MainInstruction = "Réinitialiser la rosace ?",
                        MainContent = "Tous les boutons fixés seront supprimés. Les 16 positions afficheront de nouveau les boutons récents.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    answer = dialog.Show();
                }
                finally
                {
                    window.EndModalInteraction();
                }
                if (answer != TaskDialogResult.Yes) return;
                if (!RadialButtonsPreferencesManager.ResetPinnedButtons())
                {
                    TaskDialog.Show("BIMaestro", "Impossible de réinitialiser la rosace.");
                    return;
                }
                RefreshWindow(window);
            };
            window.Completed += (accepted, _, item) =>
            {
                if (!accepted || item == null) return;
                var info = AppUI.GetRibbonButtonById(item.ButtonId) ?? AppUI.GetRibbonButtonByCommandClass(item.CommandClass);
                var commandId = AppUI.ResolveRibbonCommandId(info);
                if (commandId == null)
                {
                    TaskDialog.Show("BIMaestro", "Cette commande n’est plus disponible.");
                    return;
                }
                _postHandler.Prepare(commandId, info.DisplayName);
                _postEvent.Raise();
            };
            window.Closed += (_, __) => { if (ReferenceEquals(_activeWindow, window)) _activeWindow = null; };
            window.Show();
            window.Activate();
        }

        private static void ShowPicker(UIApplication uiApplication, RadialMenuWindow window, int slotIndex)
        {
            window.BeginModalInteraction();
            try
            {
                var picker = new RadialButtonPickerWindow(AppUI.GetRibbonButtonInfos());
                new WindowInteropHelper(picker) { Owner = uiApplication.MainWindowHandle };
                if (picker.ShowDialog() != true || picker.SelectedButton == null) return;
                if (!RadialButtonsPreferencesManager.SetPinnedButton(slotIndex, picker.SelectedButton.Id))
                {
                    TaskDialog.Show("BIMaestro", "Impossible d’enregistrer le bouton choisi.");
                    return;
                }
                RefreshWindow(window);
                // Garantie visuelle immédiate : cette mise à jour utilise l'objet
                // sélectionné, sans dépendre d'une nouvelle résolution du registre.
                window.ReplaceItem(slotIndex, BuildItem(picker.SelectedButton, true));
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMaestro", $"Impossible de mettre à jour la rosace : {ex.Message}");
            }
            finally
            {
                window.EndModalInteraction();
            }
        }

        private static void RefreshWindow(RadialMenuWindow window)
        {
            if (window == null) return;
            var preferences = RadialButtonsPreferencesManager.Load();
            ApplyPreferenceState(window, preferences);
            window.ReplaceItems(BuildItems(preferences));
        }

        private static System.Collections.Generic.List<RadialItem> BuildItems(RadialButtonsPreferences preferences)
        {
            var registry = AppUI.GetRibbonButtonInfos() ?? Array.Empty<RibbonButtonInfo>();
            var recents = ButtonRecentManager.LoadMostRecentDistinct(100, entry =>
                !string.Equals(entry?.ButtonId, "RadialMenuButtonsCommand", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entry?.CommandClass, typeof(RadialMenuButtonsCommand).FullName, StringComparison.OrdinalIgnoreCase));
            var merged = RadialButtonsPreferencesManager.MergeButtons(registry, recents, preferences);
            var items = merged.Select((info, index) => BuildItem(
                info,
                index < preferences.PinnedButtonIds.Count && info != null &&
                string.Equals(preferences.PinnedButtonIds[index], info.Id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            WriteResolvedLayout(preferences, items);
            return items;
        }

        private static void ApplyPreferenceState(RadialMenuWindow window, RadialButtonsPreferences preferences)
        {
            window.SetPageLabelFactory((index, count) =>
                $"{index + 1}/{count}");
            window.IsButtonPinned = index => index >= 0 && index < preferences.PinnedButtonIds.Count &&
                !string.IsNullOrWhiteSpace(preferences.PinnedButtonIds[index]);
        }

        private static void WriteResolvedLayout(
            RadialButtonsPreferences preferences,
            System.Collections.Generic.IReadOnlyList<RadialItem> items)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs", "SauvegardePréférence");
                Directory.CreateDirectory(folder);
                var lines = Enumerable.Range(0, RadialButtonsPreferencesManager.SlotCount)
                    .Select(index =>
                    {
                        string requested = index < preferences.PinnedButtonIds.Count
                            ? preferences.PinnedButtonIds[index]
                            : null;
                        var item = index < items.Count ? items[index] : null;
                        return $"{index + 1:00} | favori={requested ?? "-"} | affiche={item?.ButtonId ?? "-"} | libelle={item?.Label ?? "-"}";
                    });
                File.WriteAllLines(Path.Combine(folder, "RadialButtonsResolved.log"), lines);
            }
            catch { }
        }

        private static RadialItem BuildItem(RibbonButtonInfo info, bool isPinned)
        {
            if (info == null) return new RadialItem();
            return new RadialItem
            {
                ButtonId = info.Id,
                CommandClass = info.CommandClass,
                ImagePath = RibbonButtonImageCache.GetOrCreate(info.ImageResourceName),
                Label = (info.DisplayName ?? string.Empty).Replace("\r", " ").Replace("\n", " "),
                IsPinned = isPinned
            };
        }

        private static void EnsurePostEvent()
        {
            if (_postEvent != null) return;
            _postHandler = new RadialPostCommandHandler();
            _postEvent = ExternalEvent.Create(_postHandler);
        }

        private sealed class RadialPostCommandHandler : IExternalEventHandler
        {
            private RevitCommandId _commandId;
            private string _displayName;
            public void Prepare(RevitCommandId commandId, string displayName) { _commandId = commandId; _displayName = displayName; }

            public void Execute(UIApplication app)
            {
                var commandId = _commandId;
                _commandId = null;
                if (commandId == null) return;
                try
                {
                    if (!app.CanPostCommand(commandId))
                    {
                        TaskDialog.Show("BIMaestro",
                            $"La commande « {Normalize(_displayName)} » n’est pas disponible dans le contexte actif.");
                        return;
                    }
                    app.PostCommand(commandId);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("BIMaestro", $"Impossible de lancer cette commande : {ex.Message}");
                }
            }

            public string GetName() => "BIMaestro radial command launcher";
            private static string Normalize(string value) => (value ?? "bouton").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
