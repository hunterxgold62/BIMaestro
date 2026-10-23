#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$source=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryDurableBridge.cs'))
function Extract([string]$signature){$start=$source.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw "Missing production declaration: $signature"};$opening=$source.IndexOf('{',$start);$depth=0;for($i=$opening;$i -lt $source.Length;$i++){if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}};throw 'Unterminated production declaration.'}
if(!$source.Contains('ReleaseDueTransportFailures(DateTime.UtcNow);') -or !$source.Contains('if (Errors.ContainsKey("load")) return;')){throw 'Retry scheduler integration missing.'}
$root=Join-Path $repo ('tmp/history-transport-retry-tests/'+[guid]::NewGuid().ToString('N'));$null=[IO.Directory]::CreateDirectory($root)
$support=@'
using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Diagnostics;using System.Threading.Tasks;using Newtonsoft.Json;
namespace Analyse {
internal static class CollaborativeModelTrackerStore {internal static string ActiveDirectory;}
internal static class BridgeRetryChecks {
 static readonly object Sync=new object();static readonly Dictionary<string,string> Errors=new Dictionary<string,string>(StringComparer.Ordinal);
 static readonly Dictionary<string,TransportRetry> TransportRetries=new Dictionary<string,TransportRetry>(StringComparer.Ordinal);
 enum ReadinessState{Preparing,Unavailable}static readonly Dictionary<string,ReadinessState> States=new Dictionary<string,ReadinessState>();
 static void SetReadiness(string id,ReadinessState state,string reason){lock(Sync)States[id]=state;}
'@
$methods=@((Extract 'private sealed class TransportRetry'),(Extract 'private static void RecordFailure(string id, Exception ex)'),(Extract 'private static void ReleaseDueTransportFailures(DateTime nowUtc)')) -join "`n"
$checks=@'
 static int Count;static void Check(bool ok,string why){Count++;if(!ok)throw new Exception(why);}
 static int Main(string[] args){try{
  CollaborativeModelTrackerStore.ActiveDirectory=args[0];Directory.CreateDirectory(args[0]);
  RecordFailure("offline",new IOException("network down"));Check(Errors.ContainsKey("offline")&&States["offline"]==ReadinessState.Unavailable,"Transport failure was not explicit.");
  var first=TransportRetries["offline"];Check(first.Failures==1&&first.NextUtc>DateTime.UtcNow,"Initial retry deadline missing.");
  ReleaseDueTransportFailures(first.NextUtc.AddTicks(-1));Check(Errors.ContainsKey("offline"),"Retry released before deadline.");
  ReleaseDueTransportFailures(first.NextUtc);Check(!Errors.ContainsKey("offline")&&States["offline"]==ReadinessState.Preparing,"Due transport job did not become eligible.");
  RecordFailure("offline",new IOException("still offline"));Check(TransportRetries["offline"].Failures==2&&TransportRetries["offline"].NextUtc>DateTime.UtcNow.AddSeconds(1),"Failure did not increase backoff.");
  RecordFailure("corrupt",new InvalidDataException("hash mismatch"));Check(Errors.ContainsKey("corrupt")&&!TransportRetries.ContainsKey("corrupt"),"Integrity error was scheduled for automatic retry.");
  RecordFailure("unsupported",new InvalidOperationException("source missing definition proof"));Check(!TransportRetries.ContainsKey("unsupported"),"Unsupported source became retryable.");
  RecordFailure("wrapped",new InvalidOperationException("export transport",new IOException("share lost")));Check(TransportRetries.ContainsKey("wrapped"),"Wrapped IO failure did not become retryable.");
  ReleaseDueTransportFailures(DateTime.MaxValue);Check(!Errors.ContainsKey("offline")&&!Errors.ContainsKey("wrapped")&&Errors.ContainsKey("corrupt")&&Errors.ContainsKey("unsupported"),"Retry sweep cleared permanent evidence or retained recoverable failure.");
  RecordFailure("offline",new InvalidDataException("later checksum mismatch"));Check(!TransportRetries.ContainsKey("offline")&&Errors.ContainsKey("offline"),"Permanent error did not supersede prior retry state.");
  File.WriteAllText(Path.Combine(args[0],"results.json"),JsonConvert.SerializeObject(new{Passed=true,Assertions=Count,RevitInvoked=false,SimulatedClock=true,Scope="Production RecordFailure/ReleaseDueTransportFailures and retry policy; filesystem diagnostics in workspace only."}));
  Console.WriteLine("PASS: "+Count+" transport retry assertions with simulated deadlines.");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixture.cs'),$support+$methods+$checks)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="$([Security.SecurityElement]::Escape((Join-Path $folder 'ElementHistoryRetryPolicy.cs')))"/><Compile Include="Fixture.cs"/></ItemGroup></Project>
"@
$projectPath=Join-Path $root 'Retry.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Production transport retry fixture build failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/Retry.Tests.dll') (Join-Path $root 'diagnostics')
if($LASTEXITCODE -ne 0){throw "Production transport retry assertions failed: $root"}
Write-Output "Results: $root"
