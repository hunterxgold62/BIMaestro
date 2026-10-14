using Analyse;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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
    public sealed partial class HistoryNativeTests
    {
        private void VerifyNativeCostOrdering(Document doc,List<string> results)
        {
            Group group;
            DirectShape shape;
            using(var tx=new Transaction(doc,"Native cost fixtures"))
            {
                tx.Start();
                var level=Level.Create(doc,0);
                var type=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
                var first=Wall.Create(doc,Line.CreateBound(new XYZ(0,0,0),new XYZ(20,0,0)),type.Id,level.Id,10,0,false,false);
                var second=Wall.Create(doc,Line.CreateBound(new XYZ(0,20,0),new XYZ(20,20,0)),type.Id,level.Id,10,0,false,false);
                group=doc.Create.NewGroup(new[]{first.Id,second.Id});
                var loop=new CurveLoop();
                var points=new[]{new XYZ(50,0,0),new XYZ(55,0,0),new XYZ(55,5,0),new XYZ(50,5,0)};
                for(int i=0;i<4;i++) loop.Append(Line.CreateBound(points[i],points[(i+1)%4]));
                shape=DirectShape.CreateElement(doc,ElementIdExtensions.CreateElementId((long)BuiltInCategory.OST_GenericModel));
                shape.SetShape(new GeometryObject[]{GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},XYZ.BasisZ,5)});
                tx.Commit();
            }
            var heavy=ElementHistoryReconstruction.Capture(shape);
            var light=ElementHistoryReconstruction.Capture(group);
            Check(heavy?.Native!=null && light?.Native!=null,"Native ordering fixtures have no archive recipe");
            ElementHistoryNativeArchive.ProcessPending(doc,maxRoots:1);
            Check(light.Native.Ready && !heavy.Native.Ready,"Later lightweight group did not precede the earlier heavy-shape candidate");
            for(int attempt=0;attempt<4 && !heavy.Native.Ready;attempt++) ElementHistoryNativeArchive.ProcessPending(doc,maxRoots:1);
            Check(heavy.Native.Ready,"Heavy object was skipped rather than deferred");
            var requests=new[]{new HistoryRestoreRequest{SourceUniqueId=group.UniqueId,Label="Group",Recipe=light},
                new HistoryRestoreRequest{SourceUniqueId=shape.UniqueId,Label="Shape",Recipe=heavy}};
            requests=JsonConvert.DeserializeObject<HistoryRestoreRequest[]>(JsonConvert.SerializeObject(requests));
            using(var tx=new Transaction(doc,"Delete cost fixtures")){tx.Start();doc.Delete(new[]{group.Id,shape.Id});tx.Commit();}
            var restored=ElementHistoryRestoration.Restore(doc,requests);
            Check(restored.Failed==0 && restored.Created==2,"Cost ordering changed restoration: "+JsonConvert.SerializeObject(restored));
            HistoryPerformanceLog.Flush();
            var rows=Directory.GetFiles(Path.Combine(Output,"history-performance"),"*.jsonl")
                .SelectMany(File.ReadLines).Select(JObject.Parse).Where(row=>(string)row["Phase"]=="native_root").ToList();
            Check(rows.Count==2 && (string)rows[0]["ElementClass"]=="Group" && (string)rows[1]["ElementClass"]=="DirectShape",
                "Per-element timings missing or not in actual execution order");
            Check(rows.All(row=>row["ElementId"]?.Type==JTokenType.Integer && !string.IsNullOrEmpty((string)row["ElementUniqueId"])
                && (bool)row["Success"]),"Timing records lost identity or success");
            results.Add("light group captured before earlier heavier-class shape, both restored after JSON roundtrip; per-root timing identities and execution order verified");
        }

        private List<string> RunTrackerRegression(UIApplication ui)
        {
            var results = new List<string>();
            var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                var assembly = Assembly.Load(File.ReadAllBytes(File.ReadAllText(Path.Combine(Output,"production-dll.txt")).Trim()));
                var tracker = assembly.GetType("Analyse.ElementHistoryTracker", true);
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                var schedule = tracker.GetMethod("ScheduleDeferredPrime", flags);
                var audits = (System.Collections.IDictionary)tracker.GetField("PreloadAuditByDocumentKey",flags).GetValue(null);
                schedule.Invoke(null, new object[]{doc});
                Check(audits.Count == 1, "Initial document was not scheduled");
                doc.SaveAs(Path.Combine(Output,"save-as-regression.rvt"), new SaveAsOptions());
                schedule.Invoke(null, new object[]{doc});
                Check(audits.Count == 2, "Save As key did not acquire its own baseline");
                results.Add("baseline scheduled again after Save As changes the model key");

                var ids = Enumerable.Range(1000000,2000).Select(id=>ElementIdExtensions.CreateElementId(id)).ToList();
                var audit = tracker.GetMethod("BuildDeletionBatchAudit",flags).Invoke(null,new object[]{doc,ids,"Delete 2000 uncaptured elements"});
                var items = (System.Collections.IList)audit.GetType().GetProperty("Elements").GetValue(audit);
                var enqueue = tracker.GetMethod("EnqueueDeleted",flags);
                for(int i=0;i<ids.Count;i++) enqueue.Invoke(null,new object[]{doc,ids[i],"test","Delete 2000 uncaptured elements",items[i]});
                var queue = tracker.GetField("Queue",flags).GetValue(null);
                var events = (Array)queue.GetType().GetMethod("ToArray").Invoke(queue,null);
                Check(events.Length == 2000, "Uncaptured deletions were silently dropped: " + events.Length);
                var json = JArray.Parse(JsonConvert.SerializeObject(events));
                Check(json.All(ev=>(string)ev["Action"]=="delete" && (bool)ev["Delta"]["snapshotMissing"]), "Missing capture is not explicit");
                Check(json.All(ev=>(int)ev["DeletionAudit"]["BatchDeletedElementCount"]==2000
                    && (int)ev["DeletionAudit"]["BatchWithSnapshotCount"]==0), "Batch totals confused with capture totals");
                var cluster = assembly.GetType("Analyse.ElementHistoryWindow",true).GetMethod("GetClusterKey",flags);
                var eventType = events.GetValue(0).GetType();
                for(int i=0;i<events.Length;i++) eventType.GetProperty("Ts").SetValue(events.GetValue(i),DateTime.UtcNow.AddSeconds(i));
                Check(events.Cast<object>().Select(ev=>(string)cluster.Invoke(null,new[]{ev})).Distinct().Count()==1,
                    "One deletion batch splits at timestamp boundaries");
                results.Add("2000 uncaptured deletions retained, explicit missing-capture diagnosis, true batch count preserved, one batch across timestamp boundaries");
                var walls = new List<ElementId>();
                using(var tx = new Transaction(doc,"Selection preparation fixtures"))
                {
                    tx.Start();
                    var level = Level.Create(doc,0);
                    var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
                    for(int i=0;i<200;i++) walls.Add(Wall.Create(doc,
                        Line.CreateBound(new XYZ(i*30,0,0),new XYZ(i*30+20,0,0)),type.Id,level.Id,10,0,false,false).Id);
                    tx.Commit();
                }
                var captureSelection = tracker.GetMethod("CaptureSelectedElementDetails",flags);
                captureSelection.Invoke(null,new object[]{doc,walls});
                var snapshots = (System.Collections.IDictionary)tracker.GetField("SnapshotByElementId",flags).GetValue(null);
                var saved = snapshots.Values.Cast<object>().ToList();
                Check(saved.Count > 0, "Selection did not capture any simple recipe");
                var savedTimes = saved.Select(s=>s.GetType().GetProperty("CapturedUtc").GetValue(s)).ToList();
                captureSelection.Invoke(null,new object[]{doc,walls});
                Check(saved.All(s=>snapshots.Values.Cast<object>().Contains(s)), "Repeat selection replaced existing snapshots");
                Check(saved.Select(s=>s.GetType().GetProperty("CapturedUtc").GetValue(s)).SequenceEqual(savedTimes),
                    "Repeat selection recaptured existing data");
                var status = tracker.GetMethod("GetSelectionPreparationStatus",flags).Invoke(null,new object[]{doc});
                var statusFlags = BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
                Check((int)status.GetType().GetField("Total",statusFlags).GetValue(status)==200, "Selection progress total incorrect");
                Check((int)status.GetType().GetField("Pending",statusFlags).GetValue(status)>0, "Selection pending work hidden");
                results.Add("selection progress includes all 200 walls; selecting again preserves cached snapshots and their capture timestamps");
                var gate = assembly.GetType("Analyse.HistoryBackgroundWork",true);
                var pause = gate.GetField("Paused",flags);
                var beforeStop = snapshots.Count;
                pause.SetValue(null,true);
                captureSelection.Invoke(null,new object[]{doc,walls});
                Check(snapshots.Count==beforeStop,"Stop allowed new captures on selection");
                Check(saved.Select(s=>s.GetType().GetProperty("CapturedUtc").GetValue(s)).SequenceEqual(savedTimes),
                    "Stop discarded existing captures");
                pause.SetValue(null,false);
                results.Add("Stop prevents capturing missing selected walls and preserves existing snapshots");
                return results;
            }
            finally { doc.Close(false); }
        }

        // Only the saved copy placed in this run directory is opened. All destructive checks roll back.
        private List<string> RunRealModelBenchmark(UIApplication ui)
        {
            var results = new List<string>();
            var sourcePath = Path.Combine(Output,"real-model-source.rvt");
            var options = new OpenOptions();
            using (var info=BasicFileInfo.Extract(sourcePath))
                if(info.IsWorkshared) options.DetachFromCentralOption=DetachFromCentralOption.DetachAndPreserveWorksets;
            var watch=Stopwatch.StartNew();
            var doc=ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(sourcePath),options);
            var openMs=watch.Elapsed.TotalMilliseconds;
            var report=new Dictionary<string,object>{{"RevitVersion",ui.Application.VersionNumber},{"OpenMilliseconds",openMs}};
            void SaveProgress(string stage)
            {
                report["Stage"]=stage;
                File.WriteAllText(Path.Combine(Output,"real-model-benchmark.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
            }
            try
            {
                SaveProgress("opened-copy");
                var assembly=Assembly.Load(File.ReadAllBytes(File.ReadAllText(Path.Combine(Output,"production-dll.txt")).Trim()));
                var tracker=assembly.GetType("Analyse.ElementHistoryTracker",true);
                var flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static;
                var build=tracker.GetMethod("BuildSnapshot",flags);
                var invalidate=tracker.GetMethod("InvalidateCaptureCaches",flags);
                var getCache=tracker.GetMethod("GetTypeParameterCache",flags);
                var captureParameters=tracker.GetMethod("CaptureWritableParameters",flags);
                Check(build!=null && captureParameters!=null && invalidate!=null,"Real-model production capture hooks missing");
                var categories=doc.Settings.Categories.Cast<Category>().Where(c=>c.CategoryType==CategoryType.Model).Select(c=>c.Id).ToList();
                var ids=new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent()
                    .WherePasses(new ElementMulticategoryFilter(categories)).ToElementIds().ToList();
                report["AllNonTypeElements"]=new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
                report["ModelCandidates"]=ids.Count;
                report["CandidateClasses"]=ids.Select(doc.GetElement).Where(element=>element!=null)
                    .GroupBy(element=>element.GetType().Name).ToDictionary(group=>group.Key,group=>group.Count());
                var sample=ids.Select(doc.GetElement).Where(e=>e!=null).Take(200).ToList();
                var legacy=new List<object>();
                watch.Restart();
                foreach(var element in sample) legacy.Add(captureParameters.Invoke(null,new object[]{element,true,false}));
                report["LegacyParameterMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                invalidate.Invoke(null,new object[]{doc});
                watch.Restart();
                var optimized=sample.Select(element=>captureParameters.Invoke(null,new object[]{element,false,true})).ToList();
                report["OptimizedParameterMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                for(int i=0;i<sample.Count;i++)
                    Check(JToken.DeepEquals(JObject.FromObject(legacy[i]),JObject.FromObject(optimized[i])),
                        "Real-model parameter capture changed data: "+sample[i].UniqueId);
                report["ParameterEquivalenceSamples"]=sample.Count;
                results.Add("parameter enumeration/display cache preserves all history values on 200 real-model elements");

                invalidate.Invoke(null,new object[]{doc});
                var typeCache=(Dictionary<int,Dictionary<string,string>>)getCache.Invoke(null,new object[]{doc});
                var snapshots=new Dictionary<string,JObject>();
                var rawSnapshots=new Dictionary<string,object>();
                var histogram=new Dictionary<string,int>();
                var captureFailures=new List<object>();
                watch.Restart();
                foreach(var id in ids)
                {
                    var element=doc.GetElement(id);
                    if(element==null) continue;
                    try
                    {
                        var snapshot=build.Invoke(null,new object[]{element,false,0,false,typeCache});
                        rawSnapshots[element.UniqueId]=snapshot;
                        if(snapshot.GetType().GetProperty("Recipe").GetValue(snapshot)!=null)
                        {
                            var category=element.Category?.Name ?? "(none)";
                            histogram[category]=histogram.TryGetValue(category,out var count)?count+1:1;
                        }
                    }
                    catch(Exception ex) { captureFailures.Add(new {Uid=element.UniqueId,Error=(ex.InnerException??ex).Message}); }
                }
                report["BaselineMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                foreach(var pair in rawSnapshots) snapshots[pair.Key]=JObject.FromObject(pair.Value);
                report["CapturedSnapshots"]=snapshots.Count;
                report["BaselineDirectRecipes"]=histogram.Values.Sum();
                report["BaselineRecipesByCategory"]=histogram;
                report["TypeParameterReads"]=typeCache.Count;
                report["CaptureFailures"]=captureFailures;
                SaveProgress("baseline-complete");

                var audit=JObject.Parse(File.ReadAllText(Path.Combine(Output,"user-deletion-audit.json")));
                var selected=audit["Elements"].OfType<JObject>().Where(e=>e.Value<bool>("WasInLastSelection"))
                    .Select(e=>e.Value<string>("OriginalUniqueId")).Where(uid=>doc.GetElement(uid)!=null).Distinct().ToList();
                Check(selected.Count>0,"The saved test model contains none of the last selected objects");
                report["RequestedSelectionInSavedCopy"]=selected.Count;
                List<HistoryRestoreRequest> Requests() => selected.Select(uid=> {
                    snapshots.TryGetValue(uid,out var snapshot);
                    return new HistoryRestoreRequest {
                        SourceUniqueId=uid,Label=doc.GetElement(uid)?.Name ?? uid,
                        Recipe=snapshot?["Recipe"]?.Type==JTokenType.Object?snapshot["Recipe"].ToObject<HistoryRecipe>():null,
                        SuperComponentUniqueId=snapshot?.Value<string>("SuperComponentUniqueId"),
                        FamilyTypeUniqueId=snapshot?.Value<string>("FamilyTypeUniqueId")
                    };
                }).ToList();
                HistoryRestoreBatch DeleteAndRestore(List<HistoryRestoreRequest> requests)
                {
                    using(var group=new TransactionGroup(doc,"Isolated real-model restore validation"))
                    {
                        group.Start();
                        try
                        {
                            using(var tx=new Transaction(doc,"Delete saved-copy test selection"))
                            { tx.Start();doc.Delete(selected.Select(uid=>doc.GetElement(uid)?.Id).Where(id=>id!=null).ToList());tx.Commit(); }
                            return ElementHistoryRestoration.Restore(doc,requests);
                        }
                        finally { group.RollBack(); }
                    }
                }
                var baselineRequests=Requests();
                report["SelectionBaselineRecipes"]=baselineRequests.Count(request=>request.Recipe!=null);
                report["BaselineRestore"]=DeleteAndRestore(baselineRequests);
                SaveProgress("baseline-restoration-rolled-back");

                watch.Restart();
                foreach(var uid in selected)
                {
                    var element=doc.GetElement(uid);
                    snapshots.TryGetValue(uid,out var snapshot);
                    var recipe=snapshot?["Recipe"]?.Type==JTokenType.Object?snapshot["Recipe"].ToObject<HistoryRecipe>():null;
                    string diagnostic=null;
                    recipe=ElementHistoryReconstruction.Enrich(element,recipe,reason=>diagnostic=reason);
                    if(snapshot==null) snapshots[uid]=snapshot=new JObject();
                    snapshot["Recipe"]=recipe==null?JValue.CreateNull():JObject.FromObject(recipe);
                    snapshot["EnrichmentDiagnostic"]=diagnostic;
                }
                report["SelectionEnrichmentMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                var enrichedRequests=Requests();
                ElementHistoryNativeArchive.PrioritizeSelectedRoots(doc,DateTime.UtcNow,
                    enrichedRequests.Select(request=>request.Recipe?.Native?.RootSourceUniqueId).Where(uid=>uid!=null));
                // Retain the live receipt objects while archiving; serialize only once they are ready.
                foreach(var request in enrichedRequests.Where(request=>request.Recipe?.Kind=="native_archive"))
                    request.Recipe=ElementHistoryReconstruction.Capture(doc.GetElement(request.SourceUniqueId));
                watch.Restart();
                int waves=0;
                while(enrichedRequests.Any(request=>request.Recipe?.Native?.Ready==false && request.Recipe.Native.Failure==null)
                    && waves<64 && watch.Elapsed.TotalMinutes<5)
                {
                    ElementHistoryNativeArchive.ProcessPending(doc);
                    waves++;
                    report["ArchiveWaves"]=waves;
                    report["NativeArchiveMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                    SaveProgress("archiving-selected-complex-objects");
                }
                report["NativeArchiveMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                report["SelectionNativeReady"]=enrichedRequests.Count(request=>request.Recipe?.Native?.Ready==true);
                report["SelectionNativeNotReady"]=enrichedRequests.Count(request=>request.Recipe?.Native?.Ready==false);
                SaveProgress("archives-complete-restoring-selection");
                enrichedRequests=JsonConvert.DeserializeObject<List<HistoryRestoreRequest>>(JsonConvert.SerializeObject(enrichedRequests));
                watch.Restart();
                report["EnrichedRestore"]=DeleteAndRestore(enrichedRequests);
                report["SelectionRestoreMilliseconds"]=watch.Elapsed.TotalMilliseconds;
                SaveProgress("complete");
                HistoryPerformanceLog.Flush();
                results.Add("saved copy inventoried, selection deleted/restored twice with rollback; original source file never modified");
                return results;
            }
            finally { ElementHistoryNativeArchive.Shutdown(); doc.Close(false); }
        }
    }
}
