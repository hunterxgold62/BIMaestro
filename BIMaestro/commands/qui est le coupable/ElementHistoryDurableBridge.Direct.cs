using Autodesk.Revit.ApplicationServices;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Analyse
{
    internal static partial class ElementHistoryDurableBridge
    {
        private static bool DirectProof(ElementHistoryEvent ev, out string session, out long revision)
        {
            object sessionValue = null, revisionValue = null;
            ev.Delta?.TryGetValue("definitionSession", out sessionValue);
            ev.Delta?.TryGetValue("definitionRevision", out revisionValue);
            session = Convert.ToString(sessionValue, CultureInfo.InvariantCulture);
            revision = -1;
            return !string.IsNullOrEmpty(session) && long.TryParse(Convert.ToString(revisionValue, CultureInfo.InvariantCulture),
                NumberStyles.None, CultureInfo.InvariantCulture, out revision);
        }
        private static void ProcessDirectDefinitions(Application application, List<ElementHistoryEvent> jobs, ElementHistoryEvent first)
        {
            if (!DirectProof(first, out var session, out var revision)
                || !ElementHistoryDefinitionRevisions.TrySource(session, revision, out var source))
                throw new InvalidDataException("Les définitions de cet état ne sont pas encore figées et leur document source n’est plus disponible.");
            var wave = new List<Prepared>();
            foreach (var ev in jobs)
            {
                if (wave.Count >= 8) break;
                lock (Sync) if (Errors.ContainsKey(EventId(ev))) continue;
                if (!DirectProof(ev, out var otherSession, out var otherRevision) || session != otherSession || revision != otherRevision) continue;
                var request = ToRequest(ev, false);
                if (request.Recipe == null || SourceArtifact(request.Recipe) != null) continue;
                request.Recipe = JObject.FromObject(request.Recipe).ToObject<HistoryRecipe>();
                wave.Add(new Prepared { Event = ev, Request = request });
            }
            if (wave.Count == 0) throw new InvalidDataException("Aucune recette directe complète n’est disponible pour cet état.");
            var directory = Path.Combine(Path.GetTempPath(), "BIMaestro", "history-export");
            Directory.CreateDirectory(directory);
            var scratch = Path.Combine(directory, "snapshot-definitions-" + Guid.NewGuid().ToString("N") + ".rvt");
            var failures = new Dictionary<string, string>(StringComparer.Ordinal);
            var recipes = ElementHistoryNativeArchive.ExportDurableDefinitions(source, wave.Select(item => item.Request), scratch,
                ElementHistoryDefinitionRevisions.Version(session, revision),
                uid => ElementHistoryDefinitionRevisions.Unchanged(session, revision, uid), failures);
            Io = Task.Run(() =>
            {
                try
                {
                    var payload = recipes.Count > 0 && File.Exists(scratch) ? ElementHistoryDurableStore.PublishNative(scratch) : null;
                    foreach (var item in wave)
                    {
                        try
                        {
                            if (!recipes.TryGetValue(item.Request.SourceUniqueId, out var recipe))
                            { failures.TryGetValue(item.Request.SourceUniqueId, out var reason); throw new InvalidDataException(reason ?? "Les définitions nécessaires à cet élément n’ont pas pu être figées."); }
                            BindPayload(recipe, payload);
                            item.Request.Recipe = recipe; item.Request.CaptureFailure = null;
                            Publish(item);
                        }
                        catch (Exception ex) { RecordFailure(EventId(item.Event), ex); }
                    }
                }
                catch (Exception ex) { foreach (var item in wave) RecordFailure(EventId(item.Event), ex); }
                finally
                {
                    try { if (File.Exists(scratch)) File.Delete(scratch); } catch { }
                    try { var backup = Path.ChangeExtension(scratch, ".0001.rvt"); if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
            });
        }
    }
}
