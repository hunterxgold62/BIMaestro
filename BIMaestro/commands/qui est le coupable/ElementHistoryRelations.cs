using Autodesk.Revit.DB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Analyse
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryGeometryRelation
    {
        public string Kind { get; set; }
        public string First { get; set; }
        public string Second { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int End { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Style { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Justification { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double Offset { get; set; }
        public int? JoinType { get; set; }
        public int? SecondEnd { get; set; }
        public int? JoinOrder { get; set; }
        public int? JoinParticipantCount { get; set; }
        public int? SecondJoinType { get; set; }
        public List<string> JoinParticipants { get; set; }
        public List<string> SecondJoinParticipants { get; set; }
        public double? Volume { get; set; }
    }

    internal static class ElementHistoryRelations
    {
        // Wall attachment APIs appeared in Revit 2025.2. Resolve them at runtime so
        // the same history payload and add-in remain loadable on earlier versions.
        private static readonly MethodInfo WallGet = typeof(Wall).GetMethods().FirstOrDefault(m => m.Name == "GetAttachmentIds" && m.GetParameters().Length == 1);
        private static readonly MethodInfo WallAdd = typeof(Wall).GetMethods().FirstOrDefault(m => m.Name == "AddAttachment" && m.GetParameters().Length == 2);
        private static readonly Type TopoType = typeof(Element).Assembly.GetType("Autodesk.Revit.DB.Toposolid");
        private static readonly MethodInfo TopoIntersections = TopoType?.GetMethod("GetIntersectingElementData", Type.EmptyTypes);
        private static readonly MethodInfo TopoExcavate = TopoType?.GetMethod("ExcavateBy", new[] { typeof(ElementId) });
        private sealed class ExcavationIndex
        {
            internal readonly List<HistoryGeometryRelation> Relations = new List<HistoryGeometryRelation>();
        }
        private static readonly ConditionalWeakTable<Document, ExcavationIndex> Excavations = new ConditionalWeakTable<Document, ExcavationIndex>();
        private sealed class AttachmentIndex
        {
            internal readonly Dictionary<ElementId,List<HistoryGeometryRelation>> Owners = new Dictionary<ElementId,List<HistoryGeometryRelation>>();
            internal readonly Dictionary<string,List<HistoryGeometryRelation>> Targets = new Dictionary<string,List<HistoryGeometryRelation>>();
            internal readonly Dictionary<ElementId,string> TargetIds = new Dictionary<ElementId,string>();

            internal void Update(Document doc, ElementId id)
            {
                if (Owners.TryGetValue(id,out var previous))
                {
                    foreach (var relation in previous)
                        if (Targets.TryGetValue(relation.Second,out var peers))
                        { peers.Remove(relation); if (peers.Count == 0) Targets.Remove(relation.Second); }
                    Owners.Remove(id);
                }
                var element = doc.GetElement(id);
                if (!(element is Wall) && !(element is FamilyInstance column && ColumnAttachment.IsValidColumn(column))) return;
                var current = OwnerAttachments(element).Where(r => r.Second != null).ToList();
                Owners[id] = current;
                foreach (var relation in current)
                {
                    if (!Targets.TryGetValue(relation.Second,out var peers)) Targets[relation.Second] = peers = new List<HistoryGeometryRelation>();
                    peers.Add(relation);
                    var target = doc.GetElement(relation.Second);
                    if (target != null) TargetIds[target.Id] = relation.Second;
                }
            }
        }
        private static readonly ConditionalWeakTable<Document, AttachmentIndex> Attachments = new ConditionalWeakTable<Document, AttachmentIndex>();
        internal static void Invalidate(Document doc) { Attachments.Remove(doc); Excavations.Remove(doc); }

        internal static void Invalidate(Document doc, IEnumerable<ElementId> changedIds)
        {
            Excavations.Remove(doc);
            if (!Attachments.TryGetValue(doc,out var index)) return;
            try
            {
                var ids = new HashSet<ElementId>(changedIds);
                // A removed target can detach a surviving owner even when Revit
                // does not include that owner in its modified-element list.
                foreach (var id in ids.ToList())
                    if (doc.GetElement(id) == null && index.TargetIds.TryGetValue(id,out var uid))
                    {
                        if (index.Targets.TryGetValue(uid,out var peers))
                            foreach (var relation in peers.ToList())
                            { var owner = doc.GetElement(relation.First); if (owner != null) ids.Add(owner.Id); }
                        index.TargetIds.Remove(id);
                    }
                foreach (var id in ids) index.Update(doc,id);
            }
            catch { Attachments.Remove(doc); }
        }

        private static object WallLocation(int end) => Enum.Parse(WallGet.GetParameters()[0].ParameterType, end == 0 ? "Base" : "Top");
        private static IEnumerable<ElementId> WallTargets(Wall wall, int end) => WallGet == null
            ? Enumerable.Empty<ElementId>() : (IEnumerable<ElementId>)WallGet.Invoke(wall, new[] { WallLocation(end) });

        private static IEnumerable<HistoryGeometryRelation> OwnerAttachments(Element owner)
        {
            for (var end = 0; end < 2; end++)
            {
                if (owner is Wall wall)
                    foreach (var target in WallTargets(wall, end))
                        yield return new HistoryGeometryRelation { Kind = "wall_attach", First = owner.UniqueId, Second = owner.Document.GetElement(target)?.UniqueId, End = end };
                if (owner is FamilyInstance column && ColumnAttachment.IsValidColumn(column))
                {
                    var attachment = ColumnAttachment.GetColumnAttachment(column, end);
                    if (attachment != null) yield return new HistoryGeometryRelation { Kind = "column_attach", First = owner.UniqueId,
                        Second = owner.Document.GetElement(attachment.TargetId)?.UniqueId, End = end, Style = (int)attachment.CutStyle,
                        Justification = (int)attachment.Justification, Offset = attachment.AttachOffset };
                }
            }
        }

        private static AttachmentIndex BuildIndex(Document doc)
        {
            var result = new AttachmentIndex();
            var owners = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Element>()
                .Concat(new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new[] {
                    BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns })).WhereElementIsNotElementType());
            foreach (var owner in owners) result.Update(doc,owner.Id);
            return result;
        }

        private static IEnumerable<HistoryGeometryRelation> TopoExcavations(Element topo)
        {
            if (TopoIntersections == null || !TopoType.IsInstanceOfType(topo)) yield break;
            var data = TopoIntersections.Invoke(topo, null) as System.Collections.IEnumerable;
            if (data == null) yield break;
            foreach (var entry in data)
            {
                HistoryGeometryRelation relation = null;
                try
                {
                    var type = entry.GetType();
                    if (type.GetProperty("IntersectionType")?.GetValue(entry)?.ToString() != "Excavate") continue;
                    var intersecting = type.GetProperty("IntersectingElementId")?.GetValue(entry) as ElementId;
                    var intersected = type.GetProperty("IntersectedElementId")?.GetValue(entry) as ElementId;
                    var cutterId = intersecting == topo.Id ? intersected : intersecting;
                    var cutter = cutterId == null ? null : topo.Document.GetElement(cutterId);
                    if (cutter == null) continue;
                    var volume = type.GetProperty("IntersectionVolume")?.GetValue(entry);
                    relation = new HistoryGeometryRelation { Kind = "topo_excavate", First = topo.UniqueId,
                        Second = cutter.UniqueId, Volume = volume is double number ? number : (double?)null };
                }
                finally { (entry as IDisposable)?.Dispose(); }
                if (relation != null) yield return relation;
            }
        }

        private static ExcavationIndex BuildExcavationIndex(Document doc)
        {
            var result = new ExcavationIndex();
            if (TopoIntersections == null) return result;
            foreach (var topo in new FilteredElementCollector(doc).OfClass(TopoType).WhereElementIsNotElementType())
                result.Relations.AddRange(TopoExcavations(topo));
            return result;
        }

        internal static List<HistoryGeometryRelation> CaptureExcavations(Element element, List<string> warnings)
        {
            if (element == null || TopoIntersections == null || !(element is HostObject)) return new List<HistoryGeometryRelation>();
            try
            {
                return Excavations.GetValue(element.Document, BuildExcavationIndex).Relations
                    .Where(r => r.First == element.UniqueId || r.Second == element.UniqueId).ToList();
            }
            catch (Exception ex)
            {
                warnings?.Add("Excavation du terrain : " + (ex.InnerException?.Message ?? ex.Message));
                return new List<HistoryGeometryRelation>();
            }
        }

        internal static List<HistoryGeometryRelation> Capture(Element element, List<string> warnings)
        {
            var result = new List<HistoryGeometryRelation>();
            if (element is ElementType || element.ViewSpecific || element.Category?.CategoryType != CategoryType.Model
                || element is MEPSystem) return result;
            void Read(string label, Action action)
            { try { action(); } catch (Exception ex) { warnings.Add(label + " : " + (ex.InnerException?.Message ?? ex.Message)); } }
            Read("Jonctions", () => {
                foreach (var id in JoinGeometryUtils.GetJoinedElements(element.Document, element))
                {
                    var peer = element.Document.GetElement(id);
                    var cutting = JoinGeometryUtils.IsCuttingElementInJoin(element.Document, element, peer);
                    result.Add(new HistoryGeometryRelation { Kind = "join", First = cutting ? element.UniqueId : peer.UniqueId, Second = cutting ? peer.UniqueId : element.UniqueId });
                }
            });
            Read("Découpes par vide", () => {
                if (InstanceVoidCutUtils.CanBeCutWithVoid(element))
                    foreach (var id in InstanceVoidCutUtils.GetCuttingVoidInstances(element))
                        result.Add(new HistoryGeometryRelation { Kind = "void_cut", First = element.Document.GetElement(id)?.UniqueId, Second = element.UniqueId });
                if (InstanceVoidCutUtils.IsVoidInstanceCuttingElement(element))
                    foreach (var id in InstanceVoidCutUtils.GetElementsBeingCut(element))
                        result.Add(new HistoryGeometryRelation { Kind = "void_cut", First = element.UniqueId, Second = element.Document.GetElement(id)?.UniqueId });
            });
            Read("Découpes entre solides", () => {
                if (!SolidSolidCutUtils.IsAllowedForSolidCut(element)) return;
                foreach (var id in SolidSolidCutUtils.GetCuttingSolids(element))
                {
                    var peer = element.Document.GetElement(id);
                    if (SolidSolidCutUtils.CutExistsBetweenElements(peer, element, out var peerCuts) && peerCuts)
                        result.Add(new HistoryGeometryRelation { Kind = "solid_cut", First = peer.UniqueId, Second = element.UniqueId });
                }
                foreach (var id in SolidSolidCutUtils.GetSolidsBeingCut(element))
                {
                    var peer = element.Document.GetElement(id);
                    if (SolidSolidCutUtils.CutExistsBetweenElements(element, peer, out var cuts) && cuts)
                        result.Add(new HistoryGeometryRelation { Kind = "solid_cut", First = element.UniqueId, Second = peer.UniqueId });
                }
            });
            Read("Attaches", () => {
                result.AddRange(OwnerAttachments(element));
                if (element is HostObject || element is Wall || (element is FamilyInstance && ColumnAttachment.IsValidTarget(false,element)) || (WallGet != null &&
                    element.GetType().Name == "Toposolid"))
                {
                    var index = Attachments.GetValue(element.Document, BuildIndex);
                    if (index.Targets.TryGetValue(element.UniqueId,out var incoming)) result.AddRange(incoming);
                }
                if (element is Wall attachedWall && WallGet == null &&
                    new[] { BuiltInParameter.WALL_TOP_IS_ATTACHED, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED }
                        .Any(p => attachedWall.get_Parameter(p)?.AsInteger() == 1))
                    warnings.Add("Attache de mur : l’API de cette version de Revit n’expose pas la cible (API disponible depuis Revit 2025.2).");
            });
            result.AddRange(CaptureExcavations(element, warnings));
            if (element is Wall endWall)
                Read("Extrémités de mur", () => {
                    var location = (LocationCurve)endWall.Location;
                    for (var end = 0; end < 2; end++)
                    {
                        result.Add(new HistoryGeometryRelation { Kind = "wall_end", First = element.UniqueId, End = end,
                            Style = WallUtils.IsWallJoinAllowedAtEnd(endWall, end) ? 1 : 0,
                            JoinType = (int)location.get_JoinType(end) });
                        var participants = location.get_ElementsAtJoin(end).Cast<Element>().ToList();
                        for (var order = 0; order < participants.Count; order++)
                        {
                            if (!(participants[order] is Wall peer) || peer.Id == endWall.Id) continue;
                            result.Add(new HistoryGeometryRelation
                            {
                                Kind = "wall_auto_join", First = endWall.UniqueId,
                                Second = peer.UniqueId, End = end, JoinType = (int)location.get_JoinType(end),
                                JoinOrder = order, JoinParticipantCount = participants.Count,
                                JoinParticipants = participants.Select(p => p.UniqueId).ToList(),
                                SecondEnd = FindReciprocalWallEnd(peer, endWall.Id)
                            });
                            var relation = result[result.Count - 1];
                            if (relation.SecondEnd.HasValue)
                            {
                                relation.SecondJoinType = (int)((LocationCurve)peer.Location).get_JoinType(relation.SecondEnd.Value);
                                relation.SecondJoinParticipants = ((LocationCurve)peer.Location)
                                    .get_ElementsAtJoin(relation.SecondEnd.Value).Cast<Element>().Select(p => p.UniqueId).ToList();
                            }
                        }
                    }
                });
            return result.Where(r => r.First != null && (r.Second != null || r.Kind == "wall_end"))
                .Where(r => r.Kind != "join" || !result.Any(c => c.Kind == "solid_cut"
                    && (c.First == r.First && c.Second == r.Second || c.First == r.Second && c.Second == r.First)))
                // Column attachment owns its automatic solid cut. Replaying that
                // derived cut separately can be rejected when the column is trimmed.
                .Where(r => r.Kind != "solid_cut" || !result.Any(a => a.Kind == "column_attach"
                    && (a.First == r.First && a.Second == r.Second || a.First == r.Second && a.Second == r.First)))
                .GroupBy(Key).Select(g => g.First()).ToList();
        }

        internal static string Key(HistoryGeometryRelation r) => r.Kind + ":" + r.First + ":" + r.Second + ":" + r.End;

        private static int? FindReciprocalWallEnd(Wall wall, ElementId peerId)
        {
            if (!(wall.Location is LocationCurve location)) return null;
            for (var end = 0; end < 2; end++)
                try
                {
                    if (location.get_ElementsAtJoin(end).Cast<Element>().Any(e => e.Id == peerId)) return end;
                }
                catch { }
            return null;
        }

        internal static void Restore(Document doc, IEnumerable<HistoryRestoreRequest> requests, Dictionary<string, string> index, HistoryRestoreBatch batch)
        {
            var changed = new HashSet<string>(batch.Items.Where(i => i.Created || i.IncludedInParent).Select(i => i.SourceUniqueId));
            var generated = new HashSet<string>(batch.Items.Where(i => i.Created && i.UniqueId != null)
                .SelectMany(i => ElementHistoryNativeArchive.Aggregate(doc, doc.GetElement(i.UniqueId))).Select(e => e.UniqueId));
            foreach (var pair in index.Where(p => generated.Contains(p.Value))) changed.Add(pair.Key);
            var relations = requests.SelectMany(r => r.Recipe?.GeometryRelations ?? new List<HistoryGeometryRelation>())
                .Where(r => changed.Contains(r.First) || changed.Contains(r.Second)).GroupBy(Key).Select(g => g.First())
                .OrderBy(r => r.Kind == "topo_excavate" ? 10 : r.Kind.EndsWith("attach") ? 0 : r.Kind == "join" ? 1
                    : r.Kind == "wall_end" ? 3 : r.Kind == "wall_auto_join" ? 4 : 2);
            foreach (var relation in relations)
            {
                var label = relation.Kind + " [" + relation.First + " → " + relation.Second + "]";
                var relationAudit = new HistoryRelationAudit
                {
                    Kind = relation.Kind,
                    End = relation.End,
                    SecondEnd = relation.SecondEnd,
                    JoinOrder = relation.JoinOrder,
                    FirstSourceUniqueId = relation.First,
                    SecondSourceUniqueId = relation.Second
                };
                try
                {
                    var first = Resolve(doc, index, relation.First);
                    var second = Resolve(doc, index, relation.Second);
                    relationAudit.FirstResolvedUniqueId = first?.UniqueId;
                    relationAudit.SecondResolvedUniqueId = second?.UniqueId;
                    if (first == null || (second == null && relation.Kind != "wall_end"))
                        throw new InvalidOperationException("Un élément de référence n’est pas présent dans la maquette.");
                    if (IsCalo(first) || (second != null && IsCalo(second)))
                    {
                        relationAudit.Outcome = "excluded_insulation";
                        continue;
                    }
                    // Most native copies and default wall ends already match. Avoid
                    // thousands of empty transactions on large restoration batches.
                    if (Matches(doc, relation, first, second))
                    {
                        batch.RelationsExisting++;
                        relationAudit.Outcome = "already_present";
                        continue;
                    }
                    using (var relationGroup = new TransactionGroup(doc, "BIMaestro - Vérifier relation géométrique"))
                    using (var tx = new Transaction(doc, "BIMaestro - Rétablir relation géométrique"))
                    {
                        relationGroup.Start();
                        tx.Start();
                        var failures = new Failures();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                        var existing = Apply(doc, relation, first, second);
                        doc.Regenerate();
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit a refusé la relation. " + failures.Message);
                        Verify(doc, relation, first, second);
                        if (relationGroup.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("La relation historique n’a pas pu être validée.");
                        if (existing) batch.RelationsExisting++; else batch.RelationsRestored++;
                        relationAudit.Outcome = existing ? "already_present" : "restored";
                    }
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex)
                {
                    relationAudit.Outcome = "failed";
                    relationAudit.Detail = ex.InnerException?.Message ?? ex.Message;
                    batch.RelationFailures.Add(label + " : " + relationAudit.Detail);
                }
                finally
                {
                    if (relation.Kind == "wall_auto_join")
                        try
                        {
                            var actualWall = Resolve(doc, index, relation.First) as Wall;
                            var actualPeer = Resolve(doc, index, relation.Second) as Wall;
                            if (actualWall != null && actualPeer != null)
                            {
                                var participants = ((LocationCurve)actualWall.Location)
                                    .get_ElementsAtJoin(relation.End).Cast<Element>().ToList();
                                var position = participants.FindIndex(e => e.Id == actualPeer.Id);
                                relationAudit.ActualParticipantCount = participants.Count;
                                relationAudit.ActualJoinOrder = position >= 0 ? (int?)position : null;
                                if (position >= 0 && relation.JoinOrder.HasValue && relation.JoinParticipantCount == participants.Count)
                                    relationAudit.JoinOrderMatches = position == relation.JoinOrder.Value;
                                if (relationAudit.JoinOrderMatches == false && string.IsNullOrWhiteSpace(relationAudit.Detail))
                                    relationAudit.Detail = "La jonction est présente, mais l’ordre des murs diffère de l’original.";
                            }
                        }
                        catch { }
                    batch.RelationAudit.Add(relationAudit);
                }
            }
        }

        private static Element Resolve(Document doc, Dictionary<string, string> index, string uid)
        {
            if (uid == null) return null;
            var element = ElementHistoryReconstruction.FindOriginal(doc, uid);
            if (element != null) return element;
            return index.TryGetValue(uid, out var actual) ? ElementHistoryReconstruction.FindOriginal(doc, actual) : null;
        }

        private static bool IsCalo(Element e) => ElementHistoryRestoration.IsCalorifuge(e.Category?.Name,
            e is FamilyInstance f ? f.Symbol?.Family?.Name : e.Name);

        private static bool Matches(Document doc, HistoryGeometryRelation r, Element first, Element second)
        {
            if (r.Kind == "topo_excavate") return TopoExcavations(first).Any(actual => actual.Second == second.UniqueId
                && (!r.Volume.HasValue || !actual.Volume.HasValue
                    || Math.Abs(actual.Volume.Value - r.Volume.Value) <= Math.Max(1e-4, Math.Abs(r.Volume.Value) * 1e-5)));
            if (r.Kind == "join") return JoinGeometryUtils.AreElementsJoined(doc, first, second)
                && JoinGeometryUtils.IsCuttingElementInJoin(doc, first, second);
            if (r.Kind == "void_cut") return InstanceVoidCutUtils.InstanceVoidCutExists(second, first);
            if (r.Kind == "solid_cut") return SolidSolidCutUtils.CutExistsBetweenElements(first, second, out var cuts) && cuts;
            if (r.Kind == "wall_attach") return WallTargets((Wall)first, r.End).Contains(second.Id);
            if (r.Kind == "column_attach")
            {
                var attachment = ColumnAttachment.GetColumnAttachment((FamilyInstance)first, r.End);
                return attachment?.TargetId == second.Id && (int)attachment.CutStyle == r.Style
                    && (int)attachment.Justification == r.Justification && Math.Abs(attachment.AttachOffset-r.Offset)<1e-7;
            }
            if (r.Kind == "wall_end") return WallUtils.IsWallJoinAllowedAtEnd((Wall)first, r.End) == (r.Style == 1)
                && (r.Style != 1 || !r.JoinType.HasValue || (int)((LocationCurve)first.Location).get_JoinType(r.End) == r.JoinType.Value);
            if (r.Kind == "wall_auto_join")
            {
                if (!(first is Wall wall) || !(second is Wall peer)) return false;
                var location = (LocationCurve)wall.Location;
                if (!location.get_ElementsAtJoin(r.End).Cast<Element>().Any(e => e.Id == peer.Id)
                    || (r.JoinType.HasValue && (int)location.get_JoinType(r.End) != r.JoinType.Value)
                    || !WallJoinOrderMatches(location, r.End, peer, r.JoinParticipants, r.JoinOrder, r.JoinParticipantCount)) return false;
                if (r.SecondEnd.HasValue)
                {
                    var peerLocation = (LocationCurve)peer.Location;
                    if (r.SecondJoinType.HasValue && (int)peerLocation.get_JoinType(r.SecondEnd.Value) != r.SecondJoinType.Value) return false;
                    if (r.SecondJoinParticipants != null && !WallJoinOrderMatches(peerLocation,
                        r.SecondEnd.Value, wall, r.SecondJoinParticipants, null, null)) return false;
                }
                return true;
            }
            return false;
        }

        private static void Verify(Document doc, HistoryGeometryRelation r, Element first, Element second)
        { if (!Matches(doc,r,first,second)) throw new InvalidOperationException("Revit n’a pas conservé la relation après validation."); }

        private static bool Apply(Document doc, HistoryGeometryRelation r, Element first, Element second)
        {
            if (r.Kind == "topo_excavate")
            {
                if (TopoExcavate == null || !TopoType.IsInstanceOfType(first))
                    throw new InvalidOperationException("Excavation : API Revit 2025 ou ultérieure nécessaire.");
                if (Matches(doc, r, first, second)) return true;
                TopoExcavate.Invoke(first, new object[] { second.Id });
                return false;
            }
            if (r.Kind == "join")
            {
                var existing = JoinGeometryUtils.AreElementsJoined(doc, first, second);
                if (!existing) JoinGeometryUtils.JoinGeometry(doc, first, second);
                if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, first, second)) { JoinGeometryUtils.SwitchJoinOrder(doc, first, second); return false; }
                return existing;
            }
            if (r.Kind == "void_cut")
            {
                if (InstanceVoidCutUtils.InstanceVoidCutExists(second, first)) return true;
                InstanceVoidCutUtils.AddInstanceVoidCut(doc, second, first); return false;
            }
            if (r.Kind == "solid_cut")
            {
                bool firstCuts;
                if (SolidSolidCutUtils.CutExistsBetweenElements(first, second, out firstCuts))
                {
                    if (!firstCuts) throw new InvalidOperationException("Une découpe inverse existe déjà.");
                    return true;
                }
                SolidSolidCutUtils.AddCutBetweenSolids(doc, second, first); return false;
            }
            if (r.Kind == "wall_end")
            {
                var wall = (Wall)first;
                var existing = WallUtils.IsWallJoinAllowedAtEnd(wall, r.End) == (r.Style == 1);
                if (!existing) { if (r.Style == 1) WallUtils.AllowWallJoinAtEnd(wall, r.End); else WallUtils.DisallowWallJoinAtEnd(wall, r.End); }
                var curve = (LocationCurve)wall.Location;
                if (r.Style == 1 && r.JoinType.HasValue && (int)curve.get_JoinType(r.End) != r.JoinType.Value)
                { curve.set_JoinType(r.End, (JoinType)r.JoinType.Value); existing = false; }
                return existing;
            }
            if (r.Kind == "wall_auto_join")
            {
                var wall = (Wall)first;
                var peer = (Wall)second;
                if (Matches(doc, r, wall, peer)) return true;
                // Revit may not recompute an automatic join when both walls were
                // created with their ends temporarily disabled. Toggle only the
                // recorded ends after both original walls exist again.
                var connected = ((LocationCurve)wall.Location).get_ElementsAtJoin(r.End).Cast<Element>().Any(e => e.Id == peer.Id);
                if (!connected)
                {
                    WallUtils.DisallowWallJoinAtEnd(wall, r.End);
                    WallUtils.AllowWallJoinAtEnd(wall, r.End);
                    if (r.SecondEnd.HasValue)
                    {
                        WallUtils.DisallowWallJoinAtEnd(peer, r.SecondEnd.Value);
                        WallUtils.AllowWallJoinAtEnd(peer, r.SecondEnd.Value);
                    }
                    doc.Regenerate();
                }
                RestoreWallJoinType(wall, r.End, r.JoinType);
                if (r.SecondEnd.HasValue) RestoreWallJoinType(peer, r.SecondEnd.Value, r.SecondJoinType);
                doc.Regenerate();
                RestoreWallJoinOrder((LocationCurve)wall.Location, r.End, peer,
                    r.JoinParticipants, r.JoinOrder, r.JoinParticipantCount);
                if (r.SecondEnd.HasValue && r.SecondJoinParticipants != null)
                    RestoreWallJoinOrder((LocationCurve)peer.Location, r.SecondEnd.Value, wall,
                        r.SecondJoinParticipants, null, null);
                return false;
            }
            if (r.Kind == "wall_attach")
            {
                if (WallGet == null || WallAdd == null) throw new InvalidOperationException("Attaches de murs : API Revit 2025.2 ou ultérieure nécessaire.");
                var targets = WallTargets((Wall)first, r.End).ToList();
                if (targets.Contains(second.Id)) return true;
                if (targets.Count > 0) throw new InvalidOperationException("Le mur possède déjà une autre attache à cette extrémité.");
                WallAdd.Invoke(first, new[] { (object)second.Id, WallLocation(r.End) }); return false;
            }
            if (r.Kind == "column_attach")
            {
                var column = (FamilyInstance)first;
                var existing = ColumnAttachment.GetColumnAttachment(column, r.End);
                if (existing != null)
                {
                    if (existing.TargetId != second.Id || (int)existing.CutStyle != r.Style || (int)existing.Justification != r.Justification || Math.Abs(existing.AttachOffset-r.Offset)>1e-7)
                        throw new InvalidOperationException("Le poteau possède déjà une attache différente.");
                    return true;
                }
                ColumnAttachment.AddColumnAttachment(doc, column, second, r.End, (ColumnAttachmentCutStyle)r.Style, (ColumnAttachmentJustification)r.Justification, r.Offset);
                if (ColumnAttachment.GetColumnAttachment(column,r.End)?.TargetId != second.Id) throw new InvalidOperationException("L’attache du poteau n’a pas été créée.");
                return false;
            }
            throw new InvalidOperationException("Relation géométrique inconnue.");
        }

        private static void RestoreWallJoinType(Wall wall, int end, int? joinType)
        {
            if (!joinType.HasValue) return;
            var location = (LocationCurve)wall.Location;
            if ((int)location.get_JoinType(end) != joinType.Value)
                location.set_JoinType(end, (JoinType)joinType.Value);
        }

        // This function only permutes the supplied participants. It cannot add a
        // missing wall or silently remove a new participant from a live junction.
        internal static List<string> WallJoinPermutation(IList<string> current, IList<string> historical,
            string peer, int? legacyOrder, int? legacyCount)
        {
            if (current == null || current.Any(string.IsNullOrWhiteSpace)
                || current.Distinct(StringComparer.Ordinal).Count() != current.Count)
                throw new InvalidOperationException("Participants actuels de la jonction absents ou ambigus.");
            if (historical != null)
            {
                if (historical.Any(string.IsNullOrWhiteSpace) || historical.Count != current.Count
                    || historical.Distinct(StringComparer.Ordinal).Count() != historical.Count
                    || !new HashSet<string>(current, StringComparer.Ordinal).SetEquals(historical))
                    throw new InvalidOperationException("Les participants de la jonction diffèrent de l’historique ; aucune permutation n’est appliquée.");
                return historical.ToList();
            }
            if (!legacyOrder.HasValue || legacyCount != current.Count || legacyOrder.Value < 0 || legacyOrder.Value >= current.Count)
                throw new InvalidOperationException("L’ordre historique de jonction est absent ou son nombre de participants a changé.");
            var result = current.ToList();
            if (!result.Remove(peer)) throw new InvalidOperationException("Le mur voisin historique n’est pas dans la jonction.");
            result.Insert(legacyOrder.Value, peer);
            return result;
        }

        private static List<string> ResolveJoinParticipants(List<Element> current, List<string> historical)
        {
            if (historical == null) return null;
            var result = new List<string>();
            foreach (var source in historical)
            {
                var matches = current.Where(element => element.UniqueId == source
                    || (ElementHistoryRestoration.GetOrigins(element) ?? new List<string>()).Contains(source)).ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException("Un participant historique de la jonction est absent ou son identité est ambiguë.");
                result.Add(matches[0].UniqueId);
            }
            return result;
        }

        private static bool WallJoinOrderMatches(LocationCurve location, int end, Wall peer,
            List<string> historical, int? legacyOrder, int? legacyCount)
        {
            var current = location.get_ElementsAtJoin(end).Cast<Element>().ToList();
            try
            {
                var expected = WallJoinPermutation(current.Select(e => e.UniqueId).ToList(),
                    ResolveJoinParticipants(current, historical), peer.UniqueId, legacyOrder, legacyCount);
                return current.Select(e => e.UniqueId).SequenceEqual(expected, StringComparer.Ordinal);
            }
            catch (InvalidOperationException) { return false; }
        }

        private static void RestoreWallJoinOrder(LocationCurve location, int end, Wall peer,
            List<string> historical, int? legacyOrder, int? legacyCount)
        {
            var current = location.get_ElementsAtJoin(end).Cast<Element>().ToList();
            var expected = WallJoinPermutation(current.Select(e => e.UniqueId).ToList(),
                ResolveJoinParticipants(current, historical), peer.UniqueId, legacyOrder, legacyCount);
            if (current.Select(e => e.UniqueId).SequenceEqual(expected, StringComparer.Ordinal)) return;
            var byUid = current.ToDictionary(e => e.UniqueId, StringComparer.Ordinal);
            var ordered = new ElementArray();
            foreach (var uid in expected) ordered.Append(byUid[uid]);
            location.set_ElementsAtJoin(end, ordered);
        }

        private sealed class Failures : IFailuresPreprocessor
        {
            internal string Message;
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var errors = accessor.GetFailureMessages().Where(f => f.GetSeverity() != FailureSeverity.Warning).ToList();
                Message = string.Join("; ", errors.Select(f => f.GetDescriptionText()));
                foreach (var warning in accessor.GetFailureMessages().Where(f => f.GetSeverity() == FailureSeverity.Warning)) accessor.DeleteWarning(warning);
                return errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
