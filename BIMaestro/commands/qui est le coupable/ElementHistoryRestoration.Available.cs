using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace Analyse
{
    internal static partial class ElementHistoryRestoration
    {
        // Prefer a complete native clipboard operation. If that atomic operation
        // rolls back, the existing coordinated path can retain successful objects
        // and reconnect available neighbours after dependency-ordered creation.
        internal static HistoryRestoreBatch RestoreAvailable(Document doc, IEnumerable<HistoryRestoreRequest> requests,
            Action refreshView = null)
            => ConnectRestoredByGeometry(doc, RestoreAvailableCore(doc, requests, refreshView));

        private static HistoryRestoreBatch RestoreAvailableCore(Document doc, IEnumerable<HistoryRestoreRequest> requests,
            Action refreshView)
        {
            var selected = (requests ?? Enumerable.Empty<HistoryRestoreRequest>()).Where(request => request != null).ToList();
            if (selected.All(request => request.Recipe?.UsesExistingModelDefinitions == true))
                return Restore(doc, selected, refreshView);
            var allNative = selected.All(request => request.Recipe?.UsesExistingModelDefinitions != true
                && request.Recipe?.Native != null && !request.Recipe.Native.DefinitionOnly);
            if (!allNative) return Restore(doc, selected, refreshView);
            try
            {
                return RestoreNativeCopiesCore(doc, selected, allowPartial: true);
            }
            catch (Exception ex) when (!AvailableRestorationHasRegenerationFailure(ex))
            {
                // RestoreNativeCopies has rolled back its transaction group before
                // throwing. Do not split a mixed set into unrelated restore calls.
                WriteAvailableRestorationDiagnostic(ex);
                if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") == "1"
                    && File.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "atomic-only-test.txt"))) throw;
                var batch = Restore(doc, selected, refreshView);
                batch.CaptureWarnings.Insert(0, "Le copier-coller natif du lot a été annulé : " + ex.Message
                    + " La restauration a repris objet par objet. Les connexions et systèmes signalés dans le bilan restent à vérifier.");
                return batch;
            }
        }

        private static void WriteAvailableRestorationDiagnostic(Exception failure)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1") return;
            try
            {
                var directory = CollaborativeModelTrackerStore.ActiveDirectory;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "native-available-restore-failure.txt"),
                    DateTime.UtcNow.ToString("O") + Environment.NewLine + failure);
            }
            catch { } // Diagnostics must never alter restoration outcomes.
        }

        private static bool AvailableRestorationHasRegenerationFailure(Exception error)
        {
            if (error is Autodesk.Revit.Exceptions.RegenerationFailedException) return true;
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(AvailableRestorationHasRegenerationFailure)) return true;
            return error.InnerException != null && AvailableRestorationHasRegenerationFailure(error.InnerException);
        }
    }
}
