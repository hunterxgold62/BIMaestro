using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace BIMaestro.CodexTests
{
    internal static class FamilyEditNativeTests
    {
        internal static object Run(UIApplication ui)
        {
            var roots = new[] { ui.Application.FamilyTemplatePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + ui.Application.VersionNumber, "Family Templates") };
            string template = roots.Where(Directory.Exists).SelectMany(root => Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories))
                .First(p => new[] { "Modèle générique métrique", "Metric Generic Model" }.Contains(Path.GetFileNameWithoutExtension(p)));
            var doc = ui.Application.NewFamilyDocument(template);
            try
            {
                Transaction Begin(string name) { var t = new Transaction(doc, name); t.Start(); return t; }
                void Commit(Transaction t) { if (t.Commit() != TransactionStatus.Committed) throw new Exception("Native edit commit failed"); }
                string[] AllIds() => new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId)
                    .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().Select(e => e.UniqueId)).OrderBy(s => s).ToArray();
                Extrusion extrusion; SymbolicCurve manual;
                using (var t = Begin("Manual family fixture"))
                {
                    doc.FamilyManager.NewType("Manuel");
                    var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    var points = new[] { XYZ.Zero, XYZ.BasisX, XYZ.BasisX + XYZ.BasisY, XYZ.BasisY };
                    var profile = new CurveArray();
                    for (int i = 0; i < 4; i++) profile.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
                    var profiles = new CurveArrArray(); profiles.Append(profile);
                    extrusion = doc.FamilyCreate.NewExtrusion(true, profiles, plane, 1);
                    manual = doc.FamilyCreate.NewSymbolicCurve(Line.CreateBound(XYZ.Zero, XYZ.BasisX * 2), plane);
                    Commit(t);
                }
                string familyId = doc.OwnerFamily.UniqueId, extrusionId = extrusion.UniqueId, manualId = manual.UniqueId;
                FamilyConfigurationNativeTests.Run(ui, doc, template);
                var originalIds = new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId).ToArray();
                var inventory = JObject.FromObject(CodexFamilyEditor.Inspect(doc, JObject.Parse("{offset:0,limit:100}")));
                if ((string)inventory["document_key"] != familyId || !inventory["elements"].Any(e => (string)e["unique_id"] == extrusionId)) throw new Exception("Missing manual extrusion inventory");
                var request = JObject.Parse("{document_key:'',group_name:'Plan',action:'add',representation_2d:{hide_model_in:[],drawings:[{name:'Trait',mode:'symbolic',plane:'xy',offset_mm:0,curves:[{kind:'line',points_mm:[[0,0],[100,100]],radius_mm:0}],rgb:[0,0,0],fill_pattern:'solid'}]}}");
                request["document_key"] = familyId;
                object Edit(JObject r)
                {
                    File.AppendAllText(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "edit-progress.txt"), (string)r["group_name"] + " " + (string)r["action"] + Environment.NewLine);
                    return CodexFamilyEditor.EditRepresentation(doc, r, (a,b) => true, Begin, Commit);
                }
                Reject(() => CodexFamilyEditor.EditRepresentation(doc, request, (a,b) => false, Begin, Commit));
                var wrong = (JObject)request.DeepClone(); wrong["document_key"] = "wrong"; Reject(() => Edit(wrong));
                Edit(request); Reject(() => Edit(request));
                request["action"] = "replace";
                request["representation_2d"]["drawings"][0]["curves"][0]["points_mm"][1][0] = 200;
                Edit(request);
                if (originalIds.Any(id => doc.GetElement(id) == null)) throw new Exception("Editing deleted pre-existing elements");
                // Inject a failure after commit: the encompassing group must restore the old drawing.
                var beforeFailure = new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId).OrderBy(s => s).ToArray();
                Reject(() => CodexFamilyEditor.EditRepresentation(doc, request, (a,b) => true, Begin, t => { Commit(t); throw new InvalidOperationException("Injected failure"); }));
                var afterFailure = new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId).OrderBy(s => s).ToArray();
                if (!beforeFailure.SequenceEqual(afterFailure)) throw new Exception("Failed edit did not restore the original document");
                request["action"] = "remove"; request["representation_2d"] = JValue.CreateNull(); Edit(request);
                if (doc.GetElement(manualId) == null || doc.GetElement(extrusionId) == null || doc.OwnerFamily.UniqueId != familyId) throw new Exception("Original family identity lost");
                var extents = new JObject { ["document_key"] = familyId, ["element_unique_id"] = extrusionId, ["start_mm"] = 400, ["end_mm"] = 700 };
                CodexFamilyEditor.EditExtrusion(doc, extents, (a,b) => true, Begin, Commit);
                if (Math.Abs(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM).AsDouble() * 304.8 - 700) > 0.001) throw new Exception("Extrusion depth mismatch");
                using (var t = Begin("Associate existing depth"))
                {
                    var p = doc.FamilyManager.AddParameter("Profondeur", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    doc.FamilyManager.Set(p, 700 / 304.8);
                    doc.FamilyManager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), p);
                    Commit(t);
                }
                Reject(() => CodexFamilyEditor.EditExtrusion(doc, extents, (a,b) => true, Begin, Commit));
                var region = JObject.Parse("{document_key:'',group_name:'Pochage',action:'add',representation_2d:{hide_model_in:[],drawings:[{name:'Disque',mode:'filled',plane:'xy',offset_mm:0,curves:[{kind:'circle',points_mm:[[0,0]],radius_mm:25}],rgb:[100,150,200],fill_pattern:'solid'}]}}");
                region["document_key"] = familyId;
                var beforeRegionFailure = AllIds();
                Reject(() => CodexFamilyEditor.EditRepresentation(doc, region, (a,b) => true, Begin, t => { Commit(t); throw new InvalidOperationException("Injected nested region failure"); }));
                if (!beforeRegionFailure.SequenceEqual(AllIds())) throw new Exception("Nested region imports survived rollback");
                Edit(region);
                string path = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "manual-family-edit-test.rfa");
                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
                doc.Close(false); doc = ui.Application.OpenDocumentFile(path);
                var reopened = JObject.FromObject(CodexFamilyEditor.Inspect(doc, JObject.Parse("{offset:0,limit:100}")));
                if (!reopened["drawing_groups"].Any(g => (string)g["name"] == "Pochage")) throw new Exception("Drawing groups did not survive save/reopen");
                region["action"] = "replace"; Edit(region);
                region["action"] = "remove"; region["representation_2d"] = JValue.CreateNull(); Edit(region);
                return new { passed = true, manual_family = true, inventory = true, add_replace_remove = true,
                    duplicate_and_wrong_document_rejected = true, rollback_after_commit = true, original_elements_preserved = true,
                    extrusion_edited_in_place = true, parameter_association_preserved = true,
                    filled_regions = true, nested_region_rollback = true, persistent_groups_after_reopen = true,
                    existing_family_parameters_formulas = true, mixed_elements_visibility = true, independent_project_instances = true, configuration_rollback = true };
            }
            finally { doc.Close(false); }
        }
        private static void Reject(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected edit rejection"); }
    }
}
