using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        // One native clipboard operation for the whole set. Never recreate its
        // pipes, fittings, hosts, dimensions or geometry from a recipe.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document,HashSet<ElementId>> DocumentModelIds = new System.Runtime.CompilerServices.ConditionalWeakTable<Document,HashSet<ElementId>>();
        internal static bool IsDocumentModelElement(Document document,Element element)
            => element!=null && element.IsValidObject && DocumentModelIds.GetValue(document,doc=>
                new HashSet<ElementId>(new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds())).Contains(element.Id);

        private static readonly Guid NativeCopySchemaId = new Guid("C85D7BD8-A21A-48C9-9442-577369A583E9");
        private static Schema NativeCopySchema()
        {
            var schema = Schema.Lookup(NativeCopySchemaId);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(NativeCopySchemaId); builder.SetSchemaName("BIMaestroNativeCopyIdentity");
            builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("CopySourceUniqueId", typeof(string));
            builder.AddSimpleField("HistoricalUniqueId", typeof(string));
            return builder.Finish();
        }
        private static string ReadCopySourceId(Element element)
        {
            var schema = Schema.Lookup(NativeCopySchemaId); if (schema == null || element == null) return null;
            var entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(schema.GetField("CopySourceUniqueId")) : null;
        }
        internal static string NativeHistoricalIdentity(Element element)
        {
            var schema = Schema.Lookup(NativeCopySchemaId); if (schema == null || element == null) return null;
            var entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(schema.GetField("HistoricalUniqueId")) : null;
        }
        internal static Dictionary<string, Element> CopyNativeSet(Document source, IEnumerable<Element> selected, Document destination, bool sourceIsCapsule = false, bool includeConnectedContext = false)
        {
            var originals = selected.Where(e => e != null && !IsCalo(e) && !ElementHistoryExternalModels.IsExcluded(e))
                .GroupBy(e => e.UniqueId).Select(g => g.First()).ToList();
            if(originals.Any(e=>!IsDocumentModelElement(source,e)))
                throw new InvalidOperationException("Une dépendance interne ne peut pas être collée comme un objet autonome.");
            var transport = includeConnectedContext ? ExpandNativeMepContext(source,originals) : originals;
            var selectedIdentities = new HashSet<string>(transport.Select(e => e.UniqueId), StringComparer.Ordinal);
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST")=="1")
            {
                var persisted=new HashSet<ElementId>(new FilteredElementCollector(source).WhereElementIsNotElementType().ToElementIds());
                System.IO.File.WriteAllText(System.IO.Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-source-inputs.json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(originals.Select(e=>new { Id=e.Id.GetIdLongValue(),UID=e.UniqueId,Class=e.GetType().Name,
                        e.IsTransient,e.IsValidObject,Collected=persisted.Contains(e.Id),Document=e.Document.PathName,ExpectedDocument=source.PathName,
                        SourceObjectUid=source.GetElement(e.Id)?.UniqueId,SourceStringUid=source.GetElement(e.UniqueId)?.UniqueId }),Newtonsoft.Json.Formatting.Indented));
            }
            var roots = transport.Select(element => {
                var root = OpeningRoot(element);
                if (root.Id == element.Id || selectedIdentities.Contains(root.UniqueId)) return root;
                var retained = new[] { root.UniqueId }.Concat(ElementHistoryRestoration.GetOrigins(root) ?? new List<string>())
                    .Select(uid => ElementHistoryReconstruction.FindOriginal(destination, uid)).FirstOrDefault(e => e != null);
                if (retained == null) return root;
#if REVIT2024 || REVIT2025_OR_GREATER
                if (root is Toposolid terrain && element is Toposolid subdivision && subdivision.HostTopoId == terrain.Id)
                {
                    return element;
                }
#endif
                if (!(root is HostObject) || !(element is FamilyInstance))
                    throw new InvalidOperationException("Le parent natif existe déjà : ce sous-ensemble ne peut pas être collé indépendamment."
                        + " Objet=" + NativeCopyIdentity(element) + "; parent=" + NativeCopyIdentity(root)
                        + "; parent retenu=" + NativeCopyIdentity(retained));
                return element;
            }).GroupBy(e => e.UniqueId).Select(g => g.First()).ToList();
            if (roots.Count == 0) return new Dictionary<string, Element>(StringComparer.Ordinal);
            // A floor/wall clipboard root does not automatically include every
            // hosted family (RPCs in particular). Keep explicitly requested
            // independently selectable families in the same native clipboard set.
            var copyInputs = roots.Concat(transport.OfType<FamilyInstance>().Where(f => f.SuperComponent==null
                && f.GroupId==ElementId.InvalidElementId && !(f.Host is Autodesk.Revit.DB.Architecture.Stairs)
                && !(f.Host is Wall curtain && curtain.CurtainGrid!=null)))
                .GroupBy(e=>e.UniqueId).Select(g=>g.First()).ToList();
            foreach (var root in roots) RejectExternalModel(root, "native_set");
            if (!Owns(source) && Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1")
                throw new InvalidOperationException("La source doit être une copie historique isolée, jamais la maquette utilisateur.");
            // Copy provenance itself through Revit. Bounds cannot identify joined
            // walls or coincident instances reliably. This temporary metadata is
            // always rolled back in the isolated source; no source file is saved.
            using var provenance = new TransactionGroup(source, "BIMaestro - Identités temporaires de copie native");
            provenance.Start();
            try
            {
            using (var stamp = new Transaction(source, "BIMaestro - Marquer la copie historique isolée"))
            {
                stamp.Start();
                var sourceFailures = new Failures();
                stamp.SetFailureHandlingOptions(stamp.GetFailureHandlingOptions().SetFailuresPreprocessor(sourceFailures).SetClearAfterRollback(true));
                var members = copyInputs.SelectMany(e => Aggregate(source, e)).Concat(transport).GroupBy(e => e.Id).Select(g => g.First())
                    .Where(e=>IsDocumentModelElement(source,e))
                    .Where(e => !new HistoryOpeningClassification { SourceElementId=e.Id.GetIdValue(), SourceUniqueId=e.UniqueId,
                        Class=e.GetType().FullName, CategoryId=e.Category?.Id.GetIdLongValue()??0, ViewSpecific=e.ViewSpecific }.IsExcluded()).ToList();
                // Native wall geometry and joins stay unchanged during transport.
                if (sourceIsCapsule) PrepareNativeMepTypeReuse(source,destination);
                source.Regenerate();
                foreach (var original in members)
                {
                    if (!original.IsValidObject) throw new InvalidOperationException("Un objet source a disparu pendant la préparation du collage natif.");
                    var historical = sourceIsCapsule ? NativeHistoricalIdentity(original) : original.UniqueId;
                    if (string.IsNullOrWhiteSpace(historical)) historical = original.UniqueId;
                    var schema = NativeCopySchema(); var entity = new Entity(schema);
                    entity.Set(schema.GetField("CopySourceUniqueId"), original.UniqueId);
                    entity.Set(schema.GetField("HistoricalUniqueId"), historical);
                    original.SetEntity(entity);
                }
                if (stamp.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Identités de copie native non validées. " + sourceFailures.Message);
            }
            CaptureNativePlacementProbe(source, destination, null, originals.Select(element => new HistoryRestoreRequest { SourceUniqueId = element.UniqueId }), "after_source_provenance_regeneration");
            var externalBefore = SnapshotExternalModelIds(destination);
            var destinationBefore = new HashSet<ElementId>(new FilteredElementCollector(destination).WhereElementIsNotElementType().ToElementIds());
            ICollection<ElementId> copied;
            using (var options = Options())
                copied = ElementTransformUtils.CopyElements(source, copyInputs.Select(e => e.Id).ToList(), destination, Transform.Identity, options);
            destination.Regenerate();
            ValidateNoExternalModelsAdded(destination, externalBefore);
            // Revit's returned IDs and dependency traversal need not enumerate
            // every generated native owner. The copied identity is authoritative;
            // inspect all newly created tagged objects, never pre-existing ones.
            var candidates = new FilteredElementCollector(destination).WherePasses(new ExtensibleStorageFilter(NativeCopySchemaId))
                .Where(e => !destinationBefore.Contains(e.Id) && !(e is ElementType)).ToList();
            // A mixed native clipboard operation may omit a wall. Retry only
            // the missing native wall root as its own clipboard operation; MEP
            // networks remain grouped. Exact identity and graph checks still
            // reject omissions or duplicate dependencies before publication.
            foreach (var wall in roots.OfType<Wall>().Where(w => !candidates.Any(c => ReadCopySourceId(c)==w.UniqueId)))
            {
                using (var options = Options())
                    ElementTransformUtils.CopyElements(source,new[]{wall.Id},destination,Transform.Identity,options);
                destination.Regenerate();
                ValidateNoExternalModelsAdded(destination,externalBefore);
                candidates = new FilteredElementCollector(destination).WherePasses(new ExtensibleStorageFilter(NativeCopySchemaId))
                    .Where(e => !destinationBefore.Contains(e.Id) && !(e is ElementType)).ToList();
            }
            var mapping = new Dictionary<string, Element>(StringComparer.Ordinal);
            // Dependent-element graphs overlap (joined walls, hosts and their
            // children). Resolve all identities globally before visiting roots:
            // consuming an aggregate first can steal another root's candidate.
            foreach (var tagged in candidates.Where(c => !string.IsNullOrWhiteSpace(ReadCopySourceId(c)))
                .GroupBy(ReadCopySourceId, StringComparer.Ordinal))
            {
                var original = source.GetElement(tagged.Key);
                if (original == null || IsCalo(original)) continue;
                var matches = tagged.ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException("Copie native annulée : plusieurs objets portent l’identité " + tagged.Key + ".");
                mapping.Add(tagged.Key, matches[0]);
            }
            foreach (var root in roots)
            {
                if (!mapping.TryGetValue(root.UniqueId, out var copiedRoot))
                    throw new InvalidOperationException("Copie native annulée : identité ambiguë pour " + NativeCopyIdentity(root)
                        + "; candidates=" + candidates.Count + "; même classe=["
                        + string.Join("; ", candidates.Where(c => c.GetType() == root.GetType()).Take(20)
                            .Select(c => NativeCopyIdentity(c) + "/source=" + ReadCopySourceId(c))) + "].");
            }
            foreach (var element in originals)
                if (!mapping.ContainsKey(element.UniqueId)) throw new InvalidOperationException("Objet absent de la copie native : " + element.UniqueId);
            foreach (var pair in mapping)
                ElementHistoryRestoration.SetOrigins(pair.Value,
                    (ElementHistoryRestoration.GetOrigins(source.GetElement(pair.Key)) ?? new List<string>()).Concat(new[] { pair.Key }));
            RestoreNativeSetConnections(source, mapping, destination);
            VerifyNativeSetConnections(source, mapping, destination);
            return mapping;
            }
            finally
            {
                if (provenance.GetStatus() == TransactionStatus.Started && provenance.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Les métadonnées temporaires de la copie n’ont pas pu être annulées.");
            }
        }
        internal static HistoryRecipe ReadNativeMetadata(Element element, HistoryNativeArtifact artifact) => Recipe(element, artifact);

        // An attached slanted column can have a context-dependent GetTransform
        // frame while its native axis and section rotation are unchanged in a
        // partial capsule. Only that proven case defers the frame to restoration.
        // The original recipe, its raw frame and its bounds remain untouched.
        internal static bool VerifyNativeCapsulePlacement(Element original, Element copied, HistoryRecipe recipe, out string failure)
        {
            if (ElementHistoryReconstruction.VerifyHistoricalPlacement(copied, recipe, out failure, includeBoundingBox: false)) return true;
            if (!string.Equals(failure, "L’orientation historique de la famille n’a pas été rétablie.", StringComparison.Ordinal)
                || !(original is FamilyInstance sourceColumn) || !(copied is FamilyInstance copyColumn)
                || sourceColumn.StructuralType != Autodesk.Revit.DB.Structure.StructuralType.Column
                || copyColumn.StructuralType != Autodesk.Revit.DB.Structure.StructuralType.Column
                || !(sourceColumn.Location is LocationCurve) || !(copyColumn.Location is LocationCurve)
                || recipe?.HistoricalPlacement?.Curve == null) return false;

            bool attached = recipe.GeometryRelations?.Any(relation => relation.Kind == "column_attach"
                && relation.First == original.UniqueId && !string.IsNullOrWhiteSpace(relation.Second)) == true;
            if (!attached && Autodesk.Revit.DB.ColumnAttachment.IsValidColumn(sourceColumn))
                attached = Autodesk.Revit.DB.ColumnAttachment.GetColumnAttachment(sourceColumn, 0) != null
                    || Autodesk.Revit.DB.ColumnAttachment.GetColumnAttachment(sourceColumn, 1) != null;
            if (!attached) return false;

            var saved = recipe.HistoricalPlacement;
            var intrinsic = new HistoryRecipe { HistoricalPlacement = new HistoryPlacement {
                LevelSourceUniqueId = saved.LevelSourceUniqueId, HostSourceUniqueId = saved.HostSourceUniqueId,
                Point = saved.Point, Origin = saved.Origin, Curve = saved.Curve, BBox = saved.BBox,
                Mirrored = saved.Mirrored, HandFlipped = saved.HandFlipped, FacingFlipped = saved.FacingFlipped,
                ElevationParameters = saved.ElevationParameters
            } };
            if (!ElementHistoryReconstruction.VerifyHistoricalPlacement(copyColumn, intrinsic, out failure, includeBoundingBox: false)) return false;

            var sourceAngle = sourceColumn.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE);
            var copyAngle = copyColumn.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE);
            if (sourceAngle?.HasValue != true || copyAngle?.HasValue != true
                || sourceAngle.StorageType != StorageType.Double || copyAngle.StorageType != StorageType.Double)
            {
                failure = "L’angle natif de section du poteau ne peut pas être vérifié dans la capsule.";
                return false;
            }
            double expectedAngle = sourceAngle.AsDouble(), actualAngle = copyAngle.AsDouble();
            double angleDifference = Math.IEEERemainder(expectedAngle - actualAngle, 2 * Math.PI);
            if (double.IsNaN(expectedAngle) || double.IsInfinity(expectedAngle)
                || double.IsNaN(actualAngle) || double.IsInfinity(actualAngle)
                || double.IsNaN(angleDifference) || Math.Abs(angleDifference) > 1e-7)
            {
                failure = "L’angle natif de section du poteau a changé dans la capsule : attendu="
                    + expectedAngle.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "; copié="
                    + actualAngle.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ".";
                return false;
            }
            var sourceStyle = sourceColumn.get_Parameter(BuiltInParameter.SLANTED_COLUMN_TYPE_PARAM);
            var copyStyle = copyColumn.get_Parameter(BuiltInParameter.SLANTED_COLUMN_TYPE_PARAM);
            if (sourceStyle?.HasValue != true || copyStyle?.HasValue != true
                || sourceStyle.StorageType != StorageType.Integer || copyStyle.StorageType != StorageType.Integer
                || sourceStyle.AsInteger() != copyStyle.AsInteger())
            {
                failure = "Le mode natif de poteau incliné n’a pas été conservé dans la capsule.";
                return false;
            }
            failure = null;
            return true;
        }

        // Test-only, read-only snapshots for the two placement regressions. Never
        // enumerate geometry or all parameters, and never keep live API objects.
        internal static void CaptureNativePlacementProbe(Document source, Document capsule,
            Dictionary<string, Element> mapping, IEnumerable<HistoryRestoreRequest> requests, string stage)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1") return;
            try
            {
                object Parameters(Element element)
                {
                    if (element == null) return null;
                    var rows = new List<object>();
                    foreach (var name in new[] { "STRUCTURAL_BEND_DIR_ANGLE", "SLANTED_COLUMN_TYPE_PARAM",
                        "FAMILY_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM", "FAMILY_BASE_LEVEL_PARAM",
                        "FAMILY_TOP_LEVEL_PARAM", "FAMILY_BASE_LEVEL_OFFSET_PARAM", "FAMILY_TOP_LEVEL_OFFSET_PARAM",
                        "INSTANCE_ELEVATION_PARAM", "INSTANCE_FREE_HOST_OFFSET_PARAM", "COLUMN_BASE_LEVEL_PARAM",
                        "COLUMN_TOP_LEVEL_PARAM", "SCHEDULE_BASE_LEVEL_PARAM", "SCHEDULE_TOP_LEVEL_PARAM" })
                    {
                        if (!Enum.TryParse(name, out BuiltInParameter builtIn)) continue;
                        var parameter = element.get_Parameter(builtIn);
                        if (parameter == null) continue;
                        object value = null;
                        if (parameter.HasValue)
                        {
                            if (parameter.StorageType == StorageType.Double) value = parameter.AsDouble();
                            else if (parameter.StorageType == StorageType.Integer) value = parameter.AsInteger();
                            else if (parameter.StorageType == StorageType.String) value = parameter.AsString();
                            else if (parameter.StorageType == StorageType.ElementId) value = parameter.AsElementId().GetIdLongValue();
                        }
                        rows.Add(new { Name = name, Storage = parameter.StorageType.ToString(), parameter.HasValue, parameter.IsReadOnly, Value = value });
                    }
                    return rows;
                }
                object ColumnAttachments(FamilyInstance column)
                {
                    if (column == null || !Autodesk.Revit.DB.ColumnAttachment.IsValidColumn(column)) return null;
                    var rows = new List<object>();
                    for (int end = 0; end < 2; end++)
                    {
                        var attachment = Autodesk.Revit.DB.ColumnAttachment.GetColumnAttachment(column, end);
                        if (attachment == null) continue;
                        rows.Add(new { End = end, TargetId = attachment.TargetId.GetIdLongValue(),
                            TargetUID = column.Document.GetElement(attachment.TargetId)?.UniqueId,
                            Style = attachment.CutStyle.ToString(), Justification = attachment.Justification.ToString(),
                            Offset = attachment.AttachOffset });
                    }
                    return rows;
                }
                object GeometryJoins(Element element)
                {
                    try { return JoinGeometryUtils.GetJoinedElements(element.Document, element)
                        .Select(id => new { Id = id.GetIdLongValue(), UID = element.Document.GetElement(id)?.UniqueId }).ToList(); }
                    catch (Exception ex) { return new { Error = ex.Message }; }
                }
                object State(Element element)
                {
                    if (element == null || !element.IsValidObject) return null;
                    var recipe = new HistoryRecipe();
                    string placementError = null;
                    try { ElementHistoryReconstruction.CaptureHistoricalPlacement(element, recipe, includeBoundingBox: true); }
                    catch (Exception ex) { placementError = ex.ToString(); }
                    var family = element as FamilyInstance;
                    var type = element.Document.GetElement(element.GetTypeId());
                    return new { Id = element.Id.GetIdLongValue(), UID = element.UniqueId, Class = element.GetType().FullName,
                        Category = element.Category?.Name, TypeUID = type?.UniqueId, TypeName = type?.Name,
                        FamilyName = family?.Symbol?.FamilyName, GroupId = element.GroupId.GetIdLongValue(),
                        HostUID = family?.Host?.UniqueId, SuperComponentUID = family?.SuperComponent?.UniqueId,
                        Mirrored = family == null ? (bool?)null : family.Mirrored,
                        HandFlipped = family == null ? (bool?)null : family.HandFlipped,
                        FacingFlipped = family == null ? (bool?)null : family.FacingFlipped,
                        Placement = recipe.HistoricalPlacement, PlacementError = placementError,
                        Rotation = element.Location is LocationPoint point ? (double?)point.Rotation : null,
                        InstanceParameters = Parameters(element), TypeParameters = Parameters(type),
                        ColumnAttachments = ColumnAttachments(family), GeometryJoins = GeometryJoins(element) };
                }
                var rows = new List<object>();
                foreach (var request in requests ?? Enumerable.Empty<HistoryRestoreRequest>())
                {
                    var artifact = request?.Recipe?.Native ?? request?.Recipe?.OpeningFallback;
                    Element original = null;
                    var uid = request?.SourceUniqueId ?? artifact?.MemberSourceUniqueId;
                    if (!string.IsNullOrWhiteSpace(uid)) original = source.GetElement(uid);
                    if (original == null && artifact?.SourceElementId > 0)
                        original = source.GetElement(ElementIdExtensions.CreateElementId(artifact.SourceElementId));
                    if (original == null || original.Id.GetIdValue() != 1045747 && original.Id.GetIdValue() != 556953) continue;
                    Element copied = null;
                    mapping?.TryGetValue(original.UniqueId, out copied);
                    rows.Add(new { RequestSourceUID = request.SourceUniqueId, request.Label,
                        ExpectedPlacement = request.Recipe?.HistoricalPlacement, ExpectedRelations = request.Recipe?.GeometryRelations,
                        Source = State(original), Copy = State(copied) });
                }
                if (rows.Count == 0) return;
                var directory = CollaborativeModelTrackerStore.ActiveDirectory;
                System.IO.Directory.CreateDirectory(directory);
                var path = System.IO.Path.Combine(directory, "native-placement-stages.jsonl");
                System.IO.File.AppendAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(new { Stage = stage,
                    AtUtc = DateTime.UtcNow, Source = source?.PathName, Capsule = capsule?.PathName, Rows = rows }) + Environment.NewLine);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Native placement probe: " + ex); }
        }


        private static void RestoreNativeSetConnections(Document source, Dictionary<string,Element> mapping,Document destination)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);
            bool OccupancyMatches(Connector actual,Connector original)
            {
                var allowed=new HashSet<ElementId>();
                foreach(var reference in original.AllRefs.Cast<Connector>())
                    if(!(reference.Owner is MEPSystem) && reference.Owner.Id!=original.Owner.Id && original.IsConnectedTo(reference)
                        && mapping.TryGetValue(reference.Owner.UniqueId,out var mapped)) allowed.Add(mapped.Id);
                return actual.AllRefs.Cast<Connector>().Where(c=>!(c.Owner is MEPSystem)&&c.Owner.Id!=actual.Owner.Id&&actual.IsConnectedTo(c))
                    .All(c=>allowed.Contains(c.Owner.Id));
            }
            foreach(var pair in mapping)
            {
                var original=source.GetElement(pair.Key);
                foreach(var port in ElementHistoryNetwork.Ports(original))
                foreach(var peer in port.AllRefs.Cast<Connector>())
                {
                    if(peer.Owner is MEPSystem || peer.Owner.Id==original.Id || !port.IsConnectedTo(peer)
                        || !mapping.TryGetValue(peer.Owner.UniqueId,out var targetPeer)) continue;
                    var aId=original.UniqueId+":"+port.Id;var bId=peer.Owner.UniqueId+":"+peer.Id;
                    var key=string.CompareOrdinal(aId,bId)<0?aId+"|"+bId:bId+"|"+aId;
                    if(!seen.Add(key))continue;
                    var a=MatchNativePort(destination.GetElement(pair.Value.Id),port);
                    var b=MatchNativePort(destination.GetElement(targetPeer.Id),peer);
                    if(a==null||b==null)
                    {
                        if(Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST")=="1")
                        {
                            object State(Element element) => new { Identity=NativeCopyIdentity(element),Type=element.Document.GetElement(element.GetTypeId())?.Name,
                                Family=element is FamilyInstance fi ? new { Host=fi.Host?.UniqueId, fi.FacingFlipped,fi.HandFlipped,fi.Mirrored,WorkPlaneFlipped=fi.CanFlipWorkPlane ? (bool?)fi.IsWorkPlaneFlipped : null,fi.CanFlipWorkPlane,
                                    TransformOrigin=new[]{fi.GetTransform().Origin.X,fi.GetTransform().Origin.Y,fi.GetTransform().Origin.Z},
                                    BasisX=new[]{fi.GetTransform().BasisX.X,fi.GetTransform().BasisX.Y,fi.GetTransform().BasisX.Z},
                                    BasisY=new[]{fi.GetTransform().BasisY.X,fi.GetTransform().BasisY.Y,fi.GetTransform().BasisY.Z},
                                    BasisZ=new[]{fi.GetTransform().BasisZ.X,fi.GetTransform().BasisZ.Y,fi.GetTransform().BasisZ.Z} } : null,
                                Ports=ElementHistoryNetwork.Ports(element).Select(c=>new {c.Id,Kind=c.ConnectorType.ToString(),Domain=c.Domain.ToString(),Shape=c.Shape.ToString(),
                                    Point=new[]{c.Origin.X,c.Origin.Y,c.Origin.Z}}).ToList(),Parameters=ElementHistoryReconstruction.CaptureParameters(element) };
                            System.IO.File.WriteAllText(System.IO.Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-port-mismatch.json"),
                                Newtonsoft.Json.JsonConvert.SerializeObject(new {SourceA=State(original),TargetA=State(pair.Value),SourceB=State(peer.Owner),TargetB=State(targetPeer)},Newtonsoft.Json.Formatting.Indented));
                        }
                        throw new InvalidOperationException("Connecteur natif absent après collage : "+original.Id+" / "+peer.Owner.Id);
                    }
                    if(a.IsConnectedTo(b))continue;
                    bool SizeMatches(Connector copy,Connector before) => before.Shape==ConnectorProfileType.Round ? Math.Abs(copy.Radius-before.Radius)<1e-8
                        : before.Shape==ConnectorProfileType.Rectangular||before.Shape==ConnectorProfileType.Oval
                            ? Math.Abs(copy.Width-before.Width)<1e-8&&Math.Abs(copy.Height-before.Height)<1e-8 : true;
                    if(a.Origin.DistanceTo(b.Origin)>1e-5 || !SizeMatches(a,port)||!SizeMatches(b,peer)
                        || !OccupancyMatches(a,port)||!OccupancyMatches(b,peer))
                        throw new InvalidOperationException("La liaison native ne peut pas être conservée exactement : "+original.Id+" / "+peer.Owner.Id);
                    var guard=ElementHistoryNetwork.CaptureConnectionGuard(destination,a.Owner,b.Owner);
                    a.ConnectTo(b);destination.Regenerate();
                    ElementHistoryNetwork.VerifyConnectionGuard(destination,guard);
                    if(!a.IsConnectedTo(b))throw new InvalidOperationException("Liaison native refusée : "+original.Id+" / "+peer.Owner.Id);
                }
            }
        }

        internal static int VerifyNativeSetConnections(Document source, Dictionary<string, Element> mapping, Document destination)
        {
            int edges = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping)
            {
                var original = source.GetElement(pair.Key);
                var actual = destination.GetElement(pair.Value.UniqueId);
                if (original == null || actual == null) throw new InvalidOperationException("Objet supprimé pendant la validation de la copie native.");
                foreach (var port in ElementHistoryNetwork.Ports(original))
                foreach (var peer in port.AllRefs.Cast<Connector>())
                {
                    if (peer.Owner.Id == original.Id || peer.Owner is MEPSystem || !port.IsConnectedTo(peer)
                        || !mapping.TryGetValue(peer.Owner.UniqueId, out var copiedPeer)) continue;
                    var aKey = original.UniqueId + ":" + port.Id; var bKey = peer.Owner.UniqueId + ":" + peer.Id;
                    var key = string.CompareOrdinal(aKey, bKey) < 0 ? aKey + "|" + bKey : bKey + "|" + aKey;
                    if (!seen.Add(key)) continue;
                    var a = MatchNativePort(actual, port); var b = MatchNativePort(destination.GetElement(copiedPeer.UniqueId), peer);
                    if (a == null || b == null || !a.IsConnectedTo(b))
                        throw new InvalidOperationException("Copie native annulée : connexion physique non conservée entre " + original.Id + " et " + peer.Owner.Id + ".");
                    var sourceType = ReadMepSystemType(original);
                    var targetType = ReadMepSystemType(actual);
                    if (sourceType is MEPSystemType expected && targetType is MEPSystemType got
                        && expected.SystemClassification != got.SystemClassification)
                        throw new InvalidOperationException("Copie native annulée : classification du système modifiée.");
                    edges++;
                }
            }
            return edges;
        }

        private static Connector MatchNativePort(Element owner, Connector original)
        {
            if (owner == null) return null;
            var matches = ElementHistoryNetwork.Ports(owner).Where(c => c.Domain == original.Domain
                && c.Shape == original.Shape && c.Origin.DistanceTo(original.Origin) < 1e-5).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
        internal static bool NativeGeometryMatches(Element source, Element actual) => Same(source, actual);
    }
}
