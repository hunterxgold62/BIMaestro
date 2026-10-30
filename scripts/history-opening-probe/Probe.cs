using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace BIMaestro.OpeningProbe
{
    // Explicitly launched and PID-gated. Changes are limited to disposable
    // clones; opened source and destination documents are closed without saving.
    public sealed class Probe : IExternalApplication
    {
        private UIControlledApplication App;
        private readonly string BaseFolder = Path.GetDirectoryName(typeof(Probe).Assembly.Location);
        private string Folder;
        private DateTime NextCommandUtc;
        private bool Busy;
        private ManualPreparationRun ManualRun;
        private ExternalEvent Wake;
        private System.Windows.Threading.DispatcherTimer Pulse;
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        public Result OnStartup(UIControlledApplication app)
        {
            App = app; app.Idling += Idle;
            Wake = ExternalEvent.Create(new WakeHandler(this));
            Pulse = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            Pulse.Tick += (_, _) => Wake.Raise(); Pulse.Start(); return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { Pulse?.Stop(); Wake?.Dispose(); app.Idling -= Idle; return Result.Succeeded; }
        private sealed class WakeHandler : IExternalEventHandler
        {
            private readonly Probe Owner; internal WakeHandler(Probe owner) { Owner=owner; }
            public string GetName() => "BIMaestro isolated history tests";
            public void Execute(UIApplication app) { Owner.Idle(app,null); }
        }
        private void Idle(object sender, IdlingEventArgs args)
        {
            if (ManualRun != null) { TickManualPreparation((UIApplication)sender); return; }
            if (Busy) return;
            var pid = Path.Combine(BaseFolder, "pid.txt");
            if (!File.Exists(pid)) return;
            if (File.ReadAllText(pid).Trim() != Environment.ProcessId.ToString()) { App.Idling -= Idle; return; }
            bool service = File.Exists(Path.Combine(BaseFolder, "service-mode.txt"));
            Folder = BaseFolder;
            if (service)
            {
                if (!File.Exists(Path.Combine(BaseFolder, "ready.txt"))) File.WriteAllText(Path.Combine(BaseFolder, "ready.txt"), Environment.ProcessId.ToString());
                if (DateTime.UtcNow < NextCommandUtc) return;
                NextCommandUtc = DateTime.UtcNow.AddSeconds(1);
                var commandPath = Path.Combine(BaseFolder, "command.txt");
                if (!File.Exists(commandPath)) return;
                var command = File.ReadAllText(commandPath).Trim(); File.Delete(commandPath);
                if (command == "exit")
                {
                    App.Idling -= Idle;
                    var closingUi = (UIApplication)sender;
                    CleanupTestDocuments(closingUi);
                    closingUi.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); return;
                }
                if (command.Length == 0 || command.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                    throw new Exception("Invalid isolated test case name.");
                Folder = Path.Combine(BaseFolder, "cases", command);
                if (!Directory.Exists(Folder)) throw new Exception("Isolated test case is absent.");
            }
            if (!service) App.Idling -= Idle;
            Busy = true;
            File.WriteAllText(Path.Combine(Folder, "started.txt"), DateTime.UtcNow.ToString("O"));
            var ui = (UIApplication)sender;
            Document doc = null;
            try
            {
                CleanupTestDocuments(ui);
                if (ui.Application.Documents.Size != 0) throw new Exception("Requires empty isolated process.");
                var product = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("BIMaestroApp") != null);
                var store = product.GetType("Analyse.CollaborativeModelTrackerStore", true);
                var history = Path.Combine(Folder, "history"); Directory.CreateDirectory(history);
                store.GetField("_activeDirectory", Flags).SetValue(null, history);
                if (File.Exists(Path.Combine(Folder, "manual-prepare-test.txt")))
                {
                    BeginManualPreparation(ui, product);
                    return;
                }
                var native = product.GetType("Analyse.ElementHistoryNativeArchive", true);
                using (var scope = (IDisposable)native.GetMethod("BeginHistoricalScope", Flags).Invoke(null, new object[] { ui.Application }))
                {
                    var options = new OpenOptions();
                    var source = Path.Combine(Folder, "opening.rvt");
                    using (var info = BasicFileInfo.Extract(source))
                        if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    doc = ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), options);
                }
                var capture = product.GetType("Analyse.ElementHistoryReconstruction", true).GetMethod("CaptureDirectForRestore", Flags);
                var ids = JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(Folder, "ids.json")));
                var rows = new List<object>();
                foreach (var id in ids)
                {
                    var element = doc.GetElement(new ElementId((long)id));
                    string diagnostic = null;
                    object recipe = null;
                    if (element != null && !File.Exists(Path.Combine(Folder,"native-test.txt")))
                        try { recipe = capture.Invoke(null, new object[] { element, (Action<string>)(s => diagnostic = s) }); }
                        catch (Exception ex) { diagnostic = ex.InnerException?.ToString() ?? ex.ToString(); }
                    rows.Add(new { Id = id, Class = element?.GetType().FullName, Category = element?.Category?.Name,
                        WallKind = (element as Wall)?.WallType.Kind.ToString(),
                        StackedWallMember = (element as Wall)?.IsStackedWallMember,
                        StackedWallOwner = (element as Wall)?.IsStackedWallMember == true ? ((Wall)element).StackedWallOwnerId.Value : (long?)null,
                        CategoryId = element?.Category?.Id.Value, ViewSpecific = element?.ViewSpecific,
                        Name = element?.Name, IsTransient=element?.IsTransient, Ready = recipe != null, Diagnostic = diagnostic,
                        Classification = element == null ? null : new Analyse.HistoryOpeningClassification {
                            SourceElementId = id, SourceUniqueId = element.UniqueId, Class = element.GetType().FullName,
                            Category = element.Category?.Name, CategoryId = element.Category?.Id.Value ?? 0,
                            ViewSpecific = element.ViewSpecific,
                            HasBoundingBox = element.GetType() == typeof(Element) && element.Category == null && element.get_BoundingBox(null) != null
                        }
                    });
                }
                File.WriteAllText(Path.Combine(Folder, "result.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(Path.Combine(Folder, "native-test.txt"))) NativeTest(ui, product, doc, ids);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Folder, "error.txt"), ex.ToString()); }
            finally
            {
                if (ManualRun == null)
                {
                    if (doc != null && doc.IsValidObject) doc.Close(false);
                    File.WriteAllText(Path.Combine(Folder, "completed.txt"), DateTime.UtcNow.ToString("O"));
                    Busy = false;
                    if (!service && ui.Application.Documents.Size == 0) ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
                }
                else Busy = false;
            }
        }
        private sealed class ManualPreparationRun
        {
            internal Assembly Product;
            internal Type Bridge;
            internal IList Events;
            internal string History, ModelKey;
            internal IDisposable InstalledScope;
            internal DateTime StartedUtc, NextTickUtc;
            internal int Waves, SettlingTicks;
            internal bool ReusePublished;
        }
        private static object Value(object instance, string name)
            => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
                ?? instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
        private static object ParseProductJson(Assembly product, string json, Type type)
        {
            var jsonAssembly = Assembly.Load(product.GetReferencedAssemblies().First(a => a.Name == "Newtonsoft.Json"));
            var jsonType = jsonAssembly.GetType("Newtonsoft.Json.JsonConvert", true);
            return jsonType.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) }).Invoke(null, new object[] { json, type });
        }
        private void BeginManualPreparation(UIApplication ui, Assembly installed)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1")
                throw new Exception("Manual lot testing requires the explicitly isolated process environment.");
            var current = Assembly.LoadFile(File.ReadAllText(Path.Combine(Folder, "product-path.txt")).Trim());
            var history = Path.Combine(Folder, "manual-test-history");
            Directory.CreateDirectory(history);
            var store = current.GetType("Analyse.CollaborativeModelTrackerStore", true);
            store.GetField("_activeDirectory", Flags).SetValue(null, history);
            var eventType = current.GetType("Analyse.ElementHistoryEvent", true);
            var listType = typeof(List<>).MakeGenericType(eventType);
            var events = (IList)ParseProductJson(current, File.ReadAllText(Path.Combine(Folder, "events.json")), listType);
            if (events.Count == 0) throw new Exception("The manual selection must contain the exact real deletion events.");
            var modelKeys = events.Cast<object>().Select(e => (string)Value(e, "ModelKey")).Distinct().ToList();
            if (modelKeys.Count != 1) throw new Exception("This isolated test requires one exact model selection.");
            // The input preparation script already rewrote artifact paths to this
            // case's immutable opening clone. Persist that exact selection only.
            var journal = Path.Combine(history, "element-history-manual-test.jsonl");
            using (var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "events.json"))))
                File.WriteAllLines(journal, input.RootElement.EnumerateArray().Select(e => e.GetRawText()));
            var bridge = current.GetType("Analyse.ElementHistoryDurableBridge", true);
            var native = installed.GetType("Analyse.ElementHistoryNativeArchive", true);
            var run = new ManualPreparationRun { Product = current, Bridge = bridge, Events = events, History = history,
                ModelKey = modelKeys[0], StartedUtc = DateTime.UtcNow,
                ReusePublished=File.Exists(Path.Combine(Folder,"reuse-conversions-test.txt")),
                InstalledScope = (IDisposable)native.GetMethod("BeginHistoricalScope", Flags).Invoke(null, new object[] { ui.Application }) };
            ManualRun = run;
            try
            {
                if(!run.ReusePublished)
                {
                    bridge.GetMethod("EventsPersisted", Flags).Invoke(null, new object[] { events });
                    bridge.GetMethod("RequestPreparation", Flags).Invoke(null, new object[] { run.ModelKey, events });
                }
                else
                {
                    var prior=Path.Combine(Folder,"prior-readiness.json");
                    if(File.Exists(prior))using(var previous=JsonDocument.Parse(File.ReadAllText(prior)))
                        foreach(var item in previous.RootElement.EnumerateArray().Where(item=>item.GetProperty("State").GetString()=="Unavailable"))
                        {
                            var seed=bridge.GetMethods(Flags).Where(method=>method.Name=="SetReadiness")
                                .OrderByDescending(method=>method.GetParameters().Length).First();
                            var parameters=seed.GetParameters();
                            var arguments=parameters.Select(parameter=>parameter.HasDefaultValue?parameter.DefaultValue:Type.Missing).ToArray();
                            arguments[0]=item.GetProperty("EventId").GetString();
                            arguments[1]=Enum.Parse(bridge.GetNestedType("ReadinessState",BindingFlags.Public|BindingFlags.NonPublic),"Unavailable");
                            arguments[2]=item.GetProperty("Reason").GetString();
                            // Private readiness gained an optional Excluded flag;
                            // use declared defaults rather than a fixed arity.
                            for(var i=3;i<parameters.Length;i++)if(parameters[i].Name=="excluded")
                                arguments[i]=item.TryGetProperty("Excluded",out var excluded)?excluded.GetBoolean()
                                    :((string)arguments[2]).StartsWith("Dépendance non physique exclue",StringComparison.Ordinal);
                            seed.Invoke(null,arguments);
                        }
                    foreach(var ev in events)bridge.GetMethod("GetReadiness",Flags).Invoke(null,new[]{ev});
                }
                File.WriteAllText(Path.Combine(Folder, "manual-requested.txt"), DateTime.UtcNow.ToString("O"));
            }
            catch { FinishManualPreparation(ui); throw; }
        }
        private void TickManualPreparation(UIApplication ui)
        {
            var run = ManualRun;
            if (run == null || Busy || DateTime.UtcNow < run.NextTickUtc) return;
            if (File.ReadAllText(Path.Combine(BaseFolder, "pid.txt")).Trim() != Environment.ProcessId.ToString())
                throw new Exception("The isolated process ownership marker changed during a manual test.");
            run.NextTickUtc = DateTime.UtcNow.AddMilliseconds(100); Busy = true;
            try
            {
                if (DateTime.UtcNow - run.StartedUtc > TimeSpan.FromMinutes(4))
                    throw new TimeoutException("The complete manual preparation exceeded the 4 minute test limit.");
                run.Bridge.GetMethod("ProcessPending", Flags).Invoke(null, new object[] { ui.Application, false });
                run.Waves++;
                var status = run.Bridge.GetMethod("GetStatus", Flags).Invoke(null, null);
                var manual = (bool)run.Bridge.GetProperty("IsManualPreparing", Flags).GetValue(null);
                File.WriteAllText(Path.Combine(Folder, "manual-progress.json"), JsonSerializer.Serialize(new {
                    ElapsedSeconds = (DateTime.UtcNow-run.StartedUtc).TotalSeconds, run.Waves, Manual = manual,
                    Pending = Value(status,"PendingJobs"), Failed = Value(status,"FailedJobs"), Working = Value(status,"Working"),
                    Stage = Value(status,"ProgressLabel"), Completed=Value(status,"ProgressCompleted"), Total=Value(status,"ProgressTotal") },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (manual || (bool)Value(status, "Working")) { run.SettlingTicks = 0; return; }
                if(run.ReusePublished&&DateTime.UtcNow-run.StartedUtc<TimeSpan.FromSeconds(30)
                    &&run.Events.Cast<object>().Any(ev=>Value(run.Bridge.GetMethod("GetReadiness",Flags).Invoke(null,new[]{ev}),"State").ToString()=="Preparing"))return;
                // Publication/readiness notifications use the IO worker; give its
                // final cache updates a separate API callback before restoring.
                if (++run.SettlingTicks < 2) return;
                CompleteManualPreparation(ui, run, status);
                FinishManualPreparation(ui);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Folder, "error.txt"), ex.ToString());
                FinishManualPreparation(ui);
            }
            finally { Busy = false; }
        }
        private void CompleteManualPreparation(UIApplication ui, ManualPreparationRun run, object status)
        {
            var bridge = run.Bridge;
            var durable = run.Product.GetType("Analyse.ElementHistoryDurableStore", true);
            var requestType = run.Product.GetType("Analyse.HistoryRestoreRequest", true);
            var ready = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(requestType));
            var rows = new List<object>();
            int nativeCount=0,directCount=0,unprovenCount=0;
            foreach (var ev in run.Events)
            {
                var eventId = (string)bridge.GetMethod("EventId", Flags).Invoke(null, new[] { ev });
                var state = bridge.GetMethod("GetReadiness", Flags).Invoke(null, new[] { ev });
                var name = Value(state, "State").ToString();
                var request = durable.GetMethod("ResolveConversion", Flags).Invoke(null, new object[] { eventId });
                var recipe = request == null ? null : Value(request, "Recipe");
                var nativeReady = name == "Ready" && (bool)bridge.GetMethod("IsReadyNativeRequest",Flags).Invoke(null,new[]{request});
                var directReady = name == "Ready" && (bool)bridge.GetMethod("IsReadyDirectRequest",Flags).Invoke(null,new[]{request});
                if (nativeReady || directReady) ready.Add(request);
                if(nativeReady)nativeCount++; if(directReady)directCount++;
                if(name=="Ready" && !nativeReady && !directReady)unprovenCount++;
                rows.Add(new { EventId=eventId, ElementId=Value(ev,"ElementId"), State=name,
                    Reason=Value(state,"Reason"), NativePayloadReady=nativeReady,DirectReady=directReady,
                    Excluded=Value(state,"Excluded") as bool? ?? false,
                    SourceUniqueId=request == null ? null : Value(request,"SourceUniqueId") });
            }
            File.WriteAllText(Path.Combine(Folder,"manual-readiness.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(Folder,"manual-preparation-result.json"),JsonSerializer.Serialize(new {
                RequestedEvents=run.Events.Count, Ready=ready.Count, NativeReady=nativeCount,DirectReady=directCount,UnprovenReady=unprovenCount,Waves=run.Waves,
                PreparationSeconds=(DateTime.UtcNow-run.StartedUtc).TotalSeconds,
                Status=status, CompleteSelectionReady=ready.Count==run.Events.Count,
                SourcesSaved=false, UserHistoryModified=false },new JsonSerializerOptions{WriteIndented=true}));
            bridge.GetMethod("ReleasePreparationSourcesForRestore", Flags).Invoke(null,null);
            if (ready.Count == 0) return;
            var sourcePath=Path.Combine(Folder,"opening.rvt");
            var destinationPath=Path.Combine(Folder,"destination.rvt");
            File.Copy(sourcePath,destinationPath);
            Document source=null,destination=null;
            try
            {
                Document Open(string path)
                {
                    var options=new OpenOptions();
                    using(var info=BasicFileInfo.Extract(path))
                        if(info.IsWorkshared) options.DetachFromCentralOption=DetachFromCentralOption.DetachAndPreserveWorksets;
                    return ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path),options);
                }
                source=Open(sourcePath); destination=Open(destinationPath);
                var ids=run.Events.Cast<object>().Select(e=>Convert.ToInt32(Value(e,"ElementId"))).ToList();
                var modelIds=new HashSet<ElementId>(new FilteredElementCollector(source).WhereElementIsNotElementType().ToElementIds());
                var originals=ids.Select(id=>source.GetElement(new ElementId((long)id))).Where(e=>e!=null && modelIds.Contains(e.Id)
                    && e.Category?.CategoryType==CategoryType.Model && !e.ViewSpecific && !(e is ElementType || e is MEPSystem)
                    && !new Analyse.HistoryOpeningClassification{SourceElementId=(int)e.Id.Value,SourceUniqueId=e.UniqueId,
                        Class=e.GetType().FullName,CategoryId=e.Category?.Id.Value??0,ViewSpecific=e.ViewSpecific}.IsExcluded()).ToList();
                var identities=new HashSet<string>(ready.Cast<object>().Select(r=>(string)Value(r,"SourceUniqueId")));
                var restoredOriginals=originals.Where(e=>identities.Contains(e.UniqueId)).ToList();
                var physicalIdentities=new HashSet<string>(originals.Select(e=>e.UniqueId));
                var unavailablePhysicalIdentities=new HashSet<string>(originals.Where(e=>!identities.Contains(e.UniqueId)).Select(e=>e.UniqueId));
                var falseReady=identities.Where(id=>!physicalIdentities.Contains(id)).ToList();
                File.WriteAllText(Path.Combine(Folder,"manual-source-expectations.json"),JsonSerializer.Serialize(new {
                    Requested=run.Events.Count,SourcePhysicalExpected=originals.Count,ReadyPhysical=restoredOriginals.Count,
                    SourceExpectedWithoutFalseReady=falseReady.Count==0,FalseReadyIdentities=falseReady,
                    UnavailablePhysicalIdentities=unavailablePhysicalIdentities,
                    Elements=ids.Select(id=>{var e=source.GetElement(new ElementId((long)id));return new{Id=id,UniqueId=e?.UniqueId,
                        Class=e?.GetType().FullName,Category=e?.Category?.Name,Physical=e!=null&&physicalIdentities.Contains(e.UniqueId),
                        Ready=e!=null&&identities.Contains(e.UniqueId)};}) },new JsonSerializerOptions{WriteIndented=true}));
                using(var tx=new Transaction(destination,"Probe - Delete complete real raw lot"))
                {
                    tx.Start();tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new ProbeDeleteFailures(Folder)).SetClearAfterRollback(true));
                    var documentIds=new HashSet<ElementId>(new FilteredElementCollector(destination).WhereElementIsNotElementType().ToElementIds()
                        .Concat(new FilteredElementCollector(destination).WhereElementIsElementType().ToElementIds()));
                    var deletionInput=Path.Combine(Folder,"delete-ids.json");
                    var requestedDeletion=File.Exists(deletionInput)?JsonSerializer.Deserialize<int[]>(File.ReadAllText(deletionInput)).ToList():ids;
                    if(requestedDeletion.Any(id=>!ids.Contains(id)))throw new Exception("Every fixture deletion must be an authentic event in the tested full batch.");
                    var deleteIds=requestedDeletion.Select(id=>new ElementId((long)id)).Where(id=>documentIds.Contains(id)).ToList();
                    var deleted=destination.Delete(deleteIds);
                    var committed=tx.Commit();
                    File.WriteAllText(Path.Combine(Folder,"manual-deletion-result.json"),JsonSerializer.Serialize(new {
                        Requested=requestedDeletion.Count,HistoryEvents=ids.Count,ExistingRequested=deleteIds.Count,SkippedNonMembers=requestedDeletion.Count-deleteIds.Count,
                        DeletedByRevit=deleted.Select(id=>id.Value),Status=committed.ToString(),Saved=false
                    },new JsonSerializerOptions{WriteIndented=true}));
                    if(committed!=TransactionStatus.Committed)throw new Exception("The disposable destination deletion failed; see manual-deletion-failures.json.");
                }
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var restore=run.Product.GetType("Analyse.ElementHistoryRestoration",true).GetMethods(Flags)
                    .Single(method=>method.Name=="RestoreAvailable"&&method.GetParameters().Length==3);
                var result=restore.Invoke(null,new object[]{destination,ready,null});
                File.WriteAllText(Path.Combine(Folder,"manual-restore-result.json"),JsonSerializer.Serialize(new {
                    PhysicalRequested=originals.Count,Ready=ready.Count,NativeReady=nativeCount,DirectReady=directCount,Restored=result,
                    AllPhysicalReady=originals.All(e=>identities.Contains(e.UniqueId)),
                    SourceExpectedWithoutFalseReady=falseReady.Count==0,
                    RestoreSeconds=timer.Elapsed.TotalSeconds, SourceSaved=false,DestinationSaved=false },
                    new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
                AuditNativeResult(source,destination,restoredOriginals,result,true,unavailablePhysicalIdentities);
            }
            finally { try{if(destination?.IsValidObject==true)destination.Close(false);}finally{if(source?.IsValidObject==true)source.Close(false);} }
        }
        private void FinishManualPreparation(UIApplication ui)
        {
            var run=ManualRun; ManualRun=null;
            try{run?.Bridge.GetMethod("ReleasePreparationSourcesForRestore",Flags).Invoke(null,null);}
            finally
            {
                try{run?.InstalledScope?.Dispose();}
                finally
                {
                    File.WriteAllText(Path.Combine(Folder,"completed.txt"),DateTime.UtcNow.ToString("O"));
                    Busy=false;
                }
            }
        }

        private void CleanupTestDocuments(UIApplication ui)
        {
            var closed=new List<object>();
            foreach(var document in ui.Application.Documents.Cast<Document>().ToList())
            {
                var path=document.PathName;
                bool owned=string.IsNullOrWhiteSpace(path) || Path.GetFullPath(path).StartsWith(Path.GetFullPath(BaseFolder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
                if(!owned)
                    foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var native=assembly.GetType("Analyse.ElementHistoryNativeArchive");
                        var owns=native?.GetMethod("Owns",Flags);
                        if(owns!=null && (bool)owns.Invoke(null,new object[]{document})) {owned=true;break;}
                    }
                if(!owned) throw new Exception("Refusing to close an unexpected document in test process: "+path);
                var title=document.Title;
                if(!document.Close(false)) throw new Exception("Test document did not close: "+path);
                closed.Add(new{Title=title,Path=path,Saved=false});
            }
            if(closed.Count>0) File.WriteAllText(Path.Combine(Folder,"closed-prior-test-documents.json"),JsonSerializer.Serialize(closed,new JsonSerializerOptions{WriteIndented=true}));
        }

        private void NativeTest(UIApplication ui, Assembly installed, Document source, int[] ids)
        {
            var current = Assembly.LoadFile(File.ReadAllText(Path.Combine(Folder, "product-path.txt")).Trim());
            var store = current.GetType("Analyse.CollaborativeModelTrackerStore", true);
            var history = Path.Combine(Folder, "native-test-history"); Directory.CreateDirectory(Path.Combine(history, "native-history"));
            store.GetField("_activeDirectory", Flags).SetValue(null, history);
            var archiveName = "snapshot-opening-" + Guid.NewGuid().ToString("N") + ".rvt";
            File.Copy(Path.Combine(Folder, "opening.rvt"), Path.Combine(history, "native-history", archiveName));
            var destinationPath = Path.Combine(Folder, "destination.rvt");
            File.Copy(Path.Combine(Folder, "opening.rvt"), destinationPath);
            Document destination = null;
            Document auditSource = null;
            var phaseTimes=new Dictionary<string,double>();
            void SaveTimes()=>File.WriteAllText(Path.Combine(Folder,"phase-times.json"),JsonSerializer.Serialize(phaseTimes,new JsonSerializerOptions{WriteIndented=true}));
            var oldNative = installed.GetType("Analyse.ElementHistoryNativeArchive", true);
            using var scope = (IDisposable)oldNative.GetMethod("BeginHistoricalScope", Flags).Invoke(null, new object[] { ui.Application });
            try
            {
                var options = new OpenOptions();
                using (var info = BasicFileInfo.Extract(destinationPath))
                    if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                destination = ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(destinationPath), options);
                var mixed = File.Exists(Path.Combine(Folder, "mixed-test.txt"));
                var originals = ids.Select(id => source.GetElement(new ElementId((long)id))).Where(e => e != null
                    && (mixed ? e.Category?.CategoryType == CategoryType.Model && !e.ViewSpecific && !(e is ElementType || e is MEPSystem)
                        && !new Analyse.HistoryOpeningClassification { SourceElementId=(int)e.Id.Value, SourceUniqueId=e.UniqueId,
                            Class=e.GetType().FullName, CategoryId=e.Category?.Id.Value??0, ViewSpecific=e.ViewSpecific }.IsExcluded()
                        : e is Autodesk.Revit.DB.Plumbing.Pipe || e is FamilyInstance)).ToList();
                using (var tx = new Transaction(destination, "Probe - Delete physical source set"))
                {
                    tx.Start();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new StrictFailures()).SetClearAfterRollback(true));
                    var annotations = new FilteredElementCollector(destination).WhereElementIsNotElementType()
                        .Where(e => e is SpotDimension || e is IndependentTag)
                        .Select(e => e.Id).ToList();
                    if (annotations.Count > 0) destination.Delete(annotations);
                    destination.Delete(originals.Select(e => destination.GetElement(e.UniqueId).Id).ToList());
                    if (tx.Commit() != TransactionStatus.Committed) throw new Exception("Test setup deletion refused; restoration was not run.");
                }
                var requestType = current.GetType("Analyse.HistoryRestoreRequest", true);
                var recipeType = current.GetType("Analyse.HistoryRecipe", true);
                var artifactType = current.GetType("Analyse.HistoryNativeArtifact", true);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(requestType));
                void Set(object obj, string property, object value) => obj.GetType().GetProperty(property).SetValue(obj, value);
                foreach (var original in originals)
                {
                    var artifact = Activator.CreateInstance(artifactType); Set(artifact, "File", archiveName); Set(artifact, "WholeModel", true);
                    Set(artifact, "Ready", true); Set(artifact, "SourceElementId", (int)original.Id.Value);
                    var recipe = Activator.CreateInstance(recipeType); Set(recipe, "Kind", "native_archive"); Set(recipe, "Native", artifact);
                    var request = Activator.CreateInstance(requestType); Set(request, "Recipe", recipe); Set(request, "SourceUniqueId", original.UniqueId);
                    Set(request, "Label", original.Name); list.Add(request);
                }
                if (File.Exists(Path.Combine(Folder, "capsule-test.txt")))
                {
                    var nativeType = current.GetType("Analyse.ElementHistoryNativeArchive", true);
                    var capsulePath = Path.Combine(Folder, "capsule.rvt");
                    var auditIdentities=originals.Select(element=>element.UniqueId).ToList();
                    var exportTimer=System.Diagnostics.Stopwatch.StartNew();
                    var recipes = (IDictionary)nativeType.GetMethod("ExportDurableNativeBatch", Flags)
                        .Invoke(null, new object[] { source, list, capsulePath });
                    phaseTimes["ExportSeconds"]=exportTimer.Elapsed.TotalSeconds;SaveTimes();
                    // A source changed by SaveAs must close before publication; compact exports already close their output.
                    if(string.Equals(source.PathName,capsulePath,StringComparison.OrdinalIgnoreCase))
                    {
                    if(!source.Close(false))throw new Exception("Owned export source did not close.");
                    var auditOptions=new OpenOptions();
                    using(var auditInfo=BasicFileInfo.Extract(Path.Combine(Folder,"opening.rvt")))
                        if(auditInfo.IsWorkshared)auditOptions.DetachFromCentralOption=DetachFromCentralOption.DetachAndPreserveWorksets;
                    auditSource=ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(Path.Combine(Folder,"opening.rvt")),auditOptions);
                    source=auditSource; originals=auditIdentities.Select(source.GetElement).ToList();
                    }
                    var durable = current.GetType("Analyse.ElementHistoryDurableStore", true);
                    var payload = (string)durable.GetMethod("PublishNative", Flags).Invoke(null, new object[] { capsulePath });
                    foreach (var request in list)
                    {
                        var identity = (string)requestType.GetProperty("SourceUniqueId").GetValue(request);
                        var nativeRecipe = recipes[identity];
                        var artifact = recipeType.GetProperty("Native").GetValue(nativeRecipe);
                        Set(artifact, "PayloadId", payload); Set(artifact, "File", null); Set(request, "Recipe", nativeRecipe);
                    }
                    File.WriteAllText(Path.Combine(Folder, "capsule-ready.txt"), payload);
                }
                var restoreTimer=System.Diagnostics.Stopwatch.StartNew();
                var result = current.GetType("Analyse.ElementHistoryRestoration", true).GetMethod("RestoreNativeCopies", Flags)
                    .Invoke(null, new object[] { destination, list });
                phaseTimes["RestoreSeconds"]=restoreTimer.Elapsed.TotalSeconds;SaveTimes();
                AuditNativeResult(source, destination, originals, result);
                File.WriteAllText(Path.Combine(Folder, "native-result.json"), JsonSerializer.Serialize(new {
                    Product = current.Location, Restored = result, Requested = originals.Count,
                    DestinationSaved = false, SourceSaved = false
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            }
            finally { try { if (destination?.IsValidObject == true) destination.Close(false); } finally { if(auditSource?.IsValidObject==true)auditSource.Close(false); } }
        }
        private void AuditNativeResult(Document source, Document destination, List<Element> originals, object result,bool allowPartial=false,ISet<string> unavailableSourceIds=null)
        {
            object Property(object obj, string name) => obj.GetType().GetProperty(name).GetValue(obj);
            var items = ((IEnumerable)Property(result, "Items")).Cast<object>().ToList();
            var requestedCount=originals.Count;
            if (!allowPartial&&((int)Property(result, "Created") != originals.Count || (int)Property(result, "Failed") != 0))
                throw new Exception("Independent audit: not every requested physical object was restored.");
            var mapping = items.Where(i=>!string.IsNullOrWhiteSpace((string)Property(i,"UniqueId")))
                .GroupBy(i=>(string)Property(i,"SourceUniqueId")).ToDictionary(g=>g.Key,g=>destination.GetElement((string)Property(g.First(),"UniqueId")));
            if(allowPartial)originals=originals.Where(e=>mapping.TryGetValue(e.UniqueId,out var actual)&&actual!=null).ToList();
            List<Connector> Ports(Element e) => ((e as MEPCurve)?.ConnectorManager ?? (e as FamilyInstance)?.MEPModel?.ConnectorManager)
                ?.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Physical || c.ConnectorType == ConnectorType.Curve).ToList() ?? new List<Connector>();
            var forward = new Dictionary<string,string>(); var reverse = new Dictionary<string,string>();
            var edges = new HashSet<string>();var externalEdges=new HashSet<string>();var unavailableEdges=new HashSet<string>(); int ports = 0;
            foreach (var original in originals)
            {
                var actual = mapping[original.UniqueId];
                if (actual == null || actual.GetType() != original.GetType()) throw new Exception("Independent audit: native class mismatch.");
                foreach (var port in Ports(original))
                {
                    var matches = Ports(actual).Where(c => c.Domain == port.Domain && c.Shape == port.Shape && c.Origin.DistanceTo(port.Origin) < 1e-5).ToList();
                    if (matches.Count != 1) throw new Exception("Independent audit: connector geometry mismatch on " + original.Id);
                    var copied = matches[0]; ports++;
                    if (copied.CoordinateSystem.BasisZ.DotProduct(port.CoordinateSystem.BasisZ) < 1-1e-6)
                        throw new Exception("Independent audit: connector direction changed.");
                    if (port.Shape == ConnectorProfileType.Round && Math.Abs(copied.Radius-port.Radius)>1e-7
                        || (port.Shape == ConnectorProfileType.Rectangular || port.Shape == ConnectorProfileType.Oval)
                            && (Math.Abs(copied.Width-port.Width)>1e-7 || Math.Abs(copied.Height-port.Height)>1e-7))
                        throw new Exception("Independent audit: connector dimensions changed.");
                    if (port.Domain == Domain.DomainPiping || port.Domain == Domain.DomainHvac)
                    {
                        var before = port.MEPSystem; var after = copied.MEPSystem;
                        if (before != null)
                        {
                            if (after == null || forward.TryGetValue(before.UniqueId,out var expected) && expected!=after.UniqueId
                                || reverse.TryGetValue(after.UniqueId,out var prior) && prior!=before.UniqueId)
                                throw new Exception("Independent audit: system partition changed.");
                            if (((MEPSystemType)source.GetElement(before.GetTypeId())).SystemClassification != ((MEPSystemType)destination.GetElement(after.GetTypeId())).SystemClassification)
                                throw new Exception("Independent audit: system classification changed.");
                            forward[before.UniqueId]=after.UniqueId; reverse[after.UniqueId]=before.UniqueId;
                        }
                    }
                    foreach (var peer in port.AllRefs.Cast<Connector>().Where(c => !(c.Owner is MEPSystem) && c.Owner.Id != original.Id && port.IsConnectedTo(c)))
                    {
                        if(peer.Owner is Autodesk.Revit.DB.Plumbing.PipeInsulation || peer.Owner is Autodesk.Revit.DB.Mechanical.DuctInsulation
                            || (peer.ConnectorType!=ConnectorType.End&&peer.ConnectorType!=ConnectorType.Physical&&peer.ConnectorType!=ConnectorType.Curve))continue;
                        var internalPeer=mapping.TryGetValue(peer.Owner.UniqueId,out var actualPeer);
                        if(!internalPeer)actualPeer=destination.GetElement(peer.Owner.UniqueId);
                        var a=original.UniqueId+":"+port.Id; var b=peer.Owner.UniqueId+":"+peer.Id;
                        var edgeKey=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;
                        if(actualPeer==null&&unavailableSourceIds?.Contains(peer.Owner.UniqueId)==true)
                        {unavailableEdges.Add(edgeKey);continue;}
                        if(actualPeer==null)throw new Exception("Independent audit: connected neighbour absent from restored destination: "+peer.Owner.Id);
                        var peerPorts = Ports(actualPeer).Where(c=>c.Domain==peer.Domain && c.Shape==peer.Shape && c.Origin.DistanceTo(peer.Origin)<1e-5).ToList();
                        if (peerPorts.Count!=1 || !copied.IsConnectedTo(peerPorts[0])) throw new Exception("Independent audit: physical edge missing.");
                        edges.Add(edgeKey);
                        if(!internalPeer)externalEdges.Add(edgeKey);
                    }
                }
            }
            File.WriteAllText(Path.Combine(Folder,"independent-audit.json"),JsonSerializer.Serialize(new { Requested=requestedCount,AuditedRestored=originals.Count,
                Created=Property(result,"Created"),Failed=Property(result,"Failed"),Complete=requestedCount==originals.Count&&(int)Property(result,"Failed")==0,
                PhysicalEdges=edges.Count,ExternalPhysicalEdges=externalEdges.Count,Ports=ports,Systems=forward.Count,
                UnavailableNeighbourEdges=unavailableEdges.Count,UnavailableNeighbourEdgeIdentities=unavailableEdges,
                KnownUnavailablePhysicalObjects=unavailableSourceIds?.Count??0,FullOriginalGraphPreserved=unavailableEdges.Count==0&&(unavailableSourceIds?.Count??0)==0,
                AvailableHistoricalConnectionsPreserved=true,Passed=true,Scope=allowPartial?"Successful restored physical objects":"Complete selected set" },new JsonSerializerOptions{WriteIndented=true}));
        }
        private sealed class StrictFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (var failure in accessor.GetFailureMessages())
                    if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure);
                    else return FailureProcessingResult.ProceedWithRollBack;
                return FailureProcessingResult.Continue;
            }
        }
        private sealed class ProbeDeleteFailures : IFailuresPreprocessor
        {
            private readonly string Directory;
            internal ProbeDeleteFailures(string directory){Directory=directory;}
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var failures=accessor.GetFailureMessages();
                File.WriteAllText(Path.Combine(Directory,"manual-deletion-failures.json"),JsonSerializer.Serialize(failures.Select(f=>new {
                    Id=f.GetFailureDefinitionId().Guid,Severity=f.GetSeverity().ToString(),Description=f.GetDescriptionText(),
                    Failing=f.GetFailingElementIds().Select(id=>id.Value),Additional=f.GetAdditionalElementIds().Select(id=>id.Value)
                }),new JsonSerializerOptions{WriteIndented=true}));
                bool errors=false;
                foreach(var failure in failures)
                    if(failure.GetSeverity()==FailureSeverity.Warning)accessor.DeleteWarning(failure);else errors=true;
                return errors?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;
            }
        }
    }
}
