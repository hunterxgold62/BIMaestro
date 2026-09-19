using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace BIMaestro.CodexTests
{
    internal static class FamilyConfigurationNativeTests
    {
        internal static void Run(UIApplication ui, Document doc, string template)
        {
            Transaction Begin(string name) { var t = new Transaction(doc, name); t.Start(); return t; }
            void Commit(Transaction t) { if (t.Commit() != TransactionStatus.Committed) throw new Exception("Configuration transaction failed"); }
            object Apply(JObject request) => CodexFamilyConfiguration.Apply(doc, request, (a,b) => true, Begin, Commit);
            CurveLoop Profile()
            {
                var loop = new CurveLoop(); var points = new[] { XYZ.Zero, XYZ.BasisX, XYZ.BasisX + XYZ.BasisY, XYZ.BasisY };
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
                return loop;
            }
            Document child = ui.Application.NewFamilyDocument(template);
            Family loaded;
            try
            {
                using (var t = new Transaction(child, "Nested glass fixture"))
                {
                    t.Start(); child.FamilyManager.NewType("Verre");
                    using (var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { Profile() }, XYZ.BasisZ, 0.3)) FreeFormElement.Create(child, solid);
                    Commit(t);
                }
                loaded = child.LoadFamily(doc);
            }
            finally { child.Close(false); }
            Element plate, glass, cutlery;
            using (var t = Begin("Service de table fixture"))
            {
                using (var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { Profile() }, XYZ.BasisZ, 0.05)) plate = FreeFormElement.Create(doc, solid);
                var curves = new CurveArray(); foreach (Curve curve in Profile()) curves.Append(curve);
                var arrays = new CurveArrArray(); arrays.Append(curves);
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                cutlery = doc.FamilyCreate.NewExtrusion(true, arrays, plane, 0.02);
                var symbol = loaded.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().First();
                symbol.Activate(); doc.Regenerate();
                glass = doc.FamilyCreate.NewFamilyInstance(XYZ.Zero, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                Commit(t);
            }
            var elements = new[] { plate, glass, cutlery };
            var request = JObject.Parse("{document_key:'',replace_associations:false,type_names:['Variante'],parameters:[{name:'Service de table',mode:'add',kind:'yesno',instance:true,group:'visibility',shared_guid:'',formula:null,value:true}],bindings:[]}");
            request["document_key"] = doc.OwnerFamily.UniqueId;
            request["bindings"] = new JArray(elements.Select(e => new JObject { ["element_unique_id"] = e.UniqueId, ["property"] = "visibility", ["family_parameter"] = "Service de table" }));
            var originalType = doc.FamilyManager.CurrentType.Name;
            var inspection = JObject.FromObject(CodexFamilyConfiguration.Inspect(doc, new JObject { ["document_key"] = doc.OwnerFamily.UniqueId, ["element_unique_id"] = glass.UniqueId }));
            if (!inspection["parameters"].Any(p => (bool)p["associable"])) throw new Exception("Missing nested family associable parameters");
            Apply(request);
            var manager = doc.FamilyManager; var toggle = manager.get_Parameter("Service de table");
            if (toggle == null || !toggle.IsInstance || manager.CurrentType.Name != originalType) throw new Exception("Instance scope or original type lost");
            if (manager.Types.Cast<FamilyType>().Any(t => t.AsInteger(toggle) != 1)) throw new Exception("New switch default not initialized on all types");
            using (var t = Begin("Visibility flex false/true"))
            {
                foreach (int value in new[] { 0, 1 })
                {
                    manager.Set(toggle, value); doc.Regenerate();
                    if (elements.Any(e => e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() != value)) throw new Exception("A service element does not follow the switch");
                }
                Commit(t);
            }
            var reuse = (JObject)request.DeepClone(); reuse["parameters"][0]["mode"] = "reuse"; reuse["parameters"][0]["value"] = JValue.CreateNull(); Apply(reuse);
            var conflict = (JObject)request.DeepClone(); conflict["parameters"][0]["name"] = "Autre visibilité";
            foreach (var b in conflict["bindings"]) b["family_parameter"] = "Autre visibilité";
            Reject(() => Apply(conflict));
            if (manager.get_Parameter("Autre visibilité") != null) throw new Exception("Conflicting request left a parameter behind");
            var rollback = (JObject)request.DeepClone(); rollback["parameters"][0]["name"] = "Rollback test"; rollback["bindings"] = new JArray();
            Reject(() => CodexFamilyConfiguration.Apply(doc, rollback, (a,b) => true, Begin, t => { Commit(t); throw new InvalidOperationException("Injected failure"); }));
                if (manager.get_Parameter("Rollback test") != null) throw new Exception("Configuration commit failure did not roll back");
            var formula = JObject.Parse("{document_key:'',replace_associations:false,type_names:[],parameters:[{name:'BaseTest',mode:'add',kind:'length',instance:false,group:'geometry',shared_guid:'',formula:null,value:100},{name:'DoubleTest',mode:'add',kind:'length',instance:false,group:'geometry',shared_guid:'',formula:'BaseTest * 2',value:null}],bindings:[]}");
            formula["document_key"] = doc.OwnerFamily.UniqueId; Apply(formula);
            manager = doc.FamilyManager; // Reacquire the wrapper after the preceding rollback.
            if (Math.Abs(manager.CurrentType.AsDouble(manager.get_Parameter("DoubleTest")).Value * 304.8 - 200) > 0.001) throw new Exception("New formula is incorrect");
            var update = (JObject)formula.DeepClone(); update["parameters"] = new JArray(formula["parameters"][1].DeepClone());
            update["parameters"][0]["mode"] = "update"; update["parameters"][0]["formula"] = "BaseTest * 3"; Apply(update);
            manager = doc.FamilyManager;
            if (Math.Abs(manager.CurrentType.AsDouble(manager.get_Parameter("DoubleTest")).Value * 304.8 - 300) > 0.001) throw new Exception("Existing formula was not updated");
            Document project = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                var family = doc.LoadFamily(project);
                using (var t = new Transaction(project, "Two independent table instances"))
                {
                    t.Start(); var level = Level.Create(project, 0);
                    var symbol = family.GetFamilySymbolIds().Select(project.GetElement).OfType<FamilySymbol>().First(); symbol.Activate(); project.Regenerate();
                    var a = project.Create.NewFamilyInstance(XYZ.Zero, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    var b = project.Create.NewFamilyInstance(XYZ.BasisX * 10, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    a.LookupParameter("Service de table").Set(0); b.LookupParameter("Service de table").Set(1); project.Regenerate();
                    if (a.LookupParameter("Service de table").AsInteger() != 0 || b.LookupParameter("Service de table").AsInteger() != 1) throw new Exception("Instance switches are not independent");
                    Commit(t);
                }
            }
            finally { project.Close(false); }
        }
        private static void Reject(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected configuration rejection"); }
    }
}
