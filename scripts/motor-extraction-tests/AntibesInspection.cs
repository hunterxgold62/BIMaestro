using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BIMaestro.MotorTests
{
    public sealed class AntibesInspection : IExternalApplication
    {
        private bool _done;
        private string _folder;
        public Result OnStartup(UIControlledApplication app)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_ANTIBES_INSPECTION") != "1") return Result.Succeeded;
            _folder = Path.GetDirectoryName(GetType().Assembly.Location);
            app.Idling += Run;
            File.WriteAllText(Path.Combine(_folder, "loaded.txt"), "Read-only inspection of the workspace copy");
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
        private static string Id(Element e) => e.Id.Value.ToString();
        private static object Bounds(Element e)
        {
            var b = e.get_BoundingBox(null);
            return b == null ? null : new { min_mm = new[] { b.Min.X * 304.8, b.Min.Y * 304.8, b.Min.Z * 304.8 },
                size_mm = new[] { (b.Max.X-b.Min.X)*304.8, (b.Max.Y-b.Min.Y)*304.8, (b.Max.Z-b.Min.Z)*304.8 } };
        }
        private static object GeometrySummary(FamilyInstance instance)
        {
            var nested = new List<object>();
            var solids = new List<object>();
            var inverse = instance.GetTransform().Inverse;
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
            using (var geometry = instance.get_Geometry(options))
            {
                if (geometry != null) Read(geometry, 0);
                void Read(GeometryElement items, int depth)
                {
                    foreach (var item in items)
                    {
                        if (item is GeometryInstance child)
                        {
                            var symbol = instance.Document.GetElement(child.GetSymbolGeometryId().SymbolId);
                            nested.Add(new { depth, symbol_id=child.GetSymbolGeometryId().SymbolId.Value, symbol_name=symbol?.Name,
                                family=(symbol as FamilySymbol)?.Family.Name });
                            using (var expanded = child.GetInstanceGeometry()) Read(expanded, depth + 1);
                        }
                        else if (item is Solid solid && solid.Volume > 1e-12)
                        {
                            var points = solid.Edges.Cast<Edge>().SelectMany(e => e.Tessellate()).Select(inverse.OfPoint).ToList();
                            var style = instance.Document.GetElement(solid.GraphicsStyleId) as GraphicsStyle;
                            if (points.Count > 0) solids.Add(new { depth, style=style?.GraphicsStyleCategory.Name,
                                min_local_mm=new[] { points.Min(p=>p.X)*304.8, points.Min(p=>p.Y)*304.8, points.Min(p=>p.Z)*304.8 },
                                size_local_mm=new[] { (points.Max(p=>p.X)-points.Min(p=>p.X))*304.8,
                                    (points.Max(p=>p.Y)-points.Min(p=>p.Y))*304.8,(points.Max(p=>p.Z)-points.Min(p=>p.Z))*304.8 } });
                        }
                    }
                }
            }
            return new { nested, solids };
        }
        private void Run(object sender, IdlingEventArgs args)
        {
            if (_done) return; _done = true;
            var ui = (UIApplication)sender;
            var families = new List<object>();
            Document doc = null;
            string error = null;
            object inventory = null;
            try
            {
                var path = Environment.GetEnvironmentVariable("BIMAESTRO_ANTIBES_COPY") ?? Path.Combine(_folder, "Projet_AntibesV2-test.rvt");
                if (!File.Exists(path)) throw new FileNotFoundException("Workspace copy is missing", path);
                doc = ui.Application.OpenDocumentFile(path);
                var all = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>().ToList();
                var pumps = all.Where(e => (e.Symbol.Family.Name + " " + e.Symbol.Name).IndexOf("pomp", StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.Symbol.Family.Name + " " + e.Symbol.Name).IndexOf("pump", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                inventory = new {
                    mechanical_equipment_count = all.Count(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_MechanicalEquipment),
                    family_types = all.Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_MechanicalEquipment).GroupBy(e => e.Symbol.Id).Select(g => new { family = g.First().Symbol.Family.Name, type = g.First().Symbol.Name, count = g.Count() }).ToList(),
                    pump_candidates_name_heuristic = pumps.Select(e => new { id = Id(e), family = e.Symbol.Family.Name, type = e.Symbol.Name, category=e.Category?.Name,
                        geometry = GeometrySummary(e),
                        motor_parameters = e.Parameters.Cast<Parameter>().Concat(e.Symbol.Parameters.Cast<Parameter>())
                            .Where(p => p.Definition.Name.IndexOf("moteur", StringComparison.OrdinalIgnoreCase)>=0 || p.Definition.Name.StartsWith("ZDM", StringComparison.OrdinalIgnoreCase))
                            .Select(p => new { name=p.Definition.Name, value=p.AsValueString() ?? (p.StorageType == StorageType.String ? p.AsString() : null),
                                length_mm=p.StorageType==StorageType.Double && p.Definition.GetDataType()==SpecTypeId.Length ? (double?)(p.AsDouble()*304.8) : null }).ToList(),
                        bounds = Bounds(e), subcomponents = e.GetSubComponentIds().Select(doc.GetElement).Where(c => c != null)
                            .Select(c => new { id = Id(c), name = c.Name, category = c.Category?.Name }).ToList(),
                        classification = e.Parameters.Cast<Parameter>().Where(p => new[] { "ifc", "classification", "omniclass", "fabricant", "modèle", "manufacturer", "model" }
                            .Any(word => p.Definition.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)).Take(25)
                            .Select(p => new { name=p.Definition.Name, value=p.AsValueString() ?? (p.StorageType == StorageType.String ? p.AsString() : null) }).ToList()
                    }).ToList()
                };
                foreach (var group in pumps.GroupBy(e => e.Symbol.Family.Id))
                {
                    var family = group.First().Symbol.Family;
                    if (!family.IsEditable) { families.Add(new { family = family.Name, editable = false }); continue; }
                    Document fd = null;
                    try
                    {
                        fd = doc.EditFamily(family);
                        var nested = new FilteredElementCollector(fd).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
                        var forms = new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).Cast<GenericForm>().ToList();
                        var names = fd.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p => new { name = p.Definition.Name, formula = p.Formula, instance = p.IsInstance }).ToList();
                        families.Add(new {
                            family = family.Name, editable = true, occurrences = group.Count(),
                            nested = nested.Select(n => new { id=Id(n), family=n.Symbol.Family.Name, type=n.Symbol.Name, category=n.Category?.Name, bounds=Bounds(n) }).ToList(),
                            forms = forms.Select(f => new { id=Id(f), kind=f.GetType().Name, solid=f.IsSolid, subcategory=f.Subcategory?.Name, bounds=Bounds(f) }).ToList(),
                            parameters = names,
                            subcategories = fd.OwnerFamily.FamilyCategory.SubCategories.Cast<Category>().Select(c => c.Name).ToList()
                        });
                    }
                    catch (Exception ex) { families.Add(new { family = family.Name, error = ex.Message }); }
                    finally { if (fd != null) fd.Close(false); }
                }
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally { if (doc != null) doc.Close(false); }
            File.WriteAllText(Path.Combine(_folder, "inspection.json"), JsonSerializer.Serialize(new { inventory, families, error }, new JsonSerializerOptions { WriteIndented = true }));
            if (ui.ActiveUIDocument == null) ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
    }
}
