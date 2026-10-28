using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Interop;

namespace Analyse
{
    internal static class ElementHistoryMaintenance
    {
        // Keep a single emergency switch, with history enabled by default.
        internal static bool IsActive => false;
    }

    internal static class ElementHistoryCompatibility
    {
        private static readonly Lazy<string> LoadedApiIssue = new Lazy<string>(() =>
        {
            var target = Assembly.GetExecutingAssembly().GetReferencedAssemblies()
                .FirstOrDefault(reference => string.Equals(reference.Name, "RevitAPI", StringComparison.Ordinal));
            var runtime = typeof(Document).Assembly.GetName();
            return GetApiIssue(target?.Version, runtime.Version);
        });

        internal static string GetLoadedApiIssue() => LoadedApiIssue.Value;

        internal static string GetApiIssue(Version target, Version runtime)
        {
            if (target == null || runtime == null)
                return "Impossible de vérifier la compatibilité de la version de BIMaestro chargée. La préparation est bloquée pour éviter des résultats incorrects. Fermez puis relancez Revit et utilisez la version de BIMaestro installée pour cette version de Revit.";
            if (target.Major == runtime.Major) return null;
            return $"La version de BIMaestro actuellement chargée cible Revit {2000 + target.Major}, alors que cette instance utilise Revit {2000 + runtime.Major}. "
                + "La préparation est bloquée pour éviter des résultats incorrects.\n\n"
                + "Fermez puis relancez Revit et utilisez la version de BIMaestro installée pour cette version de Revit avant d’utiliser « Qui a fait ça ? ».";
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MainCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MainCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var compatibilityIssue = ElementHistoryCompatibility.GetLoadedApiIssue();
            if (compatibilityIssue != null)
            {
                TaskDialog.Show("Qui a fait ça ? — Version incompatible", compatibilityIssue);
                return Result.Cancelled;
            }
            if (ElementHistoryMaintenance.IsActive)
            {
                TaskDialog.Show("Qui a fait ça", "En maintenance");
                return Result.Succeeded;
            }
            var uidoc = data.Application?.ActiveUIDocument;
            if (uidoc?.Document == null) return Result.Cancelled;

            var selectedId = uidoc.Selection.GetElementIds().FirstOrDefault();
            Element selected = null;
            if (selectedId != null && selectedId != ElementId.InvalidElementId)
                selected = uidoc.Document.GetElement(selectedId);

            var defaultAction = selected == null ? "delete" : null;
            List<ElementHistoryEvent> initialEvents = null;
            try
            {
                ElementHistoryTracker.ScheduleDeferredPrime(uidoc.Document);
                ElementHistoryTracker.FlushPendingForHistory();
                initialEvents = ElementHistoryWindow.LoadInitialHistory(uidoc.Document, selected, out defaultAction);
            }
            catch
            {
            }

            var win = new ElementHistoryWindow(uidoc, selected, initialEvents, defaultAction);
            BIMaestro.Tutorials.DemoTourService.AttachIfRequested("history", win);
            new WindowInteropHelper(win) { Owner = data.Application.MainWindowHandle };
            win.Show();

            return Result.Succeeded;
        }
    }
}
