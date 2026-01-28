using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.RibbonLayout;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.UI
{
    [Transaction(TransactionMode.Manual)]
    public class RadialMenuButtonsCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RadialMenuButtonsCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = data.Application;
                AppUI.SetUiApplication(uiapp);

                var (screenX, screenY) = OwnerWindowHelper.GetCursorPosPx();
                var items = BuildRecentButtonItems();

                var win = new RadialMenuWindow(items, screenX, screenY);
                win.SetPageLabelFactory((index, count) => index switch
                {
                    0 => "Boutons récents (1/2)",
                    1 => "Boutons récents (2/2)",
                    _ => $"Page {index + 1}/{count}",
                });

                win.Completed += (accepted, _, item) =>
                {
                    if (!accepted || item == null || string.IsNullOrWhiteSpace(item.CommandClass)) return;
                    var commandId = ResolveCommandId(item.CommandClass);
                    if (commandId == null) return;

                    RevitIdleRunner.Run(uiapp, () =>
                    {
                        try { uiapp.PostCommand(commandId); } catch { }
                    });
                };

                win.Show();
                win.Activate();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }

        private static List<RadialItem> BuildRecentButtonItems()
        {
            var buttons = AppUI.GetRibbonButtonInfos() ?? Array.Empty<RibbonButtonInfo>();
            var byCommand = buttons
                .Where(b => !string.IsNullOrWhiteSpace(b.CommandClass))
                .ToDictionary(b => b.CommandClass, StringComparer.OrdinalIgnoreCase);
            var byButtonId = buttons
                .Where(b => !string.IsNullOrWhiteSpace(b.Id))
                .ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);

            var recentEntries = ButtonRecentManager.LoadMostRecentDistinct(16, entry =>
                MatchesRegistry(entry, byCommand, byButtonId));
            var items = new List<RadialItem>();

            foreach (var entry in recentEntries)
            {
                var info = ResolveInfo(entry, byCommand, byButtonId);
                if (info == null) continue;
                items.Add(BuildItem(info));
            }

            while (items.Count < 16) items.Add(new RadialItem());
            return items;
        }

        private static RadialItem BuildItem(RibbonButtonInfo info)
        {
            string label = info.DisplayName?.Replace("\n", " ").Replace("\r", " ");
            return new RadialItem
            {
                CommandClass = info.CommandClass,
                ImagePath = RibbonButtonImageCache.GetOrCreate(info.ImageResourceName),
                Label = label
            };
        }

        private static RevitCommandId ResolveCommandId(string commandClass)
        {
            if (string.IsNullOrWhiteSpace(commandClass)) return null;
            try
            {
                return RevitCommandId.LookupCommandId(commandClass);
            }
            catch
            {
                return null;
            }
        }

        private static bool MatchesRegistry(
            ButtonRecentManager.RecentEntry entry,
            Dictionary<string, RibbonButtonInfo> byCommand,
            Dictionary<string, RibbonButtonInfo> byButtonId)
        {
            if (entry == null) return false;
            if (!string.IsNullOrWhiteSpace(entry.CommandClass) && byCommand.ContainsKey(entry.CommandClass)) return true;
            if (!string.IsNullOrWhiteSpace(entry.ButtonId) && byButtonId.ContainsKey(entry.ButtonId)) return true;
            return false;
        }

        private static RibbonButtonInfo ResolveInfo(
            ButtonRecentManager.RecentEntry entry,
            Dictionary<string, RibbonButtonInfo> byCommand,
            Dictionary<string, RibbonButtonInfo> byButtonId)
        {
            if (entry == null) return null;
            if (!string.IsNullOrWhiteSpace(entry.CommandClass)
                && byCommand.TryGetValue(entry.CommandClass, out var byCmd))
                return byCmd;
            if (!string.IsNullOrWhiteSpace(entry.ButtonId)
                && byButtonId.TryGetValue(entry.ButtonId, out var byId))
                return byId;
            return null;
        }
    }
}
