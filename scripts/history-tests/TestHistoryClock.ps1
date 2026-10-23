#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo ('tmp/history-clock-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$source=Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryClock.cs'
Copy-Item -LiteralPath $source -Destination (Join-Path $folder 'ProductionClock.cs')
$fixture=@'
using System;using System.Collections.Generic;using System.IO;using System.Linq;using System.Text.Json;using System.Threading.Tasks;
namespace Analyse {
internal static class Checks {
 static readonly List<object> Cases=new List<object>();
 static void Check(bool ok,string name)=>Cases.Add(new{Name=name,Passed=ok});
 static void Throws<T>(Action action,string name)where T:Exception {bool ok=false;try{action();}catch(T){ok=true;}catch{}Check(ok,name);}
 static bool NearWall()=>Math.Abs((ElementHistoryClock.UtcNow-DateTime.UtcNow).TotalSeconds)<2;
 static int Main(string[] args) {
  string original=Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST");
  DateTime wallBefore=DateTime.UtcNow;
  var simulated=new DateTime(2030,1,15,12,34,56,DateTimeKind.Utc);
  try {
   Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST",null);
   Check(NearWall(),"default-clock-is-real-UTC-without-test-guard");
   Throws<InvalidOperationException>(()=>ElementHistoryClock.SetTestUtcNow(simulated),"injection-rejected-without-isolated-process-guard");
   Throws<InvalidOperationException>(()=>ElementHistoryClock.SetTestUtcNow(null),"reset-also-rejected-outside-isolated-process");
   foreach(var wrong in new[]{"0","true","1 ","TRUE"}) {
    Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST",wrong);
    Throws<InvalidOperationException>(()=>ElementHistoryClock.SetTestUtcNow(simulated),"non-exact-guard-rejected:"+wrong);
   }
   Check(NearWall(),"rejected-injections-do-not-change-clock");
   Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST","1");
   Throws<ArgumentException>(()=>ElementHistoryClock.SetTestUtcNow(DateTime.SpecifyKind(simulated,DateTimeKind.Local)),"local-date-rejected");
   Throws<ArgumentException>(()=>ElementHistoryClock.SetTestUtcNow(DateTime.SpecifyKind(simulated,DateTimeKind.Unspecified)),"unspecified-date-rejected");
   Check(NearWall(),"invalid-date-injections-do-not-change-clock");
   ElementHistoryClock.SetTestUtcNow(simulated);
   Check(ElementHistoryClock.UtcNow==simulated&&ElementHistoryClock.UtcNow.Kind==DateTimeKind.Utc,"valid-UTC-injection-read-exactly");
   var reads=new DateTime[128];Parallel.For(0,reads.Length,index=>reads[index]=ElementHistoryClock.UtcNow);
   Check(reads.All(value=>value==simulated&&value.Kind==DateTimeKind.Utc),"worker-readers-see-same-injected-UTC");
   Check(DateTime.UtcNow>=wallBefore&&DateTime.UtcNow<wallBefore.AddMinutes(1),"system-DateTime-UTC-not-changed-by-injection");
   Throws<ArgumentException>(()=>ElementHistoryClock.SetTestUtcNow(DateTime.SpecifyKind(simulated,DateTimeKind.Unspecified)),"invalid-date-rejected-while-override-active");
   Check(ElementHistoryClock.UtcNow==simulated,"rejected-change-preserves-existing-override");
   Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST",null);
   Throws<InvalidOperationException>(()=>ElementHistoryClock.SetTestUtcNow(null),"guard-cannot-be-bypassed-for-active-reset");
   Check(ElementHistoryClock.UtcNow==simulated,"unauthorized-reset-preserves-override");
   Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST","1");
   ElementHistoryClock.SetTestUtcNow(null);
   Check(NearWall()&&ElementHistoryClock.UtcNow.Kind==DateTimeKind.Utc,"authorized-null-reset-restores-real-UTC");
  }
  finally {
   Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST","1");ElementHistoryClock.SetTestUtcNow(null);
   Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST",original);
  }
  Check(Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST")==original,"fixture-restores-original-child-process-guard");
  var failed=Cases.Where(item=>!(bool)item.GetType().GetProperty("Passed").GetValue(item)).ToArray();
  File.WriteAllText(args[0],JsonSerializer.Serialize(new{Passed=failed.Length==0,Assertions=Cases.Count,Failures=failed,Cases,
   RevitInvoked=false,SystemClockChanged=false,Scope="Unmodified production clock in a separate test process; guard, UTC validation, concurrent reads and reset. Does not validate journal retention or a real fourteen-day interval."},new JsonSerializerOptions{WriteIndented=true}));
  Console.WriteLine(Cases.Count+" clock assertions; failed="+failed.Length);return failed.Length==0?0:1;
 }
}}
'@
[IO.File]::WriteAllText((Join-Path $folder 'Fixtures.cs'),$fixture)
$project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="ProductionClock.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
$projectPath=Join-Path $folder 'HistoryClock.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
@{AtUtc=[DateTime]::UtcNow.ToString('o');SourceSha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;RevitInvoked=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'compiled-source.json') -Encoding UTF8
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $folder 'build.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $folder 'build.log') -Tail 40;throw ('Clock test compilation failed: '+$folder)}
& dotnet (Join-Path $folder 'bin/Debug/net9.0/HistoryClock.Tests.dll') (Join-Path $folder 'results.json')
if($LASTEXITCODE -ne 0){throw ('Clock test failed; preserve evidence: '+$folder)}
Get-Content -LiteralPath (Join-Path $folder 'results.json') -Raw | ConvertFrom-Json | Select-Object Passed,Assertions,Failures | ConvertTo-Json -Depth 5
Write-Output $folder
