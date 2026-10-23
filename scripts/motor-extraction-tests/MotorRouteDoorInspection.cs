using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BIMaestro.MotorTests
{
    internal static class MotorRouteDoorInspection
    {
        private static double[] V(XYZ p) => p == null ? null : new[] { p.X, p.Y, p.Z };
        internal static void Run(Document doc, string folder)
        {
            var output = new Dictionary<string, object>();
            var rows = new List<object>();
            var pump = doc.GetElement(new ElementId(809675L)) as FamilyInstance;
            var target = doc.GetElement(new ElementId(794529L)) as FamilyInstance;
            output["pump"] = pump == null ? null : new { id = pump.Id.Value, origin = V(pump.GetTransform().Origin) };
            output["target"] = target == null ? null : new { id = target.Id.Value, name = target.Name, origin = V(target.GetTransform().Origin) };
            Inspect(doc, Transform.Identity, ElementId.InvalidElementId, null);
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                if (link.GetLinkDocument() is Document linked) Inspect(linked, link.GetTotalTransform(), link.Id, link.Name);
            output["doors"] = rows;
            output["scope"] = "Read-only door inventory and native wall-hole probe on the disposable copy. No geometry changed or document saved.";
            File.WriteAllText(Path.Combine(folder, "motor-route-doors.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));

            void Inspect(Document model, Transform toWorld, ElementId linkId, string linkName)
            {
                foreach (FamilyInstance door in new FilteredElementCollector(model).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfClass(typeof(FamilyInstance)))
                {
                    var row = new Dictionary<string, object>(); rows.Add(row);
                    try
                    {
                        var frame = door.GetTransform();
                        var insertion = (door.Location as LocationPoint)?.Point ?? frame.Origin;
                        row["id"] = door.Id.Value; row["linkId"] = linkId.Value; row["link"] = linkName;
                        row["family"] = door.Symbol.Family.Name; row["type"] = door.Name;
                        row["insertion"] = V(toWorld.OfPoint(insertion)); row["x"] = V(toWorld.OfVector(frame.BasisX));
                        row["y"] = V(toWorld.OfVector(frame.BasisY)); row["z"] = V(toWorld.OfVector(frame.BasisZ));
                        row["host"] = door.Host == null ? null : new { id = door.Host.Id.Value, type = door.Host.GetType().FullName, name = door.Host.Name, category = door.Host.Category?.Name };
                        row["nested"] = door.GetSubComponentIds().Select(id => id.Value).ToArray();
                        row["widthBuiltIn"] = Length(door, BuiltInParameter.DOOR_WIDTH);
                        row["heightBuiltIn"] = Length(door, BuiltInParameter.DOOR_HEIGHT);
                        row["lengthParameters"] = new Element[] { door, door.Symbol }.SelectMany(e => e.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.Double
                            && p.Definition.GetDataType() == SpecTypeId.Length && new[] { "largeur", "hauteur", "width", "height", "passage", "baie", "ouverture", "vantail" }
                                .Any(s => p.Definition.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                            .Select(p => new { source = e is FamilyInstance ? "instance" : "type", name = p.Definition.Name, mm = p.AsDouble() * 304.8 })).ToArray();
                        using (var box = door.get_BoundingBox(null))
                            row["bbox"] = box == null ? null : new { min = V(toWorld.OfPoint(box.Min)), max = V(toWorld.OfPoint(box.Max)) };
                        if (door.Host is Wall wall)
                        {
                            var cut = Cutout(model, wall, door, out XYZ direction, out string cutError);
                            row["wallWidthMm"] = wall.Width * 304.8; row["cutDirection"] = V(direction == null ? null : toWorld.OfVector(direction));
                            row["cutout"] = cut?.Select(p => V(toWorld.OfPoint(p))).ToArray(); row["cutoutError"] = cutError;
                            var normal = new XYZ(frame.BasisY.X, frame.BasisY.Y, 0).Normalize();
                            var center = cut != null && cut.Length > 0 ? new XYZ(cut.Average(p => p.X), cut.Average(p => p.Y), cut.Min(p => p.Z) + 1000 / 304.8)
                                : insertion + XYZ.BasisZ * (1000 / 304.8);
                            row["holeProbe"] = Probe(wall, center, normal, wall.Width + 1000 / 304.8);
                        }
                    }
                    catch (Exception ex) { row["error"] = ex.ToString(); }
                }
            }
        }
        private static double Length(FamilyInstance door, BuiltInParameter id)
        {
            foreach (Element element in new Element[] { door, door.Symbol })
                if (element.get_Parameter(id) is Parameter p && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
            return 0;
        }
        private static XYZ[] Cutout(Document doc, Wall wall, FamilyInstance door, out XYZ direction, out string error)
        {
            direction = null; error = null;
            try
            {
                var ifc = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RevitAPIIFC")
                    ?? Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Document).Assembly.Location), "RevitAPIIFC.dll"));
                var type = ifc.GetType("Autodesk.Revit.DB.IFC.ExporterIFCUtils");
                var method = type?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetInstanceCutoutFromWall" && m.GetParameters().Length == 4);
                if (method == null) { error = "GetInstanceCutoutFromWall unavailable"; return null; }
                var args = new object[] { doc, wall, door, null };
                using (var loop = method.Invoke(null, args) as CurveLoop)
                {
                    direction = args[3] as XYZ;
                    return loop?.SelectMany(c => c.Tessellate()).ToArray();
                }
            }
            catch (Exception ex) { error = ex.ToString(); return null; }
        }
        private static object Probe(Wall wall, XYZ center, XYZ normal, double depth)
        {
            int solids = 0; bool blocked = false;
            var x = XYZ.BasisZ.CrossProduct(normal).Normalize();
            var c = center - normal * depth / 2; double half = 50 / 304.8;
            var p = new[] { c - x * half - XYZ.BasisZ * half, c + x * half - XYZ.BasisZ * half,
                c + x * half + XYZ.BasisZ * half, c - x * half + XYZ.BasisZ * half };
            using (var loop = new CurveLoop())
            {
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(p[i], p[(i + 1) % 4]));
                using (var volume = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, normal, depth))
                {
                using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
                using (var geometry = wall.get_Geometry(options)) Read(geometry);
                void Read(GeometryElement shape)
                {
                    if (shape == null) return;
                    foreach (var item in shape)
                        if (item is GeometryInstance instance) { using (var expanded = instance.GetInstanceGeometry()) Read(expanded); }
                        else if (item is Solid solid && solid.Volume > 1e-12)
                        {
                            solids++;
                            using (var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(volume, solid, BooleanOperationsType.Intersect))
                                if (intersection.Volume > 1e-10) blocked = true;
                        }
                }
                }
            }
            return new { center = V(center), widthMm = 100, heightMm = 100, depthMm = depth * 304.8, wallSolids = solids, clear = solids > 0 && !blocked };
        }
    }
}
