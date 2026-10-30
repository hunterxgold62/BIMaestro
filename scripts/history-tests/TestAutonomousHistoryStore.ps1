#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$root=Join-Path $repo ('tmp/history-autonomous-store-tests/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
function ReadSource([string]$name){[IO.File]::ReadAllText((Join-Path $base $name))}
function ExtractBlock([string]$text,[string]$signature){
 $start=$text.IndexOf($signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Missing production declaration: $signature"}
 $opening=$text.IndexOf('{',$start);$depth=0
 for($i=$opening;$i -lt $text.Length;$i++){
  if($text[$i] -eq '{'){$depth++}
  if($text[$i] -eq '}'){$depth--;if($depth -eq 0){return $text.Substring($start,$i-$start+1)}}
 }
 throw "Unclosed production declaration: $signature"
}
$reconstruction=ReadSource 'ElementHistoryReconstruction.cs'
$network=ReadSource 'ElementHistoryNetwork.cs'
$dtoStart=$reconstruction.IndexOf('    [JsonObject(');$dtoEnd=$reconstruction.IndexOf('    internal static partial class ElementHistoryReconstruction')
$netStart=$network.IndexOf('    [JsonObject(');$netEnd=$network.IndexOf('    internal static partial class ElementHistoryNetwork')
if($dtoStart -lt 0 -or $dtoEnd -le $dtoStart -or $netStart -lt 0 -or $netEnd -le $netStart){throw 'Production DTO boundaries changed.'}
$production=@(
 'using System;using System.Collections.Generic;using System.Linq;using Newtonsoft.Json;using Newtonsoft.Json.Linq;namespace Analyse {'
 $reconstruction.Substring($dtoStart,$dtoEnd-$dtoStart)
 $network.Substring($netStart,$netEnd-$netStart)
 (ExtractBlock (ReadSource 'ElementHistoryNativeArchive.cs') 'internal sealed class HistoryNativeArtifact')
 (ExtractBlock (ReadSource 'ElementHistoryNativeArchive.cs') 'internal sealed class HistoryNativeContextDefinition')
 (ExtractBlock (ReadSource 'ElementHistoryRelations.cs') 'internal sealed class HistoryGeometryRelation')
 (ExtractBlock (ReadSource 'ElementHistoryRestoration.cs') 'internal sealed class HistoryRestoreRequest')
 (ExtractBlock (ReadSource 'ElementHistoryTracker.cs') 'internal sealed class ElementHistoryEvent')
 'internal enum ElectricalSystemType { UndefinedSystemType=0,PowerCircuit=1 }'
 'internal sealed class HistoryDeletionElementAudit { public string BatchId {get;set;} }'
 'internal static class ElementHistoryReconstruction {'
 (ExtractBlock $reconstruction 'internal static HistoryRecipe ReadRecipe(object raw)')
 '}}'
)-join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $root 'ProductionExtracts.cs'),$production)
$fixture=@'
using System;using System.IO;using System.IO.Compression;using System.Linq;using System.Collections.Generic;using Newtonsoft.Json;using Newtonsoft.Json.Linq;
namespace Analyse {
internal static class CollaborativeModelTrackerStore { internal static string ActiveDirectory; }
internal static class AutonomousStoreChecks {
 static int Assertions;
 static void Check(bool value,string reason){Assertions++;if(!value)throw new Exception(reason);}
 static void Reject(Action operation,string reason){bool rejected=false;try{operation();}catch{rejected=true;}Check(rejected,reason);}
 static HistoryRestoreRequest Direct(string kind="family")=>new HistoryRestoreRequest {
  SourceUniqueId="deleted-uid",Label="Pump",Category="Equipment",SuperComponentUniqueId="parent-uid",FamilyTypeUniqueId="type-uid",
  Recipe=new HistoryRecipe {Kind=kind,Type="type-uid",Level="level-uid",UsesExistingModelDefinitions=true,Point=new[]{12.0,20.0,30.0},
   BasisX=new[]{1.0,0.0,0.0},BasisZ=new[]{0.0,0.0,1.0},Host="wall-uid",Parameters=new List<HistoryParameter>{new HistoryParameter{BuiltIn=-1,Name="diameter",Storage=2,Number=.25}},
   Network=kind=="network"?new HistoryNetwork{Kind="pipe",SystemType="system-type",Start=new[]{0.0,1.0,2.0},End=new[]{10.0,1.0,2.0},Diameter=.25}:null,
   Ports=new List<HistoryPort>{new HistoryPort{Point=new[]{12.0,20.0,30.0},Direction=new[]{1.0,0.0,0.0},Domain=1,Classification="SupplyHydronic",SystemType="system-type",SystemSourceUniqueId="system-id",SystemSourceCaptured=true}},
   Connections=new List<HistoryConnection>{new HistoryConnection{SourceMember="deleted-uid",Peer="peer-uid",Port=new HistoryPort{Point=new[]{12.0,20.0,30.0}},PeerPort=new HistoryPort{Point=new[]{12.0,20.0,30.0}}}}
  }};
 static HistoryRestoreRequest Clone(HistoryRestoreRequest request)=>JObject.FromObject(request).ToObject<HistoryRestoreRequest>();
 static string ReadGzip(string path){using(var input=File.OpenRead(path))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip))return reader.ReadToEnd();}
 static void Gzip(string path,string text){using(var output=File.Create(path))using(var zip=new GZipStream(output,CompressionLevel.Optimal))using(var writer=new StreamWriter(zip))writer.Write(text);}
 public static int Main(string[] args){
  var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);CollaborativeModelTrackerStore.ActiveDirectory=root;
  var record=Path.Combine(root,"history-data","autonomous-conversions","event-a.json.gz");
  Check(ElementHistoryDurableStore.ResolveAutonomousConversion("missing")==null,"Missing autonomous record must be null.");
  var request=Direct();ElementHistoryDurableStore.PublishAutonomousConversion("event-a",request);
  Check(File.Exists(record),"Autonomous JSON record must have its own directory.");
  var envelope=JObject.Parse(ReadGzip(record));Check((string)envelope["Kind"]=="autonomousconversion","Separate envelope kind is required.");
  Check(envelope["Data"]["Recipe"]["Native"]==null&&envelope["Data"]["Recipe"]["OpeningFallback"]==null,"Serialized semantic recipe must contain no binary dependency.");
  var restored=ElementHistoryDurableStore.ResolveAutonomousConversion("event-a");
  Check(restored.SourceUniqueId==request.SourceUniqueId&&restored.Recipe.Type=="type-uid"&&restored.Recipe.Level=="level-uid","Exact project identities must survive disk publication.");
  Check(restored.Recipe.Host=="wall-uid"&&restored.Recipe.Point.SequenceEqual(new[]{12.0,20.0,30.0}),"Host and position must round trip.");
  Check(restored.Recipe.Ports[0].SystemType=="system-type"&&restored.Recipe.Ports[0].SystemSourceUniqueId=="system-id","Historical system metadata must remain semantic JSON.");
  Check(restored.Recipe.Connections[0].Peer=="peer-uid"&&restored.Recipe.Parameters[0].Number==.25,"Connections and instance parameters must survive.");
  Check(ElementHistoryDurableStore.ResolveConversion("event-a")==null,"Autonomous records must not change ordinary resolver precedence.");
  var saved=File.ReadAllBytes(record);ElementHistoryDurableStore.PublishAutonomousConversion("event-a",request);Check(saved.SequenceEqual(File.ReadAllBytes(record)),"Idempotent publication preserves immutable bytes.");
  request.Recipe.Point[0]=999;Check(ElementHistoryDurableStore.ResolveAutonomousConversion("event-a").Recipe.Point[0]==12,"Publisher mutations must not alter stored recipes.");
  Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("event-a",request),"An event cannot be overwritten with a different recipe.");
  Check(saved.SequenceEqual(File.ReadAllBytes(record)),"Conflicting publication must preserve original bytes.");
  File.SetLastWriteTimeUtc(record,DateTime.UtcNow.AddMonths(-4));Check(ElementHistoryDurableStore.ResolveAutonomousConversion("event-a")!=null,"Autonomous recovery must not expire with opening-copy TTL.");
  var network=Direct("network");ElementHistoryDurableStore.PublishAutonomousConversion("pipe-a",network);
  Check(ElementHistoryDurableStore.ResolveAutonomousConversion("pipe-a").Recipe.Network.End[0]==10&&ElementHistoryDurableStore.ResolveAutonomousConversion("pipe-a").Recipe.Network.Diameter==.25,"Pipe start/end/diameter must survive.");
  var nested=Direct();nested.Recipe.ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit{SourceUniqueId="circuit",Recipe=new HistoryRecipe{Kind="electrical_system",SystemMembers=new List<string>{"deleted-uid"},ElectricalSystemType=1,UsesExistingModelDefinitions=true}}};
  ElementHistoryDurableStore.PublishAutonomousConversion("nested",nested);Check(ElementHistoryDurableStore.ResolveAutonomousConversion("nested").Recipe.ElectricalCircuits.Count==1,"Supported circuit recipes must round trip.");
  var bad=Direct();bad.Recipe.Native=new HistoryNativeArtifact{PayloadId=new string('a',64)};Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-native",bad),"A top-level native payload must be rejected even when missing on disk.");
  bad=Direct();bad.Recipe.OpeningFallback=new HistoryNativeArtifact{WholeModel=true,File="snapshot-opening.rvt"};Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-fallback",bad),"Opening fallback must not hide a source dependency.");
  bad=Clone(nested);bad.Recipe.ElectricalCircuits[0].Recipe.Native=new HistoryNativeArtifact{PayloadId=new string('b',64)};Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-nested-native",bad),"Nested native payload must be rejected.");
  bad=Clone(nested);bad.Recipe.ElectricalCircuits[0].Recipe.UsesExistingModelDefinitions=false;Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-nested-mode",bad),"Nested modes must be autonomous too.");
  bad=Clone(nested);bad.Recipe.ElectricalCircuits[0]=null;Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-null-circuit",bad),"Null circuit must be rejected.");
  bad=Direct();bad.Recipe.UsesExistingModelDefinitions=false;Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-mode",bad),"Explicit current-project definition mode is required.");
  bad=Direct();bad.Recipe.Version=99;Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-version",bad),"Unknown recipe versions must be rejected.");
  bad=Direct();bad.Recipe.Kind="future-kind";Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-kind",bad),"Unknown recipe kinds must be rejected by the production parser.");
  bad=Direct();bad.Recipe.Type=null;Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-type",bad),"Incomplete semantic recipes must be rejected.");
  bad=Direct();bad.SourceUniqueId=null;Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-identity",bad),"Source identity is required for later reconnection.");
  Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("bad-failure",new HistoryRestoreRequest{CaptureFailure="not available"}),"A final failure is not an autonomous recipe.");
  Reject(()=>ElementHistoryDurableStore.PublishAutonomousConversion("../escape",Direct()),"Traversal cannot select an autonomous record path.");
  var source=Path.Combine(root,"source.rvt");File.WriteAllBytes(source,new byte[]{1,2,3,4,5});var payload=ElementHistoryDurableStore.PublishNative(source);
  var native=new HistoryRestoreRequest{SourceUniqueId="deleted-uid",Recipe=new HistoryRecipe{Kind="native_archive",Native=new HistoryNativeArtifact{PayloadId=payload,TypeVersion="v"}}};
  ElementHistoryDurableStore.PublishNativeConversion("event-a",native);Check(ElementHistoryDurableStore.ResolveConversion("event-a").Recipe.Native.PayloadId==payload,"Ordinary resolver must still prefer native conversion.");
  var nativeDirectory=Path.Combine(root,"history-data","native");File.Delete(Path.Combine(nativeDirectory,payload+".json.gz"));File.Delete(source);
  Check(ElementHistoryDurableStore.ResolveAutonomousConversion("event-a").Recipe.Point[0]==12,"Autonomous JSON must still work when all source and native bytes have gone.");
  Reject(()=>ElementHistoryDurableStore.ResolveConversion("event-a"),"Native corruption remains visible through the native resolver.");
  File.WriteAllText(record,"broken gzip");Reject(()=>ElementHistoryDurableStore.ResolveAutonomousConversion("event-a"),"Corruption must not be silently accepted.");File.WriteAllBytes(record,saved);
  envelope=JObject.Parse(ReadGzip(record));envelope["Data"]["Recipe"]["Type"]="tampered";Gzip(record,envelope.ToString(Formatting.None));Reject(()=>ElementHistoryDurableStore.ResolveAutonomousConversion("event-a"),"Valid gzip with modified JSON must fail integrity checks.");File.WriteAllBytes(record,saved);
  ElementHistoryEvent Job(string id)=>new ElementHistoryEvent{EventId=id,Action="delete",Ts=DateTime.UtcNow,ModelKey="model",UniqueId="deleted-uid",Delta=new Dictionary<string,object>{{"recipe",Direct().Recipe}}};
  string JobPath(string id)=>Path.Combine(root,"history-data","jobs",id+".json.gz");
  string AutoPath(string id)=>Path.Combine(root,"history-data","autonomous-conversions",id+".json.gz");
  ElementHistoryDurableStore.WriteJob(Job("auto-only-job"));ElementHistoryDurableStore.PublishAutonomousConversion("auto-only-job",Direct());
  Check(ElementHistoryDurableStore.HasConversion("auto-only-job"),"HasConversion must include semantic-only durable data.");
  ElementHistoryDurableStore.DeleteJob("auto-only-job");Check(!File.Exists(JobPath("auto-only-job"))&&File.Exists(AutoPath("auto-only-job")),"Only autonomous JSON must acknowledge a job without deleting recovery data.");
  ElementHistoryDurableStore.WriteJob(Job("auto-corrupt-job"));ElementHistoryDurableStore.PublishAutonomousConversion("auto-corrupt-job",Direct());Gzip(AutoPath("auto-corrupt-job"),"{}");
  Reject(()=>ElementHistoryDurableStore.DeleteJob("auto-corrupt-job"),"A corrupt autonomous record must not acknowledge its job.");Check(File.Exists(JobPath("auto-corrupt-job")),"A corrupt autonomous record must retain its job.");
  ElementHistoryDurableStore.WriteJob(Job("auto-invalid-semantic"));var invalid=Direct();invalid.Recipe.Type=null;
  typeof(ElementHistoryDurableStore).GetMethod("WriteRecord",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{Path.Combine(root,"history-data"),"autonomousconversion","auto-invalid-semantic",JObject.FromObject(invalid),false});
  Reject(()=>ElementHistoryDurableStore.DeleteJob("auto-invalid-semantic"),"A hash-valid but invalid semantic recipe cannot acknowledge a job.");Check(File.Exists(JobPath("auto-invalid-semantic")),"Semantic validation failure must preserve its job.");
  ElementHistoryDurableStore.WriteJob(Job("auto-stale-native"));ElementHistoryDurableStore.PublishAutonomousConversion("auto-stale-native",Direct());
  var nativeRecordRoot=Path.Combine(root,"history-data","native-conversions");Directory.CreateDirectory(nativeRecordRoot);Gzip(Path.Combine(nativeRecordRoot,"auto-stale-native.json.gz"),"{}");
  ElementHistoryDurableStore.DeleteJob("auto-stale-native");Check(!File.Exists(JobPath("auto-stale-native")),"Valid autonomous JSON must acknowledge without reading a stale native record.");
  Check(ElementHistoryDurableStore.HasConversion("auto-stale-native"),"Valid autonomous data must take precedence for the completion predicate too.");
  var other=Path.Combine(root,"other-store");CollaborativeModelTrackerStore.ActiveDirectory=other;Check(ElementHistoryDurableStore.ResolveAutonomousConversion("event-a")==null,"Autonomous paths must remain store-scoped.");
  CollaborativeModelTrackerStore.ActiveDirectory=root;Check(ElementHistoryDurableStore.ResolveAutonomousConversion("event-a")!=null,"Original autonomous recipe must remain readable after store switch.");
  Check(!Directory.GetFiles(Path.Combine(root,"history-data","autonomous-conversions"),"*.tmp").Any(),"Publication must not leave temporary records.");
  var result=new{Passed=true,Assertions,RevitInvoked=false,ProductionBuild=false,Scope="Real IO store, actual production DTOs and ReadRecipe; four-month-old record survives deletion of all native bytes."};
  File.WriteAllText(Path.Combine(root,"results.json"),JsonConvert.SerializeObject(result,Formatting.Indented));Console.WriteLine("PASS: "+Assertions+" autonomous JSON storage assertions");return 0;
 }
}}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'),$fixture)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>
<Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference>
<Compile Include="$([Security.SecurityElement]::Escape((Join-Path $base 'ElementHistoryDurableStore.cs')))" />
<Compile Include="$([Security.SecurityElement]::Escape((Join-Path $base 'HistoryOpeningClassification.cs')))" />
<Compile Include="ProductionExtracts.cs" /><Compile Include="Fixtures.cs" /></ItemGroup></Project>
"@
$projectPath=Join-Path $root 'AutonomousStore.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
$log=Join-Path $root 'build.log'
dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> $log
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $log -Tail 40;throw 'Autonomous JSON store fixture compilation failed.'}
dotnet (Join-Path $root 'bin/Debug/net9.0/AutonomousStore.Tests.dll') (Join-Path $root 'io')
if($LASTEXITCODE -ne 0){throw 'Autonomous JSON store checks failed.'}
Write-Output ('Report: '+(Join-Path $root 'io/results.json'))
