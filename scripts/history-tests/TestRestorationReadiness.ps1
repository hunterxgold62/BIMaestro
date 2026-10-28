#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-readiness-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.Readiness.cs') -Destination (Join-Path $output 'Production.cs')
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRetryPolicy.cs') -Destination (Join-Path $output 'RetryPolicy.cs')
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/HistoryOpeningClassification.cs') -Destination (Join-Path $output 'Classification.cs')
$directSource=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.Direct.cs'))
$readyReason=[regex]::Match($directSource,'private static string ReadyReason[\s\S]+?(?=\n        private static)').Value
if([string]::IsNullOrWhiteSpace($readyReason)){throw 'Production ReadyReason missing'}
$reconstructionSource=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$readRecipe=[regex]::Match($reconstructionSource,'internal static HistoryRecipe ReadRecipe[\s\S]+?(?=\n        // Caller owns)').Value
if([string]::IsNullOrWhiteSpace($readRecipe)){throw 'Production ReadRecipe missing'}
$fixture=@'
using System;using System.IO;using System.Collections.Generic;using System.Threading;using System.Diagnostics;using System.Collections.Concurrent;using System.Linq;using Newtonsoft.Json.Linq;
namespace Analyse {
 internal sealed class ElementHistoryEvent{public string EventId,UniqueId,Action="delete";public HistoryRecipe Recipe;public bool OpeningSourceAvailable;}
 internal sealed class HistoryRecipe{public int Version=1;public string Kind,Type,Level;public List<string> SystemMembers;public int ElectricalSystemType;public HistoryNativeArtifact Native,OpeningFallback;public bool UsesExistingModelDefinitions;public List<HistoryCircuit> ElectricalCircuits;}
 internal sealed class HistoryCircuit{public HistoryRecipe Recipe;}
 internal enum ElectricalSystemType{PowerCircuit=0}
 internal static class ElementHistoryReconstruction{__READ_RECIPE__}
 internal sealed class HistoryNativeArtifact{public string File,PayloadId;public bool Ready,WholeModel,DefinitionOnly;}
 internal sealed class HistoryRestoreRequest{public HistoryRecipe Recipe;public string CaptureFailure;}
 internal static class CollaborativeModelTrackerStore{internal static string ActiveDirectory;}
 internal static class ElementHistoryDurableStore{
  internal static readonly ManualResetEventSlim Gate=new ManualResetEventSlim(false);
  internal static readonly ConcurrentDictionary<string,HistoryRestoreRequest> Values=new ConcurrentDictionary<string,HistoryRestoreRequest>();
  internal static readonly ConcurrentDictionary<string,Exception> Faults=new ConcurrentDictionary<string,Exception>();
  internal static readonly ConcurrentDictionary<string,HistoryOpeningClassification> Classifications=new ConcurrentDictionary<string,HistoryOpeningClassification>();
  internal static HistoryOpeningClassification GetCachedOpeningClassification(string id,string directory)=>Classifications.TryGetValue(id,out var value)?value:null;
  internal static HistoryOpeningClassification ResolveOpeningClassification(string id)=>Classifications.TryGetValue(id,out var value)?value:null;
  internal static HistoryRecipe NativeRecipe()=>new HistoryRecipe{Native=new HistoryNativeArtifact{Ready=true,PayloadId=new string('a',64)}};
  internal static int CallerThread,Active,MaxActive;
  internal static readonly ConcurrentDictionary<string,bool> StaleMisses=new ConcurrentDictionary<string,bool>();
  internal static readonly ManualResetEventSlim MissStarted=new ManualResetEventSlim(false),MissGate=new ManualResetEventSlim(false),MissReturned=new ManualResetEventSlim(false);
  internal static readonly ConcurrentDictionary<string,Exception> StaleFaults=new ConcurrentDictionary<string,Exception>();
  internal static readonly ManualResetEventSlim FaultStarted=new ManualResetEventSlim(false),FaultGate=new ManualResetEventSlim(false),FaultReturned=new ManualResetEventSlim(false);
  internal static HistoryRestoreRequest ResolveConversion(string id){
   CallerThread=Thread.CurrentThread.ManagedThreadId;int current=Interlocked.Increment(ref Active),observed;
   do{observed=Volatile.Read(ref MaxActive);if(current<=observed)break;}while(Interlocked.CompareExchange(ref MaxActive,current,observed)!=observed);
   try{if(StaleFaults.TryGetValue(id,out var stale)){FaultStarted.Set();FaultGate.Wait();throw stale;}if(StaleMisses.ContainsKey(id)){MissStarted.Set();MissGate.Wait();return null;}Gate.Wait();if(Faults.TryGetValue(id,out var error))throw error;return Values.TryGetValue(id,out var value)?value:null;}
   finally{Interlocked.Decrement(ref Active);if(StaleMisses.ContainsKey(id))MissReturned.Set();if(StaleFaults.ContainsKey(id))FaultReturned.Set();}
  }
 }
 internal static partial class ElementHistoryDurableBridge{
  __READY_REASON__
  private static IEnumerable<HistoryRecipe> Recipes(HistoryRecipe recipe){if(recipe==null)yield break;yield return recipe;foreach(var c in recipe.ElectricalCircuits??new List<HistoryCircuit>())foreach(var r in Recipes(c.Recipe))yield return r;}
  static readonly object Sync=new object();static readonly Dictionary<string,string> Errors=new Dictionary<string,string>();
  internal static string EventId(ElementHistoryEvent ev)=>ev.EventId??("legacy-"+ev.UniqueId);
  internal static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev,bool resolve)=>new HistoryRestoreRequest{Recipe=ev.Recipe};
  internal static string CurrentStore="fixture",ForbiddenNativeLookupId;
  internal static HistoryRestoreRequest TryGetOpeningNativeRequest(ElementHistoryEvent ev){if(ForbiddenNativeLookupId!=null && ev.EventId==ForbiddenNativeLookupId)throw new Exception("UI rebuilt native data instead of using cache");return ev.OpeningSourceAvailable?new HistoryRestoreRequest{Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact{Ready=true,WholeModel=true}}}:null;}
  static IEnumerable<HistoryNativeArtifact> Artifacts(HistoryRecipe recipe){if(recipe?.Native!=null)yield return recipe.Native;}
  internal static void SimulatePublishedReady(string id){ElementHistoryDurableStore.Values[id]=new HistoryRestoreRequest{Recipe=ElementHistoryDurableStore.NativeRecipe()};lock(Sync)Errors.Remove(id);SetReadiness(id,ReadinessState.Ready,"Published native conversion verified.");}
  internal static void SimulateLoadFailure(bool fail){lock(Sync){if(fail)Errors["load"]="damaged archive";else Errors.Remove("load");}}
  internal static void SimulateEventFailure(string id,string reason){lock(Sync)Errors[id]=reason;}
  internal static void ExpireReadiness(string id){lock(Sync)Readiness[id].CheckedUtc=DateTime.UtcNow.AddSeconds(-3);}
  internal static bool ValidationFinished(string id){lock(Sync)return Readiness.TryGetValue(id,out var entry)&&entry.CheckedUtc!=default(DateTime)&&!entry.Checking;}
  internal static void RevalidateCurrentGeneration(ElementHistoryEvent ev){long generation;lock(Sync){generation=Readiness[ev.EventId].Generation;Readiness[ev.EventId].Checking=true;}CheckReadiness(ev,ev.EventId,generation);}
 }
 internal static class Checks{
  static int count;static void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
  static ElementHistoryEvent New()=>new ElementHistoryEvent{EventId=Guid.NewGuid().ToString("N")};
  static HistoryOpeningClassification Physical()=>new HistoryOpeningClassification{SourceElementId=42,SourceUniqueId="source-wall-identity",Class="Autodesk.Revit.DB.Wall",CategoryId=-2000011,IsDocumentElement=true};
  static void Preparing(ElementHistoryEvent ev,string why){Check(ElementHistoryDurableBridge.GetReadiness(ev).State==ElementHistoryDurableBridge.ReadinessState.Preparing,why);Check(SpinWait.SpinUntil(()=>ElementHistoryDurableBridge.ValidationFinished(ElementHistoryDurableBridge.EventId(ev)),2000),"preparing validation did not finish");Check(ElementHistoryDurableBridge.GetReadiness(ev).State==ElementHistoryDurableBridge.ReadinessState.Preparing,why+" after storage validation");}
  static ElementHistoryDurableBridge.ReadinessState Wait(ElementHistoryEvent ev){var stop=Stopwatch.StartNew();while(stop.ElapsedMilliseconds<5000){var state=ElementHistoryDurableBridge.GetReadiness(ev).State;if(state!=ElementHistoryDurableBridge.ReadinessState.Preparing)return state;Thread.Sleep(10);}throw new Exception("Readiness timeout");}
  static int Main(string[] args){try{
   CollaborativeModelTrackerStore.ActiveDirectory=Path.GetDirectoryName(args[0]);
   var ready=New();ElementHistoryDurableStore.Values[ready.EventId]=new HistoryRestoreRequest{Recipe=ElementHistoryDurableStore.NativeRecipe()};
   var watch=Stopwatch.StartNew();var before=ElementHistoryDurableBridge.GetReadiness(ready);
   Check(before.State==ElementHistoryDurableBridge.ReadinessState.Preparing,"cold cache must not enable restore");Check(watch.ElapsedMilliseconds<250,"UI query blocked on storage");
   SpinWait.SpinUntil(()=>ElementHistoryDurableStore.CallerThread!=0,1000);Check(ElementHistoryDurableStore.CallerThread!=Thread.CurrentThread.ManagedThreadId,"storage accessed on UI thread");
   ElementHistoryDurableBridge.ReadyChanged+=()=>{throw new Exception("closed UI listener");};
   ElementHistoryDurableStore.Gate.Set();Check(Wait(ready)==ElementHistoryDurableBridge.ReadinessState.Ready,"validated native sidecar should enable restore");
   var compact=New();ElementHistoryDurableStore.Values[compact.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};
   Check(Wait(compact)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"an incomplete direct recipe must not enable reconstruction");
   Check(ElementHistoryDurableBridge.GetReadiness(compact).Reason.Contains("recette enregistrée incomplète ou incompatible"),"flat recipe must explain why native restoration is unavailable");
   foreach(var pumpPoint in new[]{36.21091831573927,41.132178158258974,47.365773958783926}){
    var pump=New();ElementHistoryDurableStore.Values[pump.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{Kind="family",Type="recorded-pump-family-symbol",Level="recorded-level",UsesExistingModelDefinitions=true}};
    Check(Wait(pump)==ElementHistoryDurableBridge.ReadinessState.Ready,"known pump family recipe with recorded type/level must enable direct native-instance restoration");
    Check(ElementHistoryDurableBridge.GetReadiness(pump).Reason.Contains("types nécessaires"),"direct readiness must explain existing project definitions dependency");
   }
   var oldComplete=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N"),Recipe=new HistoryRecipe{Kind="family",Type="saved-type",Level="saved-level"}};
   Check(Wait(oldComplete)==ElementHistoryDurableBridge.ReadinessState.Ready,"complete legacy JSON must use exact existing project definitions");
   Check(!oldComplete.Recipe.UsesExistingModelDefinitions,"legacy normalization must not mutate historical recipe");
   var malformedDirect=new HistoryRestoreRequest{Recipe=new HistoryRecipe{Kind="family",Type="saved-type",UsesExistingModelDefinitions=true}};
   Check(!ElementHistoryDurableBridge.IsReadyDirectRequest(malformedDirect),"missing direct level must be rejected");
   var mixedNested=new HistoryRestoreRequest{Recipe=new HistoryRecipe{Kind="family",Type="saved-type",Level="saved-level",UsesExistingModelDefinitions=true,ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit{Recipe=ElementHistoryDurableStore.NativeRecipe()}}}};
   Check(!ElementHistoryDurableBridge.IsReadyDirectRequest(mixedNested),"nested native dependency cannot masquerade as a direct recipe");
   var plain=New();ElementHistoryDurableStore.Values[plain.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe()};
   Check(Wait(plain)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"an empty recipe is not a verified native archive");
   var compactSource=New();compactSource.OpeningSourceAvailable=true;ElementHistoryDurableStore.Values[compactSource.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};
   ElementHistoryDurableStore.Classifications[compactSource.EventId]=Physical();Preparing(compactSource,"flat recipe with source copy must await native preparation");
   var definitionOnly=New();var definitionRecipe=ElementHistoryDurableStore.NativeRecipe();definitionRecipe.Native.DefinitionOnly=true;ElementHistoryDurableStore.Values[definitionOnly.EventId]=new HistoryRestoreRequest{Recipe=definitionRecipe};
   Check(Wait(definitionOnly)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"definition-only archive cannot restore physical objects");
   var definitionSource=New();definitionSource.OpeningSourceAvailable=true;ElementHistoryDurableStore.Values[definitionSource.EventId]=new HistoryRestoreRequest{Recipe=definitionRecipe};Preparing(definitionSource,"definition-only archive with raw source must await a physical native archive");
   var rawPhysical=New();rawPhysical.OpeningSourceAvailable=true;rawPhysical.Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact{WholeModel=true,Ready=true}};
   ElementHistoryDurableStore.Classifications[rawPhysical.EventId]=Physical();Preparing(rawPhysical,"whole source plus physical classification alone is never Ready");
   var rawUnclassified=New();rawUnclassified.OpeningSourceAvailable=true;Preparing(rawUnclassified,"unclassified opening copy must await preparation");
   var internalObject=New();internalObject.OpeningSourceAvailable=true;var internalClass=Physical();internalClass.IsDocumentElement=false;ElementHistoryDurableStore.Classifications[internalObject.EventId]=internalClass;
   Check(ElementHistoryDurableBridge.GetReadiness(internalObject).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"non-document object must not become restorable from a raw copy");
   var axis=New();axis.OpeningSourceAvailable=true;ElementHistoryDurableStore.Classifications[axis.EventId]=new HistoryOpeningClassification{SourceElementId=43,SourceUniqueId="axis-identity",Class="Autodesk.Revit.DB.Element",CategoryId=-2008045,IsDocumentElement=true};
   Check(ElementHistoryDurableBridge.GetReadiness(axis).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"verified axis dependency must remain unavailable");
   var failedPreparation=New();failedPreparation.OpeningSourceAvailable=true;ElementHistoryDurableStore.Values[failedPreparation.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};ElementHistoryDurableBridge.SimulateEventFailure(failedPreparation.EventId,"native archive preparation failed");
   Check(Wait(failedPreparation)==ElementHistoryDurableBridge.ReadinessState.Unavailable&&ElementHistoryDurableBridge.GetReadiness(failedPreparation).Reason.Contains("native archive preparation failed"),"archive preparation error must take priority over raw-source Preparing");
   var failedRaw=New();failedRaw.OpeningSourceAvailable=true;ElementHistoryDurableStore.Classifications[failedRaw.EventId]=Physical();ElementHistoryDurableBridge.SimulateEventFailure(failedRaw.EventId,"raw archive could not be prepared");
   Check(Wait(failedRaw)==ElementHistoryDurableBridge.ReadinessState.Unavailable&&ElementHistoryDurableBridge.GetReadiness(failedRaw).Reason.Contains("raw archive could not be prepared"),"raw source classification must not mask an archive error without conversion");
   ElementHistoryDurableBridge.SimulatePublishedReady(failedPreparation.EventId);
   Check(ElementHistoryDurableBridge.GetReadiness(failedPreparation).State==ElementHistoryDurableBridge.ReadinessState.Ready,"new verified native publication must clear old preparation failure");
   var diagnostic=New();ElementHistoryDurableStore.Values[diagnostic.EventId]=new HistoryRestoreRequest{CaptureFailure="unsupported"};Check(Wait(diagnostic)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"acknowledgement is not restorable");
   var corrupt=New();ElementHistoryDurableStore.Faults[corrupt.EventId]=new InvalidDataException("corrupt");Check(Wait(corrupt)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"corrupt sidecar must stay disabled");
   var offline=New();ElementHistoryDurableStore.Faults[offline.EventId]=new IOException("network offline");Check(Wait(offline)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"offline transport must be explicit");
   ElementHistoryDurableStore.Faults.TryRemove(offline.EventId,out _);ElementHistoryDurableStore.Values[offline.EventId]=new HistoryRestoreRequest{Recipe=ElementHistoryDurableStore.NativeRecipe()};ElementHistoryDurableBridge.ExpireReadiness(offline.EventId);
   Check(SpinWait.SpinUntil(()=>ElementHistoryDurableBridge.GetReadiness(offline).State==ElementHistoryDurableBridge.ReadinessState.Ready,2000),"transport availability did not recover without restarting UI");
   ElementHistoryDurableStore.Faults.TryRemove(corrupt.EventId,out _);ElementHistoryDurableStore.Values[corrupt.EventId]=new HistoryRestoreRequest{Recipe=ElementHistoryDurableStore.NativeRecipe()};ElementHistoryDurableBridge.ExpireReadiness(corrupt.EventId);
   Check(ElementHistoryDurableBridge.GetReadiness(corrupt).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"corrupt data must not become automatically retryable");
   ElementHistoryDurableBridge.SimulateLoadFailure(true);var loadBlocked=New();
   Check(Wait(loadBlocked)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"Global loading failure must be explicit, never endless pending");
   Check(ElementHistoryDurableBridge.GetReadiness(loadBlocked).Reason.Contains("damaged archive"),"Loading failure must expose the actual reason");
   ElementHistoryDurableBridge.SimulateLoadFailure(false);ElementHistoryDurableStore.Values[loadBlocked.EventId]=new HistoryRestoreRequest{Recipe=ElementHistoryDurableStore.NativeRecipe()};ElementHistoryDurableBridge.ExpireReadiness(loadBlocked.EventId);
   Check(SpinWait.SpinUntil(()=>ElementHistoryDurableBridge.GetReadiness(loadBlocked).State==ElementHistoryDurableBridge.ReadinessState.Ready,2000),"Readiness must recover after a loading failure is resolved");
   var raw=New();raw.Recipe=new HistoryRecipe();ElementHistoryDurableBridge.GetReadiness(raw);Thread.Sleep(80);Check(ElementHistoryDurableBridge.GetReadiness(raw).State==ElementHistoryDurableBridge.ReadinessState.Preparing,"new raw recipe must never bypass durable conversion");
   var oldMissing=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N")};Check(Wait(oldMissing)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"legacy without recipe disabled");
   var oldFlat=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N"),Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};Check(Wait(oldFlat)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"legacy flat recipe without native copy must remain unavailable");
   var oldFlatSource=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N"),OpeningSourceAvailable=true,Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};Preparing(oldFlatSource,"legacy flat recipe with raw source must await native preparation");
   var oldDefinition=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N"),Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact{Ready=true,DefinitionOnly=true,File="snapshot-definition.rvt"}}};Check(Wait(oldDefinition)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"legacy definition-only archive is not a physical native restore");
   var oldWhole=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N"),Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact{WholeModel=true,Ready=true,File="snapshot-opening-test.rvt"}}};ElementHistoryDurableBridge.GetReadiness(oldWhole);Thread.Sleep(80);Check(ElementHistoryDurableBridge.GetReadiness(oldWhole).State==ElementHistoryDurableBridge.ReadinessState.Preparing,"opening bridge must not enable on-demand extraction");
   Check(ElementHistoryDurableBridge.GetReadiness(new ElementHistoryEvent{Action="param_change"}).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"nondeletion action must not enable delete restore");
   Check(SpinWait.SpinUntil(()=>Volatile.Read(ref ElementHistoryDurableStore.Active)==0,2000),"previous validation did not finish");
   ElementHistoryDurableStore.Gate.Reset();Interlocked.Exchange(ref ElementHistoryDurableStore.MaxActive,0);
   var batch=new List<ElementHistoryEvent>();var queryWatch=Stopwatch.StartNew();bool allPreparing=true;
   for(int i=0;i<16;i++){var item=New();batch.Add(item);ElementHistoryDurableStore.Values[item.EventId]=new HistoryRestoreRequest{Recipe=ElementHistoryDurableStore.NativeRecipe()};allPreparing&=ElementHistoryDurableBridge.GetReadiness(item).State==ElementHistoryDurableBridge.ReadinessState.Preparing;}
   Check(allPreparing&&queryWatch.ElapsedMilliseconds<250,"concurrent cold UI queries must stay nonblocking while storage is held");
   Check(SpinWait.SpinUntil(()=>Volatile.Read(ref ElementHistoryDurableStore.Active)==2,3000),"validation slots did not allow two independent reads");
   Thread.Sleep(80);Check(Volatile.Read(ref ElementHistoryDurableStore.MaxActive)==2&&Volatile.Read(ref ElementHistoryDurableStore.Active)==2,"storage validation exceeded the two production semaphore slots");
   ElementHistoryDurableStore.Gate.Set();Check(batch.TrueForAll(item=>Wait(item)==ElementHistoryDurableBridge.ReadinessState.Ready),"queued validations did not progress after releasing storage");
   var raced=New();ElementHistoryDurableStore.StaleMisses[raced.EventId]=true;
   Check(ElementHistoryDurableBridge.GetReadiness(raced).State==ElementHistoryDurableBridge.ReadinessState.Preparing,"race starts with a cold preparing entry");
   Check(ElementHistoryDurableStore.MissStarted.Wait(2000),"stale read did not enter controlled fixture gate");
   ElementHistoryDurableBridge.SimulatePublishedReady(raced.EventId);
   Check(ElementHistoryDurableBridge.GetReadiness(raced).State==ElementHistoryDurableBridge.ReadinessState.Ready,"publication must expose Ready immediately");
   ElementHistoryDurableStore.MissGate.Set();Check(ElementHistoryDurableStore.MissReturned.Wait(2000),"stale read did not return");
   Thread.Sleep(100);Check(ElementHistoryDurableBridge.GetReadiness(raced).State==ElementHistoryDurableBridge.ReadinessState.Ready,"an older missing-conversion result must not downgrade published Ready to Preparing");
   var staleFailure=New();ElementHistoryDurableStore.StaleFaults[staleFailure.EventId]=new IOException("older offline lookup");ElementHistoryDurableBridge.GetReadiness(staleFailure);
   Check(ElementHistoryDurableStore.FaultStarted.Wait(2000),"older failing read did not enter gate");ElementHistoryDurableBridge.SimulatePublishedReady(staleFailure.EventId);ElementHistoryDurableStore.FaultGate.Set();
   Check(ElementHistoryDurableStore.FaultReturned.Wait(2000),"older failing read did not return");Thread.Sleep(100);
   Check(ElementHistoryDurableBridge.GetReadiness(staleFailure).State==ElementHistoryDurableBridge.ReadinessState.Ready,"older unavailable result overwrote newer published Ready");
   ElementHistoryDurableStore.StaleFaults.TryRemove(staleFailure.EventId,out _);ElementHistoryDurableStore.Faults[staleFailure.EventId]=new InvalidDataException("corruption detected after publication");
   ElementHistoryDurableBridge.RevalidateCurrentGeneration(staleFailure);
   Check(ElementHistoryDurableBridge.GetReadiness(staleFailure).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"new-generation corruption after Ready must disable restoration");
   var cachedUi=New();ElementHistoryDurableBridge.SimulatePublishedReady(cachedUi.EventId);
   ElementHistoryDurableBridge.ForbiddenNativeLookupId=cachedUi.EventId;
   bool allCached=true;for(int i=0;i<1000;i++)allCached&=ElementHistoryDurableBridge.GetReadiness(cachedUi).State==ElementHistoryDurableBridge.ReadinessState.Ready;
   Check(allCached,"cached UI status must never reconstruct a raw native request");
   ElementHistoryDurableBridge.ForbiddenNativeLookupId=null;
   File.WriteAllText(args[0],"{\"Passed\":true,\"Assertions\":"+count+",\"ProductionSource\":\"ElementHistoryDurableBridge.Readiness.cs\"}");Console.WriteLine(count+" readiness assertions PASS");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
$fixture=$fixture.Replace('__READY_REASON__',$readyReason).Replace('__READ_RECIPE__',$readRecipe)
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
Copy-Item -LiteralPath $json -Destination (Join-Path $output 'Newtonsoft.Json.dll')
$project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit><NoWarn>0649</NoWarn></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/><Compile Include="RetryPolicy.cs"/><Compile Include="Classification.cs"/><Reference Include="Newtonsoft.Json"><HintPath>Newtonsoft.Json.dll</HintPath></Reference></ItemGroup></Project>'
$projectPath=Join-Path $output 'Readiness.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
& dotnet build $projectPath --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'build.log');throw 'Readiness test compilation failed'}
& dotnet (Join-Path $output 'bin/Debug/net8.0/Readiness.Tests.dll') (Join-Path $output 'results.json')
if($LASTEXITCODE -ne 0){throw 'Readiness checks failed'}
Write-Output $output
