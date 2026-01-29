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
        private static RadialCommandInvokeHandler s_commandHandler;
        private static ExternalEvent s_commandEvent;

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = data.Application;
                AppUI.SetUiApplication(uiapp);
                EnsureCommandHandler();

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
                    if (!accepted || item == null) return;

                    // Très important : fermer la fenêtre avant d'exécuter une commande Revit
                    try { win.Close(); } catch { }

                    EnsureCommandHandler();

                    // Toujours passer par ExternalEvent (voie fiable)
                    s_commandHandler.Prepare(item.CommandClass, data);
                    s_commandEvent.Raise();
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
                ButtonId = info.Id,
                CommandClass = info.CommandClass,
                ImagePath = RibbonButtonImageCache.GetOrCreate(info.ImageResourceName),
                Label = label
            };
        }

      

        private static void EnsureCommandHandler()
        {
            if (s_commandHandler != null && s_commandEvent != null) return;
            s_commandHandler = new RadialCommandInvokeHandler();
            s_commandEvent = ExternalEvent.Create(s_commandHandler);
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

        private sealed class RadialCommandInvokeHandler : IExternalEventHandler
        {
            private string _commandClass;
            private ExternalCommandData _data;

            public void Prepare(string commandClass, ExternalCommandData data)
            {
                _commandClass = commandClass;
                _data = data;
            }
            private static Type FindTypeAnywhere(string fullName)
            {
                if (string.IsNullOrWhiteSpace(fullName)) return null;

                // 1) Essai direct (marche si assembly-qualified)
                var t = Type.GetType(fullName, false, true);
                if (t != null) return t;

                // 2) Scan de tous les assemblies chargés
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        t = asm.GetType(fullName, false, true);
                        if (t != null) return t;
                    }
                    catch { }
                }

                return null;
            }

            public void Execute(UIApplication app)
            {
                if (string.IsNullOrWhiteSpace(_commandClass) || _data == null) return;

                var commandType = FindTypeAnywhere(_commandClass);
                if (commandType == null) return;
                if (!typeof(IExternalCommand).IsAssignableFrom(commandType)) return;

                try
                {
                    var cmd = (IExternalCommand)Activator.CreateInstance(commandType);
                    string msg = "";
                    var set = new ElementSet();
                    cmd.Execute(_data, ref msg, set);

                   
                }
                catch (Exception ex)
                {
                    
                }
            }


            public string GetName()
            {
                return "RadialMenuButtonsCommandInvoker";
            }
        }
    }
}