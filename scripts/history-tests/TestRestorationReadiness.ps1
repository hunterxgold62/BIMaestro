#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-readiness-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.Readiness.cs') -Destination (Join-Path $output 'Production.cs')
$fixture=@'
using System;using System.IO;using System.Collections.Generic;using System.Threading;using System.Diagnostics;using System.Collections.Concurrent;
namespace Analyse {
 internal sealed class ElementHistoryEvent{public string EventId,UniqueId,Action="delete";public HistoryRecipe Recipe;}
 internal sealed class HistoryRecipe{public HistoryNativeArtifact Native;}
 internal sealed class HistoryNativeArtifact{public string File,PayloadId;public bool Ready,WholeModel;}
 internal sealed class HistoryRestoreRequest{public HistoryRecipe Recipe;public string CaptureFailure;}
 internal static class CollaborativeModelTrackerStore{internal static string ActiveDirectory;}
 internal static class ElementHistoryDurableStore{
  internal static readonly ManualResetEventSlim Gate=new ManualResetEventSlim(false);
  internal static readonly ConcurrentDictionary<string,HistoryRestoreRequest> Values=new ConcurrentDictionary<string,HistoryRestoreRequest>();
  internal static readonly ConcurrentDictionary<string,Exception> Faults=new ConcurrentDictionary<string,Exception>();
  internal static int CallerThread,Active,MaxActive;
  internal static readonly ConcurrentDictionary<string,bool> StaleMisses=new ConcurrentDictionary<string,bool>();
  internal static readonly ManualResetEventSlim MissStarted=new ManualResetEventSlim(false),MissGate=new ManualResetEventSlim(false),MissReturned=new ManualResetEventSlim(false);
  internal static HistoryRestoreRequest ResolveConversion(string id){
   CallerThread=Thread.CurrentThread.ManagedThreadId;int current=Interlocked.Increment(ref Active),observed;
   do{observed=Volatile.Read(ref MaxActive);if(current<=observed)break;}while(Interlocked.CompareExchange(ref MaxActive,current,observed)!=observed);
   try{if(StaleMisses.ContainsKey(id)){MissStarted.Set();MissGate.Wait();return null;}Gate.Wait();if(Faults.TryGetValue(id,out var error))throw error;return Values.TryGetValue(id,out var value)?value:null;}
   finally{Interlocked.Decrement(ref Active);if(StaleMisses.ContainsKey(id))MissReturned.Set();}
  }
 }
 internal static partial class ElementHistoryDurableBridge{
  static readonly object Sync=new object();static readonly Dictionary<string,string> Errors=new Dictionary<string,string>();
  internal static string EventId(ElementHistoryEvent ev)=>ev.EventId??("legacy-"+ev.UniqueId);
  internal static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev,bool resolve)=>new HistoryRestoreRequest{Recipe=ev.Recipe};
  static IEnumerable<HistoryNativeArtifact> Artifacts(HistoryRecipe recipe){if(recipe?.Native!=null)yield return recipe.Native;}
  internal static void SimulatePublishedReady(string id)=>SetReadiness(id,ReadinessState.Ready,"Published conversion verified.");
 }
 internal static class Checks{
  static int count;static void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
  static ElementHistoryEvent New()=>new ElementHistoryEvent{EventId=Guid.NewGuid().ToString("N")};
  static ElementHistoryDurableBridge.ReadinessState Wait(ElementHistoryEvent ev){var stop=Stopwatch.StartNew();while(stop.ElapsedMilliseconds<5000){var state=ElementHistoryDurableBridge.GetReadiness(ev).State;if(state!=ElementHistoryDurableBridge.ReadinessState.Preparing)return state;Thread.Sleep(10);}throw new Exception("Readiness timeout");}
  static int Main(string[] args){try{
   CollaborativeModelTrackerStore.ActiveDirectory=Path.GetDirectoryName(args[0]);
   var ready=New();ElementHistoryDurableStore.Values[ready.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe()};
   var watch=Stopwatch.StartNew();var before=ElementHistoryDurableBridge.GetReadiness(ready);
   Check(before.State==ElementHistoryDurableBridge.ReadinessState.Preparing,"cold cache must not enable restore");Check(watch.ElapsedMilliseconds<250,"UI query blocked on storage");
   SpinWait.SpinUntil(()=>ElementHistoryDurableStore.CallerThread!=0,1000);Check(ElementHistoryDurableStore.CallerThread!=Thread.CurrentThread.ManagedThreadId,"storage accessed on UI thread");
   ElementHistoryDurableBridge.ReadyChanged+=()=>{throw new Exception("closed UI listener");};
   ElementHistoryDurableStore.Gate.Set();Check(Wait(ready)==ElementHistoryDurableBridge.ReadinessState.Ready,"validated sidecar should enable restore");
   var diagnostic=New();ElementHistoryDurableStore.Values[diagnostic.EventId]=new HistoryRestoreRequest{CaptureFailure="unsupported"};Check(Wait(diagnostic)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"acknowledgement is not restorable");
   var corrupt=New();ElementHistoryDurableStore.Faults[corrupt.EventId]=new InvalidDataException("corrupt");Check(Wait(corrupt)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"corrupt sidecar must stay disabled");
   var raw=New();raw.Recipe=new HistoryRecipe();ElementHistoryDurableBridge.GetReadiness(raw);Thread.Sleep(80);Check(ElementHistoryDurableBridge.GetReadiness(raw).State==ElementHistoryDurableBridge.ReadinessState.Preparing,"new raw recipe must never bypass durable conversion");
   var oldMissing=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N")};Check(Wait(oldMissing)==ElementHistoryDurableBridge.ReadinessState.Unavailable,"legacy without recipe disabled");
   var oldWhole=new ElementHistoryEvent{UniqueId=Guid.NewGuid().ToString("N"),Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact{WholeModel=true,Ready=true,File="snapshot-opening-test.rvt"}}};ElementHistoryDurableBridge.GetReadiness(oldWhole);Thread.Sleep(80);Check(ElementHistoryDurableBridge.GetReadiness(oldWhole).State==ElementHistoryDurableBridge.ReadinessState.Preparing,"opening bridge must not enable on-demand extraction");
   Check(ElementHistoryDurableBridge.GetReadiness(new ElementHistoryEvent{Action="param_change"}).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"nondeletion action must not enable delete restore");
   Check(SpinWait.SpinUntil(()=>Volatile.Read(ref ElementHistoryDurableStore.Active)==0,2000),"previous validation did not finish");
   ElementHistoryDurableStore.Gate.Reset();Interlocked.Exchange(ref ElementHistoryDurableStore.MaxActive,0);
   var batch=new List<ElementHistoryEvent>();var queryWatch=Stopwatch.StartNew();bool allPreparing=true;
   for(int i=0;i<16;i++){var item=New();batch.Add(item);ElementHistoryDurableStore.Values[item.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe()};allPreparing&=ElementHistoryDurableBridge.GetReadiness(item).State==ElementHistoryDurableBridge.ReadinessState.Preparing;}
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
   File.WriteAllText(args[0],"{\"Passed\":true,\"Assertions\":"+count+",\"ProductionSource\":\"ElementHistoryDurableBridge.Readiness.cs\"}");Console.WriteLine(count+" readiness assertions PASS");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
Copy-Item -LiteralPath $json -Destination (Join-Path $output 'Newtonsoft.Json.dll')
$exe=Join-Path $output 'Checks.exe'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /target:exe "/out:$exe" /r:System.Core.dll "/r:$json" (Join-Path $output 'Production.cs') (Join-Path $output 'Fixtures.cs')
if($LASTEXITCODE -ne 0){throw 'Readiness test compilation failed'}
& $exe (Join-Path $output 'results.json')
if($LASTEXITCODE -ne 0){throw 'Readiness checks failed'}
Write-Output $output
