#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$source=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryTracker.cs'))
function Extract([string]$signature){
 $start=$source.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw "Production method missing: $signature"}
 $opening=$source.IndexOf('{',$start);$depth=0
 for($i=$opening;$i -lt $source.Length;$i++){if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}}
 throw "Unterminated production method: $signature"
}
$root=Join-Path $repo ('tmp/history-journal-tests/'+[guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$methods=@(
 (Extract 'private static void EnqueueHistoryEvent(')
 (Extract 'private static void ScheduleLocalStage()')
 (Extract 'private static async Task WorkerLoop(CancellationToken token)')
 (Extract 'internal static void StagePendingForDurability()')
 (Extract 'private static void Flush()')
) -join "`n"
$spool=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryLocalSpool.cs'))
# Only the default LOCALAPPDATA location is redirected into the fixture directory.
$spool=$spool.Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "HistorySpool")','Path.Combine(AppContext.BaseDirectory, "local-spool")')
[IO.File]::WriteAllText((Join-Path $root 'ProductionSpool.cs'),$spool)
$support=@'
using System;using System.Collections.Concurrent;using System.Collections.Generic;using System.Diagnostics;using System.IO;using System.Linq;using System.Text;using System.Threading;using System.Threading.Tasks;using Newtonsoft.Json;
namespace Analyse {
internal sealed class ElementHistoryEvent {public int Id{get;set;}public string EventId{get;set;}public DateTime Ts{get;set;}public string Action{get;set;}="delete";}
internal static class CollaborativeModelTrackerStore {internal static string ActiveDirectory;}
internal static class ElementHistoryHoverInfoService {internal static bool IsEnabled=>false;}
internal static class LatestElementHistoryIndex {internal static void Observe(ElementHistoryEvent ev){}}
internal static class ElementHistoryDurableBridge {
 internal static bool FailOnce;internal static int Delivered;
 internal static void EventsPersisted(IEnumerable<ElementHistoryEvent> events){if(FailOnce){FailOnce=false;throw new IOException("Injected outbox failure");}Delivered+=events.Count();}
}
internal static class JsonConvert {
 internal static bool FailOnce;
 internal static string SerializeObject(object value,Formatting formatting){if(value is ElementHistoryEvent&&FailOnce){FailOnce=false;throw new InvalidOperationException("Injected serialization failure");}return Newtonsoft.Json.JsonConvert.SerializeObject(value,formatting);}
 internal static T DeserializeObject<T>(string value)=>Newtonsoft.Json.JsonConvert.DeserializeObject<T>(value);
}
// Real IO; short-write/fsync failures apply only to the shared destination.
internal sealed class FileStream:Stream {
 readonly System.IO.FileStream Inner;readonly bool Shared;internal static string SharedRoot;
 internal static bool FailWriteOnce,FailFlushOnce;
 internal FileStream(string path,FileMode mode,FileAccess access,FileShare share){Inner=new System.IO.FileStream(path,mode,access,share);Shared=SharedRoot!=null&&Path.GetFullPath(path).StartsWith(SharedRoot,StringComparison.OrdinalIgnoreCase);}
 public override bool CanRead=>Inner.CanRead;public override bool CanSeek=>Inner.CanSeek;public override bool CanWrite=>Inner.CanWrite;
 public override long Length=>Inner.Length;public override long Position{get=>Inner.Position;set=>Inner.Position=value;}
 public override int Read(byte[] bytes,int offset,int count)=>Inner.Read(bytes,offset,count);public override int ReadByte()=>Inner.ReadByte();
 public override void Write(byte[] bytes,int offset,int count){if(Shared&&FailWriteOnce){FailWriteOnce=false;Inner.Write(bytes,offset,Math.Max(1,count/2));throw new IOException("Injected partial write");}Inner.Write(bytes,offset,count);}
 public override void WriteByte(byte value)=>Inner.WriteByte(value);
 public override void Flush()=>Inner.Flush();internal void Flush(bool durable){if(Shared&&FailFlushOnce){FailFlushOnce=false;throw new IOException("Injected durable flush failure");}Inner.Flush(durable);}
 public override long Seek(long offset,SeekOrigin origin)=>Inner.Seek(offset,origin);public override void SetLength(long length)=>Inner.SetLength(length);
 protected override void Dispose(bool disposing){if(disposing)Inner.Dispose();base.Dispose(disposing);}
}
internal static class JournalChecks {
 static readonly ConcurrentQueue<ElementHistoryEvent> Queue=new ConcurrentQueue<ElementHistoryEvent>();static readonly object FileSync=new object();static int _localStageScheduled;
 static readonly TimeSpan HistoryMaintenanceInitialDelay=TimeSpan.Zero,HistoryMaintenanceInterval=TimeSpan.FromHours(1);
 static int AuditFailures,MaintenanceFailures,Assertions;
 static void FlushAudit(){if(Interlocked.Exchange(ref AuditFailures,0)>0)throw new IOException("Injected audit failure");}
 static void CompressOldFiles(){if(Interlocked.Exchange(ref MaintenanceFailures,0)>0)throw new IOException("Injected maintenance failure");}
'@
$checks=@'
 static void Check(bool ok,string why){Assertions++;if(!ok)throw new Exception(why);}
 static ElementHistoryEvent Event(int id)=>new ElementHistoryEvent{Id=id,EventId="event-"+id,Ts=DateTime.UtcNow.Date.AddHours(8).AddMilliseconds(id)};
 static void Enqueue(int count,int start=0){for(int i=0;i<count;i++)Queue.Enqueue(Event(start+i));}
 static string Journal()=>Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"element-history-"+DateTime.UtcNow.ToString("yyyy-MM-dd")+".jsonl");
 static void CheckIds(int count){var rows=File.ReadAllLines(Journal()).Select(JsonConvert.DeserializeObject<ElementHistoryEvent>).ToArray();Check(rows.Length==count,"Wrong raw journal count.");for(int i=0;i<count;i++)Check(rows[i].Id==i,"FIFO identity missing/duplicated at "+i);}
 static void ExpectIO(Action action){try{action();}catch(IOException){Assertions++;return;}throw new Exception("Injected IO failure not exercised.");}
 static int Main(string[] args){try{
  CollaborativeModelTrackerStore.ActiveDirectory=Path.GetFullPath(args[0]);FileStream.SharedRoot=CollaborativeModelTrackerStore.ActiveDirectory;Directory.CreateDirectory(FileStream.SharedRoot);
  Enqueue(2505);Flush();Check(Queue.IsEmpty,"Explicit flush did not stage complete backlog.");CheckIds(2505);
  Enqueue(45,2505);using(var held=new System.IO.FileStream(Journal(),FileMode.Open,FileAccess.ReadWrite,FileShare.None))ExpectIO(Flush);
  Check(Queue.IsEmpty&&ElementHistoryLocalSpool.Default.HasPending(FileStream.SharedRoot),"Shared lock did not preserve durable local queue.");Flush();CheckIds(2550);
  Enqueue(43,2550);var before=File.ReadAllBytes(Journal());FileStream.FailWriteOnce=true;ExpectIO(Flush);
  Check(Queue.IsEmpty&&ElementHistoryLocalSpool.Default.HasPending(FileStream.SharedRoot),"Partial transport lost locally staged deletions.");Check(before.SequenceEqual(File.ReadAllBytes(Journal())),"Short write rollback failed.");Flush();CheckIds(2593);
  Enqueue(7,2593);before=File.ReadAllBytes(Journal());FileStream.FailFlushOnce=true;ExpectIO(Flush);
  Check(before.SequenceEqual(File.ReadAllBytes(Journal()))&&ElementHistoryLocalSpool.Default.HasPending(FileStream.SharedRoot),"Failed fsync was acknowledged.");Flush();CheckIds(2600);
  Enqueue(5,2600);JsonConvert.FailOnce=true;bool failed=false;try{Flush();}catch(InvalidOperationException){failed=true;}Check(failed&&Queue.Count==5,"Failed local serialization lost RAM queue.");Flush();CheckIds(2605);
  Enqueue(5005,2605);Task.WaitAll(Task.Run((Action)Flush),Task.Run((Action)Flush));Check(Queue.IsEmpty,"Concurrent consumers left backlog.");CheckIds(7610);
  Enqueue(3,7610);ElementHistoryDurableBridge.FailOnce=true;ExpectIO(Flush);Check(ElementHistoryLocalSpool.Default.HasPending(FileStream.SharedRoot),"Outbox failure acknowledged pending batch.");Flush();CheckIds(7613);
  using(var held=new System.IO.FileStream(Journal(),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
   var watch=Stopwatch.StartNew();EnqueueHistoryEvent(Event(7613));Check(watch.ElapsedMilliseconds<250,"Enqueue callback blocked on storage.");
   Check(SpinWait.SpinUntil(()=>Queue.IsEmpty,3000),"Local worker did not drain RAM while share locked.");Check(ElementHistoryLocalSpool.Default.HasPending(FileStream.SharedRoot),"Async staging did not leave durable local batch.");
  }Flush();CheckIds(7614);
  using(var cancellation=new CancellationTokenSource()){
   AuditFailures=MaintenanceFailures=1;var worker=WorkerLoop(cancellation.Token);Enqueue(11,7614);
   Check(SpinWait.SpinUntil(()=>Queue.IsEmpty,3000),"Audit/maintenance failure stopped worker staging.");
   Check(SpinWait.SpinUntil(()=>!ElementHistoryLocalSpool.Default.HasPending(FileStream.SharedRoot),3000),"Worker did not finish shared delivery.");
   Check(!worker.IsCompleted,"Worker stopped after transient unrelated failure.");cancellation.Cancel();Check(worker.Wait(1000),"Worker failed to cancel.");
  }CheckIds(7625);Check(ElementHistoryDurableBridge.Delivered==7625,"Outbox acknowledgement count disagrees with journal.");
  File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[0]),"results.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new{Passed=true,Assertions,Events=7625,RevitInvoked=false,Scope="Production Enqueue/ScheduleLocalStage/Worker/Stage/Flush and spool; real IO with injected shared write failures. Default local location redirected into fixture."}));
  Console.WriteLine("PASS: "+Assertions+" assertions, 7625 events; async local staging, FIFO, concurrent flush, shared locks, short-write/fsync rollback, serialization and outbox retries, worker recovery.");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixture.cs'),$support+$methods+$checks)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="ProductionSpool.cs"/><Compile Include="Fixture.cs"/></ItemGroup></Project>
"@
$projectPath=Join-Path $root 'Journal.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Production journal fixture build failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/Journal.Tests.dll') (Join-Path $root 'shared')
if($LASTEXITCODE -ne 0){throw "Production journal assertions failed: $root"}
Write-Output "Results: $root"
