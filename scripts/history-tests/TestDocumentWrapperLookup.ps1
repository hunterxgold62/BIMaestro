#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$root = Join-Path $repo ('tmp/history-document-wrapper-tests/' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
function ReadSource([string] $name) { [IO.File]::ReadAllText((Join-Path $folder $name)) }
function ExtractMember([string] $text, [string] $signature) {
    $start = $text.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Production declaration missing: $signature" }
    $opening = $text.IndexOf('{', $start)
    $arrow = $text.IndexOf('=>', $start)
    if ($arrow -ge 0 -and $arrow -lt $opening) {
        $end = $text.IndexOf(';', $arrow)
        return $text.Substring($start, $end - $start + 1)
    }
    $depth = 0
    for ($i = $opening; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        if ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $text.Substring($start, $i - $start + 1) } }
    }
    throw "Production declaration is incomplete: $signature"
}
$opening = ReadSource 'ElementHistoryOpeningArchive.cs'
$tracker = ReadSource 'ElementHistoryTracker.cs'
$native = ReadSource 'ElementHistoryNativeArchive.cs'
$nativeDurable = ReadSource 'ElementHistoryNativeArchive.Durable.cs'
$bridge = ReadSource 'ElementHistoryDurableBridge.cs'
$parts = [Collections.Generic.List[string]]::new()
$parts.Add(@'
using System; using System.IO; using System.Collections.Generic; using System.Linq; using System.Runtime.CompilerServices;
using Autodesk.Revit.DB; using Autodesk.Revit.DB.Events;
namespace Analyse {
internal static class ElementHistoryDurableBridge {
'@)
$parts.Add((ExtractMember $bridge 'internal sealed class DocumentReferenceComparer'))
$parts.Add(@'
}
internal static class ElementHistoryOpeningArchive {
    private sealed class State { internal string Destination; }
    private static readonly Dictionary<Document,State> States=new Dictionary<Document,State>(ElementHistoryDurableBridge.DocumentReferenceComparer.Instance);
'@)
$parts.Add((ExtractMember $opening 'private static bool TryState(Document doc, out State state)'))
$parts.Add((ExtractMember $opening 'internal static void ForgetBySession(string id)'))
$parts.Add(@'
    internal static object Seed(Document doc,string session) {var state=new State {Destination=Path.Combine("cache","snapshot-opening-"+session+".rvt")};States.Add(doc,state);return state;}
    internal static object Lookup(Document doc) {return TryState(doc,out var state)?state:null;}
    internal static int Count=>States.Count;
}
internal static class ElementHistoryTracker {
    internal static readonly Dictionary<Document,HashSet<string>> DocumentCaptureKeys=new Dictionary<Document,HashSet<string>>(ElementHistoryDurableBridge.DocumentReferenceComparer.Instance);
    internal static readonly Dictionary<Document,object> TypeParameterCaches=new Dictionary<Document,object>(ElementHistoryDurableBridge.DocumentReferenceComparer.Instance);
    internal static readonly Dictionary<Document,object> ParameterDisplayCaches=new Dictionary<Document,object>(ElementHistoryDurableBridge.DocumentReferenceComparer.Instance);
    internal static readonly Dictionary<string,object> SnapshotByElementId=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> FamilyTypeSnapshotByKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> SketchOwnerByCurveUniqueId=new Dictionary<string,object>(StringComparer.Ordinal);
    internal static readonly HashSet<string> PrimedDocumentKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> FamilyParameterPrimedDocumentKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> DeferredPrimeByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> PreloadAuditByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> SelectionAuditByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> LastSelectionIdsByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> RestorationCaptureByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
'@)
$parts.Add((ExtractMember $tracker 'private static bool TryDocumentValue<T>'))
$parts.Add((ExtractMember $tracker 'internal static void InvalidateCaptureCaches(Document doc)'))
$parts.Add((ExtractMember $tracker 'internal static void ForgetAuditForKey(Document doc, string documentKey)'))
$parts.Add(@'
    internal static bool Lookup<T>(Dictionary<Document,T> dict,Document doc,out T value)=>TryDocumentValue(dict,doc,out value);
}
internal static class ElementHistoryNativeArchive {
    private sealed class State {internal int Retired;}
'@)
$parts.Add((ExtractMember $nativeDurable 'private sealed class DocumentReferenceComparer'))
$parts.Add((ExtractMember $nativeDurable 'private static bool SameLiveDocument(Document first, Document second)'))
$parts.Add(@'
    private static readonly Dictionary<Document,State> States=new Dictionary<Document,State>(DocumentReferenceComparer.Instance);
    private static readonly Dictionary<Document,object> GeneratedRailingOwners=new Dictionary<Document,object>(DocumentReferenceComparer.Instance);
    private static readonly Dictionary<int,Document> ClosingSourceDocuments=new Dictionary<int,Document>();
    private static bool Owns(Document doc)=>false;
    private static void Retire(State state) {state.Retired++;}
'@)
$parts.Add((ExtractMember $native 'private static bool TryGetState(Document doc, out State state)'))
$parts.Add((ExtractMember $native 'private static bool TryGetState(Document doc, out Document canonical, out State state)'))
$parts.Add((ExtractMember $native 'private static void SourceClosing(object sender,'))
$parts.Add((ExtractMember $native 'private static void SourceClosed(object sender,'))
$parts.Add((ExtractMember $native 'internal static void Forget(Document doc)'))
$parts.Add(@'
    internal static object Seed(Document doc) {var state=new State();States.Add(doc,state);GeneratedRailingOwners.Add(doc,new object());return state;}
    internal static object Lookup(Document doc,out Document canonical) {return TryGetState(doc,out canonical,out var state)?state:null;}
    internal static object SimpleLookup(Document doc)=>TryGetState(doc,out var state)?state:null;
    internal static bool Same(Document a,Document b)=>SameLiveDocument(a,b);
    internal static int Retired(object state)=>((State)state).Retired;
    internal static int Count=>States.Count;
    internal static int Owners=>GeneratedRailingOwners.Count;
    internal static Document ClosingKey(int id)=>ClosingSourceDocuments.TryGetValue(id,out var doc)?doc:null;
    internal static void Closing(Document doc,int id)=>SourceClosing(null,new DocumentClosingEventArgs {Document=doc,DocumentId=id});
    internal static void Closed(int id,RevitAPIEventStatus status)=>SourceClosed(null,new DocumentClosedEventArgs {DocumentId=id,Status=status});
}
}
'@)
[IO.File]::WriteAllText((Join-Path $root 'ProductionExtracts.cs'),($parts -join "`n"))
$fixtures = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
namespace Autodesk.Revit.Exceptions {internal class InvalidObjectException:Exception {internal InvalidObjectException(string message):base(message){}}}
namespace Autodesk.Revit.DB {
internal sealed class Document {
    internal readonly int ModelId;
    internal bool Valid=true,ThrowWhileValid;
    internal static int HashCalls,EqualsCalls;
    internal Document(int modelId) {ModelId=modelId;}
    public bool IsValidObject=>Valid;
    public override int GetHashCode() {HashCalls++;Ensure();return ModelId;}
    public override bool Equals(object other) {EqualsCalls++;Ensure();var doc=other as Document;if(doc==null)return false;doc.Ensure();return ModelId==doc.ModelId;}
    void Ensure() {if(!Valid||ThrowWhileValid)throw new Autodesk.Revit.Exceptions.InvalidObjectException("Native document wrapper is unavailable.");}
}
}
namespace Autodesk.Revit.DB.Events {
internal enum RevitAPIEventStatus {Succeeded,Failed,Cancelled}
internal sealed class DocumentClosingEventArgs {internal Document Document;internal int DocumentId;}
internal sealed class DocumentClosedEventArgs {internal int DocumentId;internal RevitAPIEventStatus Status;}
}
namespace Analyse {
internal static class WrapperChecks {
    static readonly List<object> Results=new List<object>();
    static int Failures;
    static void Check(string name,Func<bool> action) {try{if(!action())throw new Exception("Assertion returned false.");Results.Add(new{Name=name,Passed=true});}catch(Exception ex){Failures++;Results.Add(new{Name=name,Passed=false,Error=ex.ToString()});}}
    static bool NoNativeIdentityCalls(Action action) {var hashes=Document.HashCalls;var equals=Document.EqualsCalls;action();return hashes==Document.HashCalls&&equals==Document.EqualsCalls;}
    static void SeedTracker(Document doc,string oldKey,string newKey,object value) {
        ElementHistoryTracker.DocumentCaptureKeys.Add(doc,new HashSet<string>(new[]{oldKey,newKey},StringComparer.OrdinalIgnoreCase));
        ElementHistoryTracker.TypeParameterCaches.Add(doc,value);ElementHistoryTracker.ParameterDisplayCaches.Add(doc,value);
        foreach(var key in new[]{oldKey,newKey}) {
            ElementHistoryTracker.SnapshotByElementId[key+"|element|42"]=value;ElementHistoryTracker.FamilyTypeSnapshotByKey[key+"|type|1"]=value;ElementHistoryTracker.SketchOwnerByCurveUniqueId[key+"|curve|1"]=value;
            ElementHistoryTracker.PrimedDocumentKeys.Add(key);ElementHistoryTracker.FamilyParameterPrimedDocumentKeys.Add(key);
            ElementHistoryTracker.DeferredPrimeByDocumentKey[key]=value;ElementHistoryTracker.PreloadAuditByDocumentKey[key]=value;ElementHistoryTracker.SelectionAuditByDocumentKey[key]=value;ElementHistoryTracker.LastSelectionIdsByDocumentKey[key]=value;ElementHistoryTracker.RestorationCaptureByDocumentKey[key]=value;
        }
    }
    public static int Main(string[] args) {
        var original=new Document(1);var callback=new Document(1);var other=new Document(2);var stale=new Document(99){Valid=false};
        var opening=ElementHistoryOpeningArchive.Seed(original,"session-a");var otherOpening=ElementHistoryOpeningArchive.Seed(other,"session-other");ElementHistoryOpeningArchive.Seed(stale,"stale");
        Check("two distinct live wrappers reproduce the reference-only miss",()=>{var d=new Dictionary<Document,object>(ElementHistoryDurableBridge.DocumentReferenceComparer.Instance){{original,opening}};return !Object.ReferenceEquals(original,callback)&&!d.TryGetValue(callback,out _);});
        Check("opening lookup recovers same native document via live equality",()=>Object.ReferenceEquals(ElementHistoryOpeningArchive.Lookup(callback),opening));
        Check("opening lookup preserves another model",()=>Object.ReferenceEquals(ElementHistoryOpeningArchive.Lookup(new Document(2)),otherOpening));
        Check("opening lookup skips closed keys during valid fallback",()=>ElementHistoryOpeningArchive.Lookup(new Document(100))==null);
        var value=new object();SeedTracker(original,"old-key","save-as-key",value);SeedTracker(other,"other-old","other-new",new object());
        Check("tracker generic lookup finds live wrapper alias",()=>ElementHistoryTracker.Lookup(ElementHistoryTracker.DocumentCaptureKeys,callback,out var keys)&&keys.Contains("save-as-key"));
        Check("tracker type cache shares canonical value across wrappers",()=>ElementHistoryTracker.Lookup(ElementHistoryTracker.TypeParameterCaches,callback,out var cache)&&Object.ReferenceEquals(cache,value));
        Check("tracker invalidation resolves canonical live wrapper",()=>{ElementHistoryTracker.InvalidateCaptureCaches(callback);return !ElementHistoryTracker.TypeParameterCaches.ContainsKey(original)&&!ElementHistoryTracker.ParameterDisplayCaches.ContainsKey(original)&&ElementHistoryTracker.TypeParameterCaches.ContainsKey(other);});
        ElementHistoryTracker.TypeParameterCaches.Add(original,value);ElementHistoryTracker.ParameterDisplayCaches.Add(original,value);
        var native=ElementHistoryNativeArchive.Seed(original);ElementHistoryNativeArchive.Seed(other);ElementHistoryNativeArchive.Seed(stale);
        Check("native lookup returns original state and canonical reference",()=>Object.ReferenceEquals(ElementHistoryNativeArchive.Lookup(callback,out var canonical),native)&&Object.ReferenceEquals(canonical,original));
        Check("native simplified lookup supports live wrappers",()=>Object.ReferenceEquals(ElementHistoryNativeArchive.SimpleLookup(callback),native));
        Check("native null lookup is harmless",()=>ElementHistoryNativeArchive.Lookup(null,out var canonical)==null&&canonical==null);
        Check("native live comparison skips invalid wrappers",()=>!ElementHistoryNativeArchive.Same(stale,new Document(99)));
        Check("native invalid-object exception is safely rejected",()=>!ElementHistoryNativeArchive.Same(new Document(11){ThrowWhileValid=true},new Document(11)));
        Check("closing callback captures canonical native key",()=>{ElementHistoryNativeArchive.Closing(callback,7);return Object.ReferenceEquals(ElementHistoryNativeArchive.ClosingKey(7),original);});
        Check("failed native close retains state",()=>{ElementHistoryNativeArchive.Closed(7,RevitAPIEventStatus.Failed);return Object.ReferenceEquals(ElementHistoryNativeArchive.SimpleLookup(callback),native)&&ElementHistoryNativeArchive.Retired(native)==0&&ElementHistoryNativeArchive.ClosingKey(7)==null;});
        Check("cancelled native close retains state",()=>{ElementHistoryNativeArchive.Closing(callback,8);ElementHistoryNativeArchive.Closed(8,RevitAPIEventStatus.Cancelled);return Object.ReferenceEquals(ElementHistoryNativeArchive.SimpleLookup(callback),native)&&ElementHistoryNativeArchive.Retired(native)==0;});
        ElementHistoryNativeArchive.Closing(callback,9);original.Valid=false;callback.Valid=false;
        Check("closed opening canonical lookup uses no native hash or equality",()=>NoNativeIdentityCalls(()=>{if(!Object.ReferenceEquals(ElementHistoryOpeningArchive.Lookup(original),opening))throw new Exception("Lost closed canonical state.");}));
        Check("closed different wrapper does not use native equality",()=>NoNativeIdentityCalls(()=>{if(ElementHistoryOpeningArchive.Lookup(callback)!=null)throw new Exception("Invalid aliases must not be compared.");}));
        Check("closed tracker canonical lookup uses reference identity",()=>NoNativeIdentityCalls(()=>{if(!ElementHistoryTracker.Lookup(ElementHistoryTracker.DocumentCaptureKeys,original,out var keys)||!keys.Contains("old-key"))throw new Exception("Lost captured alias keys.");}));
        Check("tracker close resolves canonical by saved model key without native identity calls",()=>NoNativeIdentityCalls(()=>ElementHistoryTracker.ForgetAuditForKey(callback,"save-as-key")));
        Check("tracker close removes canonical document and type caches",()=>!ElementHistoryTracker.DocumentCaptureKeys.ContainsKey(original)&&!ElementHistoryTracker.TypeParameterCaches.ContainsKey(original)&&!ElementHistoryTracker.ParameterDisplayCaches.ContainsKey(original));
        Check("tracker close cleans both SaveAs alias snapshots",()=>!ElementHistoryTracker.SnapshotByElementId.ContainsKey("old-key|element|42")&&!ElementHistoryTracker.SnapshotByElementId.ContainsKey("save-as-key|element|42")&&!ElementHistoryTracker.FamilyTypeSnapshotByKey.Keys.Any(k=>k.StartsWith("old-key|")||k.StartsWith("save-as-key|"))&&!ElementHistoryTracker.SketchOwnerByCurveUniqueId.Keys.Any(k=>k.StartsWith("old-key|")||k.StartsWith("save-as-key|")));
        Check("tracker close clears completion and deferred queues for both aliases",()=>new[]{"old-key","save-as-key"}.All(k=>!ElementHistoryTracker.PrimedDocumentKeys.Contains(k)&&!ElementHistoryTracker.FamilyParameterPrimedDocumentKeys.Contains(k)&&!ElementHistoryTracker.DeferredPrimeByDocumentKey.ContainsKey(k)&&!ElementHistoryTracker.PreloadAuditByDocumentKey.ContainsKey(k)&&!ElementHistoryTracker.SelectionAuditByDocumentKey.ContainsKey(k)&&!ElementHistoryTracker.LastSelectionIdsByDocumentKey.ContainsKey(k)&&!ElementHistoryTracker.RestorationCaptureByDocumentKey.ContainsKey(k)));
        Check("tracker close preserves unrelated model caches",()=>ElementHistoryTracker.DocumentCaptureKeys.ContainsKey(other)&&ElementHistoryTracker.TypeParameterCaches.ContainsKey(other)&&ElementHistoryTracker.SnapshotByElementId.ContainsKey("other-old|element|42"));
        Check("successful native close removes closed canonical by reference",()=>NoNativeIdentityCalls(()=>ElementHistoryNativeArchive.Closed(9,RevitAPIEventStatus.Succeeded)));
        Check("successful native close retires exactly once",()=>ElementHistoryNativeArchive.Retired(native)==1&&ElementHistoryNativeArchive.SimpleLookup(original)==null&&ElementHistoryNativeArchive.ClosingKey(9)==null);
        Check("native unrelated states survive closure",()=>ElementHistoryNativeArchive.Count==2&&ElementHistoryNativeArchive.Owners==2);
        Check("opening session cleanup removes closed canonical without API equality",()=>NoNativeIdentityCalls(()=>ElementHistoryOpeningArchive.ForgetBySession("session-a")));
        Check("opening session cleanup preserves other sessions",()=>ElementHistoryOpeningArchive.Count==2&&Object.ReferenceEquals(ElementHistoryOpeningArchive.Lookup(other),otherOpening));
        var reopened=new Document(1);var reopenedAlias=new Document(1);var newOpening=ElementHistoryOpeningArchive.Seed(reopened,"session-reopened");var newNative=ElementHistoryNativeArchive.Seed(reopened);
        Check("reopening gets a fresh opening state rather than old wrapper",()=>!Object.ReferenceEquals(newOpening,opening)&&Object.ReferenceEquals(ElementHistoryOpeningArchive.Lookup(reopenedAlias),newOpening));
        Check("reopening gets a fresh native state and canonical key",()=>!Object.ReferenceEquals(newNative,native)&&Object.ReferenceEquals(ElementHistoryNativeArchive.Lookup(reopenedAlias,out var canonical),newNative)&&Object.ReferenceEquals(canonical,reopened));
        Check("reopening has no stale tracker document alias",()=>!ElementHistoryTracker.Lookup(ElementHistoryTracker.DocumentCaptureKeys,reopenedAlias,out _));
        var newValue=new object();SeedTracker(reopened,"reopened-old","reopened-new",newValue);
        Check("reopened tracker cache can be repopulated and shared",()=>ElementHistoryTracker.Lookup(ElementHistoryTracker.TypeParameterCaches,reopenedAlias,out var cached)&&Object.ReferenceEquals(cached,newValue));
        var output=Path.Combine(Path.GetFullPath(args[0]),"results.json");
        File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=Failures==0,Assertions=Results.Count,Failures,RevitInvoked=false,ProductionBuild=false,Scope="Production lookup/comparer/cleanup methods extracted verbatim. Document wrappers and native-retire side effect are simulated.",Results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine((Failures==0?"PASS":"FAIL")+": "+Results.Count+" wrapper-identity assertions; failures "+Failures+"; "+output);
        foreach(var result in Results.Select(r=>JsonSerializer.SerializeToElement(r)).Where(r=>!r.GetProperty("Passed").GetBoolean()))Console.WriteLine(result);
        return Failures==0?0:1;
    }
}
}
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'),$fixtures)
$project = @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="ProductionExtracts.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>
'@
$projectPath=Join-Path $root 'DocumentWrapperLookup.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
$log=Join-Path $root 'build.log'
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> $log
if($LASTEXITCODE -ne 0) {Get-Content $log -Tail 60;throw 'Standalone document-wrapper fixture compilation failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/DocumentWrapperLookup.Tests.dll') $root
if($LASTEXITCODE -ne 0) {throw "Document-wrapper regression failed; see $root/results.json"}
