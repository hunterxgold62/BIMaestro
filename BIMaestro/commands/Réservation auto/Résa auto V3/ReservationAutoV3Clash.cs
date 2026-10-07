using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace Modification
{
    public partial class ReservationAutoV3Command
    {
        private static readonly Guid ClashOpeningSchemaId = new Guid("76A93740-5859-4F46-A511-BE3F7D558D8C");

        // Callable only from a valid Revit API callback. Uses the same placement/sizing as Autoréservation.
        public static FamilyInstance CreateForClash(Document doc, Element source, Element host, RevitLinkInstance link = null,
            ReservationAutoV3Config config = null, ReservationAutoV3PersoConfig personal = null)
        {
            if (doc == null || doc.IsReadOnly || doc.IsModifiable)
                throw new InvalidOperationException("La maquette doit être modifiable et disponible pour créer une réservation.");
            if (source == null || source is ElementType || source.Category?.CategoryType != CategoryType.Model
                || !source.Document.Equals(doc) || !(host is Wall) && !(host is Floor)
                || host.Document.Equals(doc) && source.Id.Equals(host.Id))
                throw new InvalidOperationException("La réservation directe nécessite un objet de la maquette et un mur ou sol Revit distinct.");
            if (link == null ? !host.Document.Equals(doc) : !link.Document.Equals(doc) || !host.Document.Equals(link.GetLinkDocument()))
                throw new InvalidOperationException("Le mur ou le sol n'appartient plus à cette maquette ou à ce lien.");
            string key = source.UniqueId + "|" + (link?.UniqueId ?? "local") + "|" + host.UniqueId;
            var schema = Schema.Lookup(ClashOpeningSchemaId);
            if (schema != null && new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Any(f => { var entity = f.GetEntity(schema); return entity.IsValid() && entity.Get<string>(schema.GetField("Pair")) == key; }))
                throw new InvalidOperationException("Une réservation a déjà été créée pour cette traversée. Relancez l'analyse pour la vérifier.");

            config = config ?? ReservationAutoV3ConfigStore.LoadOrDefault();
            personal = personal ?? ReservationAutoV3PersoConfigStore.LoadOrDefault();
            personal.EnsureInitialized();
            bool wall = host is Wall;
            var shape = string.Equals(config.LastShapeTarget, "Circulaire", StringComparison.OrdinalIgnoreCase)
                ? ReservationAutoV3Window.ShapeTarget.Circulaire : ReservationAutoV3Window.ShapeTarget.Rectangulaire;
            var profile = ReservationAutoV3Window.ResolveClashProfile(doc, config, personal,
                wall ? ReservationAutoV3Window.HostTarget.Mur : ReservationAutoV3Window.HostTarget.Sol, shape);
            if (profile == null || !profile.IsConfigured || !TryResolveSymbol(doc, profile, out var symbol)
                || !string.IsNullOrWhiteSpace(profile.TypeName) && !string.Equals(symbol.Name, profile.TypeName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La famille ou le type configuré n'est pas chargé pour ce mur ou sol. Ouvrez Autoréservation pour le configurer.");
            bool unhosted = CanCreateReservationWithoutHost(symbol);
            if (link != null && !unhosted)
                throw new InvalidOperationException("Pour un mur ou sol lié, choisissez une famille sans hôte dans Autoréservation. La réservation sera créée dans la maquette active.");
            if (!unhosted && symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted)
                throw new InvalidOperationException("Cette famille utilise un hébergement non pris en charge pour la création directe. Choisissez une famille avec hôte ou sans hôte dans Autoréservation.");
            var transform = link?.GetTotalTransform() ?? Transform.Identity;
            // Bounding-box overlap alone is insufficient to create a model element.
            if (!TryGetElementHostIntersectionCenter(source, Transform.Identity, host, transform, out var center))
                throw new InvalidOperationException("La traversée physique n'est plus confirmée. Relancez l'analyse avant de créer une réservation.");
            var level = GetNearestLevel(doc, center.Z);
            if (level == null) throw new InvalidOperationException("Aucun niveau disponible pour placer la réservation.");
            var worker = new ReservationAutoV3Command();
            var before = new System.Collections.Generic.HashSet<string>(FindInstances(doc, symbol.Id).Select(f => f.UniqueId));
            using (var transaction = new Transaction(doc, "Clash 3D · créer la réservation"))
            {
                transaction.Start();
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                bool rect = shape == ReservationAutoV3Window.ShapeTarget.Rectangulaire;
                var objectType = source is Pipe ? ReservationAutoV3Window.ObjectType.Canalisation
                    : source is Duct ? ReservationAutoV3Window.ObjectType.Gaine : ReservationAutoV3Window.ObjectType.Autre;
                if (link == null && !unhosted)
                    worker.CreateReservation_Single(doc, host, level, symbol, objectType, source, Transform.Identity,
                        config, profile, wall, rect, config.DefaultNormeEnabled);
                else
                    worker.CreateReservation_SingleAgainstCandidate(doc, new WallScanCandidate
                    {
                        Element = host, IsLinked = link != null, ForceUnhosted = unhosted, IsFloorLike = !wall,
                        TransformToCurrentDocument = transform, BoundingBoxInCurrentDocument = GetBoundingBoxInHostCoordinates(host, transform),
                        AxisX = wall ? GetWallDirectionXY((Wall)host, transform) : XYZ.BasisX, DepthFt = GetHostDepth(host)
                    }, symbol, objectType, new MepSelection { Element = source, TransformToCurrentDocument = Transform.Identity },
                        config, profile, wall, rect, config.DefaultNormeEnabled);
                doc.Regenerate();
                var created = FindInstances(doc, symbol.Id).Where(f => !before.Contains(f.UniqueId)).ToList();
                if (created.Count != 1) throw new InvalidOperationException("Aucune réservation unique n'a été créée. La modification a été annulée.");
                var instance = created[0];
                if (!rect && objectType == ReservationAutoV3Window.ObjectType.Autre)
                {
                    var points = new System.Collections.Generic.List<XYZ>();
                    foreach (var a in GetElementSolidsInCurrentDocument(host, transform))
                    foreach (var b in GetElementSolidsInCurrentDocument(source, Transform.Identity))
                    {
                        try
                        {
                            var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
                            if (intersection?.Volume > 1e-9) CollectSolidPoints(intersection, points);
                        }
                        catch { /* Other physical solids can still yield a usable footprint. */ }
                    }
                    if (points.Count == 0) throw new InvalidOperationException("La forme circulaire ne peut pas être dimensionnée sur cette géométrie. Aucune réservation conservée.");
                    var axisX = wall ? GetWallDirectionXY((Wall)host, transform) : XYZ.BasisX;
                    var axisY = wall ? XYZ.BasisZ : XYZ.BasisY;
                    double diameter = 2 * points.Max(p => { var delta = p - center;
                        double x = delta.DotProduct(axisX), y = delta.DotProduct(axisY); return Math.Sqrt(x*x + y*y); });
                    if (config.DefaultNormeEnabled) diameter = RoundUpToNext50mm(diameter);
                    TrySet(instance, profile.ParamDiameter, diameter);
                }
                var parameters = rect ? (wall ? new[] { profile.ParamLength, profile.ParamHeight, profile.ParamDepth }
                    : new[] { profile.ParamLength, profile.ParamWidth, profile.ParamDepth }) : new[] { profile.ParamDiameter, profile.ParamDepth };
                if (parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Length)
                    throw new InvalidOperationException("Chaque dimension doit utiliser un paramètre distinct dans Autoréservation. Aucune réservation conservée.");
                foreach (string name in parameters)
                {
                    var p = string.IsNullOrWhiteSpace(name) ? null : instance.LookupParameter(name);
                    bool typeDepth = p == null && name == profile.ParamDepth && !string.IsNullOrWhiteSpace(name);
                    if (typeDepth) p = symbol.LookupParameter(name);
                    // A fixed/calculated depth is usable when it spans the host. Never modify the family type.
                    bool automaticHostDepth = name == profile.ParamDepth && p != null && p.StorageType == StorageType.Double
                        && p.AsDouble() >= GetHostDepth(host) - 1 / 304.8;
                    if (p == null || (p.IsReadOnly || typeDepth) && !automaticHostDepth || p.StorageType != StorageType.Double || p.Definition.GetDataType() != SpecTypeId.Length || p.AsDouble() <= 0)
                        throw new InvalidOperationException("Le paramètre « " + (name ?? "non configuré") + " » est absent, verrouillé ou non dimensionné. Corrigez le mapping dans Autoréservation. Aucune réservation conservée.");
                }
                if (link == null && unhosted) worker.ForceVoidCutSafe(doc, host, instance);
                if (schema == null)
                {
                    var builder = new SchemaBuilder(ClashOpeningSchemaId); builder.SetSchemaName("BIMaestroClashOpening");
                    builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
                    builder.AddSimpleField("Pair", typeof(string)); schema = builder.Finish();
                }
                var record = new Entity(schema); record.Set(schema.GetField("Pair"), key); instance.SetEntity(record);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit n'a pas validé la réservation.");
                return instance;
            }
        }
    }
}
