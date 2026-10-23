#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$root=Join-Path $repo ('tmp/history-local-spool-tests/'+[guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$fixture=@'
using System;using System.IO;using System.IO.Compression;using System.Linq;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using Newtonsoft.Json;using Newtonsoft.Json.Linq;
namespace Analyse {
internal sealed class ElementHistoryEvent {
 public string EventId{get;set;} public DateTime Ts{get;set;} public string ModelKey{get;set;} public int ElementId{get;set;}
 public string UniqueId{get;set;} public string Action{get;set;} public Dictionary<string,object> Delta{get;set;}
}
internal static class Checks {
 static int Count;static void Check(bool ok,string why){Count++;if(!ok)throw new Exception(why);}
 static void Reject<T>(Action action,string why) where T:Exception {try{action();}catch(T){Count++;return;}throw new Exception(why);}
 static ElementHistoryEvent[] Events(string prefix,int count)=>Enumerable.Range(0,count).Select(i=>new ElementHistoryEvent{EventId=prefix+i,Ts=new DateTime(2026,10,1,9,0,0,DateTimeKind.Utc).AddSeconds(i),ModelKey="model-a",ElementId=i,UniqueId=prefix+i,Action="delete",Delta=new Dictionary<string,object>{{"point",new[]{1.0,2.0,3.0}},{"recipe","frozen"}}}).ToArray();
 static string Journal(string store)=>Path.Combine(store,"element-history-2026-10-01.jsonl");
 static int Lines(string store)=>File.Exists(Journal(store))?File.ReadAllLines(Journal(store)).Length:0;
 static void Ack(IEnumerable<ElementHistoryEvent> events){Check(events.All(e=>e.Delta.ContainsKey("point")),"Frozen payload changed during replay.");}
 static string Archived(string store){var path=Path.Combine(store,"element-history-2026-10.jsonl.gz");if(!File.Exists(path))return "";using(var input=File.OpenRead(path))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip))return reader.ReadToEnd();}
 static void Compact(string store){var path=Journal(store);var text=Archived(store)+(File.Exists(path)?File.ReadAllText(path):"");var target=Path.Combine(store,"element-history-2026-10.jsonl.gz");using(var output=File.Create(target))using(var gzip=new GZipStream(output,CompressionLevel.Optimal))using(var writer=new StreamWriter(gzip))writer.Write(text);File.Delete(path);}
 static IEnumerable<int> HeldRead(ManualResetEventSlim entered,ManualResetEventSlim release){entered.Set();release.Wait(5000);yield return 7;}
 static int Main(string[] args){try{
  string root=Path.GetFullPath(args[0]),mode=args[1],store=Path.Combine(root,"shared");Directory.CreateDirectory(root);
  var spool=new ElementHistoryLocalSpool(Path.Combine(root,"spool"));
  if(mode=="stage-and-exit"){
   File.WriteAllText(store,"network share unavailable: this is deliberately a file");spool.Stage(store,Events("crash-",4));
   Check(File.ReadAllText(store).StartsWith("network share"),"Stage touched the network destination.");Check(spool.HasPending(store),"Local deletion not durable before process termination.");
   Console.WriteLine("Local batch flushed; process terminates before network delivery.");Environment.Exit(29);
  }
  if(mode=="restart"){
   File.Delete(store);Check(spool.HasPending(store),"Fresh process lost its local queue.");Check(spool.Drain(store,Ack)==4,"Fresh process could not replay all deletions.");
   Check(!spool.HasPending(store)&&Lines(store)==4,"Journal acknowledgement did not empty spool.");
   Check(!File.Exists(Path.Combine(store,"element-history-"+DateTime.UtcNow.ToString("yyyy-MM-dd")+".jsonl")),"Offline replay moved the historical date to transport day.");
  }
  if(mode=="crash-after-append"){
   spool.Stage(store,Events("append-crash-",3));spool.Drain(store,_=>Environment.Exit(31));throw new Exception("Crash point not reached.");
  }
  if(mode=="restart-after-append"){
   Check(Lines(store)==7,"Crash did not occur after shared append.");Check(spool.Drain(store,Ack)==3,"Post-append crash did not replay outbox.");
   Check(Lines(store)==7&&!spool.HasPending(store),"Post-append crash duplicated or lost journal rows.");
  }
  if(mode=="io"){
   spool.Stage(store,Events("locked-",2));
   using(var locked=new FileStream(Journal(store),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
    Reject<IOException>(()=>spool.Drain(store,Ack),"Shared lock not exercised.");
   Check(spool.HasPending(store),"Network failure acknowledged local deletion.");Check(spool.Drain(store,Ack)==2&&Lines(store)==9,"Shared unlock did not recover.");
   spool.Stage(store,Events("outbox-",2));Reject<IOException>(()=>spool.Drain(store,_=>{throw new IOException("outbox offline");}),"Outbox failure not exercised.");
   Check(spool.HasPending(store)&&Lines(store)==11,"Journal/outbox gap lost pending work.");Check(spool.Drain(store,Ack)==2&&Lines(store)==11,"Outbox retry duplicated journal events.");
   spool.Stage(store,Events("corrupt-",1));var path=Directory.GetFiles(Path.Combine(root,"spool"),"*.pending.json").Single();var original=File.ReadAllText(path);
   File.WriteAllText(path,original.Replace("frozen","changed"));Reject<InvalidDataException>(()=>spool.Drain(store,Ack),"Corrupt local data accepted.");
   Check(File.Exists(path)&&Lines(store)==11,"Corrupt local batch was deleted or delivered.");File.WriteAllText(path,original);Check(spool.Drain(store,Ack)==1,"Repaired fixture did not recover.");
   spool.Stage(store,Events("parallel-",1));var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();
   var transport=Task.Run(()=>spool.Drain(store,_=>{entered.Set();release.Wait(5000);}));Check(entered.Wait(2000),"Transport test gate did not open.");
   try{var local=Task.Run(()=>spool.Stage(store,Events("parallel-local-",1)));Check(local.Wait(1500),"Local durability waited for network IO lock.");}
   finally{release.Set();}Check(transport.Wait(2000),"Transport did not finish.");spool.Drain(store,Ack);
   Check(Lines(store)==14&&!spool.HasPending(store),"Concurrent stage and delivery lost events.");
   var otherStore=Path.Combine(root,"other");spool.Stage(otherStore,Events("other-",1));Check(spool.Drain(store,Ack)==0&&spool.HasPending(otherStore),"Store switch sent another project's pending data to the wrong destination.");spool.Drain(otherStore,Ack);
   Check(ElementHistoryRetryPolicy.IsTransient(new IOException("offline")),"IO failure not retryable.");
   Check(ElementHistoryRetryPolicy.IsTransient(new InvalidOperationException("stage",new IOException("offline"))),"Wrapped IO failure not retryable.");
   Check(!ElementHistoryRetryPolicy.IsTransient(new InvalidDataException("hash mismatch")),"Integrity failure must not auto retry.");
   Check(!ElementHistoryRetryPolicy.IsTransient(new InvalidOperationException("unsupported")),"Unsupported source must not auto retry.");
   Check(ElementHistoryRetryPolicy.Delay(1).TotalSeconds==1&&ElementHistoryRetryPolicy.Delay(3).TotalSeconds==4&&ElementHistoryRetryPolicy.Delay(20).TotalSeconds==60,"Backoff is not bounded.");
   // Reconstruct the exact disk state of a process killed mid-append: complete
   // local batch plus attempted marker and a partial final shared JSON line.
   var tornStore=Path.Combine(root,"torn-share");spool.Stage(tornStore,Events("torn-",2));Directory.CreateDirectory(tornStore);
   var tornPath=Directory.GetFiles(Path.Combine(root,"spool"),"*.pending.json").Single();var envelope=JObject.Parse(File.ReadAllText(tornPath));
   var fragment=((string)envelope["Payload"]).Substring(0,70);File.WriteAllText(tornPath+".attempted",(string)envelope["Sha256"]);File.WriteAllText(Journal(tornStore),fragment);
   Check(spool.Drain(tornStore,Ack)==2&&Lines(tornStore)==2,"Torn shared append did not replay the complete durable batch.");
   var evidence=Directory.GetFiles(Path.Combine(tornStore,"history-data","journal-recovery"),"*.fragment").Single();Check(File.ReadAllText(evidence)==fragment,"Torn fragment was discarded before preservation.");
   Check(File.ReadAllLines(Journal(tornStore)).All(line=>JObject.Parse(line)!=null),"Recovered journal still contains unreadable crash fragment.");
   var compactStore=Path.Combine(root,"compact-share");spool.Stage(compactStore,Events("archived-",2));Reject<IOException>(()=>spool.Drain(compactStore,_=>{throw new IOException("crash boundary before outbox ack");}),"Compaction gap fixture did not append.");
   spool.WithTransportLock(compactStore,()=>Compact(compactStore));Check(Archived(compactStore).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==2,"Old daily events were not compacted.");
   Check(spool.Drain(compactStore,Ack)==2&&Lines(compactStore)==0,"Replay after J+14 compaction duplicated events already in GZip.");
   var otherProcess=new ElementHistoryLocalSpool(Path.Combine(root,"other-process-spool"));var compactEntered=new ManualResetEventSlim();var compactRelease=new ManualResetEventSlim();
   var compaction=Task.Run(()=>otherProcess.WithTransportLock(compactStore,()=>{compactEntered.Set();compactRelease.Wait(5000);Compact(compactStore);}));
   Check(compactEntered.Wait(2000),"Compactor did not acquire shared lease.");spool.Stage(compactStore,Events("compaction-race-",1));
   var append=Task.Run(()=>spool.Drain(compactStore,Ack));Thread.Sleep(100);Check(!append.IsCompleted,"Append ignored another process's compaction lease.");
   compactRelease.Set();Check(Task.WaitAll(new[]{compaction,append},3000),"Shared append/compaction did not resume.");
   Check(Lines(compactStore)==1&&Archived(compactStore).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==2,"Concurrent append and compaction lost or duplicated historical events.");
   var readEntered=new ManualResetEventSlim();var readRelease=new ManualResetEventSlim();
   var streamed=Task.Run(()=>otherProcess.ReadWithTransportLock(compactStore,()=>HeldRead(readEntered,readRelease)).ToArray());
   Check(readEntered.Wait(2000),"Streamed reader did not acquire transport lease.");spool.Stage(compactStore,Events("read-race-",1));
   var readerAppend=Task.Run(()=>spool.Drain(compactStore,Ack));Thread.Sleep(100);Check(!readerAppend.IsCompleted,"Append exposed partial data to a leased strict reader.");
   readRelease.Set();Check(Task.WaitAll(new Task[]{streamed,readerAppend},3000)&&streamed.Result.Single()==7,"Streamed lease did not release after complete enumeration.");
   using(var enumerator=otherProcess.ReadWithTransportLock(compactStore,()=>new[]{1,2}).GetEnumerator())Check(enumerator.MoveNext(),"Streamed reader did not yield first item.");
   bool reacquired=false;spool.WithTransportLock(compactStore,()=>reacquired=true);Check(reacquired,"Early iterator disposal leaked shared lease.");
   // Event dates are 1 Oct; validate the durable transport view at simulated J+14.
   var simulatedNow=new DateTime(2026,10,15,9,0,0,DateTimeKind.Utc);var rows=File.ReadAllLines(Journal(store)).Select(JsonConvert.DeserializeObject<ElementHistoryEvent>).ToArray();
   Check(rows.Length==14&&rows.Select(e=>e.EventId).Distinct().Count()==14&&rows.All(e=>simulatedNow.Date-e.Ts.Date==TimeSpan.FromDays(14)),"Simulated J+14 lost identities/date/payload.");
  }
  File.WriteAllText(Path.Combine(root,mode+"-result.json"),JsonConvert.SerializeObject(new{Passed=true,Assertions=Count,RevitInvoked=false,SimulatedDate="2026-10-15",Scope="Production local spool/retry policy; actual disk IO and fresh-process crash boundaries. No native Revit restoration proof."}));
  Console.WriteLine("PASS "+mode+": "+Count+" assertions");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'),$fixture)
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="$([Security.SecurityElement]::Escape((Join-Path $folder 'ElementHistoryLocalSpool.cs')))"/><Compile Include="$([Security.SecurityElement]::Escape((Join-Path $folder 'ElementHistoryRetryPolicy.cs')))"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>
"@
$projectPath=Join-Path $root 'LocalSpool.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Production spool fixture build failed.'}
$dll=Join-Path $root 'bin/Debug/net9.0/LocalSpool.Tests.dll'
foreach($phase in @(@('stage-and-exit',29),@('restart',0),@('crash-after-append',31),@('restart-after-append',0),@('io',0))){
 & dotnet $dll (Join-Path $root 'io') $phase[0]
 if($LASTEXITCODE -ne $phase[1]){throw "Spool phase $($phase[0]) returned $LASTEXITCODE; expected $($phase[1])."}
}
Write-Output "PASS: local-first durable staging, process exits/replay, network/outbox retry without duplicate journal rows, corruption refusal, concurrent staging, store isolation and simulated J+14. Results: $root"
