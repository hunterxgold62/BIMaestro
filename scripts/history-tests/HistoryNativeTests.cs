using Analyse;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Architecture;
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
                if (File.Exists(Path.Combine(Output,"extended-only.txt")))
                { VerifyExtendedElements(ui,doc,results); return results; }
                if (File.Exists(Path.Combine(Output,"relations-only.txt")))
                { VerifyGeometryRelations(ui,doc,results); return results; }
                if (File.Exists(Path.Combine(Output,"electrical-only.txt")))
                { VerifyElectricalSystems(ui,doc,results); return results; }
                if (File.Exists(Path.Combine(Output,"power-only.txt")))
                { VerifyElectricalSystem(ui,doc,results,true); return results; }
                if (File.Exists(Path.Combine(Output,"native-only.txt")))
                { VerifyNativeArchive(ui,doc,results); VerifyLogicalSystems(doc,results); return results; }
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
                VerifyExtendedElements(ui, doc, results);
                VerifyNativeArchive(ui,doc,results);
                VerifyLogicalSystems(doc,results);
                VerifyElectricalSystems(ui,doc,results);
                VerifyGeometryRelations(ui,doc,results);
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
            // A line-based family's element box also contains its placement
            // curve/reference geometry. Compare solid geometry to preview triangles.
            if (element is FamilyInstance && element.Location is LocationCurve)
            {
                var points = new List<XYZ>();
                void CollectPoints(GeometryElement geometry)
                {
                    foreach (var item in geometry)
                        if (item is GeometryInstance nested) CollectPoints(nested.GetInstanceGeometry());
                        else if (item is Solid solid)
                            foreach (Face face in solid.Faces)
                            {
                                var mesh = face.Triangulate();
                                points.AddRange(mesh.Vertices);
                            }
                }
                CollectPoints(element.get_Geometry(new Options { DetailLevel=ViewDetailLevel.Fine }));
                box = new BoundingBoxXYZ { Min=new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),
                    Max=new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)) };
            }
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
        private static FamilySymbol CreateFamily(UIApplication ui,Document project,string templateName, bool network = false, bool electrical = false, bool power = false, bool cuttingVoid = false)
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
                    if(cuttingVoid)
                    {
                        var voidProfile=new CurveArrArray(); var voidCurves=new CurveArray();
                        foreach(Curve c in Rectangle(4,-2,6,2,0)) voidCurves.Append(c);
                        voidProfile.Append(voidCurves);
                        familyDoc.FamilyCreate.NewExtrusion(false,voidProfile,sketchPlane,10);
                        familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS).Set(1);
                    }
                    if(electrical)
                    {
                        familyDoc.Regenerate();
                        var solid = extrusion.get_Geometry(new Options { ComputeReferences=true }).OfType<Solid>().First(s=>s.Volume>0);
                        var face = solid.Faces.Cast<Face>().OfType<PlanarFace>().First(f=>f.FaceNormal.Z>0.99);
                        var connector=ConnectorElement.CreateElectricalConnector(familyDoc,power ? ElectricalSystemType.PowerBalanced : ElectricalSystemType.Data,face.Reference);
                        if(power)
                        {
                            var voltage=familyDoc.FamilyManager.AddParameter("History voltage",GroupTypeId.Electrical,connector.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE).Definition.GetDataType(),true);
                            familyDoc.FamilyManager.Set(voltage,UnitUtils.ConvertToInternalUnits(templateName.Contains("Equipment") ? 400 : 230,UnitTypeId.Volts));
                            familyDoc.FamilyManager.AssociateElementParameterToFamilyParameter(connector.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE),voltage);
                            var poles=familyDoc.FamilyManager.AddParameter("History poles",GroupTypeId.Electrical,connector.get_Parameter(BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES).Definition.GetDataType(),true);
                            familyDoc.FamilyManager.Set(poles,templateName.Contains("Equipment") ? 3 : 1);
                            familyDoc.FamilyManager.AssociateElementParameterToFamilyParameter(connector.get_Parameter(BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES),poles);
                        }
                        if(templateName.Contains("Equipment"))
                            familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE).Set((int)(power ? PartType.PanelBoard : PartType.OtherPanel));
                    }
                    else if(network)
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
                    network ? "BIMaestro-test-network.rfa" : "BIMaestro-test-" + Path.GetFileNameWithoutExtension(templateName) + (power ? "-power" : "") + (cuttingVoid ? "-void" : "") + ".rfa");
                familyDoc.SaveAs(fixturePath,new SaveAsOptions { OverwriteExistingFile = false });
                var family = familyDoc.LoadFamily(project);
                return (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());
            }
            finally { familyDoc.Close(false); }
        }
        private void VerifyGeometryRelations(UIApplication ui, Document doc, List<string> results)
        {
            var voidSymbol=CreateFamily(ui,doc,"Metric Generic Model.rft",cuttingVoid:true);
            var columnSymbol=CreateFamily(ui,doc,"Metric Structural Column.rft");
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
            var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
            Wall a,b,attached; Floor floor;
            using(var tx=new Transaction(doc,"Geometry relation fixtures"))
            {
                tx.Start();
                a=Wall.Create(doc,Line.CreateBound(new XYZ(0,600,level.Elevation),new XYZ(20,600,level.Elevation)),wt.Id,level.Id,20,0,false,false);
                b=Wall.Create(doc,Line.CreateBound(new XYZ(10,590,level.Elevation),new XYZ(10,610,level.Elevation)),wt.Id,level.Id,20,0,false,false);
                attached=Wall.Create(doc,Line.CreateBound(new XYZ(30,600,level.Elevation),new XYZ(50,600,level.Elevation)),wt.Id,level.Id,20,0,false,false);
                var ft=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t=>!t.IsFoundationSlab);
                floor=Floor.Create(doc,new[]{Rectangle(25,595,55,605,level.Elevation)},ft.Id,level.Id);
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(10);
                WallUtils.DisallowWallJoinAtEnd(a,0);
                doc.Regenerate(); JoinGeometryUtils.JoinGeometry(doc,a,b);
                if(!JoinGeometryUtils.IsCuttingElementInJoin(doc,a,b)) JoinGeometryUtils.SwitchJoinOrder(doc,a,b);
                var add=typeof(Wall).GetMethods().FirstOrDefault(m=>m.Name=="AddAttachment" && m.GetParameters().Length==2);
                if(add!=null) add.Invoke(attached,new[]{(object)floor.Id,Enum.Parse(add.GetParameters()[1].ParameterType,"Top")});
                Check(tx.Commit()==TransactionStatus.Committed,"Relation fixtures commit");
            }
            HistoryRestoreRequest Request(Element e) => new HistoryRestoreRequest { SourceUniqueId=e.UniqueId, Label=e.Name,
                Category=e.Category?.Name,Recipe=ElementHistoryReconstruction.Capture(e) };
            ElementHistoryRelations.Invalidate(doc);
            var ra=Request(a); var rb=Request(b); var rf=Request(floor); var rw=Request(attached);
            for(int wave=0;wave<20 && rw.Recipe.Native?.Ready==false && rw.Recipe.Native.Failure==null;wave++) ElementHistoryNativeArchive.ProcessPending(doc);
            Check(ra.Recipe.GeometryRelations.Any(r=>r.Kind=="join" && r.First==a.UniqueId && r.Second==b.UniqueId),"Join cutting order capture");
            var expectedVolume=Volume(a.get_Geometry(new Options()))+Volume(b.get_Geometry(new Options()));
            using(var tx=new Transaction(doc,"Delete joined wall")){tx.Start();doc.Delete(a.Id);tx.Commit();}
            var batch=ElementHistoryRestoration.Restore(doc,new[]{ra});
            Check(batch.Created==1 && batch.Failed==0 && batch.RelationFailures.Count==0,"Join restoration: "+JsonConvert.SerializeObject(batch));
            a=(Wall)doc.GetElement(batch.Items.Single().UniqueId);
            Check(JoinGeometryUtils.AreElementsJoined(doc,a,b) && JoinGeometryUtils.IsCuttingElementInJoin(doc,a,b),"Join direction restored");
            Check(!WallUtils.IsWallJoinAllowedAtEnd(a,0),"Disabled wall end preserved");
            Check(Math.Abs(Volume(a.get_Geometry(new Options()))+Volume(b.get_Geometry(new Options()))-expectedVolume)<1e-5,"Joined volume restored");
            results.Add("joined wall restored against surviving wall; cutting order, volume and disabled end preserved");
            var repeat=ElementHistoryRestoration.Restore(doc,new[]{ra});
            Check(repeat.Created==0 && repeat.RelationsRestored==0 && repeat.RelationFailures.Count==0,"Repeated join restoration");
            results.Add("repeated relation restoration creates no duplicate or unnecessary change");
            var hasAttach=rf.Recipe.GeometryRelations?.Any(r=>r.Kind=="wall_attach")==true;
            if(hasAttach)
            {
                using(var tx=new Transaction(doc,"Delete attachment target")){tx.Start();doc.Delete(floor.Id);tx.Commit();}
                batch=ElementHistoryRestoration.Restore(doc,new[]{rf});
                Check(batch.Created==1 && batch.RelationsRestored>=1 && batch.RelationFailures.Count==0,"Target-only attachment restoration: "+JsonConvert.SerializeObject(batch));
                floor=(Floor)doc.GetElement(batch.Items.Single().UniqueId);
                var captured=ElementHistoryRelations.Capture(attached,new List<string>());
                Check(captured.Any(r=>r.Kind=="wall_attach" && r.Second==floor.UniqueId),"Surviving wall reattached to restored floor");
                results.Add("floor-only restoration reattaches surviving wall to its original top target");
                using(var tx=new Transaction(doc,"Delete attached wall")){tx.Start();doc.Delete(attached.Id);tx.Commit();}
                batch=ElementHistoryRestoration.Restore(doc,new[]{rw});
                Check(batch.Created==1 && batch.RelationFailures.Count==0,"Owner-only attachment restoration: "+JsonConvert.SerializeObject(batch));
                attached=(Wall)doc.GetElement(batch.Items.Single().UniqueId);
                Check(ElementHistoryRelations.Capture(attached,new List<string>()).Any(r=>r.Kind=="wall_attach" && r.Second==floor.UniqueId),"Restored wall attached through restored target identity");
                results.Add("wall-only restoration resolves attachment target restored in an earlier operation");
            }
            using(var tx=new Transaction(doc,"Delete join peer")){tx.Start();doc.Delete(a.Id);doc.Delete(b.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{rb});
            Check(batch.Created==1 && batch.RelationFailures.Any(f=>f.Contains("référence")),"Missing join peer must be reported");
            results.Add("missing geometric reference explicitly reported without losing restored physical element");
            Wall target; FamilyInstance cutter;
            using(var tx=new Transaction(doc,"Void cut fixtures"))
            {
                tx.Start();voidSymbol.Activate();doc.Regenerate();
                target=Wall.Create(doc,Line.CreateBound(new XYZ(0,650,level.Elevation),new XYZ(20,650,level.Elevation)),wt.Id,level.Id,20,0,false,false);
                cutter=doc.Create.NewFamilyInstance(new XYZ(0,650,level.Elevation),voidSymbol,level,StructuralType.NonStructural);
                doc.Regenerate();InstanceVoidCutUtils.AddInstanceVoidCut(doc,target,cutter);
                Check(tx.Commit()==TransactionStatus.Committed,"Void fixture commit");
            }
            var rt=Request(target);var rc=Request(cutter);var cutVolume=Volume(target.get_Geometry(new Options()));
            for(int wave=0;wave<20 && new[]{rt,rc}.Any(r=>r.Recipe.Native?.Ready==false && r.Recipe.Native.Failure==null);wave++) ElementHistoryNativeArchive.ProcessPending(doc);
            Check(rt.Recipe.GeometryRelations.Any(r=>r.Kind=="void_cut" && r.First==cutter.UniqueId),"Void target capture");
            using(var tx=new Transaction(doc,"Delete void cutter")){tx.Start();doc.Delete(cutter.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{rc});
            Check(batch.Created==1 && batch.RelationFailures.Count==0,"Void cutter restoration: "+JsonConvert.SerializeObject(batch));
            cutter=(FamilyInstance)doc.GetElement(batch.Items.Single().UniqueId);
            Check(InstanceVoidCutUtils.InstanceVoidCutExists(target,cutter) && Math.Abs(Volume(target.get_Geometry(new Options()))-cutVolume)<1e-5,"Void cut and volume restored");
            results.Add("void cutter restored against surviving wall; cut relation and removed volume preserved");
            using(var tx=new Transaction(doc,"Delete void cut target")){tx.Start();doc.Delete(target.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{rt});
            Check(batch.Created==1 && batch.RelationFailures.Count==0,"Void target restoration: "+JsonConvert.SerializeObject(batch));
            target=(Wall)doc.GetElement(batch.Items.Single().UniqueId);
            Check(InstanceVoidCutUtils.InstanceVoidCutExists(target,cutter),"Void target restored through earlier cutter identity");
            results.Add("void target restored against cutter restored in an earlier operation");
            FamilyInstance column; Floor support;
            using(var tx=new Transaction(doc,"Column attachment fixture"))
            {
                tx.Start();columnSymbol.Activate();doc.Regenerate();
                var top=Level.Create(doc,level.Elevation+15);
                column=doc.Create.NewFamilyInstance(new XYZ(5,700,level.Elevation),columnSymbol,level,StructuralType.Column);
                column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).Set(top.Id);
                var ft=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t=>!t.IsFoundationSlab);
                support=Floor.Create(doc,new[]{Rectangle(0,695,20,710,level.Elevation)},ft.Id,level.Id);
                support.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(10);
                doc.Regenerate();ColumnAttachment.AddColumnAttachment(doc,column,support,1,(ColumnAttachmentCutStyle)0,ColumnAttachmentJustification.Minimum,0.25);
                Check(tx.Commit()==TransactionStatus.Committed,"Column attach fixture commit");
            }
            ElementHistoryRelations.Invalidate(doc);
            var rs=Request(support);var rp=Request(column);
            var expectedAttach=ColumnAttachment.GetColumnAttachment(column,1);
            var attachStyle=expectedAttach.CutStyle;var attachJustification=expectedAttach.Justification;var attachOffset=expectedAttach.AttachOffset;
            Check(rs.Recipe.GeometryRelations.Any(r=>r.Kind=="column_attach"),"Column target captures reverse attachment");
            using(var tx=new Transaction(doc,"Detach column for incremental cache"))
            { tx.Start();ColumnAttachment.RemoveColumnAttachment(column,1);tx.Commit(); }
            ElementHistoryRelations.Invalidate(doc,new[]{column.Id});
            Check(!ElementHistoryRelations.Capture(support,new List<string>()).Any(r=>r.Kind=="column_attach" && r.First==column.UniqueId),
                "Removed column attachment survived incremental cache refresh");
            using(var tx=new Transaction(doc,"Reattach column for incremental cache"))
            { tx.Start();ColumnAttachment.AddColumnAttachment(doc,column,support,1,attachStyle,attachJustification,attachOffset);tx.Commit(); }
            ElementHistoryRelations.Invalidate(doc,new[]{column.Id});
            Check(ElementHistoryRelations.Capture(support,new List<string>()).Any(r=>r.Kind=="column_attach" && r.First==column.UniqueId),
                "New column attachment missing from incremental cache refresh");
            results.Add("incremental attachment cache tracks removal and reattachment without a full model rebuild");
            for(int wave=0;wave<20 && rp.Recipe.Native?.Ready==false && rp.Recipe.Native.Failure==null;wave++) ElementHistoryNativeArchive.ProcessPending(doc);
            using(var tx=new Transaction(doc,"Delete column support")){tx.Start();doc.Delete(support.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{rs});
            Check(batch.Created==1 && batch.RelationFailures.Count==0,"Column support restoration: "+JsonConvert.SerializeObject(batch));
            support=(Floor)doc.GetElement(batch.Items.Single().UniqueId);
            var actualAttach=ColumnAttachment.GetColumnAttachment(column,1);
            Check(actualAttach?.TargetId==support.Id && actualAttach.CutStyle==attachStyle && actualAttach.Justification==attachJustification
                && Math.Abs(actualAttach.AttachOffset-attachOffset)<1e-7,"Column attachment settings preserved");
            results.Add("floor-only restoration reattaches surviving structural column with cut style, justification and offset");
            using(var tx=new Transaction(doc,"Delete attached column")){tx.Start();doc.Delete(column.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{rp});
            Check(batch.Created==1 && batch.RelationFailures.Count==0,"Column restoration: "+JsonConvert.SerializeObject(batch));
            column=(FamilyInstance)doc.GetElement(batch.Items.Single().UniqueId);
            Check(ColumnAttachment.GetColumnAttachment(column,1)?.TargetId==support.Id,"Restored column attached to earlier restored floor");
            results.Add("column-only restoration resolves its support restored in an earlier operation");
            var solidSymbol=CreateSolidCutFamily(ui,doc);
            FamilyInstance solidTarget,solidCutter;
            using(var tx=new Transaction(doc,"Solid cut fixture"))
            {
                tx.Start();solidSymbol.Activate();doc.Regenerate();
                solidTarget=AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc,solidSymbol);
                solidCutter=AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc,solidSymbol);
                ElementTransformUtils.MoveElement(doc,solidTarget.Id,new XYZ(0,750,0));
                ElementTransformUtils.MoveElement(doc,solidCutter.Id,new XYZ(1,751,1));
                doc.Regenerate();Check(SolidSolidCutUtils.CanElementCutElement(solidCutter,solidTarget,out var reason),"Solid cut fixture validity: "+reason);
                SolidSolidCutUtils.AddCutBetweenSolids(doc,solidTarget,solidCutter);
                Check(tx.Commit()==TransactionStatus.Committed,"Solid cut fixture commit");
            }
            var st=Request(solidTarget);var sc=Request(solidCutter);var solidVolume=Volume(solidTarget.get_Geometry(new Options()));
            Check(st.Recipe.GeometryRelations.Any(r=>r.Kind=="solid_cut" && r.First==solidCutter.UniqueId),"Solid cut capture");
            for(int wave=0;wave<20 && new[]{st,sc}.Any(r=>r.Recipe.Native?.Ready==false && r.Recipe.Native.Failure==null);wave++) ElementHistoryNativeArchive.ProcessPending(doc);
            using(var tx=new Transaction(doc,"Delete cutting solid")){tx.Start();doc.Delete(solidCutter.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{sc});
            Check(batch.Created==1 && batch.RelationFailures.Count==0,"Cutting solid restore: "+JsonConvert.SerializeObject(batch));
            solidCutter=(FamilyInstance)doc.GetElement(batch.Items.Single().UniqueId);
            Check(SolidSolidCutUtils.CutExistsBetweenElements(solidCutter,solidTarget,out var cutterFirst) && cutterFirst
                && Math.Abs(Volume(solidTarget.get_Geometry(new Options()))-solidVolume)<1e-5,"Solid cut direction and volume preserved");
            results.Add("solid cutter restored against surviving adaptive solid; cutting direction and volume preserved");
            using(var tx=new Transaction(doc,"Delete cut solid")){tx.Start();doc.Delete(solidTarget.Id);tx.Commit();}
            batch=ElementHistoryRestoration.Restore(doc,new[]{st});
            Check(batch.Created==1 && batch.RelationFailures.Count==0,"Cut solid restore: "+JsonConvert.SerializeObject(batch));
            solidTarget=(FamilyInstance)doc.GetElement(batch.Items.Single().UniqueId);
            Check(SolidSolidCutUtils.CutExistsBetweenElements(solidCutter,solidTarget,out cutterFirst) && cutterFirst,"Cut solid restored against earlier restored cutter");
            results.Add("solid target restored against cutter restored in an earlier operation");
        }

        private FamilySymbol CreateSolidCutFamily(UIApplication ui,Document project)
        {
            var template=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Autodesk",
                "RVT "+ui.Application.VersionNumber,"Family Templates","English","Metric Generic Model Adaptive.rft");
            var familyDoc=ui.Application.NewFamilyDocument(template);
            try
            {
                using(var tx=new Transaction(familyDoc,"Adaptive solid fixture"))
                {
                    tx.Start();familyDoc.FamilyManager.NewType("Cuttable solid");
                    var plane=SketchPlane.Create(familyDoc,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));
                    var refs=new ReferenceArray();
                    foreach(Curve curve in Rectangle(0,0,3,3,0)) refs.Append(familyDoc.FamilyCreate.NewModelCurve(curve,plane).GeometryCurve.Reference);
                    familyDoc.FamilyCreate.NewExtrusionForm(true,refs,new XYZ(0,0,4));
                    Check(tx.Commit()==TransactionStatus.Committed,"Adaptive solid fixture commit");
                }
                familyDoc.SaveAs(Path.Combine(Output,"BIMaestro-solid-cut.rfa"),new SaveAsOptions());
                var family=familyDoc.LoadFamily(project);
                return (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());
            }
            finally{familyDoc.Close(false);}
        }

        private void VerifyExtendedElements(UIApplication ui, Document doc, List<string> results)
        {
            var roofSymbol = CreateFamily(ui, doc, "Metric Generic Model roof based.rft");
            var faceSymbol = CreateFamily(ui, doc, "Metric Generic Model face based.rft");
            var curveSymbol = CreateFamily(ui, doc, "Metric Generic Model line based.rft");
            var columnSymbol = CreateFamily(ui, doc, "Metric Generic Model two level based.rft");
            var requests = new List<HistoryRestoreRequest>();
            void Save(Element element)
            {
                string detail = null;
                var recipe = ElementHistoryReconstruction.Capture(element, s => detail = s);
                Check(recipe != null, "Extended fixture capture " + element.GetType().Name + ": " + detail);
                requests.Add(new HistoryRestoreRequest { SourceUniqueId = element.UniqueId, Label = element.GetType().Name, Recipe = recipe });
            }
            using (var tx = new Transaction(doc, "Create extended reconstruction fixtures"))
            {
                tx.Start();
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()));
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).First();
                var top = Level.Create(doc, level.ProjectElevation + 12);
                var roofType = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().First();
                var footprint = new CurveArray();
                foreach (var curve in Rectangle(300,300,320,320,level.ProjectElevation)) footprint.Append(curve);
                var mapping = new ModelCurveArray();
                var roof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out mapping);
                foreach (ModelCurve model in mapping) roof.set_DefinesSlope(model, false);
                roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM).Set(3);
                doc.Regenerate();
                Verify(doc, roof, "flat footprint roof with level offset", results);
                roofSymbol.Activate(); faceSymbol.Activate(); curveSymbol.Activate(); columnSymbol.Activate(); doc.Regenerate();
                var roofHosted = doc.Create.NewFamilyInstance(new XYZ(304,304,level.ProjectElevation+3), roofSymbol, roof, level, StructuralType.NonStructural);
                doc.Regenerate();
                Verify(doc, roofHosted, "roof hosted generic family", results);
                var topFace = HostObjectUtils.GetTopFaces(roof).First();
                var face = (Face)roof.GetGeometryObjectFromReference(topFace);
                var point = face.Project(new XYZ(312,312,level.ProjectElevation+3)).XYZPoint;
                var faceHosted = doc.Create.NewFamilyInstance(topFace, point, XYZ.BasisX, faceSymbol);
                var linear = doc.Create.NewFamilyInstance(Line.CreateBound(new XYZ(330,300,level.ProjectElevation),new XYZ(350,300,level.ProjectElevation)),curveSymbol,level,StructuralType.NonStructural);
                var column = doc.Create.NewFamilyInstance(new XYZ(360,300,level.ProjectElevation),columnSymbol,level,StructuralType.NonStructural);
                column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(top.Id);
                doc.Regenerate();
                Verify(doc, faceHosted, "face hosted generic family", results);
                Verify(doc, linear, "line based generic family", results);
                Verify(doc, column, "two level generic family", results);
                Save(roofHosted); Save(faceHosted); Save(linear); Save(column); Save(roof);
                foreach (var saved in requests.Last().Recipe.SketchCurves)
                    requests.Insert(0, new HistoryRestoreRequest { SourceUniqueId = saved.SourceUniqueId, Label = "Sketch child", Category = "<Esquisse>", CaptureFailure = "Historical sketch member" });
                var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
                var profilePoints = new[] { new XYZ(400,300,level.ProjectElevation), new XYZ(420,300,level.ProjectElevation),
                    new XYZ(420,300,level.ProjectElevation+9), new XYZ(410,300,level.ProjectElevation+12), new XYZ(400,300,level.ProjectElevation+9) };
                var wall = Wall.Create(doc, Enumerable.Range(0,profilePoints.Length).Select(i=>(Curve)Line.CreateBound(profilePoints[i],profilePoints[(i+1)%profilePoints.Length])).ToList(),wallType.Id,level.Id,false,XYZ.BasisY);
                WallUtils.DisallowWallJoinAtEnd(wall,0); WallUtils.DisallowWallJoinAtEnd(wall,1);
                doc.Regenerate(); Verify(doc,wall,"wall with a non-rectangular sketch profile",results); Save(wall);
                var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t=>!t.IsFoundationSlab);
                var shaped = Floor.Create(doc,new[] { Rectangle(430,300,450,320,level.ProjectElevation) },floorType.Id,level.Id);
                doc.Regenerate();
