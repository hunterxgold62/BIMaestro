using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Modification;

namespace Analyse.Tests
{
    public sealed partial class NativeValidation
    {
        private void ReservationFixture(UIApplication ui)
        {
            Test("Autoréservation creates a sized opening and prevents duplicate creation", () =>
            {
                var root = Directory.Exists(ui.Application.FamilyTemplatePath) ? ui.Application.FamilyTemplatePath
                    : "C:/ProgramData/Autodesk/RVT " + ui.Application.VersionNumber + "/Family Templates";
                var template = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                    .First(p => p.IndexOf("Metric Generic Model.rft", StringComparison.OrdinalIgnoreCase) >= 0
                        || Path.GetFileName(p).Equals("Modèle générique métrique.rft", StringComparison.OrdinalIgnoreCase));
                var familyDoc = ui.Application.NewFamilyDocument(template);
                var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
                try
                {
                    using (var t = new Transaction(familyDoc, "Create mapped opening fixture"))
                    {
                        t.Start();
                        foreach (var name in new[] { "TestLength", "TestHeight", "TestWidth", "TestDepth", "TestDiameter" })
                            familyDoc.FamilyManager.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, true);
                        familyDoc.FamilyManager.NewType("Test opening"); t.Commit();
                    }
                    var family = familyDoc.LoadFamily(doc);
                    var symbol = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().First();
                    Wall wall; Floor floor; Pipe pipe, floorPipe; Level level;
                    using (var t = new Transaction(doc, "Wall and floor traversals"))
                    {
                        t.Start(); level = Level.Create(doc, 0);
                        wall = Wall.Create(doc, Line.CreateBound(new XYZ(0,-6,0), new XYZ(0,6,0)), level.Id, false);
                        var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First();
                        var outline = new CurveLoop(); var corners = new[] { new XYZ(7,-3,0),new XYZ(13,-3,0),new XYZ(13,3,0),new XYZ(7,3,0) };
                        for (int i=0; i<4; i++) outline.Append(Line.CreateBound(corners[i], corners[(i+1)%4]));
                        floor = Floor.Create(doc, new[] { outline }, floorType.Id, level.Id);
                        var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                        var system = PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "Opening test");
                        pipe = Pipe.Create(doc, system.Id, pipeType.Id, level.Id, new XYZ(-3,0,2), new XYZ(3,0,2));
                        floorPipe = Pipe.Create(doc, system.Id, pipeType.Id, level.Id, new XYZ(10,0,-2), new XYZ(10,0,2));
                        pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8);
                        floorPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8); t.Commit();
                    }
                    var profile = new ProfileConfig { FamilyName = family.Name, TypeName = symbol.Name,
                        ParamLength = "TestLength", ParamHeight = "TestHeight", ParamWidth = "TestWidth", ParamDepth = "TestDepth", ParamDiameter = "TestDiameter", VerticalPlacementMode = VerticalPlacementMode.Center };
                    var config = new ReservationAutoV3Config { WallRect = profile, FloorRect = profile, FloorCirc = profile,
                        LastShapeTarget = "Rectangulaire", DefaultNormeEnabled = true };
                    var personal = new ReservationAutoV3PersoConfig();
                    var count = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount();
                    var created = ReservationAutoV3Command.CreateForClash(doc, pipe, wall, null, config, personal);
                    Check(created != null && created.Symbol.Id == symbol.Id && Math.Abs(((LocationPoint)created.Location).Point.X) < .1, "Opening uses the wrong family or location.");
                    Check(created.LookupParameter("TestLength").AsDouble() >= 100/304.8 && created.LookupParameter("TestDepth").AsDouble() > 0, "Opening not sized.");
                    bool duplicateRefused = false;
                    try { ReservationAutoV3Command.CreateForClash(doc, pipe, wall, null, config, personal); }
                    catch (InvalidOperationException ex) { duplicateRefused = ex.Message.Contains("déjà"); }
                    Check(duplicateRefused && new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount() == count+1, "Duplicate reservation created.");
                    Test("reservation mapping failure rolls back the whole model change", () =>
                    {
                        string original = profile.ParamWidth; profile.ParamWidth = "MissingMappedParameter";
                        try
                        {
                            bool refused = false;
                            try { ReservationAutoV3Command.CreateForClash(doc, floorPipe, floor, null, config, personal); }
                            catch (InvalidOperationException ex) { refused = ex.Message.Contains("paramètre"); }
                            Check(refused && new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount() == count+1, "Invalid mapping retained a model element.");
                        }
                        finally { profile.ParamWidth = original; }
                    });
                    Test("floor traversal creates an opening with configured shape and personal profile", () =>
                    {
                        personal.FloorCircUnhosted = profile; config.LastShapeTarget = "Circulaire";
                        config.LastShapeOptionLabel = "Circulaire - Ma famille sans hôte";
                        var opening = ReservationAutoV3Command.CreateForClash(doc, floorPipe, floor, null, config, personal);
                        Check(opening.LookupParameter("TestDiameter").AsDouble() >= 100/304.8 && opening.LookupParameter("TestDepth").AsDouble() > 0, "Circular floor mapping lost.");
                    });
                    Test("non intersecting geometry is refused without creating an opening", () =>
                    {
                        bool refused = false;
                        try { ReservationAutoV3Command.CreateForClash(doc, pipe, floor, null, config, personal); }
                        catch (InvalidOperationException ex) { refused = ex.Message.Contains("traversée"); }
                        Check(refused && new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount() == count+2, "Unrelated source produced an opening.");
                    });
                    var scan = Scan(new SmartScanSetup { Document = doc }, new SmartScanOptions { Ducts=false, CableTrays=false,Conduits=false,LinkedClashes=false });
                    Test("duct traversal uses rectangular dimensions from Autoréservation", () =>
                    {
                        Autodesk.Revit.DB.Mechanical.Duct duct;
                        using (var t = new Transaction(doc,"Duct opening fixture"))
                        {
                            t.Start(); var type=new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Mechanical.DuctType)).Cast<Autodesk.Revit.DB.Mechanical.DuctType>().First();
                            var system=Autodesk.Revit.DB.Mechanical.MechanicalSystemType.Create(doc,MEPSystemClassification.SupplyAir,"Opening air");
                            duct=Autodesk.Revit.DB.Mechanical.Duct.Create(doc,system.Id,type.Id,level.Id,new XYZ(-3,4,2),new XYZ(3,4,2)); t.Commit();
                        }
                        config.LastShapeTarget="Rectangulaire"; config.LastShapeOptionLabel="";
                        var opening=ReservationAutoV3Command.CreateForClash(doc,duct,wall,null,config,personal);
                        Check(opening.LookupParameter("TestLength").AsDouble()>0 && opening.LookupParameter("TestHeight").AsDouble()>0,"Duct dimensions lost.");
                    });
                    var issue = scan.Issues.First(i => i.ElementUniqueId == pipe.UniqueId && i.RelatedUniqueId == wall.UniqueId);
                    Check(issue.CanCreateReservation && !issue.IsApproximate, "Confirmed pipe/wall issue lacks the opening action.");
                    var inspector = new SmartIssueInspector(issue, _=>{}, (i,s,c)=>true, _=>{}, (i,n)=>{}, ()=>{}, _=>{});
                    try
                    {
                        inspector.Show(); inspector.SetActions(issue,false,false,false); Render(inspector,"inspection-opening-actions-480.png",480,500);
                        inspector.SetActions(issue,true,false,true); Render(inspector,"inspection-context-actions-650.png",650,760);
                        Check(Descendants(inspector).OfType<System.Windows.Controls.Button>().First(b => (string)b.Content == "Créer la réservation").IsEnabled == false, "Stale opening action remains enabled.");
                    }
                    finally { inspector.Close(); }
                    File.WriteAllText(Path.Combine(_folder,"reservation-fixture-summary.txt"), "Wall and floor: correct sizes, duplicate refusal, rollback, personal profile, stale UI.");
                    LinkedReservationFixture(ui, doc, profile);
                    Test("created generic geometry can request a wall opening", () =>
                    {
                        DirectShape element;
                        using (var t = new Transaction(doc,"Generic element in wall"))
                        { t.Start(); element=Shape(doc,Box(new XYZ(-.5,-2.5,1.5),new XYZ(.5,-1.5,2.5))); t.Commit(); }
                        config.LastShapeTarget="Rectangulaire"; config.LastShapeOptionLabel="";
                        var detected=Scan(new SmartScanSetup { Document=doc },new SmartScanOptions { Pipes=false,Ducts=false,CableTrays=false,Conduits=false,GenericModels=true,LinkedClashes=false });
                        Check(detected.Issues.Any(i=>i.ElementUniqueId==element.UniqueId && i.RelatedUniqueId==wall.UniqueId && i.CanCreateReservation && !i.IsApproximate),"Generic wall intersection lacks an opening action.");
                        var opening=ReservationAutoV3Command.CreateForClash(doc,element,wall,null,config,personal);
                        Check(opening.LookupParameter("TestLength").AsDouble()>=1 && opening.LookupParameter("TestHeight").AsDouble()>=1,"Generic footprint is not enclosed.");
                    });
                    Test("air terminal family is analysed and receives a circular opening enclosing its geometry", () =>
                    {
                        var sourceFamily=ui.Application.NewFamilyDocument(template);
                        Family loadedSource;
                        try
                        {
                            using(var t=new Transaction(sourceFamily,"Air terminal solid"))
                            {
                                t.Start(); sourceFamily.OwnerFamily.FamilyCategory=Category.GetCategory(sourceFamily,BuiltInCategory.OST_DuctTerminal);
                                var plane=SketchPlane.Create(sourceFamily,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));
                                var profileLoop=new CurveArray(); var pts=new[] { new XYZ(-.5,-.5,0),new XYZ(.5,-.5,0),new XYZ(.5,.5,0),new XYZ(-.5,.5,0) };
                                for(int i=0;i<4;i++)profileLoop.Append(Line.CreateBound(pts[i],pts[(i+1)%4]));
                                var loops=new CurveArrArray();loops.Append(profileLoop);sourceFamily.FamilyCreate.NewExtrusion(true,loops,plane,1); t.Commit();
                            }
                            loadedSource=sourceFamily.LoadFamily(doc);
                        }
                        finally { sourceFamily.Close(false); }
                        var sourceSymbol=loadedSource.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().First();
                        FamilyInstance element;
                        using(var t=new Transaction(doc,"Place air terminal across floor"))
                        { t.Start();sourceSymbol.Activate();doc.Regenerate();element=doc.Create.NewFamilyInstance(new XYZ(11,1,-.2),sourceSymbol,level,Autodesk.Revit.DB.Structure.StructuralType.NonStructural);t.Commit(); }
                        var detected=Scan(new SmartScanSetup { Document=doc },new SmartScanOptions { Pipes=false,Ducts=false,CableTrays=false,Conduits=false,Equipment=true,LinkedClashes=false });
                        Check(detected.Issues.Any(i=>i.ElementUniqueId==element.UniqueId && i.RelatedUniqueId==floor.UniqueId && i.CanCreateReservation && !i.IsApproximate),"Air terminal category is missing.");
                        config.LastShapeTarget="Circulaire";config.LastShapeOptionLabel="";
                        var opening=ReservationAutoV3Command.CreateForClash(doc,element,floor,null,config,personal);
                        Check(opening.LookupParameter("TestDiameter").AsDouble()>=Math.Sqrt(2),"Circular opening does not enclose the rectangular terminal footprint.");
                    });
                }
                finally { doc.Close(false); familyDoc.Close(false); }
            });
            RealOpeningFamilyFixture(ui);
        }
        private void LinkedReservationFixture(UIApplication ui, Document doc, ProfileConfig profile)
        {
            Test("linked wall opening uses its rotated transform and is created only in the host model", () =>
            {
                var linkedDoc = ui.Application.NewProjectDocument(UnitSystem.Metric);
                string path = Path.Combine(_folder,"reservation-linked-wall.rvt"); string wallId;
                try
                {
                    using (var t = new Transaction(linkedDoc,"Linked wall"))
                    { t.Start(); var level = Level.Create(linkedDoc,0); wallId = Wall.Create(linkedDoc,Line.CreateBound(new XYZ(0,-6,0),new XYZ(0,6,0)),level.Id,false).UniqueId; t.Commit(); }
                    linkedDoc.SaveAs(path,new SaveAsOptions { OverwriteExistingFile=true });
                }
                finally { linkedDoc.Close(false); }
                RevitLinkInstance link; Pipe pipe;
                using (var t = new Transaction(doc,"Load reservation link"))
                {
                    t.Start(); var loaded=RevitLinkType.Create(doc,ModelPathUtils.ConvertUserVisiblePathToModelPath(path),new RevitLinkOptions(false));
                    link=RevitLinkInstance.Create(doc,loaded.ElementId);
                    ElementTransformUtils.RotateElement(doc,link.Id,Line.CreateBound(XYZ.Zero,XYZ.BasisZ),Math.PI/2);
                    ElementTransformUtils.MoveElement(doc,link.Id,new XYZ(20,30,0));
                    var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                    var type=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                    var system=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().First();
                    pipe=Pipe.Create(doc,system.Id,type.Id,level.Id,new XYZ(20,27,2),new XYZ(20,33,2));
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8); t.Commit();
                }
                var linkedHost=link.GetLinkDocument().GetElement(wallId);
                var config=new ReservationAutoV3Config { WallRect=profile,LastShapeTarget="Rectangulaire",DefaultNormeEnabled=true };
                int linkedCount=new FilteredElementCollector(link.GetLinkDocument()).OfClass(typeof(FamilyInstance)).GetElementCount();
                var opening=ReservationAutoV3Command.CreateForClash(doc,pipe,linkedHost,link,config,new ReservationAutoV3PersoConfig());
                var point=((LocationPoint)opening.Location).Point;
                Check(point.DistanceTo(new XYZ(20,30,2))<.5 && opening.Document.Equals(doc),"Linked placement transformed incorrectly.");
                Check(new FilteredElementCollector(link.GetLinkDocument()).OfClass(typeof(FamilyInstance)).GetElementCount()==linkedCount,"Linked model was modified.");
            });
        }
        private void RealOpeningFamilyFixture(UIApplication ui)
        {
            Test("real CML opening family uses its mapped dimensions at a wall traversal", () =>
            {
                var doc=ui.Application.NewProjectDocument(UnitSystem.Metric);
                try
                {
                    Family family; Wall wall; Pipe pipe;
                    using(var t=new Transaction(doc,"Real opening fixture"))
                    {
                        t.Start();
                        var path=Path.GetFullPath(Path.Combine(_folder,"../../..","Demo/Maquette/Familles/CML_Réservation rectangulaire murale.rfa"));
                        Check(doc.LoadFamily(path,out family),"Real opening family did not load.");
                        var level=Level.Create(doc,0);
                        wall=Wall.Create(doc,Line.CreateBound(new XYZ(0,-6,0),new XYZ(0,6,0)),level.Id,false);
                        var type=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                        var system=PipingSystemType.Create(doc,MEPSystemClassification.DomesticColdWater,"Real opening test");
                        pipe=Pipe.Create(doc,system.Id,type.Id,level.Id,new XYZ(-3,0,2),new XYZ(3,0,2));
                        pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8); t.Commit();
                    }
                    var edit=doc.EditFamily(family); string[] names;
                    try { names=edit.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p=>p.Definition.Name).ToArray(); }
                    finally { edit.Close(false); }
                    File.WriteAllText(Path.Combine(_folder,"cml-opening-parameters.json"),Newtonsoft.Json.JsonConvert.SerializeObject(names));
                    Func<string,string> map=word=>names.First(n=>n.Equals(word,StringComparison.OrdinalIgnoreCase)||n.Equals("COM_"+word,StringComparison.OrdinalIgnoreCase)
                        || word=="Longueur" && (n.Equals("Largeur",StringComparison.OrdinalIgnoreCase)||n.Equals("COM_Largeur",StringComparison.OrdinalIgnoreCase)));
                    var symbol=family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().First();
                    using (var probe = new Transaction(doc,"Inspect real family depth"))
                    {
                        probe.Start();symbol.Activate();doc.Regenerate();
                        var example = family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted
                            ? doc.Create.NewFamilyInstance(new XYZ(0,0,2),symbol,wall,doc.GetElement(wall.LevelId) as Level,Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                            : doc.Create.NewFamilyInstance(new XYZ(0,0,2),symbol,doc.GetElement(wall.LevelId) as Level,Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        doc.Regenerate(); var depth=example.LookupParameter("Profondeur"); var typeDepth=symbol.LookupParameter("Profondeur");
                        File.WriteAllText(Path.Combine(_folder,"cml-opening-depth.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new
                        { placement=family.FamilyPlacementType.ToString(), wallDepth=wall.Width, instance=depth==null?null:new { depth.IsReadOnly, storage=depth.StorageType.ToString(), value=depth.StorageType==StorageType.Double?depth.AsDouble():0, spec=depth.Definition.GetDataType().TypeId },
                            type=typeDepth==null?null:new { typeDepth.IsReadOnly, storage=typeDepth.StorageType.ToString(), value=typeDepth.StorageType==StorageType.Double?typeDepth.AsDouble():0, spec=typeDepth.Definition.GetDataType().TypeId } }));
                        probe.RollBack();
                    }
                    var profile=new ProfileConfig { FamilyName=family.Name,TypeName=symbol.Name,ParamLength=map("Longueur"),ParamHeight=map("Hauteur"),ParamDepth=map("Profondeur"),VerticalPlacementMode=VerticalPlacementMode.Center };
                    var config=new ReservationAutoV3Config { WallRect=profile,LastShapeTarget="Rectangulaire",DefaultNormeEnabled=true };
                    var opening=ReservationAutoV3Command.CreateForClash(doc,pipe,wall,null,config,new ReservationAutoV3PersoConfig());
                    Check(opening.get_BoundingBox(null)!=null && opening.Symbol.Family.Id==family.Id,"Real family has no geometry or wrong type.");
                    Check(opening.LookupParameter(profile.ParamLength).AsDouble()>=100/304.8 && opening.LookupParameter(profile.ParamHeight).AsDouble()>=100/304.8,"Real family not sized.");
                }
                finally { doc.Close(false); }
            });
        }
    }
}
