#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableStore.cs'
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$root=Join-Path $repo ('tmp/history-durable-store-tests/'+[guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$fixtures=@'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Analyse {
internal static class CollaborativeModelTrackerStore {internal static string ActiveDirectory;}
internal sealed class ElementHistoryEvent {
    public string EventId {get;set;} public DateTime Ts {get;set;} public string ModelKey {get;set;}
    public int ElementId {get;set;} public string Action {get;set;} public string UniqueId {get;set;}
    public Dictionary<string,object> Delta {get;set;}
}
internal sealed class HistoryNativeArtifact {
    public string File {get;set;} public string PayloadId {get;set;} public string TypeVersion {get;set;}
    public bool WholeModel {get;set;} public bool DefinitionOnly {get;set;} public bool Ready {get;set;}
    public DateTime? ReadyUtc {get;set;} public string RootUniqueId {get;set;}
    public string RootSourceUniqueId {get;set;} public string MemberSourceUniqueId {get;set;}
    public int SourceElementId {get;set;} public int SourceRootElementId {get;set;}
}
internal sealed class HistoryCircuit { public HistoryRecipe Recipe {get;set;} }
internal sealed class HistoryRecipe {
    public int Version {get;set;}=1; public string Kind {get;set;} public string Type {get;set;}
    public HistoryNativeArtifact Native {get;set;} public HistoryNativeArtifact OpeningFallback {get;set;}
    public List<HistoryCircuit> ElectricalCircuits {get;set;}
}
internal sealed class HistoryRestoreRequest {
    public string SourceUniqueId {get;set;} public string Label {get;set;}
    public string CaptureFailure {get;set;} public HistoryRecipe Recipe {get;set;}
}
internal static class StoreChecks {
    static int Assertions;static string Root,Store,Temp;
    static void Check(bool ok,string why) {Assertions++;if(!ok)throw new Exception(why);}
    static void Reject(Action action,string why) {bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,why);}
    static string Hash(byte[] data) {using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
    static string Capsule(string id) {return Path.Combine(Store,"history-data","native",id+".json.gz");}
    static string Conversion(string id) {return Path.Combine(Store,"history-data","conversions",id+".json.gz");}
    static string JobPath(string id) {return Path.Combine(Store,"history-data","jobs",id+".json.gz");}
    static void Gzip(string path,string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using(var file=File.Create(path))using(var gzip=new GZipStream(file,CompressionLevel.Optimal))
        using(var writer=new StreamWriter(gzip,new UTF8Encoding(false)))writer.Write(content);
    }
    static string Gunzip(string path) {using(var file=File.OpenRead(path))using(var zip=new GZipStream(file,CompressionMode.Decompress))using(var r=new StreamReader(zip))return r.ReadToEnd();}
    static HistoryNativeArtifact Artifact(string id) {return new HistoryNativeArtifact {PayloadId=id,Ready=true,TypeVersion="type-version",RootUniqueId="root",RootSourceUniqueId="original-root",MemberSourceUniqueId="original-member"};}
    static HistoryRestoreRequest Request(string id) {return new HistoryRestoreRequest {SourceUniqueId="original-member",Label="retained",Recipe=new HistoryRecipe {Kind="native_archive",Native=Artifact(id)}};}
    static ElementHistoryEvent Job(string id) {return new ElementHistoryEvent {EventId=id,Ts=new DateTime(2026,10,23,9,0,0,DateTimeKind.Utc),ModelKey="model-a",ElementId=42,UniqueId="deleted-original",Action="delete",Delta=new Dictionary<string,object>{{"recipe",new HistoryRecipe {Kind="native_archive",Native=new HistoryNativeArtifact {File="snapshot-opening-bridge.rvt",WholeModel=true,SourceElementId=42}}}}};}
    static void NoPartial() {Check(!Directory.GetFiles(Temp,"*.partial",SearchOption.AllDirectories).Any(),"A failed materialization left a partial binary behind.");}
    static void CorruptCapsule(string id,string content,string reason) {
        var path=Capsule(id);var before=File.ReadAllBytes(path);Gzip(path,content);
        Reject(()=>ElementHistoryDurableStore.MaterializeNative(id),reason);NoPartial();File.WriteAllBytes(path,before);
    }
    public static int Main(string[] args) {
        Root=Path.GetFullPath(args[0]);Directory.CreateDirectory(Root);
        Temp=Path.Combine(Root,"temp");Directory.CreateDirectory(Temp);
        Environment.SetEnvironmentVariable("TMP",Temp);Environment.SetEnvironmentVariable("TEMP",Temp);
        Check(Path.GetFullPath(Path.GetTempPath()).StartsWith(Temp,StringComparison.OrdinalIgnoreCase),"Tests must keep every real scratch write inside the workspace fixture.");
        Store=Path.Combine(Root,"store");CollaborativeModelTrackerStore.ActiveDirectory=Store;
        var inputs=Path.Combine(Root,"inputs");Directory.CreateDirectory(inputs);
        var lengths=new[]{1,2,3,49151,49152,49153,65536,131073};string payload=null,source=null;
        foreach(var length in lengths) {
            var data=new byte[length];new Random(1024+length).NextBytes(data);
            var path=Path.Combine(inputs,"source-"+length+".rvt");File.WriteAllBytes(path,data);
            var id=ElementHistoryDurableStore.PublishNative(path);
            Check(id==Hash(data),"Payload identity must be the SHA256 of the original binary.");
            Check(File.ReadAllBytes(path).SequenceEqual(data),"Publishing a capsule must leave its source scratch/bridge untouched.");
            var envelope=JObject.Parse(Gunzip(Capsule(id)));
            Check((int)envelope["Schema"]==1 && (string)envelope["Sha256"]==id && (long)envelope["Length"]==length,"Native JSON metadata did not match binary identity/length.");
            Check(Convert.FromBase64String((string)envelope["NativeBase64"]).SequenceEqual(data),"The GZ must contain actual native bytes as JSON Base64, not a filename or renamed RVT.");
            var first=ElementHistoryDurableStore.MaterializeNative(id);var second=ElementHistoryDurableStore.MaterializeNative(id);
            Check(first!=second && File.ReadAllBytes(first).SequenceEqual(data) && File.ReadAllBytes(second).SequenceEqual(data),"Each acquisition must materialize an exact independently owned scratch RVT.");
            ElementHistoryDurableStore.ReleaseMaterialized(first);ElementHistoryDurableStore.ReleaseMaterialized(second);
            Check(!File.Exists(first)&&!File.Exists(second),"Released materializations must disappear.");
            ElementHistoryDurableStore.ReleaseMaterialized(first);
            Check(ElementHistoryDurableStore.PublishNative(path)==id,"A fully verified existing capsule must be reusable.");
            payload=id;source=path;
        }
        Check(!Directory.GetFiles(Path.Combine(Store,"history-data"),"*.rvt",SearchOption.AllDirectories).Any(),"No permanent RVT may appear in history-data.");
        var nativeFile=Capsule(payload);var saved=File.ReadAllBytes(nativeFile);
        var text=Gunzip(nativeFile);var obj=JObject.Parse(text);var base64=(string)obj["NativeBase64"];
        CorruptCapsule(payload,text.Replace("\"Schema\":1","\"Schema\":2"),"Unknown capsule schemas must be refused.");
        CorruptCapsule(payload,text.Replace(payload,new string('0',64)),"A declared hash differing from its ID must fail.");
        CorruptCapsule(payload,"{\"Schema\":1,\"Kind\":\"native\",\"Sha256\":\""+payload+"\",\"Length\":"+(ElementHistoryDurableStore.MaxNativeBytes+1)+",\"NativeBase64\":\"\"}","Oversized declared payloads must fail before allocation/materialization.");
        CorruptCapsule(payload,text.Replace("\"Length\":131073","\"Length\":0131073"),"Non-JSON leading-zero lengths must be rejected.");
        CorruptCapsule(payload,text.Replace(base64,"!!!!"),"Invalid base64 must fail.");
        CorruptCapsule(payload,text.Replace(base64,base64+" "),"Whitespace in native Base64 must not bypass canonical validation.");
        CorruptCapsule(payload,text+"{}","Trailing JSON after the native capsule must fail.");
        CorruptCapsule(payload,text.Substring(0,text.Length-3),"Truncated native JSON must fail.");
        var changed=JObject.Parse(text);var bytes=Convert.FromBase64String(base64);bytes[0]^=1;changed["NativeBase64"]=Convert.ToBase64String(bytes);
        CorruptCapsule(payload,changed.ToString(Formatting.None),"A well-formed capsule with changed bytes must fail SHA verification.");
        Gzip(nativeFile,"corrupt-existing");Reject(()=>ElementHistoryDurableStore.PublishNative(source),"Existing content-addressed files must be verified instead of blindly deduplicated.");
        Check(Gunzip(nativeFile)=="corrupt-existing","Corrupt immutable content must not be silently overwritten.");File.WriteAllBytes(nativeFile,saved);
        Reject(()=>ElementHistoryDurableStore.MaterializeNative("../bad"),"Payload path traversal must fail.");
        Reject(()=>ElementHistoryDurableStore.MaterializeNative(new string('f',64)),"A missing native payload must throw.");NoPartial();
        var all=Enumerable.Range(0,12).Select(_=>Task.Run(()=>ElementHistoryDurableStore.PublishNative(source))).ToArray();Task.WaitAll(all);
        Check(all.All(task=>task.Result==payload),"Concurrent native publications must converge on one verified immutable capsule.");
        Check(!Directory.GetFiles(Path.Combine(Store,"history-data"),"*.tmp",SearchOption.AllDirectories).Any(),"Atomic publication must leave no owned temporary files after success/failure.");
        Reject(()=>ElementHistoryDurableStore.ReleaseMaterialized(source),"Release must not delete an unrelated RVT.");Check(File.Exists(source),"An unrelated RVT was deleted.");
        var scratchDirectory=Path.Combine(Path.GetTempPath(),"BIMaestro","history-native");
        var forged=Path.Combine(scratchDirectory,"snapshot-payload-"+payload+"-"+Guid.NewGuid().ToString("N")+".rvt");File.WriteAllBytes(forged,new byte[]{1});
        Reject(()=>ElementHistoryDurableStore.ReleaseMaterialized(forged),"Matching filenames do not prove process ownership.");Check(File.Exists(forged),"A forged scratch name was deleted.");File.Delete(forged);
        var released=ElementHistoryDurableStore.MaterializeNative(payload);ElementHistoryDurableStore.ReleaseMaterialized(released);File.WriteAllBytes(released,new byte[]{1});
        Reject(()=>ElementHistoryDurableStore.ReleaseMaterialized(released),"A replacement at a released path must not be deleted.");Check(File.Exists(released),"A replacement scratch path was deleted.");File.Delete(released);
        var request=Request(payload);ElementHistoryDurableStore.PublishConversion("event-a",request);
        Check(ElementHistoryDurableStore.HasConversion("event-a"),"Published conversions must resolve.");
        using(var locked=new FileStream(nativeFile,FileMode.Open,FileAccess.Read,FileShare.None)) {
            Check(ElementHistoryDurableStore.ResolveConversion("event-a")!=null,"Verified conversion references should use metadata memoization instead of rereading the locked native blob.");
            ElementHistoryDurableStore.PublishConversion("memo-publish",Request(payload));
            Check(ElementHistoryDurableStore.HasConversion("memo-publish"),"Publishing another conversion must not rehash the same immutable blob.");
            Reject(()=>ElementHistoryDurableStore.MaterializeNative(payload),"Materialization must force a native read even when reference validation is memoized.");
            Reject(()=>ElementHistoryDurableStore.PublishNative(source),"Existing native publication must not trust the reference memo blindly.");
        }
        ElementHistoryDurableStore.ResolveConversion("event-a");
        var metadataBefore=File.GetLastWriteTimeUtc(nativeFile);var corrupted=(byte[])saved.Clone();corrupted[corrupted.Length/2]^=1;
        File.WriteAllBytes(nativeFile,corrupted);File.SetLastWriteTimeUtc(nativeFile,metadataBefore.AddSeconds(2));
        Reject(()=>ElementHistoryDurableStore.ResolveConversion("event-a"),"Changed native metadata must invalidate the reference memo and verify integrity again.");
        File.WriteAllBytes(nativeFile,saved);ElementHistoryDurableStore.ResolveConversion("event-a");
        metadataBefore=File.GetLastWriteTimeUtc(nativeFile);File.WriteAllBytes(nativeFile,corrupted);File.SetLastWriteTimeUtc(nativeFile,metadataBefore);
        Reject(()=>ElementHistoryDurableStore.MaterializeNative(payload),"Even a same-length external corruption with restored timestamp must fail full materialization verification.");
        File.WriteAllBytes(nativeFile,saved);ElementHistoryDurableStore.ResolveConversion("event-a");NoPartial();
        var resolved=ElementHistoryDurableStore.ResolveConversion("event-a");Check(resolved.Recipe.Native.PayloadId==payload && resolved.Recipe.Native.TypeVersion=="type-version","Conversion must preserve capsule references and type version metadata.");
        resolved.Label="mutated";Check(ElementHistoryDurableStore.ResolveConversion("event-a").Label=="retained","Readers must not expose a mutable cached request.");
        ElementHistoryDurableStore.PublishConversion("event-a",request);request.Label="different";
        Reject(()=>ElementHistoryDurableStore.PublishConversion("event-a",request),"Different conversion content must not overwrite the same event ID.");
        Check(ElementHistoryDurableStore.ResolveConversion("absent")==null,"Only a truly absent conversion may return null.");
        Reject(()=>ElementHistoryDurableStore.PublishConversion("../escape",Request(payload)),"Event IDs cannot contain path separators.");
        var whole=Request(payload);whole.Recipe.Native.WholeModel=true;
        Reject(()=>ElementHistoryDurableStore.PublishConversion("whole",whole),"A complete-model reference must not be published as a durable conversion.");
        var bridge=Request(payload);bridge.Recipe.Native.File=source;
        Reject(()=>ElementHistoryDurableStore.PublishConversion("bridge",bridge),"A conversion must not retain an external scratch/bridge filename.");
        var missing=Request(new string('a',64));Reject(()=>ElementHistoryDurableStore.PublishConversion("missing",missing),"Missing content IDs must block conversion publication.");
        var nested=new HistoryRestoreRequest {Recipe=new HistoryRecipe {Kind="family",OpeningFallback=Artifact(payload),ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit {Recipe=new HistoryRecipe {Kind="native_archive",Native=Artifact(payload)}}}}};
        nested.Recipe.ElectricalCircuits[0].Recipe.Native.WholeModel=true;
        Reject(()=>ElementHistoryDurableStore.PublishConversion("nested",nested),"Nested electrical recipes must not hide complete checkpoints.");
        nested.Recipe.ElectricalCircuits[0].Recipe.Native.WholeModel=false;nested.Recipe.OpeningFallback.DefinitionOnly=true;
        ElementHistoryDurableStore.PublishConversion("nested",nested);
        Check(ElementHistoryDurableStore.ResolveConversion("nested").Recipe.OpeningFallback.DefinitionOnly,"Definition-only capsule metadata must survive the round trip.");
        var version=Request(payload);version.Recipe.Version=2;Reject(()=>ElementHistoryDurableStore.PublishConversion("future",version),"Unknown future recipe versions must fail closed.");
        Reject(()=>ElementHistoryDurableStore.PublishConversion("empty",new HistoryRestoreRequest()),"Null recipes without an explicit diagnostic must fail.");
        ElementHistoryDurableStore.PublishConversion("policy",new HistoryRestoreRequest {CaptureFailure="Insulation excluded by policy."});
        Check(ElementHistoryDurableStore.ResolveConversion("policy").Recipe==null,"Explicit non-restorable policy outcomes must be durable without claiming recipe fidelity.");
        var conversionFile=Conversion("event-a");var conversionSaved=File.ReadAllBytes(conversionFile);
        Gzip(conversionFile,"{}");Reject(()=>ElementHistoryDurableStore.ResolveConversion("event-a"),"Corruption must throw rather than appear absent.");
        Reject(()=>ElementHistoryDurableStore.HasConversion("event-a"),"HasConversion must not silently accept corrupt content.");File.WriteAllBytes(conversionFile,conversionSaved);
        var envelope2=JObject.Parse(Gunzip(conversionFile));envelope2["Data"]["Label"]="tampered";Gzip(conversionFile,envelope2.ToString(Formatting.None));
        Reject(()=>ElementHistoryDurableStore.ResolveConversion("event-a"),"Conversion payload hashes must be checked on read.");File.WriteAllBytes(conversionFile,conversionSaved);
        ElementHistoryDurableStore.WriteJob(Job("job-a"));var jobSaved=File.ReadAllBytes(JobPath("job-a"));var later=Job("job-a");
        var laterRecipe=(HistoryRecipe)later.Delta["recipe"];laterRecipe.Native.Ready=true;laterRecipe.Native.ReadyUtc=DateTime.UtcNow;
        ElementHistoryDurableStore.WriteJob(later);Check(File.ReadAllBytes(JobPath("job-a")).SequenceEqual(jobSaved),"Ready/ReadyUtc updates must not rewrite an immutable, identical deletion job.");
        later.ElementId=43;Reject(()=>ElementHistoryDurableStore.WriteJob(later),"A different source identity must conflict with an existing job.");
        Reject(()=>ElementHistoryDurableStore.DeleteJob("job-a"),"Jobs cannot be deleted before a durable conversion is published and verified.");
        Check(File.Exists(JobPath("job-a")),"A job was lost before conversion publication.");
        var firstJob=ElementHistoryDurableStore.ReadPendingJobs().Single();firstJob.ElementId=99;
        Check(ElementHistoryDurableStore.ReadPendingJobs().Single().ElementId==42,"Job readers must receive independent DTOs.");
        ElementHistoryDurableStore.WriteJob(Job("z-corrupt"));Gzip(JobPath("z-corrupt"),"{}");
        Check(ElementHistoryDurableStore.ReadPendingJobs().Take(1).Single().EventId=="job-a","Reading one pending job must not eagerly read all other payloads.");
        Reject(()=>ElementHistoryDurableStore.ReadPendingJobs().ToList(),"Corrupt pending job content must throw when reached.");
        File.Delete(JobPath("z-corrupt")); // fixture-owned fault injection, not product cleanup
        ElementHistoryDurableStore.PublishConversion("job-a",Request(payload));
        var jobConversionSaved=File.ReadAllBytes(Conversion("job-a"));Gzip(Conversion("job-a"),"{}");
        Reject(()=>ElementHistoryDurableStore.DeleteJob("job-a"),"A corrupt conversion must not authorize deleting its job.");
        Check(File.Exists(JobPath("job-a")),"A job was deleted after a corrupt conversion.");File.WriteAllBytes(Conversion("job-a"),jobConversionSaved);
        Gzip(nativeFile,"corrupt");Reject(()=>ElementHistoryDurableStore.DeleteJob("job-a"),"A corrupt referenced native capsule must keep its job.");File.WriteAllBytes(nativeFile,saved);
        ElementHistoryDurableStore.DeleteJob("job-a");Check(!File.Exists(JobPath("job-a")) && File.Exists(Conversion("job-a")),"Only the acknowledged job should be removed.");
        var originalStore=Store;CollaborativeModelTrackerStore.ActiveDirectory=Path.Combine(Root,"other-store");
        Check(!ElementHistoryDurableStore.ReadPendingJobs().Any() && ElementHistoryDurableStore.ResolveConversion("event-a")==null,"Indexes and conversion paths must not leak across store changes.");
        CollaborativeModelTrackerStore.ActiveDirectory=originalStore;
        Check(ElementHistoryDurableStore.ResolveConversion("event-a")!=null,"The original store must remain readable after switching back.");
        Check(File.Exists(source),"The store must never remove the original bridge passed to PublishNative.");
        var result=new {Passed=true,Assertions,WorkspaceRoot=Root,RevitInvoked=false,ProductionBuild=false,BoundNativeBytes=ElementHistoryDurableStore.MaxNativeBytes,BoundRecordBytes=ElementHistoryDurableStore.MaxRecordBytes};
        var resultPath=Path.Combine(Root,"results.json");File.WriteAllText(resultPath,JsonConvert.SerializeObject(result,Formatting.Indented));
        Console.WriteLine("PASS: "+Assertions+" real IO assertions against production ElementHistoryDurableStore; results "+resultPath);return 0;
    }
}
}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'),$fixtures)
$project=@"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup>
 <ItemGroup>
  <Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference>
  <Compile Include="$([Security.SecurityElement]::Escape($source))" />
  <Compile Include="Fixtures.cs" />
 </ItemGroup>
</Project>
"@
$projectPath=Join-Path $root 'DurableStore.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
$log=Join-Path $root 'build.log'
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> $log
if($LASTEXITCODE -ne 0) {Get-Content $log -Tail 60;throw 'Standalone durable-store fixture compilation failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/DurableStore.Tests.dll') (Join-Path $root 'io')
if($LASTEXITCODE -ne 0) {throw 'Durable-store IO fixtures failed.'}
