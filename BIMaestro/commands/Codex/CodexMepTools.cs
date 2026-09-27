using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BIMaestro.Codex
{
    // First MEP pilot: the drafter places equipment and supplies the intended connection
    // and waypoints. No P&ID interpretation or automatic network design is implied here.
    internal static class CodexMepTools
    {
        private const double FeetPerMm = 1.0 / 304.8;
        private const double PointTolerance = 1.0 * FeetPerMm;
        private const double DiameterTolerance = 1.0 * FeetPerMm;
        private const double ObstacleClearance = 25.0 * FeetPerMm;
        private static readonly BuiltInCategory[] CheckedCategories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows, BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit
        };

        internal static IEnumerable<JObject> Definitions()
        {
            yield return Definition("revit_mep_inspect",
                "Lit les connecteurs de tuyauterie des équipements sélectionnés et les types de tuyaux, systèmes et niveaux du projet. Lecture seule. Les identifiants de connecteurs doivent être repris tels quels pour un tracé.",
                new JObject(), new JArray());

            var endpoint = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["element_id"] = new JObject { ["type"] = "string" },
                    ["connector_id"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("element_id", "connector_id")
            };
            var waypoint = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["x"] = Number(), ["y"] = Number(), ["z"] = Number()
                },
                ["required"] = new JArray("x", "y", "z")
            };
            var properties = new JObject
            {
                ["start"] = endpoint.DeepClone(), ["end"] = endpoint.DeepClone(),
                ["waypoints_mm"] = new JObject { ["type"] = "array", ["maxItems"] = 16,
                    ["description"] = "Points intermédiaires en coordonnées internes Revit, en mm. Chaque segment entre points successifs doit être parallèle à X, Y ou Z. Utiliser [] pour une liaison droite.",
                    ["items"] = waypoint },
                ["pipe_type_id"] = new JObject { ["type"] = "string" },
                ["system_type_id"] = new JObject { ["type"] = "string" },
                ["level_id"] = new JObject { ["type"] = "string" }
            };
            var required = new JArray("start", "end", "waypoints_mm", "pipe_type_id", "system_type_id", "level_id");
            yield return Definition("revit_mep_preview_route",
                "Vérifie sans modifier le projet UN tracé entre deux connecteurs libres d'équipements placés. DN identique exigé, tronçons orthogonaux, sortie des connecteurs alignée, longueur minimale et contrôle conservateur par boîtes englobantes des obstacles du projet. Ne vérifie pas les raccords natifs ni les modèles liés : leur création est testée dans revit_mep_apply_route.",
                (JObject)properties.DeepClone(), (JArray)required.DeepClone());
            yield return Definition("revit_mep_apply_route",
                "Crée UN tracé prévisualisé entre deux connecteurs libres d'équipements placés : tuyaux Revit natifs, coudes, connexion aux équipements et vérification des raccordements. Refuse les obstacles détectés, puis annule toute la transaction si Revit refuse un segment ou un raccord. Ne choisit ni les équipements ni le DN à la place du dessinateur.",
                properties, required);
        }

        internal static object Inspect(Document doc, ICollection<ElementId> selectedIds)
        {
            RequireProject(doc);
            var equipment = new List<object>();
            var equipmentBounds = new List<XYZ>();
            var equipmentIds = new HashSet<ElementId>();
            foreach (var id in selectedIds.Take(30))
            {
                var instance = doc.GetElement(id) as FamilyInstance;
                var manager = instance?.MEPModel?.ConnectorManager;
                if (manager == null) continue;
                var connectors = manager.Connectors.Cast<Connector>()
                    .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
                    .OrderBy(c => c.Id).Select(c => new
                    {
                        connector_id = c.Id,
                        position_mm = Point(c.Origin),
                        outward_direction = Direction(c),
                        shape = c.Shape.ToString(),
                        pipe_system_type = c.PipeSystemType.ToString(),
                        diameter_mm = c.Shape == ConnectorProfileType.Round ? Math.Round(c.Radius * 2 / FeetPerMm, 3) : 0,
                        connected = c.IsConnected,
                        connected_to = c.AllRefs.Cast<Connector>()
                            .Where(r => r.Owner.Id != instance.Id)
                            .Select(r => new { element_id = r.Owner.Id.ToString(), connector_id = r.Id })
                            .Take(8).ToArray()
                    }).ToArray();
                if (connectors.Length == 0) continue;
                equipmentIds.Add(instance.Id);
                var equipmentBox = instance.get_BoundingBox(null);
                if (equipmentBox != null)
                {
                    equipmentBounds.Add(equipmentBox.Min);
                    equipmentBounds.Add(equipmentBox.Max);
                }
                else
                    equipmentBounds.AddRange(manager.Connectors.Cast<Connector>()
                        .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
                        .Select(c => c.Origin));
                equipment.Add(new
                {
                    element_id = instance.Id.ToString(), name = instance.Name,
                    category = instance.Category?.Name, family = instance.Symbol?.FamilyName,
                    connectors
                });
            }
            var nearbyObstacles = ReadNearbyObstacles(doc, equipmentBounds, equipmentIds,
                out object obstacleWindow, out bool obstaclesTruncated);
            return new
            {
                document = doc.Title, selected_count = selectedIds.Count, equipment,
                obstacle_window_mm = obstacleWindow,
                nearby_obstacles = nearbyObstacles,
                obstacles_truncated = obstaclesTruncated,
                checked_obstacle_categories = CheckedCategories.Select(c => c.ToString()).ToArray(),
                pipe_types = new FilteredElementCollector(doc).OfClass(typeof(PipeType))
                    .Cast<PipeType>().OrderBy(t => t.Name).Take(100)
                    .Select(t => new { id = t.Id.ToString(), name = t.Name }).ToArray(),
                system_types = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType))
                    .Cast<PipingSystemType>().OrderBy(t => t.Name).Take(100)
                    .Select(t => new { id = t.Id.ToString(), name = t.Name,
                        classification = t.SystemClassification.ToString() }).ToArray(),
                levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).Take(100)
                    .Select(l => new { id = l.Id.ToString(), name = l.Name,
                        elevation_mm = Math.Round(l.Elevation / FeetPerMm, 3) }).ToArray(),
                limits = "Lecture des 30 premiers éléments sélectionnés, des 100 premiers types/niveaux par liste et d'au plus 100 obstacles du projet à 10 m de l'enveloppe des équipements sélectionnés. Les boîtes peuvent être plus grandes que les solides réels. Sélectionner un groupe local si obstacles_truncated=true. Les modèles liés et le P&ID ne sont pas lus."
            };
        }

        internal static object Preview(Document doc, JObject args)
        {
            var plan = Parse(doc, args);
            var clashes = FindObstacles(doc, plan);
            return Report(doc, plan, clashes, false, null);
        }

        internal static string Describe(Document doc, JObject args)
        {
            var plan = Parse(doc, args);
            var clashes = FindObstacles(doc, plan);
            if (clashes.Count != 0)
                throw new InvalidOperationException("Tracé refusé : collision possible avec " +
                    string.Join(", ", clashes.Take(8).Select(e => e.Name + " #" + e.Id)) + ".");
            return "Un tracé entre équipement #" + plan.StartOwner.Id + " / connecteur " + plan.Start.Id +
                " et équipement #" + plan.EndOwner.Id + " / connecteur " + plan.End.Id +
                ". DN " + Math.Round(plan.Diameter / FeetPerMm, 1) + " mm, " + (plan.Points.Count - 1) +
                " tuyau(x), type « " + plan.PipeType.Name + " », système « " + plan.SystemType.Name +
                " », niveau « " + plan.Level.Name + " ». Coudes natifs requis aux changements de direction. " +
                "Aucune collision détectée dans les catégories du projet contrôlées par boîtes englobantes. " +
                "Les modèles liés, l'isolation, les supports, les pentes et les règles P&ID ne sont pas vérifiés.";
        }

        internal static object Apply(Document doc, JObject args)
        {
            var plan = Parse(doc, args);
            if (doc.IsReadOnly || doc.IsModifiable)
                throw new InvalidOperationException("Le projet doit être modifiable et aucune autre transaction ne doit être ouverte.");
            var clashes = FindObstacles(doc, plan);
            if (clashes.Count != 0)
                throw new InvalidOperationException("Tracé refusé : obstacle(s) détecté(s) : " +
                    string.Join(", ", clashes.Take(8).Select(e => e.Name + " #" + e.Id)) + ".");
            var created = new List<Pipe>();
            using (var transaction = new Transaction(doc, "Assistant MEP — raccorder deux équipements"))
            {
                transaction.Start();
                try
                {
                    for (int i = 0; i < plan.Points.Count - 1; i++)
                    {
                        var pipe = Pipe.Create(doc, plan.SystemType.Id, plan.PipeType.Id, plan.Level.Id,
                            plan.Points[i], plan.Points[i + 1]);
                        if (pipe == null) throw new InvalidOperationException("Revit n'a pas créé le tuyau " + (i + 1) + ".");
                        var diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diameter == null || diameter.IsReadOnly || !diameter.Set(plan.Diameter))
                            throw new InvalidOperationException("Impossible de régler le DN du tuyau " + (i + 1) + ".");
                        created.Add(pipe);
                    }
                    doc.Regenerate();
                    for (int i = 0; i < created.Count; i++)
                    {
                        var actual = created[i].get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble();
                        if (!actual.HasValue || Math.Abs(actual.Value - plan.Diameter) > DiameterTolerance)
                            throw new InvalidOperationException("Le DN du tuyau " + (i + 1) + " ne correspond pas aux connecteurs.");
                    }
                    for (int i = 0; i < created.Count - 1; i++)
                    {
                        var at = plan.Points[i + 1];
                        var a = PipeEnd(created[i], at); var b = PipeEnd(created[i + 1], at);
                        if (a == null || b == null) throw new InvalidOperationException("Connecteur de coude introuvable.");
                        var elbow = doc.Create.NewElbowFitting(a, b);
                        if (elbow == null) throw new InvalidOperationException("Revit n'a pas créé le coude " + (i + 1) + ".");
                    }
                    doc.Regenerate();
                    var first = PipeEnd(created[0], plan.Points[0]);
                    var last = PipeEnd(created[created.Count - 1], plan.Points[plan.Points.Count - 1]);
                    if (first == null || last == null)
                        throw new InvalidOperationException("Connecteur terminal de tuyau introuvable.");
                    if (!plan.Start.IsConnectedTo(first)) plan.Start.ConnectTo(first);
                    if (!plan.End.IsConnectedTo(last)) plan.End.ConnectTo(last);
                    doc.Regenerate();
                    if (!plan.Start.IsConnectedTo(first) || !plan.End.IsConnectedTo(last))
                        throw new InvalidOperationException("Au moins un équipement n'est pas raccordé au tuyau créé.");
                    // Fittings may trim pipe ends away from the original corner. Check
                    // every actual end connector after regeneration, not its old position.
                    for (int i = 0; i < created.Count; i++)
                        foreach (Connector connector in created[i].ConnectorManager.Connectors)
                            if (connector.Domain == Domain.DomainPiping &&
                                connector.ConnectorType == ConnectorType.End && !connector.IsConnected)
                                throw new InvalidOperationException("Le tuyau " + (i + 1) + " conserve une extrémité ouverte.");
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit a annulé la transaction de raccordement.");
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    throw;
                }
            }
            return Report(doc, plan, clashes, true, created.Select(p => p.Id.ToString()).ToArray());
        }

        private sealed class RoutePlan
        {
            internal FamilyInstance StartOwner, EndOwner;
            internal Connector Start, End;
            internal PipeType PipeType;
            internal PipingSystemType SystemType;
            internal Level Level;
            internal double Diameter;
            internal List<XYZ> Points;
        }

        private static RoutePlan Parse(Document doc, JObject args)
        {
            RequireProject(doc);
            if (args == null || !new[] { "start", "end", "waypoints_mm", "pipe_type_id", "system_type_id", "level_id" }
                .OrderBy(x => x).SequenceEqual(args.Properties().Select(p => p.Name).OrderBy(x => x)))
                throw new InvalidOperationException("Arguments MEP non conformes.");
            var start = ResolveEndpoint(doc, args["start"] as JObject, out var startOwner);
            var end = ResolveEndpoint(doc, args["end"] as JObject, out var endOwner);
            if (startOwner.Id == endOwner.Id) throw new InvalidOperationException("Choisissez deux équipements distincts.");
            if (start.IsConnected || end.IsConnected) throw new InvalidOperationException("Les deux connecteurs doivent être libres.");
            if (start.Shape != ConnectorProfileType.Round || end.Shape != ConnectorProfileType.Round ||
                start.Radius <= 0 || end.Radius <= 0)
                throw new InvalidOperationException("Ce pilote accepte seulement deux connecteurs ronds de tuyauterie.");
            var diameter = 2 * start.Radius;
            if (Math.Abs(diameter - 2 * end.Radius) > DiameterTolerance)
                throw new InvalidOperationException("Les DN des connecteurs diffèrent : aucun réducteur n'est créé par ce pilote.");
            var pipeType = doc.GetElement(ParseElementId(args["pipe_type_id"], "pipe_type_id")) as PipeType;
            var systemType = doc.GetElement(ParseElementId(args["system_type_id"], "system_type_id")) as PipingSystemType;
            var level = doc.GetElement(ParseElementId(args["level_id"], "level_id")) as Level;
            if (pipeType == null || systemType == null || level == null)
                throw new InvalidOperationException("Type de tuyau, système ou niveau inconnu dans ce projet.");
            string classification = systemType.SystemClassification.ToString();
            foreach (var connector in new[] { start, end })
            {
                string connectorSystem = connector.PipeSystemType.ToString();
                if (connectorSystem != "UndefinedSystemType" && connectorSystem != "Fitting" &&
                    connectorSystem != "Global" && connectorSystem != classification)
                    throw new InvalidOperationException("Le connecteur " + connector.Id + " est de classe « " +
                        connectorSystem + " », incompatible avec le système « " + classification + " ».");
            }
            if (!(args["waypoints_mm"] is JArray waypointTokens) || waypointTokens.Count > 16)
                throw new InvalidOperationException("waypoints_mm doit contenir 0 à 16 points.");
            var points = new List<XYZ> { start.Origin };
            points.AddRange(waypointTokens.Select(ReadWaypoint));
            points.Add(end.Origin);
            if (points.Count - 1 > 17) throw new InvalidOperationException("Trop de tronçons.");
            double minLength = Math.Max(150 * FeetPerMm, diameter * 2);
            for (int i = 0; i < points.Count - 1; i++)
            {
                var delta = points[i + 1] - points[i];
                int movingAxes = (Math.Abs(delta.X) > PointTolerance ? 1 : 0) +
                    (Math.Abs(delta.Y) > PointTolerance ? 1 : 0) +
                    (Math.Abs(delta.Z) > PointTolerance ? 1 : 0);
                if (movingAxes != 1 || delta.GetLength() < minLength)
                    throw new InvalidOperationException("Tronçon " + (i + 1) + " non orthogonal ou trop court (minimum " +
                        Math.Round(minLength / FeetPerMm) + " mm).");
                if (i > 0)
                {
                    var previous = (points[i] - points[i - 1]).Normalize();
                    var current = delta.Normalize();
                    if (Math.Abs(previous.DotProduct(current)) > 0.01)
                        throw new InvalidOperationException("Point intermédiaire " + i + " redondant ou demi-tour ; seuls les coudes à 90° sont admis.");
                }
            }
            var firstDirection = (points[1] - points[0]).Normalize();
            var finalDirection = (points[points.Count - 1] - points[points.Count - 2]).Normalize();
            var startOut = start.CoordinateSystem?.BasisZ;
            var endOut = end.CoordinateSystem?.BasisZ;
            if (startOut == null || endOut == null || startOut.GetLength() < 0.99 || endOut.GetLength() < 0.99 ||
                firstDirection.DotProduct(startOut.Normalize()) < 0.98 ||
                finalDirection.DotProduct(endOut.Normalize()) > -0.98)
                throw new InvalidOperationException("Le premier et le dernier tronçon doivent suivre l'axe extérieur de leurs connecteurs.");
            return new RoutePlan { StartOwner = startOwner, EndOwner = endOwner, Start = start, End = end,
                PipeType = pipeType, SystemType = systemType, Level = level, Diameter = diameter, Points = points };
        }

        private static Connector ResolveEndpoint(Document doc, JObject token, out FamilyInstance owner)
        {
            if (token == null || token.Properties().Count() != 2 || token["element_id"] == null || token["connector_id"] == null ||
                token.Properties().Any(p => p.Name != "element_id" && p.Name != "connector_id"))
                throw new InvalidOperationException("Référence de connecteur non conforme.");
            owner = doc.GetElement(ParseElementId(token["element_id"], "element_id")) as FamilyInstance;
            if (owner?.MEPModel?.ConnectorManager == null)
                throw new InvalidOperationException("Équipement MEP introuvable dans le projet actif.");
            if (token["connector_id"].Type != JTokenType.Integer ||
                !int.TryParse(token["connector_id"].ToString(), out int connectorId) || connectorId < 0)
                throw new InvalidOperationException("Identifiant de connecteur invalide.");
            var connector = owner.MEPModel.ConnectorManager.Lookup(connectorId);
            if (connector == null || connector.Domain != Domain.DomainPiping || connector.ConnectorType != ConnectorType.End)
                throw new InvalidOperationException("Connecteur de tuyauterie introuvable sur l'équipement #" + owner.Id + ".");
            return connector;
        }

        private static ElementId ParseElementId(JToken token, string name)
        {
#if REVIT2024
            if (token?.Type != JTokenType.String ||
                !long.TryParse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value <= 0)
                throw new InvalidOperationException("Identifiant Revit invalide : " + name + ".");
            return new ElementId(value);
#else
            if (token?.Type != JTokenType.String ||
                !int.TryParse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
                throw new InvalidOperationException("Identifiant Revit invalide : " + name + ".");
            return new ElementId(value);
#endif
        }

        private static XYZ ReadWaypoint(JToken token)
        {
            if (!(token is JObject point) || point.Properties().Count() != 3 ||
                point.Properties().Any(p => p.Name != "x" && p.Name != "y" && p.Name != "z"))
                throw new InvalidOperationException("Point intermédiaire non conforme.");
            return new XYZ(Coordinate(point["x"], "x"), Coordinate(point["y"], "y"), Coordinate(point["z"], "z"));
        }

        private static double Coordinate(JToken token, string axis)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new InvalidOperationException("Coordonnée " + axis + " manquante.");
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 100000000)
                throw new InvalidOperationException("Coordonnée " + axis + " hors limites.");
            return value * FeetPerMm;
        }

        private static List<Element> FindObstacles(Document doc, RoutePlan plan)
        {
            double pad = plan.Diameter / 2 + ObstacleClearance;
            var min = new XYZ(plan.Points.Min(p => p.X) - pad, plan.Points.Min(p => p.Y) - pad, plan.Points.Min(p => p.Z) - pad);
            var max = new XYZ(plan.Points.Max(p => p.X) + pad, plan.Points.Max(p => p.Y) + pad, plan.Points.Max(p => p.Z) + pad);
            var candidates = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(CheckedCategories.ToList()))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(min, max)))
                .WhereElementIsNotElementType();
            var blocked = new List<Element>();
            foreach (var element in candidates)
            {
                if (element.Id == plan.StartOwner.Id || element.Id == plan.EndOwner.Id) continue;
                var box = element.get_BoundingBox(null);
                if (box == null) continue;
                for (int i = 0; i < plan.Points.Count - 1; i++)
                    if (SegmentHitsBox(plan.Points[i], plan.Points[i + 1], box, pad))
                    { blocked.Add(element); break; }
                if (blocked.Count >= 30) break;
            }
            return blocked;
        }

        private static object[] ReadNearbyObstacles(Document doc, IList<XYZ> equipmentBounds,
            ISet<ElementId> excludedEquipment, out object window, out bool truncated)
        {
            window = null;
            truncated = false;
            if (equipmentBounds.Count == 0) return new object[0];
            const double reach = 10000 * FeetPerMm;
            var min = new XYZ(equipmentBounds.Min(p => p.X) - reach,
                equipmentBounds.Min(p => p.Y) - reach, equipmentBounds.Min(p => p.Z) - reach);
            var max = new XYZ(equipmentBounds.Max(p => p.X) + reach,
                equipmentBounds.Max(p => p.Y) + reach, equipmentBounds.Max(p => p.Z) + reach);
            window = new { min_mm = Point(min), max_mm = Point(max) };
            var candidates = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(CheckedCategories.ToList()))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(min, max)))
                .WhereElementIsNotElementType();
            var obstacles = new List<object>();
            foreach (var element in candidates)
            {
                if (excludedEquipment.Contains(element.Id)) continue;
                var box = element.get_BoundingBox(null);
                if (box == null) continue;
                if (obstacles.Count == 100) { truncated = true; break; }
                obstacles.Add(new
                {
                    element_id = element.Id.ToString(), name = element.Name,
                    category = element.Category?.Name,
                    bbox_min_mm = Point(box.Min), bbox_max_mm = Point(box.Max)
                });
            }
            return obstacles.ToArray();
        }

        private static bool SegmentHitsBox(XYZ a, XYZ b, BoundingBoxXYZ box, double pad)
        {
            var lo = new[] { box.Min.X - pad, box.Min.Y - pad, box.Min.Z - pad };
            var hi = new[] { box.Max.X + pad, box.Max.Y + pad, box.Max.Z + pad };
            var p = new[] { a.X, a.Y, a.Z }; var d = new[] { b.X - a.X, b.Y - a.Y, b.Z - a.Z };
            double enter = 0, leave = 1;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(d[axis]) < 1e-10)
                { if (p[axis] < lo[axis] || p[axis] > hi[axis]) return false; }
                else
                {
                    double t1 = (lo[axis] - p[axis]) / d[axis];
                    double t2 = (hi[axis] - p[axis]) / d[axis];
                    enter = Math.Max(enter, Math.Min(t1, t2));
                    leave = Math.Min(leave, Math.Max(t1, t2));
                    if (enter > leave) return false;
                }
            }
            return true;
        }

        private static Connector PipeEnd(Pipe pipe, XYZ point) => pipe.ConnectorManager.Connectors.Cast<Connector>()
            .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(point)).FirstOrDefault(c => c.Origin.DistanceTo(point) <= PointTolerance * 2);

        private static object Report(Document doc, RoutePlan plan, List<Element> clashes, bool applied, string[] created)
        {
            bool hasLinks = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Any();
            return new
            {
                valid = clashes.Count == 0, applied, document = doc.Title,
                start = new { element_id = plan.StartOwner.Id.ToString(), connector_id = plan.Start.Id },
                end = new { element_id = plan.EndOwner.Id.ToString(), connector_id = plan.End.Id },
                diameter_mm = Math.Round(plan.Diameter / FeetPerMm, 3),
                points_mm = plan.Points.Select(Point).ToArray(),
                segment_count = plan.Points.Count - 1,
                total_length_mm = Math.Round(Enumerable.Range(0, plan.Points.Count - 1)
                    .Sum(i => plan.Points[i].DistanceTo(plan.Points[i + 1])) / FeetPerMm, 1),
                pipe_type_id = plan.PipeType.Id.ToString(), system_type_id = plan.SystemType.Id.ToString(),
                level_id = plan.Level.Id.ToString(),
                obstacles = clashes.Select(e =>
                {
                    var box = e.get_BoundingBox(null);
                    return new { element_id = e.Id.ToString(), name = e.Name,
                        category = e.Category?.Name,
                        bbox_min_mm = box == null ? null : Point(box.Min),
                        bbox_max_mm = box == null ? null : Point(box.Max) };
                }).ToArray(),
                obstacle_clearance_mm = Math.Round(plan.Diameter / 2 / FeetPerMm + 25, 3),
                checked_categories = CheckedCategories.Select(c => c.ToString()).ToArray(),
                check_method = "Intersections des segments avec les boîtes englobantes gonflées de DN/2 + 25 mm ; faux positifs possibles.",
                linked_models_present = hasLinks,
                limitations = "Modèles liés, géométrie exacte et saillies des raccords, isolation, supports, réservations, pentes, nomenclature des circuits et règles P&ID non vérifiés. Les équipements de départ et d'arrivée sont exclus du contrôle des obstacles. Le précontrôle ne teste pas les familles de raccords ; leur création et les connexions sont vérifiées dans la transaction d'application.",
                created_pipe_ids = created ?? new string[0]
            };
        }

        private static object Point(XYZ p) => new { x = Math.Round(p.X / FeetPerMm, 3),
            y = Math.Round(p.Y / FeetPerMm, 3), z = Math.Round(p.Z / FeetPerMm, 3) };
        private static object Direction(Connector c)
        {
            var p = c.CoordinateSystem?.BasisZ;
            return p == null ? null : new { x = Math.Round(p.X, 4), y = Math.Round(p.Y, 4), z = Math.Round(p.Z, 4) };
        }
        private static JObject Number() => new JObject { ["type"] = "number",
            ["minimum"] = -100000000, ["maximum"] = 100000000 };
        private static JObject Definition(string name, string description, JObject properties, JArray required) =>
            new JObject { ["type"] = "function", ["name"] = name, ["description"] = description,
                ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = properties,
                    ["required"] = required, ["additionalProperties"] = false } };
        private static void RequireProject(Document doc)
        {
            if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument)
                throw new InvalidOperationException("Ouvrez un projet Revit actif pour utiliser l'assistant MEP.");
        }
    }
}
