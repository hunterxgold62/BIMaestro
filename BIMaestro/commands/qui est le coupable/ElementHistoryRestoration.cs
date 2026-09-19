using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal sealed class HistoryRestoreRequest
    {
        public string SourceUniqueId { get; set; }
        public string Label { get; set; }
        public HistoryRecipe Recipe { get; set; }
    }

    internal sealed class HistoryRestoreItem
    {
        public string Label { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
        public string UniqueId { get; set; }
        public bool Created { get; set; }
        public bool Existing { get; set; }
    }

    internal sealed class HistoryRestoreBatch
    {
        public List<HistoryRestoreItem> Items { get; } = new List<HistoryRestoreItem>();
        public int Created => Items.Count(i => i.Created);
        public int Existing => Items.Count(i => i.Existing);
        public int Failed => Items.Count(i => !i.Created && !i.Existing);
        public int ConnectionsRestored { get; set; }
        public int ConnectionsExisting { get; set; }
        public List<string> ConnectionFailures { get; } = new List<string>();
    }

    internal static class ElementHistoryRestoration
    {
        // Saved on each native element. Survives project save/reopen and follows Undo.
        private static readonly Guid OriginSchemaId = new Guid("4b0d2453-93dd-4b44-9280-d2758acb4a84");
        private static Schema OriginSchema()
        {
            var schema = Schema.Lookup(OriginSchemaId);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(OriginSchemaId);
            builder.SetSchemaName("BIMaestroHistoryRestorationV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField("OriginalUniqueIds", typeof(string));
            return builder.Finish();
        }

        internal static List<string> GetOrigins(Element element)
        {
            var schema = Schema.Lookup(OriginSchemaId);
            if (schema == null) return null;
            var entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<IList<string>>(schema.GetField("OriginalUniqueIds")).ToList() : null;
        }

        private static Dictionary<string, string> ReadIndex(Document doc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Schema.Lookup(OriginSchemaId) == null) return result;
            foreach (var element in new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(OriginSchemaId)))
                foreach (var origin in GetOrigins(element) ?? new List<string>())
                    result[origin] = element.UniqueId;
            return result;
        }

        private static string Resolve(Document doc, Dictionary<string, string> index, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (ElementHistoryReconstruction.FindOriginal(doc, id) != null) return id;
            return index.TryGetValue(id, out var restored)
                && ElementHistoryReconstruction.FindOriginal(doc, restored) != null ? restored : null;
        }

        internal static HistoryRestoreBatch Restore(Document doc, IEnumerable<HistoryRestoreRequest> requests)
        {
            if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable)
                throw new InvalidOperationException("Restoration requires an editable project outside a transaction.");
            var batch = new HistoryRestoreBatch();
            var index = ReadIndex(doc);
            var selected = (requests ?? Enumerable.Empty<HistoryRestoreRequest>()).Where(r => r != null)
                .GroupBy(r => r.SourceUniqueId ?? r.Label ?? string.Empty).Select(g => g.First())
                .OrderBy(r => r.Recipe?.Kind == "family" ? 1 : 0).ToList();
            using (var group = new TransactionGroup(doc, "BIMaestro - Restaurer éléments supprimés"))
            {
                group.Start();
                foreach (var request in selected)
                {
                    var result = new HistoryRestoreItem { Label = request.Label };
                    batch.Items.Add(result);
                    if (string.IsNullOrEmpty(request.SourceUniqueId)) { result.Reason = "identity"; continue; }
                    var existing = Resolve(doc, index, request.SourceUniqueId);
                    if (existing != null)
                    {
                        result.Existing = true; result.UniqueId = existing; continue;
                    }
                    var recipe = ElementHistoryReconstruction.ReadRecipe(request.Recipe);
                    if (recipe == null) { result.Reason = "recipe"; continue; }
                    // Clone before remapping dependencies. The historical payload is immutable.
                    recipe = JObject.FromObject(recipe).ToObject<HistoryRecipe>();
                    if (ElementHistoryReconstruction.FindOriginal(doc, recipe.Type) == null) { result.Reason = "type"; continue; }
                    if (!(ElementHistoryReconstruction.FindOriginal(doc, recipe.Level) is Level)) { result.Reason = "level"; continue; }
                    if (!string.IsNullOrEmpty(recipe.Host))
                    {
                        recipe.Host = Resolve(doc, index, recipe.Host);
                        if (recipe.Host == null) { result.Reason = "host"; continue; }
                    }
                    foreach (var parameter in recipe.Parameters ?? new List<HistoryParameter>())
                        if (!string.IsNullOrEmpty(parameter.Reference))
                            parameter.Reference = Resolve(doc, index, parameter.Reference) ?? parameter.Reference;

                    using (var transaction = new Transaction(doc, "BIMaestro - Restaurer élément supprimé"))
                    {
                        transaction.Start();
                        var failures = new RestoreFailures();
                        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                        try
                        {
                            var element = ElementHistoryReconstruction.RestoreNative(doc, recipe);
                            var origins = (recipe.RestorationOrigins ?? new List<string>())
                                .Concat(new[] { request.SourceUniqueId }).Distinct().ToList();
                            var schema = OriginSchema();
                            var entity = new Entity(schema);
                            entity.Set<IList<string>>(schema.GetField("OriginalUniqueIds"), origins);
                            element.SetEntity(entity);
                            string uid = element.UniqueId;
                            if (transaction.Commit() == TransactionStatus.Committed)
                            {
                                // Revit can auto-correct a commit by removing the created
                                // object. Never count a transient object as restored.
                                if (ElementHistoryReconstruction.FindOriginal(doc, uid) == null)
                                {
                                    result.Reason = "creation";
                                    result.Detail = "Revit a supprimé l’élément lors de la validation. " + failures.Message;
                                }
                                else
                                {
                                    result.Created = true; result.UniqueId = uid; result.Detail = failures.Message;
                                    foreach (var origin in origins) index[origin] = uid;
                                }
                            }
                            else
                            {
                                result.Reason = "creation"; result.Detail = failures.Message;
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (Exception ex)
                        {
                            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                            result.Reason = "creation"; result.Detail = ex.Message;
                        }
                    }
                }
                Reconnect(doc, selected, index, batch);
                if (batch.Created > 0 || batch.ConnectionsRestored > 0)
                {
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Restoration could not be committed.");
                }
                else group.RollBack();
            }
            return batch;
        }

        private static void Reconnect(Document doc, List<HistoryRestoreRequest> selected,
            Dictionary<string, string> index, HistoryRestoreBatch batch)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in selected)
            {
                var uid = Resolve(doc, index, request.SourceUniqueId);
                if (uid == null) continue;
                foreach (var link in request.Recipe?.Connections ?? new List<HistoryConnection>())
                {
                    // Both ends save the same edge. Include ports to retain multiple links between owners.
                    string aKey = request.SourceUniqueId + ":" + Newtonsoft.Json.JsonConvert.SerializeObject(link.Port?.Point);
                    string bKey = link.Peer + ":" + Newtonsoft.Json.JsonConvert.SerializeObject(link.PeerPort?.Point);
                    string key = string.CompareOrdinal(aKey, bKey) < 0 ? aKey + "|" + bKey : bKey + "|" + aKey;
                    if (!visited.Add(key)) continue;
                    string label = request.Label + " → " + link.Peer;
                    var peerUid = Resolve(doc, index, link.Peer);
                    if (peerUid == null) { batch.ConnectionFailures.Add(label + " : élément voisin absent."); continue; }
                    using (var connectionGroup = new TransactionGroup(doc, "BIMaestro - Valider connexion historique"))
                    {
                        connectionGroup.Start();
                        using (var tx = new Transaction(doc, "BIMaestro - Rétablir connexion historique"))
                        {
                            tx.Start();
                            var failures = new RestoreFailures();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                            try
                            {
                                var a = ElementHistoryNetwork.Match(doc.GetElement(uid), link.Port);
                                var b = ElementHistoryNetwork.Match(doc.GetElement(peerUid), link.PeerPort);
                                if (a == null || b == null) throw new InvalidOperationException("Connecteur déplacé ou introuvable.");
                                if (a.IsConnectedTo(b)) { tx.RollBack(); batch.ConnectionsExisting++; continue; }
                                if (a.IsConnected || b.IsConnected) throw new InvalidOperationException("Connecteur déjà utilisé par une autre connexion.");
                                if (a.Origin.DistanceTo(b.Origin) > 1e-4) throw new InvalidOperationException("Les connecteurs ne coïncident plus.");
                                a.ConnectTo(b);
                                doc.Regenerate();
                                // A direct historical edge must not silently become an extra fitting.
                                a = ElementHistoryNetwork.Match(doc.GetElement(uid), link.Port);
                                b = ElementHistoryNetwork.Match(doc.GetElement(peerUid), link.PeerPort);
                                if (a == null || b == null || !a.IsConnectedTo(b)) throw new InvalidOperationException("Connexion directe non rétablie.");
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(failures.Message ?? "Connexion refusée par Revit.");
                                a = ElementHistoryNetwork.Match(ElementHistoryReconstruction.FindOriginal(doc, uid), link.Port);
                                b = ElementHistoryNetwork.Match(ElementHistoryReconstruction.FindOriginal(doc, peerUid), link.PeerPort);
                                if (a == null || b == null || !a.IsConnectedTo(b))
                                    throw new InvalidOperationException("Connexion supprimée par Revit lors de la validation.");
                                if (connectionGroup.Assimilate() != TransactionStatus.Committed)
                                    throw new InvalidOperationException("Connexion non validée.");
                                batch.ConnectionsRestored++;
                            }
                            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                            catch (Exception ex)
                            {
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                                batch.ConnectionFailures.Add(label + " : " + ex.Message);
                            }
                        }
                    }
                }
            }
        }

        private sealed class RestoreFailures : IFailuresPreprocessor
        {
            public string Message { get; private set; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages = accessor.GetFailureMessages();
                var errors = messages.Where(m => m.GetSeverity() != FailureSeverity.Warning).ToList();
                if (errors.Count > 0)
                {
                    Message = string.Join("\n", errors.Select(m => m.GetDescriptionText()));
                    return FailureProcessingResult.ProceedWithRollBack;
                }
                // Return transaction warnings to the caller instead of blocking the
                // batch with a modal; errors always roll back the affected operation.
                Message = string.Join("\n", messages.Select(m => m.GetDescriptionText()));
                foreach (var warning in messages) accessor.DeleteWarning(warning);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
