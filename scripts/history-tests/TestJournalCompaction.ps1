#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$source=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryTracker.cs'))
function Extract([string]$signature){$start=$source.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw "Production method missing: $signature"};$opening=$source.IndexOf('{',$start);$depth=0;for($i=$opening;$i -lt $source.Length;$i++){if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}};throw 'Unterminated production method.'}
$methods=@('private static void CompressOldFiles()','private static void ConsolidateDailyGzipArchives(string dir)','private static void AppendHistoryFileToArchive(string sourcePath, string archivePath)','private static IEnumerable<string> ReadCompactionLines(string path, bool compressed)','private static void HashCompactionLine(System.Security.Cryptography.SHA256 hash, string line)','private static void VerifyCompactionArchive(string path, string expectedDigest, long expectedLines)','private static void ReplaceFile(string tempPath, string destinationPath)','private static string GetMonthlyArchivePath(string dir, DateTime date)','private static DateTime? TryGetDailyHistoryDate(string path)','private static string GetHistoryDateToken(string path)')|ForEach-Object{Extract $_}
if(($methods -join "`n").Contains('lock (FileSync)')){throw 'Compaction still blocks local staging FileSync.'}
$root=Join-Path $repo ('tmp/history-compaction-tests/'+[guid]::NewGuid().ToString('N'));$null=[IO.Directory]::CreateDirectory($root)
$spool=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryLocalSpool.cs')).Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "HistorySpool")','Path.Combine(AppContext.BaseDirectory, "local-spool")')
[IO.File]::WriteAllText((Join-Path $root 'ProductionSpool.cs'),$spool)
$fixture=@'
using System;using System.IO;using System.IO.Compression;using System.Linq;using System.Collections.Generic;using System.Globalization;using System.Text;using System.Threading;using System.Threading.Tasks;using Newtonsoft.Json;
namespace Analyse {
internal sealed class ElementHistoryEvent{public string EventId{get;set;}public DateTime Ts{get;set;}public string Action{get;set;}="delete";}
internal static class CollaborativeModelTrackerStore{internal static string ActiveDirectory;}
internal static class ElementHistoryClock{internal static DateTime UtcNow=new DateTime(2026,10,15,12,0,0,DateTimeKind.Utc);}
internal static class File{
 internal static bool CorruptFlushOnce,BlockFlushOnce;internal static readonly ManualResetEventSlim Entered=new ManualResetEventSlim(),Release=new ManualResetEventSlim();
 internal sealed class ArchiveOutput:System.IO.FileStream{internal ArchiveOutput(string path):base(path,FileMode.Create,FileAccess.ReadWrite,FileShare.None){}
  public override void Flush(bool durable){base.Flush(durable);if(BlockFlushOnce){BlockFlushOnce=false;Entered.Set();Release.Wait(5000);}if(CorruptFlushOnce){CorruptFlushOnce=false;SetLength(8);base.Flush(durable);}}}
 internal static System.IO.FileStream Create(string path)=>new ArchiveOutput(path);
 internal static bool Exists(string p)=>System.IO.File.Exists(p);internal static void Delete(string p)=>System.IO.File.Delete(p);
 internal static void Move(string a,string b)=>System.IO.File.Move(a,b);internal static void Replace(string a,string b,string c)=>System.IO.File.Replace(a,b,c);
 internal static DateTime GetLastWriteTimeUtc(string p)=>System.IO.File.GetLastWriteTimeUtc(p);internal static byte[] ReadAllBytes(string p)=>System.IO.File.ReadAllBytes(p);
}
internal static class Checks{
'@
$checks=@'
 static int Count;static string Store;static void Check(bool ok,string why){Count++;if(!ok)throw new Exception(why);}
 static string Daily=>Path.Combine(Store,"element-history-2026-10-01.jsonl");static string Monthly=>Path.Combine(Store,"element-history-2026-10.jsonl.gz");
 static ElementHistoryEvent[] Events(string prefix,int count)=>Enumerable.Range(0,count).Select(i=>new ElementHistoryEvent{EventId=prefix+i,Ts=new DateTime(2026,10,1,9,0,0,DateTimeKind.Utc).AddSeconds(i)}).ToArray();
 static void Stage(string prefix,int count){ElementHistoryLocalSpool.Default.Stage(Store,Events(prefix,count));}
 static void Deliver(){ElementHistoryLocalSpool.Default.Drain(Store,_=>{});if(System.IO.File.Exists(Daily))System.IO.File.SetLastWriteTimeUtc(Daily,new DateTime(2026,10,1,10,0,0,DateTimeKind.Utc));}
 static string[] ArchiveRows(){using(var input=System.IO.File.OpenRead(Monthly))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip))return reader.ReadToEnd().Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);}
 static int Main(string[] args){try{
  Store=args[0];CollaborativeModelTrackerStore.ActiveDirectory=Store;Directory.CreateDirectory(Store);
  var abandoned=Path.Combine(Store,"history-data","journal-compaction-staging");Directory.CreateDirectory(abandoned);System.IO.File.WriteAllText(Path.Combine(abandoned,"element-history-2026-10.jsonl.gz.abandoned.tmp"),"incomplete after process crash");
  Stage("first-",2501);Deliver();CompressOldFiles();Check(!System.IO.File.Exists(Daily)&&ArchiveRows().Length==2501,"J+14 production compaction lost initial large batch.");
  Stage("second-",3);Deliver();CompressOldFiles();Check(!System.IO.File.Exists(Daily)&&ArchiveRows().Length==2504,"Monthly append did not preserve prior archive.");
  Stage("failed-",1);Deliver();var preserved=System.IO.File.ReadAllBytes(Monthly);File.CorruptFlushOnce=true;CompressOldFiles();
  Check(System.IO.File.Exists(Daily)&&System.IO.File.ReadAllLines(Daily).Length==1,"Failed archive verification removed source journal.");Check(preserved.SequenceEqual(System.IO.File.ReadAllBytes(Monthly)),"Corrupt temporary archive replaced prior history.");
  CompressOldFiles();Check(!System.IO.File.Exists(Daily)&&ArchiveRows().Length==2505,"Retry after failed compaction lost data.");
  Stage("race-before-",1);Deliver();File.BlockFlushOnce=true;var compact=Task.Run((Action)CompressOldFiles);Check(File.Entered.Wait(2000),"Production compactor did not enter controlled IO gate.");
  var stage=Task.Run(()=>Stage("race-after-",1));Check(stage.Wait(1500),"Network compaction lock blocked local durability.");
  var append=Task.Run((Action)Deliver);Thread.Sleep(100);Check(!append.IsCompleted,"Append did not respect compaction lease.");File.Release.Set();Check(Task.WaitAll(new[]{compact,append},3000),"Append/compaction failed to resume.");
  Check(ArchiveRows().Length==2506&&System.IO.File.ReadAllLines(Daily).Length==1,"Concurrent production compaction lost shared append.");
  CompressOldFiles();var rows=ArchiveRows().Select(JsonConvert.DeserializeObject<ElementHistoryEvent>).ToArray();Check(rows.Length==2507&&rows.Select(e=>e.EventId).Distinct().Count()==2507,"Final J+14 archive lost or duplicated identities.");
  Check(Directory.GetFiles(Store,"*.tmp").Length==0,"Compaction left abandoned owned temporary files.");
  Check(System.IO.File.ReadAllText(Path.Combine(abandoned,"element-history-2026-10.jsonl.gz.abandoned.tmp"))=="incomplete after process crash","Compaction removed another process's unacknowledged crash evidence.");
  System.IO.File.WriteAllText(Path.Combine(Path.GetDirectoryName(Store),"results.json"),JsonConvert.SerializeObject(new{Passed=true,Assertions=Count,SimulatedClock="2026-10-15",DeletedAt="2026-10-01",Events=2507,RevitInvoked=false,Scope="Actual production compaction methods, spool, real IO; archive corruption injection and concurrent append. No Revit restore."}));
  Console.WriteLine("PASS: "+Count+" production compaction assertions, 2507 J+14 events, verification failure and concurrent append.");return 0;
 }catch(Exception ex){File.Release.Set();Console.Error.WriteLine(ex);return 1;}}
}}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixture.cs'),$fixture+($methods -join "`n")+$checks)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="ProductionSpool.cs"/><Compile Include="Fixture.cs"/></ItemGroup></Project>
"@
$projectPath=Join-Path $root 'Compaction.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Production compaction fixture build failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/Compaction.Tests.dll') (Join-Path $root 'shared')
if($LASTEXITCODE -ne 0){throw "Production compaction assertions failed: $root"}
Write-Output "Results: $root"
