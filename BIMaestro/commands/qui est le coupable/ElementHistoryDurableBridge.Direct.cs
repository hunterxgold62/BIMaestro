using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Analyse
{
    internal static partial class ElementHistoryDurableBridge
    {
        private static string ReadyReason(HistoryRecipe recipe) => recipe?.UsesExistingModelDefinitions == true
            ? "État enregistré ; types nécessaires encore présents dans la maquette pour restaurer."
            : "Archive de restauration prête et vérifiée.";

        private static HistoryRecipe CompactDirectRecipe(HistoryRecipe recipe)
        {
            if (recipe == null) throw new InvalidDataException("Aucune recette directe enregistrée pour cet objet.");
            // Clone before removing obsolete backup receipts. The deletion DTO
            // and its last captured instance state must remain immutable.
            var copy = JObject.FromObject(recipe).ToObject<HistoryRecipe>();
            foreach (var node in Recipes(copy))
            {
                if (node == null || node.Kind == "native_archive" || node.Native != null)
                    throw new InvalidDataException("Cet objet ou un circuit dépendant nécessite une copie native ; sa reconstruction directe n’est pas prise en charge.");
                node.OpeningFallback = null;
                node.UsesExistingModelDefinitions = true;
            }
            return copy;
        }

        private static void MakeUnavailable(HistoryRestoreRequest request, string reason)
        {
            request.Recipe = null;
            request.CaptureFailure = "État non récupérable : " + reason;
        }

        private static void PublishPreparedAsync(List<Prepared> prepared)
        {
            Io = Task.Run(() =>
            {
                foreach (var item in prepared)
                    try { Publish(item); }
                    catch (Exception ex) { RecordFailure(EventId(item.Event), ex); }
            });
        }

        private static void ProcessDirectDefinitions(Application application, List<ElementHistoryEvent> jobs, ElementHistoryEvent first)
        {
            var prepared = new List<Prepared>();
            foreach (var ev in jobs)
            {
                if (prepared.Count >= 8) break;
                lock (Sync) if (Errors.ContainsKey(EventId(ev))) continue;
                var request = ToRequest(ev, false);
                if (request.Recipe?.Native != null || request.Recipe?.Kind == "native_archive") continue;
                try { request.Recipe = CompactDirectRecipe(request.Recipe); request.CaptureFailure = null; }
                catch (InvalidDataException ex) { MakeUnavailable(request, ex.Message); }
                prepared.Add(new Prepared { Event = ev, Request = request });
            }
            if (prepared.Count == 0) throw new InvalidDataException("Aucune recette directe disponible pour cette préparation.");
            // No Revit source, definition closure or native export is needed.
            // Purging required model types prevents restoration by contract.
            PublishPreparedAsync(prepared);
        }

        private static void PrepareCompactOpeningRequest(Document source, HistoryRestoreRequest request)
        {
            var saved = request.Recipe;
            var artifact = SourceArtifact(saved);
            if (artifact?.WholeModel != true || saved?.Native?.WholeModel != true)
                throw new InvalidDataException("Une source native imbriquée ne permet pas une reconstruction directe complète.");
            var element = source.GetElement(ElementIdExtensions.CreateElementId(artifact.SourceElementId));
            if (element == null) throw new InvalidDataException("Objet absent de la copie d’ouverture.");
            if ((!string.IsNullOrWhiteSpace(request.SourceUniqueId) && request.SourceUniqueId != element.UniqueId)
                || (!string.IsNullOrWhiteSpace(artifact.MemberSourceUniqueId) && artifact.MemberSourceUniqueId != element.UniqueId))
                throw new InvalidDataException("L’identité de l’objet ne correspond pas à la copie d’ouverture.");
            if (ElementHistoryExternalModels.IsExcluded(element))
                throw new InvalidDataException("Lien/import externe exclu de la récupération d’objets.");
            if (artifact.ExcludedElementIds?.Contains(element.Id.GetIdValue()) == true
                || artifact.SourceRootElementId > 0 && artifact.ExcludedElementIds?.Contains(artifact.SourceRootElementId) == true)
                throw new InvalidDataException("L’objet a changé après l’ouverture ; la copie ne prouve pas son dernier état.");
            string diagnostic = null;
            var direct = ElementHistoryReconstruction.CaptureDirectForRestore(element, text => diagnostic = text);
            if (direct == null)
                throw new InvalidDataException(diagnostic ?? "Catégorie non reconstructible directement avec les types encore présents dans la maquette.");
            // Prefer the lightweight state captured while the object was live.
            if (saved.HistoricalPlacement != null) direct.HistoricalPlacement = saved.HistoricalPlacement;
            if (saved.Parameters != null) direct.Parameters = saved.Parameters;
            if (saved.GeometryRelations?.Count > 0)
                direct.GeometryRelations = saved.GeometryRelations.Concat(direct.GeometryRelations ?? new List<HistoryGeometryRelation>())
                    .GroupBy(ElementHistoryRelations.Key).Select(group => group.First()).ToList();
            if (saved.Connections != null) direct.Connections = saved.Connections;
            request.Recipe = CompactDirectRecipe(direct);
            request.CaptureFailure = null;
            request.SourceUniqueId = element.UniqueId;
            request.Category = element.Category?.Name ?? request.Category;
            request.SuperComponentUniqueId = (element as FamilyInstance)?.SuperComponent?.UniqueId;
            request.FamilyTypeUniqueId = (element as FamilyInstance)?.Symbol?.UniqueId;
        }
    }
}