#if REVIT2024 || REVIT2025_OR_GREATER
                var editor = shaped.GetSlabShapeEditor();
#else
                var editor = shaped.SlabShapeEditor;
#endif
                editor.Enable(); doc.Regenerate();
                var baseZ=editor.SlabShapeVertices.Cast<SlabShapeVertex>().First().Position.Z;
                var vertex=editor.DrawPoint(new XYZ(440,310,baseZ));
                Check(vertex!=null,"Shape fixture point"); editor.ModifySubElement(vertex,2); doc.Regenerate();
                Verify(doc,shaped,"floor modified by an interior shape point",results); Save(shaped);
                var plane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,new XYZ(0,0,level.ProjectElevation)));
                var modelCurve=doc.Create.NewModelCurve(Arc.Create(new XYZ(470,300,level.ProjectElevation),new XYZ(490,300,level.ProjectElevation),new XYZ(480,305,level.ProjectElevation)),plane);
                doc.Regenerate(); Save(modelCurve);
                var ceilingType = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).FirstElementId();
                if (ceilingType != ElementId.InvalidElementId)
                {
                    var ceiling = Ceiling.Create(doc,new[] { Rectangle(370,300,390,320,level.ProjectElevation), Rectangle(375,305,380,310,level.ProjectElevation) },ceilingType,level.Id);
                    ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM).Set(9);
                    doc.Regenerate(); Verify(doc, ceiling, "ceiling with hole and offset", results); Save(ceiling);
                }
                Check(tx.Commit()==TransactionStatus.Committed,"Extended fixture commit");
            }
            var originals = requests.Where(r=>r.Recipe!=null).Select(r=>doc.GetElement(r.SourceUniqueId).Id).ToList();
            using(var tx=new Transaction(doc,"Delete extended fixtures"))
            { tx.Start(); doc.Delete(originals); Check(tx.Commit()==TransactionStatus.Committed,"Extended fixture deletion"); }
            requests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(JsonConvert.SerializeObject(requests));
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Failed==0 && batch.Created==requests.Count(r=>r.Recipe!=null) && batch.IncludedInParent==4,
                "Extended restoration with children before parents: "+JsonConvert.SerializeObject(batch));
            var roofItem=batch.Items.Single(i=>i.Label=="FootPrintRoof");
            var restoredHost=doc.GetElement(roofItem.UniqueId);
            foreach(var item in batch.Items.Where(i=>i.Label=="FamilyInstance"))
            {
                var restored=(FamilyInstance)doc.GetElement(item.UniqueId);
                var request=requests.Single(r=>r.SourceUniqueId==item.SourceUniqueId);
                if(request.Recipe.Host!=null)
                {
                    var expectedHost=doc.GetElement(request.Recipe.Host)
                        ?? doc.GetElement(batch.Items.Single(i=>i.SourceUniqueId==request.Recipe.Host).UniqueId);
                    Check(restored.Host?.Id==expectedHost.Id,"Family must use its original or restored support");
                }
                if(request.Recipe.Placement==FamilyPlacementType.CurveBased.ToString())
                {
                    var actual=((LocationCurve)restored.Location).Curve;
                    var saved=ElementHistoryReconstruction.RestoreCurve(request.Recipe.Loops.Single().Single());
                    Check(actual.GetEndPoint(0).DistanceTo(saved.GetEndPoint(0))<1e-6
                        && actual.GetEndPoint(1).DistanceTo(saved.GetEndPoint(1))<1e-6,"Line based family placement curve changed");
                }
            }
            var repeated=ElementHistoryRestoration.Restore(doc,requests);
            Check(repeated.Created==0 && repeated.Failed==0 && repeated.Existing==requests.Count,"Extended restoration duplicated parent or sketch children");
            var newerRequests=batch.Items.Select(i=>
            {
                var element=doc.GetElement(i.UniqueId);
                return new HistoryRestoreRequest { SourceUniqueId=element.UniqueId,Label=i.Label,Category=element.Category?.Name,
                    Recipe=ElementHistoryReconstruction.Capture(element) };
            }).ToList();
            using(var tx=new Transaction(doc,"Delete restored extended fixtures"))
            { tx.Start(); doc.Delete(batch.Items.Select(i=>doc.GetElement(i.UniqueId).Id).ToList()); Check(tx.Commit()==TransactionStatus.Committed,"Repeated extended deletion"); }
            newerRequests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(JsonConvert.SerializeObject(newerRequests));
            var newerBatch=ElementHistoryRestoration.Restore(doc,newerRequests);
            Check(newerBatch.Failed==0,"Restore latest extended history: "+JsonConvert.SerializeObject(newerBatch));
            var olderBatch=ElementHistoryRestoration.Restore(doc,requests);
            Check(olderBatch.Created==0 && olderBatch.Failed==0 && olderBatch.Existing==requests.Count,"Older sketch identities lost after a second deletion/restoration");
            var calo=ElementHistoryRestoration.Restore(doc,new[] {
                new HistoryRestoreRequest{SourceUniqueId="calo-generic",Category="Modèles génériques",Label="CML_Calorifuge [1]"},
                new HistoryRestoreRequest{SourceUniqueId="calo-pipe",Category="Pipe Insulations",Label="Pipe insulation"},
                new HistoryRestoreRequest{SourceUniqueId="calo-duct",Category="Duct Insulations",Label="Duct insulation"} });
            Check(calo.Items.Count==0 && calo.Failed==0,"Calorifuge must stay deleted");
            results.Add("roof, roof/face hosted, line and two-level families, ceiling; dependency ordering; sketch ownership and duplicate prevention");
            results.Add("older sketch origins preserved across repeated deletion/restoration; calorifuge remains excluded");
        }

        private void VerifyNativeArchive(UIApplication ui, Document doc, List<string> results)
        {
            var roots = new List<Element>();
            Pipe outsidePeer=null;
            Level level, top;
            using (var tx = new Transaction(doc,"Native archive fixtures"))
            {
                tx.Start();
                level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                top = Level.Create(doc,level.Elevation+10);
                var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
                var wall = Wall.Create(doc,Line.CreateBound(new XYZ(0,0,0),new XYZ(20,0,0)),wallType.Id,level.Id,10,0,false,false);
                var other = Wall.Create(doc,Line.CreateBound(new XYZ(0,10,0),new XYZ(20,10,0)),wallType.Id,level.Id,10,0,false,false);
                var calo = DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
                calo.Name = "CML_Calorifuge";
                calo.SetShape(new GeometryObject[]{GeometryCreationUtilities.CreateExtrusionGeometry(new[]{Rectangle(3,3,5,5,0)},XYZ.BasisZ,2)});
                var nested=doc.Create.NewGroup(new[]{wall.Id,other.Id});
                roots.Add(doc.Create.NewGroup(new[]{nested.Id,calo.Id}));
                var curtainType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Curtain);
                roots.Add(Wall.Create(doc,Line.CreateBound(new XYZ(40,0,0),new XYZ(60,0,0)),curtainType.Id,level.Id,10,0,false,false));
                var shape = DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
                shape.SetShape(new GeometryObject[]{GeometryCreationUtilities.CreateExtrusionGeometry(new[]{Rectangle(80,0,85,5,0)},XYZ.BasisZ,7)});
                roots.Add(shape);
                var railType = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).First();
                roots.Add(Railing.Create(doc,CurveLoop.Create(new List<Curve>{Line.CreateBound(new XYZ(100,0,0),new XYZ(120,0,0))}),railType.Id,level.Id));
                var pipeType=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                var systemType=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
                var p1=Pipe.Create(doc,systemType,pipeType,level.Id,new XYZ(200,0,5),new XYZ(210,0,5));
                var p2=Pipe.Create(doc,systemType,pipeType,level.Id,new XYZ(210,0,5),new XYZ(220,0,5));
                outsidePeer=Pipe.Create(doc,systemType,pipeType,level.Id,new XYZ(220,0,5),new XYZ(230,0,5));
                ElementHistoryNetwork.Ports(p1).OrderBy(p=>p.Origin.X).Last().ConnectTo(ElementHistoryNetwork.Ports(p2).OrderBy(p=>p.Origin.X).First());
                ElementHistoryNetwork.Ports(p2).OrderBy(p=>p.Origin.X).Last().ConnectTo(ElementHistoryNetwork.Ports(outsidePeer).OrderBy(p=>p.Origin.X).First());
                roots.Add(doc.Create.NewGroup(new[]{p1.Id,p2.Id}));
                Check(tx.Commit()==TransactionStatus.Committed,"Native fixture commit");
            }
            using (var scope = new StairsEditScope(doc,"Native stair fixture"))
            {
                var id = scope.Start(level.Id,top.Id);
                using(var tx=new Transaction(doc,"Stair run"))
                { tx.Start(); var stair=(Stairs)doc.GetElement(id); var length=(stair.DesiredRisersNumber-1)*stair.ActualTreadDepth;
                    StairsRun.CreateStraightRun(doc,id,Line.CreateBound(new XYZ(140,0,level.Elevation),new XYZ(140+length,0,level.Elevation)),StairsRunJustification.Center); tx.Commit(); }
                scope.Commit(new FixtureFailures());
                roots.Add(doc.GetElement(id));
            }
            using(var tx=new Transaction(doc,"Isolate native fixture geometry"))
            { tx.Start();ElementTransformUtils.MoveElements(doc,roots.Select(e=>e.Id).Concat(new[]{outsidePeer.Id}).ToList(),new XYZ(1000,1000,0));tx.Commit(); }
            var requests = roots.Select(e=>new HistoryRestoreRequest{SourceUniqueId=e.UniqueId,Label=e.GetType().Name,
                Recipe=ElementHistoryReconstruction.Capture(e)}).ToList();
            // Capture every group/stair/curtain component through the production fallback.
            var group = roots.OfType<Group>().First();
            foreach(var id in group.GetMemberIds())
            {
                var e=doc.GetElement(id); requests.Add(new HistoryRestoreRequest{SourceUniqueId=e.UniqueId,Label="group member",Recipe=ElementHistoryReconstruction.Capture(e)});
                if(e.Name.Contains("Calorifuge")) requests.RemoveAt(requests.Count-1);
            }
            var stairs=roots.OfType<Stairs>().Single();
            foreach(var id in stairs.GetStairsRuns())
            {
                var e=doc.GetElement(id);requests.Add(new HistoryRestoreRequest{SourceUniqueId=e.UniqueId,Label="stair run",Recipe=ElementHistoryReconstruction.Capture(e)});
            }
            var curtain=roots.OfType<Wall>().Single();
            foreach(var id in curtain.CurtainGrid.GetPanelIds())
            {
                var e=doc.GetElement(id); requests.Add(new HistoryRestoreRequest{SourceUniqueId=e.UniqueId,Label="curtain panel",Recipe=ElementHistoryReconstruction.Capture(e)});
            }
            var before=Ids(doc);
            var volumes=requests.ToDictionary(r=>r.SourceUniqueId,r=>Volume(doc.GetElement(r.SourceUniqueId).get_Geometry(new Options())));
            for(int wave=0;wave<20 && requests.Any(r=>r.Recipe?.Native?.Ready==false && r.Recipe.Native.Failure==null);wave++) ElementHistoryNativeArchive.ProcessPending(doc);
            Check(before.SequenceEqual(Ids(doc)),"Archiving changed source model");
            Check(requests.All(r=>r.Recipe?.Native?.Ready==true),"Archive receipt failure: "+JsonConvert.SerializeObject(requests));
            // After one fallback exists, edits to supported objects must not start
            // archiving the entire model. This reproduced the continuous idle load.
            Wall ordinary;
            using(var tx=new Transaction(doc,"Supported object alongside native archive"))
            {
                tx.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
                ordinary=Wall.Create(doc,Line.CreateBound(new XYZ(2000,2000,0),new XYZ(2020,2000,0)),type.Id,level.Id,10,0,false,false);
                tx.Commit();
            }
            var directory=Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-history");
            var archiveCount=Directory.GetFiles(directory,"snapshot-*.rvt").Length;
            Check(ElementHistoryReconstruction.Capture(ordinary)?.Kind=="wall","Supported wall unexpectedly needs a native fallback");
            ElementHistoryNativeArchive.Invalidate(doc,new[]{ordinary.Id,outsidePeer.Id});
            ElementHistoryNativeArchive.ProcessPending(doc);
            Check(Directory.GetFiles(directory,"snapshot-*.rvt").Length==archiveCount
                && ElementHistoryNativeArchive.Find(doc,ordinary.UniqueId)==null && ElementHistoryNativeArchive.Find(doc,outsidePeer.UniqueId)==null,
                "Ordinary edits queued unnecessary native archives");
            results.Add("supported wall and pipe edits do not create native archives after fallback initialization");
            File.WriteAllText(Path.Combine(Output,"native-recipes.json"),JsonConvert.SerializeObject(requests,Formatting.Indented));
            requests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(JsonConvert.SerializeObject(requests));
            using(var tx=new Transaction(doc,"Delete native archive fixtures"))
            { tx.Start();doc.Delete(roots.Select(e=>e.Id).ToList());Check(tx.Commit()==TransactionStatus.Committed,"Native deletion"); }
            ElementHistoryNativeArchive.Forget(doc);
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            File.WriteAllText(Path.Combine(Output,"native-restoration.json"),JsonConvert.SerializeObject(batch,Formatting.Indented));
            Check(batch.Failed==0,"Native restore failed: "+JsonConvert.SerializeObject(batch));
            Check(batch.ConnectionFailures.Count==0,"Grouped MEP reconnection failed: "+string.Join("; ",batch.ConnectionFailures));
            var peerPort=ElementHistoryNetwork.Ports(outsidePeer).OrderBy(p=>p.Origin.X).First();
            Check(peerPort.IsConnected,"Grouped pipe lost connection to its existing external neighbor");
            foreach(var r in requests)
            {
                var item=batch.Items.Single(i=>i.SourceUniqueId==r.SourceUniqueId);
                Check(doc.GetElement(item.UniqueId)!=null,"Restored native component missing");
                var restoredVolume=Volume(doc.GetElement(item.UniqueId).get_Geometry(new Options()));
                Check(Math.Abs(restoredVolume-volumes[r.SourceUniqueId])<1e-4,"Native geometry changed: "+r.Label+" "+volumes[r.SourceUniqueId]+" -> "+restoredVolume);
                results.Add("native archive: "+r.Label+"; identity preserved");
            }
            var again=ElementHistoryRestoration.Restore(doc,requests);
            Check(again.Created==0 && again.Existing==requests.Count && again.Failed==0,"Native restoration duplicated roots/members");
            Check(!new FilteredElementCollector(doc).WhereElementIsNotElementType().Any(e=>e.Name=="CML_Calorifuge"),"Native group restored calorifuge");
            var restoredGroup=(Group)doc.GetElement(batch.Items.Single(i=>i.SourceUniqueId==requests[0].SourceUniqueId).UniqueId);
            Check(restoredGroup.GetMemberIds().Count==1,"Filtered outer group retained calorifuge or lost nested group");
            var restoredNested=doc.GetElement(restoredGroup.GetMemberIds().Single()) as Group;
            Check(restoredNested!=null && restoredNested.GetMemberIds().Count==2,"Filtered nested group lost useful walls");
            results.Add("native archives remain usable after serialization and cache disposal; repeated restore creates no duplicates; source unchanged");
            results.Add("native group retains its two walls and excludes embedded calorifuge");
            results.Add("native MEP group preserves its internal connection and reconnects the existing external pipe");
            var newer=batch.Items.Select(item=>
            {
                var element=doc.GetElement(item.UniqueId);
                return new HistoryRestoreRequest { SourceUniqueId=element.UniqueId,Label=item.Label,Recipe=ElementHistoryReconstruction.Capture(element) };
            }).ToList();
            for(int wave=0;wave<20 && newer.Any(r=>r.Recipe?.Native?.Ready==false && r.Recipe.Native.Failure==null);wave++) ElementHistoryNativeArchive.ProcessPending(doc);
            Check(newer.All(r=>r.Recipe?.Native?.Ready==true),"Native recapture failed");
            using(var tx=new Transaction(doc,"Delete restored native objects again"))
            { tx.Start();doc.Delete(batch.Items.Where(i=>i.Created).Select(i=>doc.GetElement(i.UniqueId).Id).ToList());tx.Commit(); }
            var newerBatch=ElementHistoryRestoration.Restore(doc,newer);
            Check(newerBatch.Failed==0 && newerBatch.ConnectionFailures.Count==0,"Native second restoration failed: "+JsonConvert.SerializeObject(newerBatch));
            var olderBatch=ElementHistoryRestoration.Restore(doc,requests);
            Check(olderBatch.Created==0 && olderBatch.Existing==requests.Count && olderBatch.Failed==0,"Native older identities lost after second deletion");
            results.Add("nested groups and original native component identities preserved through a second deletion/restoration");
        }

        private void VerifyLogicalSystems(Document doc,List<string> results)
        {
            var members=new List<Element>();
            using(var tx=new Transaction(doc,"Logical system fixtures"))
            {
                tx.Start();
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();
                var pipeType=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                var pipeSystemType=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
                var ductType=new FilteredElementCollector(doc).OfClass(typeof(DuctType)).FirstElementId();
                var ductSystemType=new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
                members.Add(Pipe.Create(doc,pipeSystemType,pipeType,level,new XYZ(200,100,10),new XYZ(210,100,10)));
                members.Add(Duct.Create(doc,ductSystemType,ductType,level,new XYZ(200,120,10),new XYZ(210,120,10)));
                Check(tx.Commit()==TransactionStatus.Committed,"Logical fixtures commit");
            }
            var systems=members.SelectMany(ElementHistoryNetwork.Ports).Select(p=>p.MEPSystem).Where(s=>s!=null).GroupBy(s=>s.UniqueId).Select(g=>g.First()).ToList();
            Check(systems.Count==2,"Missing pipe/duct logical fixtures");
            var requests=systems.Cast<Element>().Concat(members).Select(e=>new HistoryRestoreRequest{SourceUniqueId=e.UniqueId,Label=e.GetType().Name,Recipe=ElementHistoryReconstruction.Capture(e)}).ToList();
            Check(requests.All(r=>r.Recipe!=null),"Missing logical system recipe");
            using(var tx=new Transaction(doc,"Delete logical networks"))
            { tx.Start(); doc.Delete(members.Select(e=>e.Id).ToList());tx.Commit(); }
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Failed==0,"Logical system restoration failed: "+JsonConvert.SerializeObject(batch));
            foreach(var request in requests.Where(r=>r.Recipe.SystemMembers!=null))
            {
                var item=batch.Items.Single(i=>i.SourceUniqueId==request.SourceUniqueId);
                var system=doc.GetElement(item.UniqueId) as MEPSystem;
                Check(system!=null && system.Name==request.Recipe.SystemName && system.GetTypeId()==doc.GetElement(request.Recipe.Type).Id,"Logical system name/type not preserved");
                results.Add("logical "+request.Recipe.Kind+": members, type, name and historical identity restored");
            }
            var again=ElementHistoryRestoration.Restore(doc,requests);
            Check(again.Created==0 && again.Existing==requests.Count && again.Failed==0,"Logical systems duplicated on second restore");
        }

        private void VerifyElectricalSystems(UIApplication ui, Document doc, List<string> results)
        {
            VerifyElectricalSystem(ui,doc,results,false);
            VerifyElectricalSystem(ui,doc,results,true);
        }

        private void VerifyElectricalSystem(UIApplication ui, Document doc, List<string> results, bool power)
        {
            var firstResult=results.Count;
            var kind=power ? ElectricalSystemType.PowerCircuit : ElectricalSystemType.Data;
            var fixture = CreateFamily(ui,doc,"Metric Electrical Fixture.rft",electrical:true,power:power);
            var panelType = CreateFamily(ui,doc,"Metric Electrical Equipment.rft",electrical:true,power:power);
            List<HistoryRestoreRequest> requests;
            double expectedVoltage;
            using(var tx=new Transaction(doc,"Electrical circuit fixtures"))
            {
                tx.Start(); fixture.Activate(); panelType.Activate(); doc.Regenerate();
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                var panel=doc.Create.NewFamilyInstance(new XYZ(500,500,0),panelType,level,StructuralType.NonStructural);
                if(power)
                {
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_MAX_POLE_BREAKERS)?.Set(12);
                    var settings=ElectricalSetting.GetElectricalSettings(doc);
                    var voltage=settings.AddVoltageType("History 230",230,220,240);
                    var lineVoltage=settings.AddVoltageType("History 400",400,380,420);
                    var distribution=settings.AddDistributionSysType("History three phase",ElectricalPhase.ThreePhase,ElectricalPhaseConfiguration.Wye,4,lineVoltage,voltage);
                    doc.Regenerate();
                    File.WriteAllText(Path.Combine(Output,"panel-parameters.json"),JsonConvert.SerializeObject(panel.Parameters.Cast<Parameter>().Select(p=>new{Name=p.Definition.Name,Id=p.Id.GetIdLongValue(),Value=p.AsValueString()})));
                    ((ElectricalEquipment)panel.MEPModel).DistributionSystem=distribution;
                }
                var a=doc.Create.NewFamilyInstance(new XYZ(510,500,0),fixture,level,StructuralType.NonStructural);
                var b=doc.Create.NewFamilyInstance(new XYZ(520,500,0),fixture,level,StructuralType.NonStructural);
                doc.Regenerate();
                var system=ElectricalSystem.Create(doc,new[]{a.Id,b.Id},kind);
                system.SelectPanel(panel);
                system.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.Set("History data circuit");
                doc.Regenerate();
                if(power)
                {
                    var schedule=PanelScheduleView.CreateInstanceView(doc,panel.Id);
                    IList<int> rows,cols,targetRows,targetCols;
                    schedule.GetCellsBySlotNumber(system.StartSlot,out rows,out cols);
                    schedule.GetCellsBySlotNumber(7,out targetRows,out targetCols);
                    Check(rows.Count>0 && targetRows.Count>0 && schedule.CanMoveSlotTo(rows[0],cols[0],targetRows[0],targetCols[0]),"Power slot fixture unavailable");
                    schedule.MoveSlotTo(rows[0],cols[0],targetRows[0],targetCols[0]);
                    doc.Regenerate();
                    system.SetCircuitPath(system.GetCircuitPath());
                }
                requests=new Element[]{system,a,b,panel}.Select(e=>new HistoryRestoreRequest{SourceUniqueId=e.UniqueId,Label=e.GetType().Name,Recipe=ElementHistoryReconstruction.Capture(e)}).ToList();
                expectedVoltage=power ? system.Voltage : 0;
                Check(requests.All(r=>r.Recipe!=null),"Missing electrical fixture recipe");
                Check(tx.Commit()==TransactionStatus.Committed,"Electrical fixtures commit");
            }
            requests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(JsonConvert.SerializeObject(requests));
            var circuitRequest=requests[0];
            File.WriteAllText(Path.Combine(Output,power ? "power-requests.json" : "data-requests.json"),JsonConvert.SerializeObject(requests,Formatting.Indented));
            // First recreate only the logical circuit while its physical components survive.
            using(var tx=new Transaction(doc,"Delete only the circuit"))
            { tx.Start(); doc.Delete(doc.GetElement(circuitRequest.SourceUniqueId).Id); tx.Commit(); }
            var only=ElementHistoryRestoration.Restore(doc,new[]{circuitRequest});
            Check(only.Created==1 && only.Failed==0,"Circuit-only restore failed: "+JsonConvert.SerializeObject(only));
            results.Add("electrical circuit restored on surviving equipment and panel");
            var current=(ElectricalSystem)doc.GetElement(only.Items[0].UniqueId);
            Check(current.BaseEquipment.UniqueId==circuitRequest.Recipe.Host && current.Elements.Size==2,"Electrical members/panel mismatch");
            Check(current.LoadName=="History data circuit","Electrical load name mismatch");
            var memberRequest=requests[1];
            using(var tx=new Transaction(doc,"Delete one circuit member"))
            {tx.Start();current.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME).Set("Surviving edited circuit");doc.Delete(doc.GetElement(memberRequest.SourceUniqueId).Id);tx.Commit();}
            Check(current.Elements.Size==1,"Expected surviving circuit after deleting one member");
            var deviceBatch=ElementHistoryRestoration.Restore(doc,new[]{memberRequest});
            Check(deviceBatch.Failed==0 && current.Elements.Size==2,"Deleted electrical member not reattached: "+JsonConvert.SerializeObject(deviceBatch));
            Check(current.LoadName=="Surviving edited circuit","Surviving circuit user edits overwritten");
            results.Add("restoring a device alone also reattaches it to its surviving circuit");
            using(var tx=new Transaction(doc,"Delete electrical network"))
            { tx.Start();doc.Delete(requests.Skip(1).Select(r=>doc.GetElement(r.SourceUniqueId)
                ?? doc.GetElement(deviceBatch.Items.FirstOrDefault(i=>i.SourceUniqueId==r.SourceUniqueId)?.UniqueId)).Select(e=>e.Id).Concat(new[]{current.Id}).ToList());tx.Commit(); }
            var batch=ElementHistoryRestoration.Restore(doc,requests);
            Check(batch.Failed==0 && batch.Created==requests.Count,"Electrical network restore failed: "+JsonConvert.SerializeObject(batch));
            current=(ElectricalSystem)doc.GetElement(batch.Items.Single(i=>i.SourceUniqueId==circuitRequest.SourceUniqueId).UniqueId);
            Check(current.BaseEquipment.Id==doc.GetElement(batch.Items.Single(i=>i.SourceUniqueId==circuitRequest.Recipe.Host).UniqueId).Id
                && current.Elements.Size==2 && current.SystemType==kind && current.LoadName=="History data circuit","Restored electrical network mismatch");
            if(power) Check(current.PolesNumber==1 && Math.Abs(current.Voltage-expectedVoltage)<1e-6
                && current.StartSlot==circuitRequest.Recipe.ElectricalStartSlot && current.CircuitNumber==circuitRequest.Recipe.ElectricalCircuitNumber,"Power circuit voltage/poles/slot/number mismatch");
            results.Add("electrical equipment and panel restored before circuit; members, type and load name preserved through JSON");
            var again=ElementHistoryRestoration.Restore(doc,requests);
            Check(again.Created==0 && again.Existing==requests.Count && again.Repaired==0,"Electrical circuit duplicated or unnecessarily repaired");
            results.Add("electrical circuit restoration is idempotent");
            using(var tx=new Transaction(doc,"Missing equipment electrical fixture"))
            { tx.Start();doc.Delete(current.Id);tx.Commit(); }
            var missing=JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(circuitRequest));
            missing.Recipe.SystemMembers.Add(Guid.NewGuid().ToString()+"-00000001");
            var missingBatch=ElementHistoryRestoration.Restore(doc,new[]{missing});
            Check(missingBatch.Failed==1 && new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).GetElementCount()==0,"Incomplete electrical circuit was created");
            results.Add("missing electrical member rolls back instead of creating an incomplete circuit");
            var missingPanel=JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(circuitRequest));
            missingPanel.Recipe.Host=Guid.NewGuid().ToString()+"-00000001";
            Check(ElementHistoryRestoration.Restore(doc,new[]{missingPanel}).Failed==1,"Missing electrical panel accepted");
            results.Add("missing electrical panel does not create a disconnected circuit");
            using(var tx=new Transaction(doc,"Conflicting circuit fixture"))
            {
                tx.Start();
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                var third=doc.Create.NewFamilyInstance(new XYZ(530,500,0),fixture,level,StructuralType.NonStructural);
                doc.Regenerate();
                var member=doc.GetElement(batch.Items.Single(i=>i.SourceUniqueId==requests[1].SourceUniqueId).UniqueId);
                ElectricalSystem.Create(doc,new[]{member.Id,third.Id},kind);
                tx.Commit();
            }
            var conflict=ElementHistoryRestoration.Restore(doc,new[]{circuitRequest});
            Check(conflict.Failed==1 && new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).GetElementCount()==1,"Surviving electrical circuit was overwritten");
            results.Add("equipment already in a different circuit is preserved and conflict reported");
            using(var tx=new Transaction(doc,"Clean electrical conflict fixture"))
            {tx.Start();doc.Delete(new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).ToElementIds());tx.Commit();}
            for(var i=firstResult;i<results.Count;i++) results[i]=(power ? "power circuit: " : "data circuit: ")+results[i];
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
