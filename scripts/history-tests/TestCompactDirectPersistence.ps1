#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo ('tmp/history-compact-direct-tests/' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$fixture = @'
using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Threading.Tasks;
using Newtonsoft.Json; using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.ApplicationServices { internal class Application { public List<Autodesk.Revit.DB.Document> Documents=new List<Autodesk.Revit.DB.Document>(); } }
namespace Autodesk.Revit.DB {
 internal class ElementId { internal int Value; internal ElementId(int value){Value=value;} }
 internal class Category { public string Name="Model"; }
 internal class Element { public string UniqueId; public ElementId Id=new ElementId(42); public Category Category=new Category(); public bool Excluded; public Analyse.HistoryRecipe Direct; }
 internal class FamilyInstance:Element { public Element SuperComponent; public Element Symbol; }
 internal class Document { public bool IsValidObject=true; public Dictionary<int,Element> Elements=new Dictionary<int,Element>(); public Element GetElement(ElementId id)=>Elements.TryGetValue(id.Value,out var found)?found:null; }
}
namespace Analyse {
internal static class FixturePaths { internal static string Bridge; }
internal static class ElementIdExtensions {internal static int GetIdValue(this Autodesk.Revit.DB.ElementId id)=>id.Value;internal static Autodesk.Revit.DB.ElementId CreateElementId(int id)=>new Autodesk.Revit.DB.ElementId(id);}
internal static class CollaborativeModelTrackerStore { internal static string ActiveDirectory; }
internal class HistoryDeletionElementAudit { public string BatchId {get;set;} public string Category {get;set;} }
internal class ElementHistoryEvent {
 public string EventId {get;set;} public string Action {get;set;}="delete"; public DateTime Ts {get;set;}=DateTime.UtcNow;
 public string ModelKey {get;set;}="fixture"; public string ModelGuid {get;set;} public string UniqueId {get;set;}
 public int ElementId {get;set;}=42; public string Family {get;set;}="Family";public string TypeName {get;set;}="Type";
 public string Category {get;set;}="Model"; public string Tx {get;set;} public Dictionary<string,object> Delta {get;set;}
 public HistoryDeletionElementAudit DeletionAudit {get;set;}
}
[JsonObject(ItemNullValueHandling=NullValueHandling.Ignore)] internal class HistoryNativeArtifact {
 public string File {get;set;} public string PayloadId {get;set;} public string TypeVersion {get;set;}
 public bool WholeModel {get;set;} public bool Ready {get;set;} public int SourceElementId {get;set;}
 public string MemberSourceUniqueId {get;set;} public int SourceRootElementId {get;set;} public List<int> ExcludedElementIds {get;set;}
}
internal class HistoryParameter { public string Reference {get;set;} public string Text {get;set;} public double Number {get;set;} }
internal class HistoryPlacement { public string LevelSourceUniqueId {get;set;} public double[] Point {get;set;} }
internal class HistoryPort { public string SystemType {get;set;} public double[] Origin {get;set;} }
internal class HistoryConnection { public string Peer {get;set;} }
internal class HistoryGeometryRelation { public string Peer {get;set;} }
internal class HistoryCircuit { public HistoryRecipe Recipe {get;set;} }
[JsonObject(ItemNullValueHandling=NullValueHandling.Ignore)] internal class HistoryRecipe {
 public int Version {get;set;}=1;public string Kind {get;set;}="family";public string Type {get;set;}="type-a";public string Level {get;set;}="level-a";
 [JsonProperty(DefaultValueHandling=DefaultValueHandling.Ignore)] public bool UsesExistingModelDefinitions {get;set;}
 public HistoryNativeArtifact Native {get;set;} public HistoryNativeArtifact OpeningFallback {get;set;}
 public string Host {get;set;} public double[] Point {get;set;} public HistoryPlacement HistoricalPlacement {get;set;}
 public List<HistoryParameter> Parameters {get;set;} public List<HistoryPort> Ports {get;set;} public List<HistoryConnection> Connections {get;set;}
 public List<HistoryGeometryRelation> GeometryRelations {get;set;} public List<HistoryCircuit> ElectricalCircuits {get;set;}
}
internal class HistoryRestoreRequest {
 public string SourceUniqueId {get;set;} public string Label {get;set;} public string Category {get;set;}
 public HistoryRecipe Recipe {get;set;} public string CaptureFailure {get;set;} public string SuperComponentUniqueId {get;set;} public string FamilyTypeUniqueId {get;set;} public HistoryDeletionElementAudit DeletionAudit {get;set;}
}
internal static class ElementHistoryReconstruction {
 internal static int CaptureCalls;
 internal static HistoryRecipe ReadRecipe(HistoryRecipe recipe)=>recipe?.Version==1?recipe:null;
 internal static HistoryRecipe CaptureDirectForRestore(Autodesk.Revit.DB.Element element,Action<string> diagnostic=null){CaptureCalls++; if(element.Direct==null)diagnostic?.Invoke("Unsupported fixture category");return element.Direct;}
}
internal static class ElementHistoryExternalModels {internal static bool IsExcluded(Autodesk.Revit.DB.Element e)=>e.Excluded;internal static bool IsExcludedClass(string kind)=>kind=="Autodesk.Revit.DB.ImportInstance";}
internal static class ElementHistoryRelations {internal static string Key(HistoryGeometryRelation r)=>r.Peer;}
internal static class HistoryBackgroundWork {internal static bool Paused;internal static bool IdleAllowed=true;internal static bool PreparationCanRun()=>IdleAllowed;}
internal static class ElementHistoryTracker {internal static string GetDocumentKeyForHistory(Autodesk.Revit.DB.Document doc)=>"fixture";internal static void FlushPendingForDurability(){} }
internal static class ElementHistoryLocalSpool {
 internal static readonly Spool Default=new Spool(); internal class Spool {
 internal bool HasPending(string store)=>false;internal void Drain(string store,Action<IEnumerable<ElementHistoryEvent>> callback){}
 internal IEnumerable<T> ReadWithTransportLock<T>(string store,Func<IEnumerable<T>> read)=>read(); }
}
internal static class ElementHistoryNativeArchive {
 internal static bool Owns(Autodesk.Revit.DB.Document doc)=>true;internal static Autodesk.Revit.DB.Document SourceDocument;internal static int OpenCalls;
 internal class Sources:IDisposable {internal Autodesk.Revit.DB.Document Open(Autodesk.Revit.ApplicationServices.Application app,HistoryNativeArtifact artifact){OpenCalls++;return SourceDocument??throw new IOException("Source unavailable");}public void Dispose(){} }
}
internal static partial class ElementHistoryDurableBridge {
 internal enum ReadinessState {Preparing,Ready,Unavailable}
 internal static Dictionary<string,string> Reasons=new Dictionary<string,string>();
 private static void SetReadiness(string id,ReadinessState state,string reason){Reasons[id]=state+":"+reason;}
 internal static void Reset(){Sources?.Dispose();Sources=null;Live.Clear();Sessions.Clear();Pending.Clear();Errors.Clear();TransportRetries.Clear();PreparedJobs.Clear();Loaded=true;Converted=0;Io=null;NextPassUtc=default;CurrentStore=null;}
 internal static void Step(){NextPassUtc=default;ProcessPending(new Autodesk.Revit.ApplicationServices.Application(),true);Io?.GetAwaiter().GetResult();}
 internal static void Reload(){Loaded=false;Load();}
 internal static void AutomaticStep(){NextPassUtc=default;ProcessPending(new Autodesk.Revit.ApplicationServices.Application());Io?.GetAwaiter().GetResult();}
 internal static void Cleanup()=>CleanupClosedSessions();
 internal static string AddClosedSession(string id){Directory.CreateDirectory(LocalBridgeDirectory);var path=Path.Combine(LocalBridgeDirectory,"snapshot-opening-"+id+".rvt");File.WriteAllText(path,"opening support");Sessions[id]=new Session{Id=id,BridgeFile=path,Closed=true,Referenced=true,ModelKey="fixture"};return path;}
 internal static string AddUnreferencedSession(string id){var path=AddClosedSession(id);Sessions[id].Referenced=false;Sessions[id].Closed=false;return path;}
}
internal static class Checks {
 static int count;static void Check(bool ok,string reason){count++;if(!ok)throw new Exception(reason);}
 static HistoryRecipe Recipe(double x)=>new HistoryRecipe{Point=new[]{x,2d,3d},Host="host-a",HistoricalPlacement=new HistoryPlacement{Point=new[]{x,2d,3d},LevelSourceUniqueId="level-a"},Parameters=new List<HistoryParameter>{new HistoryParameter{Reference="material-a",Text="latest",Number=x}},Ports=new List<HistoryPort>{new HistoryPort{SystemType="system-a",Origin=new[]{x,2d,3d}}},Connections=new List<HistoryConnection>{new HistoryConnection{Peer="peer-a"}},GeometryRelations=new List<HistoryGeometryRelation>{new HistoryGeometryRelation{Peer="joined-a"}}};
 static ElementHistoryEvent Event(string id,HistoryRecipe recipe)=>new ElementHistoryEvent{EventId=id,Ts=DateTime.UtcNow.AddMinutes(-2),Delta=new Dictionary<string,object>{{"deletedUniqueId","source-uid"},{"recipe",recipe==null?null:JObject.FromObject(recipe)}}};
 static HistoryRestoreRequest Drive(ElementHistoryEvent ev){ElementHistoryDurableBridge.EventsPersisted(new[]{ev});for(int i=0;i<4;i++){ElementHistoryDurableBridge.Step();var result=ElementHistoryDurableStore.ResolveConversion(ev.EventId);if(result!=null)return result;}throw new Exception("Not published: "+ev.EventId);}
 static HistoryRecipe Opening(string file,List<int> excluded=null)=>new HistoryRecipe{Kind="native_archive",Native=new HistoryNativeArtifact{WholeModel=true,File=file,SourceElementId=42,MemberSourceUniqueId="source-uid",ExcludedElementIds=excluded}};
 public static int Main(string[] args){try{
  var io=args[0];CollaborativeModelTrackerStore.ActiveDirectory=Path.Combine(io,"store");FixturePaths.Bridge=Path.Combine(io,"bridges");Directory.CreateDirectory(io);Directory.CreateDirectory(CollaborativeModelTrackerStore.ActiveDirectory);ElementHistoryDurableBridge.Reset();
  if(args.Length>1){var reread=ElementHistoryDurableStore.ResolveConversion("direct");Check(reread.Recipe.UsesExistingModelDefinitions&&reread.Recipe.Point[0]==99,"Cold read after process/source closure");Check(!Directory.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"history-data","native")),"Cold read needs no native payload");Console.WriteLine("Cold process read PASS");return 0;}
  var stale=Event("old-other-model",Opening("gone.rvt"));stale.ModelKey="other";
  var current=Event("current-opening",Opening("active.rvt"));current.ModelKey="fixture";
  var legacy=Event("legacy-old",Opening("old.rvt"));legacy.ModelKey="fixture";legacy.Ts=DateTime.UtcNow;
  var latest=Event("latest-current",Opening("active.rvt"));latest.ModelKey="fixture";latest.Ts=DateTime.UtcNow;
  var ordered=ElementHistoryDurableBridge.OrderPendingJobs(new[]{stale,legacy,current,latest},new[]{"FIXTURE"});
  Check(ordered[0]==latest&&ordered[1]==current,"Recent opening work for an open model must precede legacy jobs and other models");
  Check(ordered.Count==4&&ordered.Distinct().Count()==4,"Prioritization must preserve all old and current work");
  var damaged=Event("damaged-old-job",Recipe(1));ElementHistoryDurableStore.WriteJob(damaged);
  var damagedPath=Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"history-data","jobs","damaged-old-job.json.gz");
  var damagedBytes=System.Text.Encoding.UTF8.GetBytes("invalid old gzip");File.WriteAllBytes(damagedPath,damagedBytes);
  var healthy=Event("healthy-next-to-damaged",Recipe(2));ElementHistoryDurableStore.WriteJob(healthy);
  var failures=new List<string>();var readable=ElementHistoryDurableStore.ReadPendingJobs((id,ex)=>failures.Add(id)).ToList();
  Check(failures.SequenceEqual(new[]{"damaged-old-job"})&&readable.Any(e=>e.EventId==healthy.EventId),"One damaged old job must not block valid neighboring jobs");
  ElementHistoryDurableBridge.EventsPersisted(new[]{damaged,healthy});
  Check(ElementHistoryDurableBridge.Reasons[damaged.EventId].StartsWith("Unavailable:"),"Damaged job remains explicitly unavailable");
  Check(File.ReadAllBytes(damagedPath).SequenceEqual(damagedBytes),"Damaged file must remain untouched for diagnosis");
  ElementHistoryDurableBridge.Reload();
  Check(ElementHistoryDurableBridge.GetStatus().PendingJobs>0,"Reload progresses despite damaged old job");
  var openSupport=ElementHistoryDurableBridge.AddUnreferencedSession("active-user");
  HistoryBackgroundWork.IdleAllowed=false;ElementHistoryDurableBridge.AutomaticStep();
  Check(ElementHistoryDurableStore.ResolveConversion(healthy.EventId)?.Recipe!=null,"Normal automatic direct publication must progress while heavy preparation is blocked");
  Check(ElementHistoryNativeArchive.OpenCalls==0&&File.Exists(openSupport),"Automatic JSON work never opens or removes the active opening model");
  HistoryBackgroundWork.IdleAllowed=true;ElementHistoryDurableBridge.Reset();
  var recipe=Recipe(99);recipe.OpeningFallback=new HistoryNativeArtifact{WholeModel=true,File="missing-source.rvt"};recipe.ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit{Recipe=new HistoryRecipe{Kind="electrical_system",Type="circuit-type"}}};var ev=Event("direct",recipe);var before=JObject.FromObject(ev).ToString();var result=Drive(ev);
  Check(result.Recipe.UsesExistingModelDefinitions&&result.Recipe.ElectricalCircuits[0].Recipe.UsesExistingModelDefinitions,"All direct recipe nodes marked");Check(result.Recipe.Native==null&&result.Recipe.OpeningFallback==null,"Backup receipts removed");Check(JObject.FromObject(ev).ToString()==before,"Source event never mutated");Check(result.Recipe.Point[0]==99&&result.Recipe.Parameters[0].Text=="latest"&&result.Recipe.Ports[0].Origin[0]==99&&result.Recipe.Connections[0].Peer=="peer-a","Latest instance state preserved");Check(ElementHistoryNativeArchive.OpenCalls==0,"Direct publication never opens a Revit source");Check(ElementHistoryDurableBridge.GetStatus().PendingJobs==0,"Job acknowledged after durable publication");Check(ElementHistoryDurableBridge.Reasons["direct"].Contains("types nécessaires"),"Ready does not promise autonomous definitions");Check(!Directory.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"history-data","native")),"No native capsule generated");
  result.Recipe.Point[0]=-5;Check(ElementHistoryDurableStore.ResolveConversion("direct").Recipe.Point[0]==99,"Read result cannot mutate durable data");
  var unsupported=Recipe(12);unsupported.ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit{Recipe=new HistoryRecipe{Kind="native_archive",Native=new HistoryNativeArtifact{File="nested.rvt"}}}};var unsupportedEvent=Event("nested-native",unsupported);var nestedBefore=JObject.FromObject(unsupportedEvent).ToString();var unavailable=Drive(unsupportedEvent);Check(unavailable.Recipe==null&&unavailable.CaptureFailure.Contains("circuit"),"Nested native circuit explicitly unavailable, never dropped");Check(JObject.FromObject(unsupportedEvent).ToString()==nestedBefore,"Unsupported source event retained");Check(Drive(Event("no-recipe",null)).CaptureFailure.Contains("Aucune recette"),"Missing recipe acknowledged with reason");
  var path=ElementHistoryDurableBridge.AddClosedSession("opening");var openingEvent=Event("opening",Opening(path));File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"element-history-20261023.jsonl"),JsonConvert.SerializeObject(openingEvent)+Environment.NewLine);ElementHistoryDurableBridge.Cleanup();Check(File.Exists(path),"Unacknowledged fallback retains opening copy");
  ElementHistoryNativeArchive.SourceDocument=new Autodesk.Revit.DB.Document();ElementHistoryNativeArchive.SourceDocument.Elements[42]=new Autodesk.Revit.DB.FamilyInstance{UniqueId="source-uid",Direct=Recipe(5),Symbol=new Autodesk.Revit.DB.Element{UniqueId="type-a"}};
  var openingBefore=JObject.FromObject(openingEvent).ToString();var openingResult=Drive(openingEvent);Check(openingResult.Recipe.UsesExistingModelDefinitions&&openingResult.Recipe.Native==null&&openingResult.Recipe.Point[0]==5,"Opening fallback becomes compact direct recipe");Check(JObject.FromObject(openingEvent).ToString()==openingBefore,"Opening event retains original receipt");ElementHistoryDurableBridge.Cleanup();Check(!File.Exists(path),"Closed opening copy deleted only after verified ACK");ElementHistoryNativeArchive.SourceDocument=null;Check(ElementHistoryDurableStore.ResolveConversion("opening").Recipe.Point[0]==5,"Converted fallback reads with source gone");
  ElementHistoryNativeArchive.SourceDocument=new Autodesk.Revit.DB.Document();ElementHistoryNativeArchive.SourceDocument.Elements[42]=new Autodesk.Revit.DB.Element{UniqueId="source-uid"};var unsupportedOpening=Drive(Event("unsupported-opening",Opening(path)));Check(unsupportedOpening.Recipe==null&&unsupportedOpening.CaptureFailure.Contains("Unsupported fixture"),"Non-direct opening category has explicit unavailable ACK");
  ElementHistoryNativeArchive.SourceDocument.Elements[42].Direct=Recipe(7);var captureBefore=ElementHistoryReconstruction.CaptureCalls;var changedOpening=Drive(Event("changed-opening",Opening(path,new List<int>{42})));Check(changedOpening.Recipe==null&&changedOpening.CaptureFailure.Contains("dernier état"),"Incompatible opening state never replaces newer state");Check(ElementHistoryReconstruction.CaptureCalls==captureBefore,"Changed state rejected before API capture");
  ElementHistoryNativeArchive.SourceDocument.Elements[42].UniqueId="other-uid";Check(Drive(Event("wrong-identity",Opening(path))).Recipe==null,"UID mismatch rejected");
  ElementHistoryNativeArchive.SourceDocument.Elements[42].UniqueId="source-uid";var missingSnapshot=Event("snapshot-missing",Opening(path));missingSnapshot.Delta.Remove("deletedUniqueId");var missingSnapshotResult=Drive(missingSnapshot);Check(missingSnapshotResult.SourceUniqueId=="source-uid"&&missingSnapshotResult.Recipe.UsesExistingModelDefinitions,"Missing-snapshot fallback may resolve unknown UID from opening copy");
  var unsupportedPath=ElementHistoryDurableBridge.AddClosedSession("unsupported");var unsupportedFallback=Event("unsupported-cleanup",Opening(unsupportedPath));ElementHistoryNativeArchive.SourceDocument.Elements[42].Direct=null;File.AppendAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"element-history-20261023.jsonl"),JsonConvert.SerializeObject(unsupportedFallback)+Environment.NewLine);ElementHistoryDurableBridge.Cleanup();Check(File.Exists(unsupportedPath),"Unavailable object still retains bridge before ACK");Check(Drive(unsupportedFallback).Recipe==null,"Unsupported opening object acknowledged unavailable");ElementHistoryDurableBridge.Cleanup();Check(!File.Exists(unsupportedPath),"Unavailable ACK also releases closed bridge");
  Check(ElementHistoryDurableBridge.GetStatus().FailedJobs==0&&ElementHistoryDurableBridge.GetStatus().PendingJobs==0,"Unavailable ACKs do not block completion");
  ElementHistoryDurableBridge.Reset();Check(ElementHistoryDurableBridge.GetStatus().SessionClosed,"No required session is closed by definition");var unreferencedPath=ElementHistoryDurableBridge.AddUnreferencedSession("unreferenced");Check(!ElementHistoryDurableBridge.GetStatus().SessionClosed&&ElementHistoryDurableBridge.GetStatus().BridgeFiles.Count==0,"Known unreferenced live session is not reported closed");ElementHistoryDurableBridge.CloseSession("unreferenced");Check(System.Threading.SpinWait.SpinUntil(()=>ElementHistoryDurableBridge.GetStatus().SessionClosed,3000),"Real CloseSession closes a JSON-only session");ElementHistoryDurableBridge.Cleanup();Check(!File.Exists(unreferencedPath),"JSON-only session releases its opening support without artificial references");
  var json=JsonConvert.SerializeObject(ElementHistoryDurableStore.ResolveConversion("direct"));var jsonBytes=System.Text.Encoding.UTF8.GetByteCount(json);var gzipBytes=new FileInfo(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"history-data","conversions","direct.json.gz")).Length;Check(jsonBytes<16384&&gzipBytes<16384,"Fixture recipe remains compact");File.WriteAllText(Path.Combine(io,"converted.json"),json);
  File.WriteAllText(Path.Combine(io,"results.json"),JsonConvert.SerializeObject(new{Passed=true,Assertions=count,RevitInvoked=false,DirectRecipeJsonBytes=jsonBytes,ConversionGzipBytes=gzipBytes,NativeCapsules=0,Scope="Real production bridge scheduling/publication/ACK/cleanup and durable JSON store. Revit API and unchanged transport are adapters; no native restore performed."},Formatting.Indented));Console.WriteLine(count+" compact direct assertions PASS; JSON="+jsonBytes+" bytes; GZip="+gzipBytes+" bytes");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
}
'@
$bridge = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.cs'))
$bridge = $bridge.Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "HistoryBridge")','FixturePaths.Bridge')
[IO.File]::WriteAllText((Join-Path $root 'Bridge.cs'),$bridge)
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'),$fixture)
$json = Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$direct = Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.Direct.cs'
$store = Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableStore.cs'
$retry = Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRetryPolicy.cs'
$project = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>
<Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference>
<Compile Include="$([Security.SecurityElement]::Escape($direct))"/><Compile Include="$([Security.SecurityElement]::Escape($store))"/><Compile Include="$([Security.SecurityElement]::Escape($retry))"/><Compile Include="Bridge.cs"/><Compile Include="Fixtures.cs"/>
</ItemGroup></Project>
"@
$projectPath = Join-Path $root 'CompactDirect.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $root 'build.log')
if ($LASTEXITCODE -ne 0) {Get-Content -LiteralPath (Join-Path $root 'build.log') -Tail 60;throw 'Compact fixture compile failed'}
$dll=Join-Path $root 'bin/Debug/net9.0/CompactDirect.Tests.dll';$io=Join-Path $root 'io'
& dotnet $dll $io
if ($LASTEXITCODE -ne 0) {throw 'Compact fixture failed'}
& dotnet $dll $io cold-read
if ($LASTEXITCODE -ne 0) {throw 'Compact cold process read failed'}
Write-Output $root
