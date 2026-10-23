#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$root = Join-Path $repo ('tmp/history-durable-bridge-read-tests/' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
function ReadSource([string] $name) { [IO.File]::ReadAllText((Join-Path $folder $name)) }
function ExtractBlock([string] $text, [string] $signature) {
    $start = $text.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Production declaration missing: $signature" }
    $opening = $text.IndexOf('{', $start)
    $depth = 0
    for ($i = $opening; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        if ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $text.Substring($start, $i - $start + 1) } }
    }
    throw "Production declaration is incomplete: $signature"
}
$bridge = ReadSource 'ElementHistoryDurableBridge.cs'
$reconstruction = ReadSource 'ElementHistoryReconstruction.cs'
$network = ReadSource 'ElementHistoryNetwork.cs'
$dtoStart = $reconstruction.IndexOf('    [JsonObject(')
$dtoEnd = $reconstruction.IndexOf('    internal static partial class ElementHistoryReconstruction')
$networkStart = $network.IndexOf('    [JsonObject(')
$networkEnd = $network.IndexOf('    internal static partial class ElementHistoryNetwork')
if ($dtoStart -lt 0 -or $dtoEnd -le $dtoStart -or $networkStart -lt 0 -or $networkEnd -le $networkStart) { throw 'Production DTO boundaries changed.' }
$production = @(
    'using System; using System.IO; using System.Collections.Generic; using System.Linq; using System.Text; using System.Security.Cryptography; using Newtonsoft.Json; using Newtonsoft.Json.Linq; namespace Analyse {'
    $reconstruction.Substring($dtoStart, $dtoEnd - $dtoStart)
    $network.Substring($networkStart, $networkEnd - $networkStart)
    (ExtractBlock (ReadSource 'ElementHistoryNativeArchive.cs') 'internal sealed class HistoryNativeArtifact')
    (ExtractBlock (ReadSource 'ElementHistoryRelations.cs') 'internal sealed class HistoryGeometryRelation')
    (ExtractBlock (ReadSource 'ElementHistoryRestoration.cs') 'internal sealed class HistoryRestoreRequest')
    (ExtractBlock (ReadSource 'ElementHistoryTracker.cs') 'internal sealed class ElementHistoryEvent')
    'internal static class ElementHistoryReconstruction {'
    (ExtractBlock $reconstruction 'internal static HistoryRecipe ReadRecipe(object raw)')
    '}'
    'internal static class ElementHistoryDurableBridge {'
    (ExtractBlock $bridge 'internal static string EventId(ElementHistoryEvent ev)')
    (ExtractBlock $bridge 'internal static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev, bool resolve = true)')
    (ExtractBlock $bridge 'private static IEnumerable<HistoryRecipe> Recipes(HistoryRecipe recipe)')
    '}'
    '}'
) -join "`n"
[IO.File]::WriteAllText((Join-Path $root 'ProductionExtracts.cs'), $production)
$fixtures = @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Analyse {
// Only the Revit enum and the unrelated audit envelope are adapted. All recipe,
// artifact, port and request DTOs, plus the actual readers, come from production.
internal enum ElectricalSystemType { UndefinedSystemType = 0, PowerCircuit = 1 }
internal sealed class HistoryDeletionElementAudit { public string BatchId {get;set;} }
internal static class ElementHistoryDurableStore {
    internal static HistoryRestoreRequest Sidecar;
    internal static Exception Fault;
    internal static int Reads;
    internal static string LastEvent;
    internal static HistoryRestoreRequest ResolveConversion(string eventId) {
        Reads++;LastEvent=eventId;if(Fault!=null)throw Fault;return Sidecar;
    }
}
internal static class BridgeReadChecks {
    static readonly List<object> Results=new List<object>();
    static int Failures;
    static void Check(string name,Func<bool> assertion) {
        try {if(!assertion())throw new Exception("Assertion returned false.");Results.Add(new {Name=name,Passed=true});}
        catch(Exception ex) {Failures++;Results.Add(new {Name=name,Passed=false,Error=ex.ToString()});}
    }
    static void Reject(string name,Action action) {Check(name,()=>{try{action();return false;}catch{return true;}});}
    static ElementHistoryEvent Event(object raw) {
        return new ElementHistoryEvent {EventId="event-a",Ts=new DateTime(2026,10,23,8,0,0,DateTimeKind.Utc),Action="delete",ElementId=42,
            Family="Family",TypeName="Type",Category="Category",ModelKey="model",Delta=new Dictionary<string,object> {
                {"recipe",raw},{"deletedUniqueId","source-uid"},{"captureFailure","legacy diagnostic"},
                {"superComponentUniqueId","parent-uid"},{"familyTypeUniqueId","type-uid"}}};
    }
    static JObject Native() {return JObject.Parse(@"{'Version':1,'Kind':'native_archive','Native':{'WholeModel':true,'File':'snapshot-opening-source.rvt','SourceElementId':42}}");}
    static JObject Direct() {return JObject.Parse(@"{'Version':1,'Kind':'family','Type':'type-uid','Level':'level-uid'}");}
    static void Reset() {ElementHistoryDurableStore.Sidecar=null;ElementHistoryDurableStore.Fault=null;ElementHistoryDurableStore.Reads=0;}
    public static int Main(string[] args) {
        Reset();
        Check("valid native DTO stays linked to its source",()=> {var r=ElementHistoryDurableBridge.ToRequest(Event(Native()));return r.Recipe.Native.WholeModel&&r.Recipe.Native.SourceElementId==42&&r.SourceUniqueId=="source-uid";});
        Check("request metadata preserved",()=> {var r=ElementHistoryDurableBridge.ToRequest(Event(Direct()),false);return r.Label=="Family Type [42]"&&r.Category=="Category"&&r.SuperComponentUniqueId=="parent-uid"&&r.FamilyTypeUniqueId=="type-uid";});
        Check("typed recipe uses same production validation",()=>ElementHistoryDurableBridge.ToRequest(Event(new HistoryRecipe {Kind="family",Type="t",Level="l"}),false).Recipe.Kind=="family");
        foreach(var version in new[]{0,2,99}) {var n=Native();n["Version"]=version;Reject("unknown version "+version+" blocks read and cleanup",()=>ElementHistoryDurableBridge.ToRequest(Event(n),false));}
        var unknown=Native();unknown["Kind"]="future-kind";Reject("unknown kind blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(unknown),false));
        var badSource=Native();badSource["Native"]["SourceElementId"]="not-an-id";Reject("corrupt native property blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(badSource),false));
        var extra=Native();extra["UnexpectedProperty"]=123;Reject("unknown recipe field blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(extra),false));
        var extraArtifact=Native();extraArtifact["Native"]["UnexpectedProperty"]=123;Reject("unknown artifact field blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(extraArtifact),false));
        Reject("non-null empty raw recipe blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(new JObject()),false));
        Reject("non-null array raw recipe blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(new JArray()),false));
        Reject("non-null string raw recipe blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event("{broken"),false));
        Reject("non-null undefined raw recipe blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(JValue.CreateUndefined()),false));
        var missingType=Direct();missingType.Remove("Type");Reject("direct recipe without required type blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(missingType),false));
        var missingLevel=Direct();missingLevel.Remove("Level");Reject("direct recipe without required level blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(missingLevel),false));
        Check("legacy null recipe retains explicit diagnostic",()=> {var r=ElementHistoryDurableBridge.ToRequest(Event(null),false);return r.Recipe==null&&r.CaptureFailure=="legacy diagnostic";});
        Check("JSON null recipe retains explicit diagnostic",()=> {var r=ElementHistoryDurableBridge.ToRequest(Event(JValue.CreateNull()),false);return r.Recipe==null&&r.CaptureFailure=="legacy diagnostic";});
        var nested=Direct();nested["ElectricalCircuits"]=new JArray(new JObject { ["SourceUniqueId"]="circuit",["Recipe"]=Native() });
        Check("valid nested recipe survives read",()=>ElementHistoryDurableBridge.ToRequest(Event(nested),false).Recipe.ElectricalCircuits.Single().Recipe.Native.WholeModel);
        var nestedBad=(JObject)nested.DeepClone();nestedBad["ElectricalCircuits"][0]["Recipe"]["Version"]=2;Reject("nested unknown version blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(nestedBad),false));
        nestedBad=(JObject)nested.DeepClone();nestedBad["ElectricalCircuits"][0]["Recipe"]["Kind"]="future-kind";Reject("nested unknown kind blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(nestedBad),false));
        var deep=(JObject)nested.DeepClone();deep["ElectricalCircuits"][0]["Recipe"]=nestedBad;Reject("deeply nested unknown recipe blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(deep),false));
        nestedBad=(JObject)nested.DeepClone();nestedBad["ElectricalCircuits"][0]["Recipe"]["Unknown"]=1;Reject("nested unknown property blocks read",()=>ElementHistoryDurableBridge.ToRequest(Event(nestedBad),false));
        var metadata=Native();metadata["Native"]["SourceSystemTypeDefined"]=false;metadata["Native"]["DefinitionOnly"]=true;metadata["Native"]["TypeVersion"]="version-a";
        metadata["Ports"]=new JArray(new JObject { ["SystemType"]="system-uid",["SystemVersion"]="version-a" });
        Check("source undefined flag and port definition version survive strict DTO read",()=> {var r=ElementHistoryDurableBridge.ToRequest(Event(metadata),false).Recipe;return r.Native.SourceSystemTypeDefined==false&&r.Native.DefinitionOnly&&r.Native.TypeVersion=="version-a"&&r.Ports[0].SystemVersion=="version-a";});
        Reset();var saved=new HistoryRestoreRequest {SourceUniqueId="durable-source",Recipe=new HistoryRecipe {Kind="native_archive",Native=new HistoryNativeArtifact {PayloadId=new string('a',64)}}};ElementHistoryDurableStore.Sidecar=saved;
        Check("validated sidecar has priority even over corrupt original raw recipe",()=>Object.ReferenceEquals(ElementHistoryDurableBridge.ToRequest(Event(new JObject())),saved));
        Check("sidecar resolved by event identity",()=>ElementHistoryDurableStore.LastEvent=="event-a");
        Reject("resolve false explicitly validates original recipe despite sidecar",()=>ElementHistoryDurableBridge.ToRequest(Event(new JObject()),false));
        Reset();ElementHistoryDurableStore.Fault=new InvalidDataException("corrupt sidecar");
        Reject("corrupt sidecar must propagate instead of falling back to bridge",()=>ElementHistoryDurableBridge.ToRequest(Event(Native())));
        Check("resolve false does not consult sidecar",()=> {var reads=ElementHistoryDurableStore.Reads;return ElementHistoryDurableBridge.ToRequest(Event(Native()),false).Recipe.Native.WholeModel&&ElementHistoryDurableStore.Reads==reads;});
        Reset();var legacy=Event(Native());legacy.EventId=null;var first=ElementHistoryDurableBridge.EventId(legacy);
        Check("legacy event ID is stable across DTO JSON roundtrip",()=>first==ElementHistoryDurableBridge.EventId(JsonConvert.DeserializeObject<ElementHistoryEvent>(JsonConvert.SerializeObject(legacy))));
        legacy.ElementId++;Check("legacy event ID separates source identities",()=>first!=ElementHistoryDurableBridge.EventId(legacy));
        var report=new {Passed=Failures==0,Assertions=Results.Count,Failures,RevitInvoked=false,ProductionBuild=false,Scope="Extracted production ToRequest/Recipes/EventId/ReadRecipe and actual DTOs; sidecar resolver is a controlled adapter, not the real IO store.",Results};
        var output=Path.Combine(Path.GetFullPath(args[0]),"results.json");File.WriteAllText(output,JsonConvert.SerializeObject(report,Formatting.Indented));
        Console.WriteLine((Failures==0?"PASS":"FAIL")+": "+Results.Count+" production-reader assertions; failures "+Failures+"; "+output);
        foreach(var item in Results.Select(JObject.FromObject).Where(r=>(bool)r["Passed"]==false))Console.WriteLine(item.ToString(Formatting.None));
        return Failures==0?0:1;
    }
}
}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'), $fixtures)
$json = Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup>
 <ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="ProductionExtracts.cs"/><Compile Include="Fixtures.cs"/></ItemGroup>
</Project>
"@
$projectPath = Join-Path $root 'DurableBridgeReads.Tests.csproj'
[IO.File]::WriteAllText($projectPath, $project)
$log = Join-Path $root 'build.log'
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> $log
if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 60; throw 'Standalone production bridge-reader fixture compilation failed.' }
& dotnet (Join-Path $root 'bin/Debug/net9.0/DurableBridgeReads.Tests.dll') $root
if ($LASTEXITCODE -ne 0) { throw "Bridge-reader fixtures failed; see $root/results.json" }
