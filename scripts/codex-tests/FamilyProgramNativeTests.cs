using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace BIMaestro.CodexTests
{
    internal static class FamilyProgramNativeTests
    {
        private const string Db = "Autodesk.Revit.DB.";
        private static JObject R(string id) => new JObject { ["ref"] = id };
        private static JObject New(string id, string type, params object[] args) => new JObject { ["op"] = "new", ["id"] = id, ["type"] = Db + type, ["args"] = new JArray(args) };
        private static JObject Call(string id, string target, string member, params object[] args) => new JObject { ["op"] = "call", ["id"] = id, ["target"] = R(target), ["member"] = member, ["args"] = new JArray(args) };
        private static JObject Static(string id, string type, string member, params object[] args) => new JObject { ["op"] = "static", ["id"] = id, ["type"] = Db + type, ["member"] = member, ["args"] = new JArray(args) };
        private static JObject Get(string id, string type, string member) => new JObject { ["op"] = "get", ["id"] = id, ["type"] = Db + type, ["member"] = member };
        private static JObject Mm(string id, double value) => new JObject { ["op"] = "mm", ["id"] = id, ["value"] = value };
        private static JObject Point(string id, double x, double y, double z) => new JObject { ["op"] = "xyz_mm", ["id"] = id, ["args"] = new JArray(x,y,z) };
        private static JObject SetInteger(string id, string parameter, int value)
        {
            var step = Call(id, "manager", "Set", R(parameter), value);
            step["signature"] = new JArray(Db + "FamilyParameter", "System.Int32"); return step;
        }
        private static JArray Axes() => new JArray(Get("zero", "XYZ", "Zero"), Get("x", "XYZ", "BasisX"), Get("y", "XYZ", "BasisY"), Get("z", "XYZ", "BasisZ"));
        private static JObject Program()
        {
            var child = Axes();
            child.Add(Static("plane", "Plane", "CreateByNormalAndOrigin", R("z"), R("zero")));
            child.Add(Static("work", "SketchPlane", "Create", R("doc"), R("plane")));
            // Native hollow glass: two circular loops, not an opaque cylinder.
            foreach (var radius in new[] { new { id = "outer", mm = 45.0 }, new { id = "inner", mm = 42.0 } })
            {
                child.Add(Mm(radius.id + "Radius", radius.mm));
                child.Add(New(radius.id, "CurveArray"));
                child.Add(Static(radius.id + "A", "Arc", "Create", R("zero"), R(radius.id + "Radius"), 0, Math.PI, R("x"), R("y")));
                child.Add(Static(radius.id + "B", "Arc", "Create", R("zero"), R(radius.id + "Radius"), Math.PI, 2 * Math.PI, R("x"), R("y")));
                child.Add(Call("appendA", radius.id, "Append", R(radius.id + "A")));
                child.Add(Call("appendB", radius.id, "Append", R(radius.id + "B")));
            }
            child.Add(New("loops", "CurveArrArray"));
            child.Add(Call("appendOuter", "loops", "Append", R("outer")));
            child.Add(Call("appendInner", "loops", "Append", R("inner")));
            child.Add(Mm("height", 100)); child.Add(Call("glass", "factory", "NewExtrusion", true, R("loops"), R("work"), R("height")));
            child.Add(New("bottomLoops", "CurveArrArray")); child.Add(Call("appendBottom", "bottomLoops", "Append", R("outer")));
            child.Add(Mm("bottomHeight", 3)); child.Add(Call("bottom", "factory", "NewExtrusion", true, R("bottomLoops"), R("work"), R("bottomHeight")));
            // Dish with a raised lip, modeled as a native revolution of a radial profile.
            child.Add(New("dishProfile", "CurveArray"));
            var vertices = new[] { new[] {0.0,0.0}, new[] {120.0,0.0}, new[] {125.0,15.0}, new[] {122.0,15.0}, new[] {117.0,3.0}, new[] {0.0,3.0} };
            for (int i=0;i<vertices.Length;i++) child.Add(Point("p"+i, vertices[i][0], 0, vertices[i][1]));
            for (int i=0;i<vertices.Length;i++)
            { child.Add(Static("edge", "Line", "CreateBound", R("p"+i), R("p"+((i+1)%vertices.Length)))); child.Add(Call("appendEdge", "dishProfile", "Append", R("edge"))); }
            child.Add(New("dishLoops", "CurveArrArray")); child.Add(Call("appendDish", "dishLoops", "Append", R("dishProfile")));
            child.Add(Static("dishPlane", "Plane", "CreateByNormalAndOrigin", R("y"), R("zero")));
            child.Add(Static("dishWork", "SketchPlane", "Create", R("doc"), R("dishPlane")));
            child.Add(Static("axis", "Line", "CreateBound", R("zero"), R("z")));
            child.Add(Call("dish", "factory", "NewRevolution", true, R("dishLoops"), R("dishWork"), R("axis"), 0, 2*Math.PI));
            child.Add(new JObject { ["op"]="get", ["id"]="glassId", ["target"]=R("glass"), ["member"]="Id" });
            child.Add(new JObject { ["op"]="get", ["id"]="bottomId", ["target"]=R("bottom"), ["member"]="Id" });
            child.Add(Point("glassOffset", 190, 0, 0));
            child.Add(Static("moveGlass", "ElementTransformUtils", "MoveElement", R("doc"), R("glassId"), R("glassOffset")));
            child.Add(Static("moveBottom", "ElementTransformUtils", "MoveElement", R("doc"), R("bottomId"), R("glassOffset")));
            var steps = new JArray(new JObject { ["op"]="nested", ["id"]="service", ["name"]="Service_detaille_programme", ["hosting"]="free", ["steps"]=child });
            foreach (var step in Axes()) steps.Add(step);
            steps.Add(Call("symbolIds", "service", "GetFamilySymbolIds"));
            steps.Add(new JObject { ["op"]="item", ["id"]="symbolId", ["target"]=R("symbolIds"), ["index"]=0 });
            steps.Add(Call("symbol", "doc", "GetElement", R("symbolId"))); steps.Add(Call("activate", "symbol", "Activate")); steps.Add(Call("regen", "doc", "Regenerate"));
            steps.Add(new JObject { ["op"]="enum", ["id"]="structural", ["type"]=Db+"Structure.StructuralType", ["value"]="NonStructural" });
            steps.Add(new JObject { ["op"]="enum", ["id"]="anchor", ["type"]=Db+"ArrayAnchorMember", ["value"]="Second" });
            steps.Add(new JObject { ["op"]="typeof", ["id"]="viewType", ["type"]=Db+"ViewPlan" });
            steps.Add(New("views", "FilteredElementCollector", R("doc"))); steps.Add(Call("plans", "views", "OfClass", R("viewType")));
            steps.Add(new JObject { ["op"]="item", ["id"]="plan", ["target"]=R("plans"), ["index"]=0 });
            steps.Add(Get("group", "GroupTypeId", "Geometry")); steps.Add(Get("integerType", "SpecTypeId+Int", "Integer"));
            steps.Add(Call("count", "manager", "AddParameter", "NombreServices", R("group"), R("integerType"), false)); steps.Add(SetInteger("initialCount", "count", 3));
            steps.Add(Point("pitch", 900, 0, 0));
            foreach (string side in new[] { "left", "right" })
            {
                steps.Add(Point(side+"Origin", 0, side=="left"?-450:450, 750));
                steps.Add(Call(side, "factory", "NewFamilyInstance", R(side+"Origin"), R("symbol"), R("structural")));
                steps.Add(new JObject { ["op"]="get", ["id"]=side+"Id", ["target"]=R(side), ["member"]="Id" });
                steps.Add(Static(side+"Array", "LinearArray", "Create", R("doc"), R("plan"), R(side+"Id"), 3, R("pitch"), R("anchor")));
                steps.Add(new JObject { ["op"]="set", ["target"]=R(side+"Array"), ["member"]="Label", ["value"]=R("count") });
            }
            return new JObject { ["steps"]=steps, ["outputs"]=new JArray("service","leftArray","rightArray") };
        }
        internal static object Run(UIApplication ui)
        {
            var doc=ui.Application.NewFamilyDocument(CodexFamilyBuilder.FindTemplate(ui,"free"));
            try
            {
                using(var t=new Transaction(doc,"Initial manual family")) { t.Start(); doc.FamilyManager.NewType("Manuel"); t.Commit(); }
                string[] Ids() => new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e=>e.UniqueId)
                    .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().Select(e=>e.UniqueId)).OrderBy(s=>s).ToArray();
                var program=Program(); var before=Ids();
                var args=new JObject { ["document_key"]=doc.OwnerFamily.UniqueId,["description"]="Service détaillé en deux réseaux au pas de 900 mm",["validate_only"]=true,["program_json"]=program.ToString(Newtonsoft.Json.Formatting.None) };
                JObject Execute() => JObject.FromObject(CodexFamilyProgram.Run(ui,doc,args,(a,b)=>true));
                var validation=Execute(); if((bool)validation["applied"] || !before.SequenceEqual(Ids())) throw new Exception("Dry run changed the family");
                args["validate_only"]=false; var applied=Execute();
                if(!(bool)applied["applied"]) throw new Exception("Program did not apply");
                var family=(Family)doc.GetElement((string)applied["outputs"]["service"]["unique_id"]);
                int Count() => new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Count(i=>i.Symbol.Family.Id==family.Id);
                if(Count()!=6) throw new Exception("Expected six detailed nested services, got "+Count());
                var inspection=doc.EditFamily(family);
                try
                {
                    var extrusions=new FilteredElementCollector(inspection).OfClass(typeof(Extrusion)).Cast<Extrusion>().ToArray();
                    if(!extrusions.Any(e=>e.Sketch.Profile.Size==2) || new FilteredElementCollector(inspection).OfClass(typeof(Revolution)).GetElementCount()!=1) throw new Exception("Hollow glass or revolved dish missing");
                }
                finally { inspection.Close(false); }
                using(var t=new Transaction(doc,"Verify native array flex"))
                { t.Start(); doc.FamilyManager.Set(doc.FamilyManager.get_Parameter("NombreServices"),4); doc.Regenerate(); if(Count()!=8) throw new Exception("Detailed array did not flex to eight services"); t.RollBack(); }
                // Edit only the hollow glass inside the already loaded service, using API discovery primitives.
                string familyUniqueId = family.UniqueId;
                var instanceKeys = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(i=>i.Symbol.Family.Id==family.Id).Select(i=>i.UniqueId).OrderBy(x=>x).ToArray();
                var body = new JArray(
                    new JObject { ["op"]="typeof",["id"]="extrusionType",["type"]=Db+"Extrusion" },
                    New("collector","FilteredElementCollector",R("doc")), Call("extrusions","collector","OfClass",R("extrusionType")),
                    new JObject { ["op"]="enum",["id"]="endParameter",["type"]=Db+"BuiltInParameter",["value"]="EXTRUSION_END_PARAM" },
                    new JObject { ["op"]="foreach",["target"]=R("extrusions"),["var"]="extrusion",["steps"]=new JArray(
                        new JObject { ["op"]="get",["id"]="sketch",["target"]=R("extrusion"),["member"]="Sketch" },
                        new JObject { ["op"]="get",["id"]="profile",["target"]=R("sketch"),["member"]="Profile" },
                        new JObject { ["op"]="get",["id"]="loopCount",["target"]=R("profile"),["member"]="Size" },
                        new JObject { ["op"]="math",["id"]="hollow",["member"]="equal",["args"]=new JArray(R("loopCount"),2) },
                        new JObject { ["op"]="if",["value"]=R("hollow"),["then"]=new JArray(Call("end","extrusion","get_Parameter",R("endParameter")),Mm("newHeight",120),Call("setHeight","end","Set",R("newHeight"))) }
                    ) });
                var edit = new JObject { ["steps"]=new JArray(Call("existing","doc","GetElement",familyUniqueId),
                    new JObject { ["op"]="nested",["id"]="updated",["source_family"]=R("existing"),["overwrite_parameter_values"]=false,["steps"]=body }),["outputs"]=new JArray("updated") };
                args["program_json"]=edit.ToString(Newtonsoft.Json.Formatting.None); var updated=Execute();
                family=(Family)doc.GetElement((string)updated["outputs"]["updated"]["unique_id"]);
                if(Count()!=6 || !instanceKeys.SequenceEqual(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(i=>i.Symbol.Family.Id==family.Id).Select(i=>i.UniqueId).OrderBy(x=>x))) throw new Exception("Nested edit lost placed instances; count="+Count());
                inspection=doc.EditFamily(family);
                try { var glass=new FilteredElementCollector(inspection).OfClass(typeof(Extrusion)).Cast<Extrusion>().Single(e=>e.Sketch.Profile.Size==2);
                    if(Math.Abs(glass.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM).AsDouble()*304.8-120)>0.001)throw new Exception("Nested glass height edit was not reloaded"); }
                finally { inspection.Close(false); }
                var loops = JObject.Parse("{steps:[{op:'range',id:'indices',args:[0,3,1]},{op:'list',id:'values',type:'System.Double',items:[]},{op:'foreach',target:{ref:'indices'},var:'i',steps:[{op:'math',id:'double',member:'multiply',args:[{ref:'i'},2]},{op:'call',id:'append',target:{ref:'values'},member:'Add',args:[{ref:'double'}]}]}],outputs:['values']}");
                args["program_json"]=loops.ToString(Newtonsoft.Json.Formatting.None);
                if(!Execute()["outputs"]["values"].Values<double>().SequenceEqual(new[]{0.0,2.0,4.0}))throw new Exception("Loop/math/list execution failed");
                var snapshot=Ids();
                foreach(var invalid in new[] {
                    new JObject{["op"]="new",["type"]="System.IO.FileInfo",["args"]=new JArray("C:/forbidden")},
                    new JObject{["op"]="call",["target"]=R("doc"),["member"]="SaveAs",["args"]=new JArray("C:/forbidden.rfa")},
                    new JObject{["op"]="get",["target"]=R("doc"),["member"]="Application"},
                    new JObject{["op"]="range",["args"]=new JArray(0,201,1)},
                    new JObject{["op"]="math",["member"]="divide",["args"]=new JArray(1,0)},
                    new JObject{["op"]="assert",["value"]=false,["message"]="expected rollback"} })
                {
                    // Make a real change before the deliberately failing step.
                    var bad=new JObject{["steps"]=new JArray(Call("temporaryType","manager","NewType","MustRollback"),invalid),["outputs"]=new JArray()};
                    args["program_json"]=bad.ToString(Newtonsoft.Json.Formatting.None);
                    bool rejected=false;try{Execute();}catch(InvalidOperationException){rejected=true;}
                    if(!rejected || !snapshot.SequenceEqual(Ids()) || doc.FamilyManager.Types.Cast<FamilyType>().Any(t=>t.Name=="MustRollback")) throw new Exception("Forbidden/failed program was not rolled back");
                }
                var api=JObject.FromObject(CodexFamilyProgram.Api(JObject.Parse("{type_name:'Autodesk.Revit.DB.LinearArray',member_name:'Create',offset:0,limit:100}")));
                if((int)api["total"]<1) throw new Exception("API discovery missing native array");
                string folder=Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                File.WriteAllText(Path.Combine(folder,"service-program.json"),program.ToString());
                File.WriteAllText(Path.Combine(folder,"edit-nested-program.json"),edit.ToString());
                return new { passed=true, hollow_glass=true, revolved_dish=true, detailed_nested_families=true, two_native_arrays_at_900mm=true,
                    array_count_flex_6_to_8=true, dry_run_restores_all_elements=true, rollback_after_mutation=true, system_and_document_access_rejected=true,
                    nested_geometry_edited_and_reloaded=true, instance_identity_preserved=true, loops_conditions_and_math=true,
                    finite_values_and_iteration_limits=true, api_discovery=true, applied_report=applied };
            }
            finally { doc.Close(false); }
        }
    }
}
