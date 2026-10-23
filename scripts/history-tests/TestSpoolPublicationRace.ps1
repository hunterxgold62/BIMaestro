#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$root=Join-Path $repo ('tmp/history-spool-publication-tests/'+[guid]::NewGuid().ToString('N'));$null=[IO.Directory]::CreateDirectory($root)
$fixture=@'
using System;using System.IO;using System.Linq;using System.Threading;using System.Threading.Tasks;using Newtonsoft.Json;
namespace Analyse {
internal sealed class ElementHistoryEvent{public string EventId{get;set;}public DateTime Ts{get;set;}public string Action{get;set;}="delete";}
// Only publication is paused. Both producers execute the unchanged production
// Stage/Drain methods with actual files and their actual independent locks.
internal static class File{
 internal static readonly ManualResetEventSlim Published=new ManualResetEventSlim(),Resume=new ManualResetEventSlim();internal static string PublishedPath;
 internal static bool Exists(string p)=>System.IO.File.Exists(p);internal static void Delete(string p)=>System.IO.File.Delete(p);internal static byte[] ReadAllBytes(string p)=>System.IO.File.ReadAllBytes(p);
 internal static void Move(string source,string destination){System.IO.File.Move(source,destination);if(destination.EndsWith(".pending.json",StringComparison.Ordinal)){PublishedPath=destination;Published.Set();if(!Resume.Wait(5000))throw new TimeoutException("Publication gate timed out.");}}
}
internal static class Checks{
 static int Count;static void Check(bool ok,string why){Count++;if(!ok)throw new Exception(why);}
 static int Main(string[] args){try{
  var root=args[0];Directory.CreateDirectory(root);var destination=Path.Combine(root,"shared");var spool=new ElementHistoryLocalSpool(Path.Combine(root,"spool"));var outbox=new ManualResetEventSlim();
  var stage=Task.Run(()=>spool.Stage(destination,new[]{new ElementHistoryEvent{EventId="publication-race",Ts=new DateTime(2026,10,1,9,0,0,DateTimeKind.Utc)}}));
  Check(File.Published.Wait(2000),"Stage did not reach post-Move/pre-verification boundary.");
  var transport=Task.Run(()=>spool.Drain(destination,events=>outbox.Set()));Check(outbox.Wait(2000),"Drain did not finish shared/outbox IO while Stage was paused.");
  Thread.Sleep(100);Check(System.IO.File.Exists(File.PublishedPath),"Drain deleted pending file before producer verification/queue dequeue.");
  Check(!transport.IsCompleted,"Transport acknowledged local file before Stage finished verification.");File.Resume.Set();
  Check(Task.WaitAll(new[]{stage,transport},3000),"Producer and transport did not complete after publication.");
  Check(!spool.HasPending(destination),"Verified and delivered batch remained pending.");
  Check(System.IO.File.ReadAllLines(Path.Combine(destination,"element-history-2026-10-01.jsonl")).Length==1,"Publication race duplicated/lost deletion.");
  System.IO.File.WriteAllText(Path.Combine(root,"results.json"),JsonConvert.SerializeObject(new{Passed=true,Assertions=Count,RevitInvoked=false,Scope="Deterministic pause after production Stage publishes the file, before producer re-read; real concurrent Drain/outbox must wait only at local acknowledgement."}));
  Console.WriteLine("PASS: "+Count+" deterministic Stage/Drain publication-race assertions.");return 0;
 }catch(Exception ex){File.Resume.Set();Console.Error.WriteLine(ex);return 1;}}
}}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixture.cs'),$fixture)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="$([Security.SecurityElement]::Escape((Join-Path $folder 'ElementHistoryLocalSpool.cs')))"/><Compile Include="Fixture.cs"/></ItemGroup></Project>
"@
$projectPath=Join-Path $root 'Race.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Production spool race fixture build failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/Race.Tests.dll') (Join-Path $root 'io')
if($LASTEXITCODE -ne 0){throw "Production spool race assertions failed: $root"}
Write-Output "Results: $root"
