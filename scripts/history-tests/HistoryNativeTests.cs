using Analyse;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BIMaestro.HistoryTests
{
    // Run with the existing isolated launcher; never operate on an open user model.
    public sealed class HistoryNativeTests : IExternalApplication
    {
        private UIControlledApplication application;
        private string Output => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public Result OnStartup(UIControlledApplication app)
        { application = app; app.Idling += OnIdle; return Result.Succeeded; }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= OnIdle; return Result.Succeeded; }
        private void OnIdle(object sender, IdlingEventArgs args)
        {
            var marker = Path.Combine(Output, "launched-pid.txt");
            if (!File.Exists(marker)) return;
            if (File.ReadAllText(marker).Trim() != Process.GetCurrentProcess().Id.ToString())
            { application.Idling -= OnIdle; return; }
            application.Idling -= OnIdle;
            File.WriteAllText(Path.Combine(Output, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            var ui = (UIApplication)sender;
            try
            {
                Check(ui.Application.Documents.Size == 0, "Requires an empty test process");
                var results = File.Exists(Path.Combine(Output,"CML-source.rvt")) ? RunCml(ui) : Run(ui);
                File.WriteAllText(Path.Combine(Output, "result.json"), JsonConvert.SerializeObject(new { passed = !results.Any(r => r.StartsWith("FAILED")), tests = results }, Formatting.Indented));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Output, "error.txt"), ex.ToString()); }
            if (ui.Application.Documents.Size == 0) ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
        private List<string> Run(UIApplication ui)
        {
            var results = new List<string>();
            var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                var symbol = CreateFamily(ui, doc, "Metric Generic Model.rft");
                string freeSymbolId = symbol.UniqueId;
                var hostedSymbol = CreateFamily(ui, doc, "Metric Generic Model wall based.rft");
                var networkSymbol = CreateFamily(ui, doc, "Metric Generic Model.rft", true);
                string networkSymbolId = networkSymbol.UniqueId;
                using (var transaction = new Transaction(doc, "Historical reconstruction tests"))
                {
                    transaction.Start();
                    var level = Level.Create(doc, 7);
                    var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
                    var wall = Wall.Create(doc, Line.CreateBound(new XYZ(0,0,7), new XYZ(20,0,7)), wallType.Id, level.Id, 9, 2, true, false);
                    WallUtils.DisallowWallJoinAtEnd(wall, 0); WallUtils.DisallowWallJoinAtEnd(wall, 1);
                    wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(2);
                    doc.Regenerate();
                    Verify(doc, wall, "straight wall with offset and location line", results);
                    var curved = Wall.Create(doc, Arc.Create(new XYZ(30,0,7), new XYZ(50,0,7), new XYZ(40,5,7)), wallType.Id, level.Id, 12, 0, false, false);
                    WallUtils.DisallowWallJoinAtEnd(curved,0); WallUtils.DisallowWallJoinAtEnd(curved,1);
                    doc.Regenerate(); Verify(doc, curved, "curved wall", results);
                    var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t => !t.IsFoundationSlab);
                    var floor = Floor.Create(doc, new[] { Rectangle(0,30,20,50,7), Rectangle(5,35,10,40,7) }, floorType.Id, level.Id);
                    floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(1.25);
                    doc.Regenerate(); Verify(doc, floor, "floor with hole and offset", results);
                    symbol.Activate(); hostedSymbol.Activate(); doc.Regenerate();
                    var instance = doc.Create.NewFamilyInstance(new XYZ(3,70,10), symbol, level, StructuralType.NonStructural);
                    instance.LookupParameter("HistoryDepth").Set(4.0);
                    ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(new XYZ(3,70,10),new XYZ(3,70,11)),0.7);
                    string instanceUid = instance.UniqueId;
                    doc.Regenerate(); Verify(doc, instance, "rotated family with instance dimension", results);
                    instance = (FamilyInstance)doc.GetElement(instanceUid);
                    var host = Wall.Create(doc, Line.CreateBound(new XYZ(0,90,7),new XYZ(20,90,7)),wallType.Id,level.Id,10,0,false,false);
                    doc.Regenerate();
                    var hosted = doc.Create.NewFamilyInstance(new XYZ(7,90,9),hostedSymbol,host,level,StructuralType.NonStructural);
                    string hostedUid = hosted.UniqueId, hostUid = host.UniqueId;
                    doc.Regenerate(); Verify(doc,hosted,"wall hosted family",results);
                    hosted = (FamilyInstance)doc.GetElement(hostedUid);
                    host = (Wall)doc.GetElement(hostUid);
                    if(results.Any(r => r.StartsWith("FAILED"))) return results;

                    var recipe = ElementHistoryReconstruction.Capture(hosted);
                    doc.Delete(hosted.Id); doc.Delete(host.Id); doc.Regenerate();
                    VerifyFailure(doc,recipe,"missing host",results);
                    recipe = ElementHistoryReconstruction.Capture(instance);
                    recipe.Type = Guid.NewGuid().ToString() + "-00000001";
                    VerifyFailure(doc,recipe,"missing type",results);
                    recipe = ElementHistoryReconstruction.Capture(instance); recipe.Level = recipe.Type;
                    VerifyFailure(doc,recipe,"invalid level",results);
                    recipe = ElementHistoryReconstruction.Capture(instance); recipe.Version = 999;
                    VerifyFailure(doc,recipe,"unknown recipe version",results);
                    Check(ElementHistoryReconstruction.Reconstruct(doc, JObject.Parse("{Version:1}")) == null,"Malformed recipe accepted");
                    results.Add("malformed/legacy recipe fallback");

                    // UniqueId lookup must return the real element, then null after deletion.
                    var uid = instance.UniqueId;
                    Check(ElementHistoryReconstruction.FindOriginal(doc,uid)?.Id == instance.Id,"Original lookup failed");
                    doc.Delete(instance.Id);
                    Check(ElementHistoryReconstruction.FindOriginal(doc,uid) == null,"Deleted original found");
                    results.Add("original element lookup");
                    transaction.RollBack();
                }
                VerifyPermanent(ui, ref doc, symbol.UniqueId, hostedSymbol.UniqueId, results);
                VerifyNetworks(ui, ref doc, results);
                VerifyNetworkFamily(doc, networkSymbolId, results);
                VerifyMirroredFamily(doc, freeSymbolId, results);
                var filtered = ElementHistoryRestoration.Restore(doc, new[] {
                    new HistoryRestoreRequest { SourceUniqueId="excluded-a", Label="CML_Calorifuge [1]", Category="Modèles génériques" },
                    new HistoryRestoreRequest { SourceUniqueId="excluded-b", Label="Isolation [2]", Category="Isolants de canalisation" },
                    new HistoryRestoreRequest { SourceUniqueId="excluded-c", Label="Insulation [3]", Category="Duct Insulations" },
                    new HistoryRestoreRequest { SourceUniqueId="visible-error", Label="CML_Piquage acier [4]", Category="Raccords de canalisation", CaptureFailure="Placement non pris en charge" }
                });
                Check(filtered.Items.Count==1 && filtered.Failed==1 && filtered.Items[0].SourceUniqueId=="visible-error"
                    && filtered.Items[0].Detail=="Placement non pris en charge","Insulation must be excluded without hiding pipe/fitting failures");
                results.Add("native and named calorifuge excluded from all counts; other errors and capture diagnostics preserved");
            }
            finally { doc.Close(false); }
            return results;
        }

        private List<string> RunCml(UIApplication ui)
        {
            var path=Path.Combine(Output,"CML-source.rvt");
            var options=new OpenOptions();
            using(var info=BasicFileInfo.Extract(path))
                if(info.IsWorkshared) options.DetachFromCentralOption=DetachFromCentralOption.DetachAndPreserveWorksets;
            var doc=ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path),options);
            var results=new List<string>();
            try
            {
                var targetsPath=Path.Combine(Output,"CML-targets.json");
                var targets=File.Exists(targetsPath) ? JsonConvert.DeserializeObject<string[]>(File.ReadAllText(targetsPath))
                    : new[]{"CML_Coude acier", "CML_Réduction acier"};
                var reportedIds=new[]{903986L,903766L,877948L,770610L,770608L,744493L};
                var fixtures=new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(f=>targets.Contains(f.Symbol.Family.Name))
                    .OrderByDescending(f=>reportedIds.Contains(f.Id.GetIdLongValue()))
                    .GroupBy(f=>File.Exists(targetsPath) ? f.UniqueId : f.Symbol.Family.Name+":"+string.Join("/",ElementHistoryNetwork.Ports(f).Select(c=>Math.Round(c.Radius,4)+":"+Math.Round(c.Angle,4))))
                    .Select(g=>g.First()).Take(10).ToList();
                Check(fixtures.Count>=2,"CML fixture families missing in isolated source copy");
                var fixtureIds=fixtures.Select(f=>f.UniqueId).ToList();
                if(File.Exists(targetsPath)) Check(reportedIds.All(id=>fixtures.Any(f=>f.Id.GetIdLongValue()==id)),"A reported fitting is missing from the isolated copy");
                foreach(var fixtureUid in fixtureIds)
                foreach(var mode in new[]{"current", "legacy", "repair"})
                {
                    // Rollback can invalidate wrappers of connected neighbors too.
                    var original=(FamilyInstance)doc.GetElement(fixtureUid);
                    var uid=fixtureUid;
                    var request=new HistoryRestoreRequest { SourceUniqueId=uid,Label=original.Symbol.Family.Name+" ["+original.Id.GetIdLongValue()+"]",
                        Recipe=ElementHistoryReconstruction.Capture(original) };
                    Check(request.Recipe!=null,"Missing CML capture: "+request.Label);
                    if(mode=="current") File.AppendAllText(Path.Combine(Output,"cml-parameters.jsonl"),JsonConvert.SerializeObject(new {
                        request.Label, Part=(original.MEPModel as MechanicalFitting)?.PartType.ToString(),
                        Parameters=original.Parameters.Cast<Parameter>().Where(p=>p.StorageType==StorageType.Double).Select(p=>new {
                            Name=p.Definition.Name, Id=p.Id.GetIdLongValue(), ReadOnly=p.IsReadOnly, Value=p.AsDouble() }) })+Environment.NewLine);
                    request.Label += " ("+mode+")";
                    if(mode!="current")
                    {
                        request.Recipe.Ports=null;
                        // Older captures omitted solver-controlled dimensions. Do
                        // not accidentally let new parameter capture mask that gap.
                        request.Recipe.Parameters.RemoveAll(p=>p.BuiltIn==0 && p.Storage==(int)StorageType.Double
                            && original.Parameters.Cast<Parameter>().Any(source=>source.IsReadOnly
                                && (p.Definition!=null ? source.Id==doc.GetElement(p.Definition)?.Id
                                    : p.Shared!=null ? source.IsShared && source.GUID.ToString()==p.Shared : source.Definition.Name==p.Name)));
                        foreach(var link in request.Recipe.Connections)
                        { link.Port.Id=null; link.Port.Angle=null; link.PeerPort.Id=null; link.PeerPort.Angle=null; }
                    }
                    File.AppendAllText(Path.Combine(Output,"cml-recipes.jsonl"),JsonConvert.SerializeObject(request)+Environment.NewLine);
                    using(var group=new TransactionGroup(doc,"CML isolated restoration verification"))
                    {
                        group.Start();
                        var curvesBefore=new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Select(e=>e.UniqueId).OrderBy(s=>s).ToList();
                        var curveClasses=new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).ToDictionary(e=>e.UniqueId,e=>e.GetType().Name+" / "+e.Name);
                        using(var tx=new Transaction(doc,"Delete CML test fitting"))
                        { tx.Start(); tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true)); doc.Delete(original.Id); Check(tx.Commit()==TransactionStatus.Committed,"CML deletion failed"); }
                        var deletedWithFixture=curvesBefore.Where(id=>doc.GetElement(id)==null).ToList();
                        File.AppendAllText(Path.Combine(Output,"fixture-deletions.jsonl"),JsonConvert.SerializeObject(deletedWithFixture.Select(id=>new{Id=id,Class=curveClasses[id]}))+Environment.NewLine);
                        curvesBefore=curvesBefore.Except(deletedWithFixture).ToList();
                        var curveGeometry=curvesBefore.Select(id=>doc.GetElement(id)).Where(e=>e.Location is LocationCurve)
                            .ToDictionary(e=>e.UniqueId,e=>new[]{((LocationCurve)e.Location).Curve.GetEndPoint(0),((LocationCurve)e.Location).Curve.GetEndPoint(1)});
                        if(mode=="repair")
                        {
                            var bad=JsonConvert.DeserializeObject<HistoryRecipe>(JsonConvert.SerializeObject(request.Recipe));
                            bad.Ports=new List<HistoryPort>(); bad.Connections=new List<HistoryConnection>();
                            var initial=ElementHistoryRestoration.Restore(doc,new[]{new HistoryRestoreRequest{SourceUniqueId=uid,Label=request.Label,Recipe=bad}});
                            Check(initial.Created==1,"Bad legacy fitting fixture failed");
                        }
                        var batch=ElementHistoryRestoration.Restore(doc,new[]{request});
                        File.AppendAllText(Path.Combine(Output,"cml-results.jsonl"),JsonConvert.SerializeObject(batch)+Environment.NewLine);
                        var curvesAfter=new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Select(e=>e.UniqueId).OrderBy(s=>s).ToList();
                        Check(curvesBefore.SequenceEqual(curvesAfter),"Temporary sizing pipes leaked or real pipe deleted: removed="
                            +string.Join(",",curvesBefore.Except(curvesAfter))+"; added="+string.Join(",",curvesAfter.Except(curvesBefore)));
                        foreach(var entry in curveGeometry)
                        {
                            var actual=((LocationCurve)doc.GetElement(entry.Key).Location).Curve;
                            Check(actual.GetEndPoint(0).DistanceTo(entry.Value[0])<1e-5 && actual.GetEndPoint(1).DistanceTo(entry.Value[1])<1e-5,
                                "Existing network curve moved: "+entry.Key);
                        }
                        results.Add(((mode=="repair" ? batch.Existing==1 : batch.Created==1) && batch.Failed==0 && batch.ConnectionFailures.Count==0 && batch.RepairFailures.Count==0 ? "" : "FAILED ")
                            +request.Label+" dimensions, angle, placement and connections restored");
                        if(batch.Failed==0 && batch.RepairFailures.Count==0)
                        {
                            var retry=ElementHistoryRestoration.Restore(doc,new[]{request});
                            Check(retry.Created==0 && retry.Existing==1 && retry.Repaired==0,"Restoration retry must be idempotent");
                        }
                        group.RollBack();
                    }
                }
            }
            finally { doc.Close(false); }
            return results;
        }


        private void VerifyMirroredFamily(Document doc, string symbolId, List<string> results)
        {
            HistoryRestoreRequest request;
            Transform expected;
            XYZ expectedMin, expectedMax;
            using(var tx=new Transaction(doc,"Create reflected family fixture"))
            {
                tx.Start();
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                var symbol=(FamilySymbol)doc.GetElement(symbolId);
                var instance=doc.Create.NewFamilyInstance(new XYZ(0,350,15),symbol,level,StructuralType.NonStructural);
                ElementTransformUtils.MirrorElements(doc,new[]{instance.Id},Plane.CreateByNormalAndOrigin(XYZ.BasisX,new XYZ(0,350,15)),false);
                ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(new XYZ(0,350,15),new XYZ(0,350,16)),0.4);
                doc.Regenerate(); expected=instance.GetTransform();
                string diagnostic=null;
                var recipe=ElementHistoryReconstruction.Capture(instance, s=>diagnostic=s);
                Check(recipe!=null && recipe.Mirrored,"Reflected recipe missing: " + diagnostic
                    + "; determinant=" + expected.Determinant + "; mirrored=" + instance.Mirrored);
                request=new HistoryRestoreRequest { SourceUniqueId=instance.UniqueId,Label="Reflected fixture",Recipe=recipe };
                var box=instance.get_BoundingBox(null); expectedMin=box.Min; expectedMax=box.Max;
                Check(tx.Commit()==TransactionStatus.Committed,"Reflected fixture commit");
            }
            using(var tx=new Transaction(doc,"Delete reflected fixture"))
            { tx.Start(); doc.Delete(doc.GetElement(request.SourceUniqueId).Id); tx.Commit(); }
            request=JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(request));
            var batch=ElementHistoryRestoration.Restore(doc,new[]{request});
            Check(batch.Created==1 && batch.Failed==0,"Mirrored restore: "+JsonConvert.SerializeObject(batch));
            var restored=(FamilyInstance)doc.GetElement(batch.Items[0].UniqueId);
            Check(restored.Mirrored,"Restored family must remain mirrored");
            var restoredBox=restored.get_BoundingBox(null);
            Check(restoredBox.Min.DistanceTo(expectedMin)<1e-6 && restoredBox.Max.DistanceTo(expectedMax)<1e-6,"Mirrored geometry mismatch");
            var actual=restored.GetTransform();
            Check(actual.Origin.DistanceTo(expected.Origin)<1e-6 && actual.BasisX.DistanceTo(expected.BasisX)<1e-6
                && actual.BasisY.DistanceTo(expected.BasisY)<1e-6 && actual.BasisZ.DistanceTo(expected.BasisZ)<1e-6,"Reflected frame mismatch");
            results.Add("mirrored rotated family restored with the exact reflected transform after JSON roundtrip");
        }

        private void VerifyNetworkFamily(Document doc, string symbolId, List<string> results)
        {
            File.AppendAllText(Path.Combine(Output,"progress.txt"),"3D accessory network"+Environment.NewLine);
            var requests=new List<HistoryRestoreRequest>();
            using(var tx=new Transaction(doc,"Create 3D accessory network"))
            {
                tx.Start();
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                var symbol=(FamilySymbol)doc.GetElement(symbolId);
                symbol.Activate(); doc.Regenerate();
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                var instance=doc.Create.NewFamilyInstance(new XYZ(0,250,15),symbol,level,StructuralType.NonStructural);
                ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(new XYZ(0,250,15),new XYZ(0,250,16)),0.7);
                doc.Regenerate();
                var elements=new List<Element>{instance};
                var pipeType=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                var systemType=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>()
                    .FirstOrDefault(t=>t.SystemClassification==MEPSystemClassification.DomesticColdWater)
                    ?? PipingSystemType.Create(doc,MEPSystemClassification.DomesticColdWater,"History accessory water");
                var system=systemType.Id;
                foreach(var port in ElementHistoryNetwork.Ports(instance))
                {
                    var pipe=Pipe.Create(doc,system,pipeType,level.Id,port.Origin,port.Origin+port.CoordinateSystem.BasisZ*8);
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(port.Radius*2);
                    doc.Regenerate();
                    port.ConnectTo(ElementHistoryNetwork.Ports(pipe).OrderBy(c=>c.Origin.DistanceTo(port.Origin)).First());
                    elements.Add(pipe);
                }
                doc.Regenerate(); Check(elements.Count==3,"Accessory requires two connectors");
                foreach(var element in elements)
                {
                    string reason=null;
                    var recipe=ElementHistoryReconstruction.Capture(element,s=>reason=s);
                    Check(recipe!=null,"Accessory network recipe: "+reason);
                    requests.Add(new HistoryRestoreRequest { SourceUniqueId=element.UniqueId,Label=element.UniqueId,Recipe=recipe });
                }
                Check(tx.Commit()==TransactionStatus.Committed,"Accessory fixture commit");
            }
            foreach(var request in requests) Check(doc.GetElement(request.SourceUniqueId)!=null,"Accessory fixture disappeared at commit: "+request.Recipe.Kind);
            using(var tx=new Transaction(doc,"Delete 3D accessory network"))
            { tx.Start(); doc.Delete(requests.Select(r=>doc.GetElement(r.SourceUniqueId).Id).ToList()); tx.Commit(); }
            requests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(JsonConvert.SerializeObject(requests));
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Created==3 && batch.ConnectionsRestored==2 && batch.ConnectionFailures.Count==0,"Accessory restore: "+JsonConvert.SerializeObject(batch));
            var restored=(FamilyInstance)doc.GetElement(batch.Items.Single(i=>i.Label==requests[0].Label).UniqueId);
            Check(restored.GetTransform().BasisZ.DistanceTo(ElementHistoryNetwork.Vector(requests[0].Recipe.BasisZ))<1e-6,"Accessory 3D orientation");
            results.Add("rotated pipe accessory restored with both physical pipe connections");
            var unsupported=JObject.FromObject(requests[0]).ToObject<HistoryRestoreRequest>();
            unsupported.SourceUniqueId=Guid.NewGuid().ToString()+"-00000001";
            var rotation=Transform.CreateRotation(XYZ.BasisY,0.7);
            var x=rotation.OfVector(ElementHistoryNetwork.Vector(unsupported.Recipe.BasisX));
            var z=rotation.OfVector(ElementHistoryNetwork.Vector(unsupported.Recipe.BasisZ));
            unsupported.Recipe.BasisX=new[]{x.X,x.Y,x.Z}; unsupported.Recipe.BasisZ=new[]{z.X,z.Y,z.Z};
            unsupported.Recipe.Connections=null;
            var failed=ElementHistoryRestoration.Restore(doc,new[]{unsupported});
            Check(failed.Created==0 && failed.Failed==1,"Forbidden family rotation must fail without a modal dialog");
            results.Add("forbidden family inclination rolls back with a reported error and no modal dialog");
            // Reusing history must never move an existing neighbor back or replace its connection.
            using(var tx=new Transaction(doc,"Move historical network away"))
            {
                tx.Start();
                foreach(var port in ElementHistoryNetwork.Ports(restored))
                    foreach(Connector peer in port.AllRefs.Cast<Connector>().ToList())
                        if(peer.Owner.Id!=restored.Id && port.IsConnectedTo(peer)) port.DisconnectFrom(peer);
                ElementTransformUtils.MoveElement(doc,restored.Id,new XYZ(0,0,3)); tx.Commit();
            }
            batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Created==0 && batch.ConnectionsRestored==0 && batch.ConnectionFailures.Count==2,"Moved connections must be rejected");
            results.Add("moved existing accessory is preserved; obsolete connector positions are reported");
        }

        private sealed class FixtureFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages=accessor.GetFailureMessages();
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"fixture-failures.txt"),
                    string.Join("\n",messages.Select(m=>m.GetSeverity()+": "+m.GetDescriptionText()))+"\n");
                if(messages.Any(m=>m.GetSeverity()!=FailureSeverity.Warning)) return FailureProcessingResult.ProceedWithRollBack;
                foreach(var warning in messages) accessor.DeleteWarning(warning);
                return FailureProcessingResult.Continue;
            }
        }

        private void VerifyNetworks(UIApplication ui, ref Document doc, List<string> results)
        {
            File.AppendAllText(Path.Combine(Output,"progress.txt"),"Network restoration" + Environment.NewLine);
            var requests = new List<HistoryRestoreRequest>();
            string survivor;
            using (var tx = new Transaction(doc,"Create network fixtures"))
            {
                tx.Start();
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                var pipeSystem = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
                if (pipeSystem == ElementId.InvalidElementId) pipeSystem = PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "History water").Id;
                var p1 = Pipe.Create(doc, pipeSystem, pipeType, level.Id, new XYZ(0,150,15),new XYZ(10,150,17));
                var p2 = Pipe.Create(doc, pipeSystem, pipeType, level.Id, new XYZ(10,150,17),new XYZ(20,150,19));
                var p3 = Pipe.Create(doc, pipeSystem, pipeType, level.Id, new XYZ(20,150,19),new XYZ(30,150,21));
                foreach(var p in new[]{p1,p2,p3}) p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(0.25);
                doc.Regenerate();
                ElementHistoryNetwork.Ports(p1).OrderBy(c=>c.Origin.DistanceTo(new XYZ(10,150,17))).First()
                    .ConnectTo(ElementHistoryNetwork.Ports(p2).OrderBy(c=>c.Origin.DistanceTo(new XYZ(10,150,17))).First());
                ElementHistoryNetwork.Ports(p2).OrderBy(c=>c.Origin.DistanceTo(new XYZ(20,150,19))).First()
                    .ConnectTo(ElementHistoryNetwork.Ports(p3).OrderBy(c=>c.Origin.DistanceTo(new XYZ(20,150,19))).First());
                survivor = p3.UniqueId;
                var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).FirstElementId();
                var ductSystem = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
                if (ductSystem == ElementId.InvalidElementId) ductSystem = MechanicalSystemType.Create(doc, MEPSystemClassification.SupplyAir, "History air").Id;
                var duct = Duct.Create(doc,ductSystem,ductType,level.Id,new XYZ(0,160,15),new XYZ(10,160,15));
                var conduitType = new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).FirstElementId();
                var conduit = Conduit.Create(doc,conduitType,new XYZ(0,170,15),new XYZ(0,170,25),level.Id);
                var trayType = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).FirstElementId();
                var tray = CableTray.Create(doc,trayType,new XYZ(0,180,15),new XYZ(10,180,17),level.Id);
                var flexPipeType = new FilteredElementCollector(doc).OfClass(typeof(FlexPipeType)).FirstElementId();
                var flexDuctType = new FilteredElementCollector(doc).OfClass(typeof(FlexDuctType)).Cast<FlexDuctType>().First(t=>t.Shape==ConnectorProfileType.Round).Id;
                var flexPipe = FlexPipe.Create(doc,pipeSystem,flexPipeType,level.Id,new[]{new XYZ(0,190,15),new XYZ(5,192,17),new XYZ(10,190,15)});
                var flexDuct = FlexDuct.Create(doc,ductSystem,flexDuctType,level.Id,new[]{new XYZ(0,200,15),new XYZ(5,202,17),new XYZ(10,200,15)});
                File.WriteAllText(Path.Combine(Output,"flex-diagnostics.json"),JsonConvert.SerializeObject(new[]{flexPipe as Element,flexDuct}.Select(e=>new {
                    kind=e.GetType().Name, parameters=e.Parameters.Cast<Parameter>().Select(p=>new { id=p.Id.GetIdLongValue(),name=p.Definition.Name,value=p.AsValueString() }),
                    ports=ElementHistoryNetwork.Ports(e).Select(c=>new { shape=c.Shape.ToString(),type=c.ConnectorType.ToString() }) }),Formatting.Indented));
                (flexPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) ?? flexPipe.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM))?.Set(0.25);
                (flexDuct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM) ?? flexDuct.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM))?.Set(0.5);
                doc.Regenerate();
                foreach(var element in new Element[]{p1,p2,duct,conduit,tray,flexPipe,flexDuct})
                {
                    string reason = null;
                    var recipe = ElementHistoryReconstruction.Capture(element, s=>reason=s);
                    Check(recipe != null,"Missing network recipe: " + element.GetType().Name + " " + reason);
                    Check(element.get_BoundingBox(null)!=null,"Invalid fixture geometry: " + element.GetType().Name);
                    requests.Add(new HistoryRestoreRequest { SourceUniqueId=element.UniqueId, Label=element.UniqueId, Recipe=recipe });
                }
                Check(requests[0].Recipe.Connections.Count==1 && requests[1].Recipe.Connections.Count==2,"Missing pipe connections");
                Check(tx.Commit()==TransactionStatus.Committed,"Network fixture commit failed");
            }
            using(var tx=new Transaction(doc,"Delete network fixtures"))
            {
                tx.Start(); var currentDoc=doc; doc.Delete(requests.Select(r=>currentDoc.GetElement(r.SourceUniqueId).Id).ToList()); tx.Commit();
            }
            var json = JsonConvert.SerializeObject(requests);
            File.WriteAllText(Path.Combine(Output,"network-deletions.json"),json);
            doc.Save(); var path=doc.PathName; doc.Close(false); doc=ui.Application.OpenDocumentFile(path);
            requests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(json);
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Created==7 && batch.Failed==0,"Network creation: " + JsonConvert.SerializeObject(batch));
            Check(batch.ConnectionsRestored==2 && batch.ConnectionFailures.Count==0,"Network links: " + JsonConvert.SerializeObject(batch));
            foreach(var request in requests)
            {
                var restored=doc.GetElement(batch.Items.Single(i=>i.Label==request.Label).UniqueId);
                var actual=ElementHistoryReconstruction.Capture(restored).Network;
                var expected=request.Recipe.Network;
                Check(actual.Kind==expected.Kind && actual.SystemType==expected.SystemType,"Network type/system mismatch");
                Check(ElementHistoryNetwork.Vector(actual.Start).DistanceTo(ElementHistoryNetwork.Vector(expected.Start))<1e-6
                    && ElementHistoryNetwork.Vector(actual.End).DistanceTo(ElementHistoryNetwork.Vector(expected.End))<1e-6,"Network location mismatch");
                Check(Math.Abs(actual.Diameter-expected.Diameter)<1e-6 && Math.Abs(actual.Width-expected.Width)<1e-6
                    && Math.Abs(actual.Height-expected.Height)<1e-6,"Network dimensions mismatch");
                if (expected.Points != null)
                    Check(actual.Points.Count==expected.Points.Count && actual.Points.Zip(expected.Points,(a,b)=>ElementHistoryNetwork.Vector(a).DistanceTo(ElementHistoryNetwork.Vector(b))).All(d=>d<1e-6)
                        && ElementHistoryNetwork.Vector(actual.StartTangent).DistanceTo(ElementHistoryNetwork.Vector(expected.StartTangent))<1e-6
                        && ElementHistoryNetwork.Vector(actual.EndTangent).DistanceTo(ElementHistoryNetwork.Vector(expected.EndTangent))<1e-6,"Flexible geometry mismatch");
            }
            results.Add("pipe/duct/conduit/cable tray restored after reopen, sections and sloped/vertical placement");
            results.Add("flex pipe and flex duct restored with points, tangents and diameter");
            Check(ElementHistoryNetwork.Ports(doc.GetElement(survivor)).Any(c=>c.IsConnected),"Existing network not reconnected");
            results.Add("two physical pipe links restored including existing neighbor");
            doc.Save(); path=doc.PathName; doc.Close(false); doc=ui.Application.OpenDocumentFile(path);
            batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Created==0 && batch.Existing==7 && batch.ConnectionsExisting==2 && batch.ConnectionFailures.Count==0,"Duplicate network restore: "+JsonConvert.SerializeObject(batch));
            results.Add("network connections survive save/reopen; repeated restore is idempotent");
            using(var tx=new Transaction(doc,"Remove network fixture again"))
            {
                tx.Start(); var currentDoc=doc; doc.Delete(batch.Items.Select(i=>currentDoc.GetElement(i.UniqueId).Id).ToList()); tx.Commit();
            }
            batch=ElementHistoryRestoration.Restore(doc,requests.Take(1));
            Check(batch.Created==1 && batch.ConnectionFailures.Count==1,"Missing peer must not block pipe creation");
            batch=ElementHistoryRestoration.Restore(doc,requests.Skip(1).Take(1));
            Check(batch.Created==1 && batch.ConnectionsRestored==2 && batch.ConnectionFailures.Count==0,"Later peer restore must reconnect both ends");
            results.Add("partial network restored first; missing neighbor restored later and reconnected");
        }

        private void VerifyPermanent(UIApplication ui, ref Document projectDoc, string symbolId, string hostedSymbolId, List<string> results)
        {
            Document doc = projectDoc;
            try
            {
            File.AppendAllText(Path.Combine(Output,"progress.txt"),"Permanent restoration and save/reopen" + Environment.NewLine);
            var requests = new List<HistoryRestoreRequest>();
            var boxes = new Dictionary<string, Tuple<XYZ,XYZ>>();
            using (var transaction = new Transaction(doc,"Create permanent restoration fixtures"))
            {
                transaction.Start();
                var level = Level.Create(doc, 12);
                var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
                var wall = Wall.Create(doc,Line.CreateBound(new XYZ(0,0,12),new XYZ(20,0,12)),wallType.Id,level.Id,10,0,false,false);
                var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t=>!t.IsFoundationSlab);
                var floor = Floor.Create(doc,new[]{Rectangle(0,25,20,45,12),Rectangle(4,29,9,34,12)},floorType.Id,level.Id);
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(0.75);
                var symbol = (FamilySymbol)doc.GetElement(symbolId);
                var hostedSymbol = (FamilySymbol)doc.GetElement(hostedSymbolId);
                symbol.Activate(); hostedSymbol.Activate(); doc.Regenerate();
                var pump = doc.Create.NewFamilyInstance(new XYZ(6,60,14),symbol,level,StructuralType.NonStructural);
                pump.LookupParameter("HistoryDepth").Set(3.5);
                ElementTransformUtils.RotateElement(doc,pump.Id,Line.CreateBound(new XYZ(6,60,14),new XYZ(6,60,15)),0.42);
                var hosted = doc.Create.NewFamilyInstance(new XYZ(7,0,14),hostedSymbol,wall,level,StructuralType.NonStructural);
                doc.Regenerate();
                // Deliberately put the hosted family before its host in the request.
                foreach(var element in new Element[]{hosted,pump,floor,wall})
                {
                    var recipe = ElementHistoryReconstruction.Capture(element);
                    Check(recipe!=null,"Persistent recipe missing for " + element.GetType().Name);
                    var request = new HistoryRestoreRequest { SourceUniqueId=element.UniqueId,Label=element.GetType().Name,Recipe=recipe };
                    requests.Add(request);
                    var box=element.get_BoundingBox(null);
                    boxes[request.SourceUniqueId]=Tuple.Create(box.Min,box.Max);
                }
                Check(transaction.Commit()==TransactionStatus.Committed,"Fixture commit failed");
            }
            using(var transaction=new Transaction(doc,"Delete restoration fixtures"))
            {
                transaction.Start();
                doc.Delete(requests.Select(r=>doc.GetElement(r.SourceUniqueId).Id).ToList());
                Check(transaction.Commit()==TransactionStatus.Committed,"Deletion commit failed");
            }
            string historyPath=Path.Combine(Output,"persistent-deletions.json");
            File.WriteAllText(historyPath,JsonConvert.SerializeObject(requests));
            string projectPath=Path.Combine(Output,"permanent-restoration.rvt");
            doc.SaveAs(projectPath,new SaveAsOptions { OverwriteExistingFile=false });
            doc.Close(false); doc=ui.Application.OpenDocumentFile(projectPath);
            requests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(File.ReadAllText(historyPath));
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Created==4 && batch.Failed==0,"Permanent restore failed: " + JsonConvert.SerializeObject(batch));
            foreach(var request in requests)
            {
                var item=batch.Items.Single(i=>i.Label==request.Label && ElementHistoryRestoration.GetOrigins(doc.GetElement(i.UniqueId)).Contains(request.SourceUniqueId));
                var element=doc.GetElement(item.UniqueId);
                Check(!(element is DirectShape),"Restoration substituted a DirectShape");
                var box=element.get_BoundingBox(null); var old=boxes[request.SourceUniqueId];
                Check(box.Min.DistanceTo(old.Item1)<0.03 && box.Max.DistanceTo(old.Item2)<0.03,"Persistent bounds mismatch: " + request.Label);
                if(element is FamilyInstance instance && request.Recipe.Host==null)
                    Check(Math.Abs(instance.LookupParameter("HistoryDepth").AsDouble()-3.5)<1e-8,"Instance parameter not restored");
            }
            var hostedRequest=requests.First(r=>r.Recipe.Host!=null);
            var hostedRestored=(FamilyInstance)batch.Items.Select(i=>doc.GetElement(i.UniqueId)).Single(e=>ElementHistoryRestoration.GetOrigins(e).Contains(hostedRequest.SourceUniqueId));
            Check(ElementHistoryRestoration.GetOrigins(hostedRestored.Host).Contains(hostedRequest.Recipe.Host),"Restored host not remapped");
            results.Add("permanent native restoration after deletion/save/reopen: families, host, wall, floor and instance values");

            doc.Save(); doc.Close(false); doc=ui.Application.OpenDocumentFile(projectPath);
            var before=Ids(doc);
            batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Created==0 && batch.Existing==4 && before.SequenceEqual(Ids(doc)),"Duplicate restoration after reopen");
            results.Add("persistent duplicate prevention after save/reopen");

            using(var undo=new TransactionGroup(doc,"Simulate Undo of restoration"))
            {
                undo.Start();
                using(var deletion=new Transaction(doc,"Delete restored fixtures"))
                {
                    deletion.Start(); doc.Delete(batch.Items.Select(i=>doc.GetElement(i.UniqueId).Id).ToList()); deletion.Commit();
                }
                batch=ElementHistoryRestoration.Restore(doc,requests);
                Check(batch.Created==4,"Restoration after another deletion failed");
                undo.RollBack();
            }
            Check(before.SequenceEqual(Ids(doc)),"Transaction group rollback did not recover original state");
            batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Existing==4 && batch.Created==0,"Rollback left a stale restoration index");
            results.Add("restore again after deletion and transaction-group rollback");

            var valid=JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(requests.First(r=>r.Recipe.Kind=="family" && r.Recipe.Host==null)));
            valid.SourceUniqueId=Guid.NewGuid().ToString()+"-00000001"; valid.Label="valid partial batch";
            var invalid=JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(valid));
            invalid.SourceUniqueId=Guid.NewGuid().ToString()+"-00000002"; invalid.Recipe.Type=Guid.NewGuid().ToString()+"-00000003";
            var broken=JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(valid));
            broken.SourceUniqueId=Guid.NewGuid().ToString()+"-00000004"; broken.Recipe.Point=null;
            var legacy=new HistoryRestoreRequest { SourceUniqueId=Guid.NewGuid().ToString()+"-00000005",Label="old history" };
            batch=ElementHistoryRestoration.Restore(doc,new[]{invalid,broken,legacy,valid});
            Check(batch.Created==1 && batch.Failed==3,"Partial batch did not preserve successes/reject failures: "+JsonConvert.SerializeObject(batch));
            results.Add("partial batch: missing type, malformed geometry and old history reported without losing successful restoration");
            }
            finally { projectDoc = doc; }
        }
        private void Verify(Document doc, Element element, string name, List<string> results)
        {
            try { VerifyCore(doc,element,name,results); }
            catch(Exception ex)
            {
                File.AppendAllText(Path.Combine(Output,"diagnostics.txt"),ex + Environment.NewLine);
                results.Add("FAILED: " + name + ": " + ex.Message);
            }
        }
        private void VerifyCore(Document doc, Element element, string name, List<string> results)
        {
            File.AppendAllText(Path.Combine(Output,"progress.txt"),name + Environment.NewLine);
            void Diagnostic(string text) => File.AppendAllText(Path.Combine(Output,"diagnostics.txt"),name + ": " + text + Environment.NewLine);
            var recipe = ElementHistoryReconstruction.Capture(element, Diagnostic);
            if(recipe == null)
            {
                Diagnostic("Level="+element.LevelId+" type="+element.GetTypeId()+" location="+element.Location.GetType().Name);
                if(element is Wall w) Diagnostic("kind="+w.WallType.Kind+" sketch="+w.SketchId+" inserts="+w.FindInserts(true,true,true,true).Count);
                foreach(Parameter p in element.Parameters)
                    Diagnostic(p.Definition.Name+" / "+p.Id+" / "+p.StorageType+" / "+p.AsValueString());
            }
            Check(recipe != null,"Recipe absent: " + name);
            var box = element.get_BoundingBox(null);
            double volume = Volume(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            Check(volume > 0,"Test fixture has no original visible volume: " + name);
            var raw = JObject.Parse(JsonConvert.SerializeObject(recipe));
            File.WriteAllText(Path.Combine(Output,recipe.Kind + "-" + element.Id.GetIdLongValue() + "-recipe.json"),raw.ToString());
            // Delete inside a surrounding subtransaction so each assertion uses a real deletion.
            using (var deletion = new SubTransaction(doc))
            {
                deletion.Start(); doc.Delete(element.Id); doc.Regenerate();
                var before = Ids(doc);
                var triangles = ElementHistoryReconstruction.Reconstruct(doc,raw,Diagnostic);
                Check(triangles != null && triangles.Count > 0,"Empty reconstruction: " + name);
                Check(before.SequenceEqual(Ids(doc)),"Reconstruction leaked native elements: " + name);
                var points = triangles.SelectMany(t => t).ToList();
                var min = new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z));
                var max = new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z));
                Check(min.DistanceTo(box.Min)<0.03 && max.DistanceTo(box.Max)<0.03,
                    "Bounds mismatch: " + name + " expected " + box.Min + " / " + box.Max + " actual " + min + " / " + max);
                double reconstructedVolume = Math.Abs(triangles.Sum(t=>t[0].DotProduct(t[1].CrossProduct(t[2])))/6);
                Check(Math.Abs(volume-reconstructedVolume)<Math.Max(0.05,volume*0.02),"Volume mismatch: " + name);
                // Geometry must remain usable after the native element has been rolled back.
                var builder = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.AnyGeometry, Fallback = TessellatedShapeBuilderFallback.Mesh };
                builder.OpenConnectedFaceSet(false);
                foreach(var triangle in triangles) builder.AddFace(new TessellatedFace(triangle,ElementId.InvalidElementId));
                builder.CloseConnectedFaceSet(); builder.Build();
                var preview = DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
                preview.SetShape(builder.GetBuildResult().GetGeometricalObjects());
                doc.Regenerate();
                Check(preview.get_Geometry(new Options()) != null,"Detached preview invalid: " + name);
                deletion.RollBack();
            }
            results.Add(name + " (JSON, deletion, bounds, volume, rollback, detached preview)");
        }
        private static void VerifyFailure(Document doc,HistoryRecipe recipe,string name,List<string> results)
        {
            var before = Ids(doc);
            Check(ElementHistoryReconstruction.Reconstruct(doc,JObject.FromObject(recipe)) == null,"Expected fallback: " + name);
            Check(before.SequenceEqual(Ids(doc)),"Failed reconstruction leaked elements: " + name);
            results.Add(name + " fallback and rollback");
        }
        private static FamilySymbol CreateFamily(UIApplication ui,Document project,string templateName, bool network = false)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Autodesk","RVT " + ui.Application.VersionNumber,"Family Templates","English");
            var familyDoc = ui.Application.NewFamilyDocument(Path.Combine(root,templateName));
            try
            {
                using(var t=new Transaction(familyDoc,"Test family"))
                {
                    t.Start(); familyDoc.FamilyManager.NewType("History fixture");
                    var profile = new CurveArrArray(); var curves = new CurveArray();
                    foreach(Curve curve in Rectangle(0,0,2,1,0)) curves.Append(curve);
                    profile.Append(curves);
                    var sketchPlane = SketchPlane.Create(familyDoc,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));
                    var extrusion = familyDoc.FamilyCreate.NewExtrusion(true,profile,sketchPlane,2);
                    if(network)
                    {
                        familyDoc.OwnerFamily.FamilyCategory = familyDoc.Settings.Categories.get_Item(BuiltInCategory.OST_PipeAccessory);
                        familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALWAYS_VERTICAL)?.Set(0);
                        familyDoc.Regenerate();
                        var solid = extrusion.get_Geometry(new Options { ComputeReferences=true }).OfType<Solid>().First(s=>s.Volume>0);
                        foreach(var face in solid.Faces.Cast<Face>().OfType<PlanarFace>().Where(f=>Math.Abs(f.FaceNormal.X)>0.99))
                        {
                            var connector=ConnectorElement.CreatePipeConnector(familyDoc,PipeSystemType.DomesticColdWater,face.Reference);
                            connector.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS).Set(0.125);
                        }
                    }
                    else if(!templateName.Contains("wall based"))
                    {
                        var depth = familyDoc.FamilyManager.AddParameter("HistoryDepth",GroupTypeId.Geometry,SpecTypeId.Length,true);
                        familyDoc.FamilyManager.Set(depth,2.0);
                        familyDoc.FamilyManager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),depth);
                    }
                    familyDoc.Regenerate();
                    Check(Volume(extrusion.get_Geometry(new Options())) > 0,"Test extrusion has no volume before loading");
                    Check(t.Commit() == TransactionStatus.Committed,"Test family transaction failed");
                }
                string fixturePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    network ? "BIMaestro-test-network.rfa" : templateName.Contains("wall based") ? "BIMaestro-test-hosted.rfa" : "BIMaestro-test-free.rfa");
                familyDoc.SaveAs(fixturePath,new SaveAsOptions { OverwriteExistingFile = false });
                var family = familyDoc.LoadFamily(project);
                return (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());
            }
            finally { familyDoc.Close(false); }
        }
        private static CurveLoop Rectangle(double x,double y,double x2,double y2,double z)
        {
            var points=new[]{new XYZ(x,y,z),new XYZ(x2,y,z),new XYZ(x2,y2,z),new XYZ(x,y2,z)};
            return CurveLoop.Create(Enumerable.Range(0,4).Select(i=>(Curve)Line.CreateBound(points[i],points[(i+1)%4])).ToList());
        }
        private static double Volume(GeometryElement geometry) => geometry.Cast<GeometryObject>().Sum(g=>g is Solid s ? s.Volume : g is GeometryInstance i ? Volume(i.GetInstanceGeometry()) : 0);
        private static string[] Ids(Document doc) => new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e=>e.UniqueId)
            .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().Select(e=>e.UniqueId)).OrderBy(id=>id).ToArray();
        private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    }
}
