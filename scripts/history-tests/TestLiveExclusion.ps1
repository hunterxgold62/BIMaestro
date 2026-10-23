#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo ('tmp/history-live-exclusion-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$productionFolder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$tracker=[IO.File]::ReadAllText((Join-Path $productionFolder 'ElementHistoryTracker.cs'))
$bridge=[IO.File]::ReadAllText((Join-Path $productionFolder 'ElementHistoryDurableBridge.cs'))
$audit=[IO.File]::ReadAllText((Join-Path $productionFolder 'ElementHistoryAudit.cs'))
$external=[IO.File]::ReadAllText((Join-Path $productionFolder 'ElementHistoryExternalModels.cs'))
function Extract([string]$Source,[string]$Start,[string]$End) {
    $first=$Source.IndexOf($Start,[StringComparison]::Ordinal)
    $last=if($first -ge 0){$Source.IndexOf($End,$first+$Start.Length,[StringComparison]::Ordinal)}else{-1}
    if($first -lt 0 -or $last -le $first){throw ('Production extraction anchors missing: '+$Start)}
    $Source.Substring($first,$last-$first)
}
$event=Extract $tracker '    internal sealed class ElementHistoryEvent' '    internal static class ElementHistoryTracker'
$metadata=Extract $tracker '        private sealed class ExcludedElementMetadata' '        private static readonly Dictionary<string, ExcludedElementMetadata>'
$deletionAudit=Extract $audit '    internal sealed class HistoryDeletionElementAudit' '    internal sealed class HistoryDeletionBatchAudit'
$predicate=Extract $bridge '        private static bool HasLiveExclusion(ElementHistoryEvent ev)' '        private static bool QueueExcludedAnnotationAcknowledgements'
$externalPredicate=Extract $external '        internal static bool IsExcludedClass(string fullName)' '    }'
$header=@'
using System; using System.Collections.Generic; using System.Linq; using Newtonsoft.Json; using Newtonsoft.Json.Linq;
namespace Analyse {
'@
$harness=@'
internal static partial class LiveExclusionHarness {
 internal static bool Invoke(ElementHistoryEvent value)=>HasLiveExclusion(value);
 internal static object Metadata(int id,string uid,string kind,string reason,bool alive=true) => new ExcludedElementMetadata {
  SourceElementId=id,SourceUniqueId=uid,Class=kind,Reason=reason,CapturedWhileAlive=alive,Category="fixture-live-category",
  CategoryId=reason=="axis"?-2008214:0,ViewSpecific=false
 };
'@
[IO.File]::WriteAllText((Join-Path $folder 'ProductionExtracts.cs'),$header+'internal static class ElementHistoryExternalModels {'+$externalPredicate+'}'+$event+$deletionAudit+$harness+$metadata+$predicate+"`n}}")
$fixture=@'
using System; using System.Collections.Generic; using System.IO; using System.Linq; using Newtonsoft.Json; using Newtonsoft.Json.Linq;
namespace Analyse {
internal static class Checks {
 static readonly List<object> Cases=new List<object>();
 static void Expect(string name,bool wanted,ElementHistoryEvent value) {
  bool? actual=null;string error=null;try { actual=LiveExclusionHarness.Invoke(value); } catch(Exception ex){error=ex.GetType().Name+":"+ex.Message;}
  Cases.Add(new{Name=name,Expected=wanted,Actual=actual,Error=error,Passed=actual==wanted&&error==null});
 }
 static ElementHistoryEvent Event(object metadata=null,bool includeMetadata=true) => new ElementHistoryEvent {
  EventId="fixture-real-dto",ElementId=123,UniqueId="deleted:123",Action="delete",Category="Non capturé",
  Delta=includeMetadata ? new Dictionary<string,object>{{"excluded3D",metadata},{"deletedUniqueId","live-uid-123"},{"recipe",null}}
    : new Dictionary<string,object>{{"deletedUniqueId","live-uid-123"},{"recipe",null}}
 };
 static JObject Good(string kind="Autodesk.Revit.DB.LinearDimension",string reason="annotation") =>
  JObject.FromObject(LiveExclusionHarness.Metadata(123,"live-uid-123",kind,reason));
 static void Mutate(string name,Action<JObject> mutate) {var metadata=Good();mutate(metadata);Expect(name,false,Event(metadata));}
 static int Main(string[] args) {
  Expect("live-production-DTO-annotation-accepted",true,Event(LiveExclusionHarness.Metadata(123,"live-uid-123","Autodesk.Revit.DB.LinearDimension","annotation")));
  Expect("real-JObject-axis-accepted",true,Event(Good("Autodesk.Revit.DB.Element","axis")));
  Expect("real-JObject-pipe-insulation-accepted",true,Event(Good("Autodesk.Revit.DB.Plumbing.PipeInsulation","insulation")));
  Expect("real-JObject-duct-insulation-accepted",true,Event(Good("Autodesk.Revit.DB.Mechanical.DuctInsulation","insulation")));
  foreach(var kind in new[]{"RevitLinkInstance","RevitLinkType","ImportInstance","CADLinkType"}) {
   var external=Good("Autodesk.Revit.DB."+kind,"external_model");
   Expect("live-external-"+kind+"-accepted",true,Event(external));
   external["CapturedWhileAlive"]=false;Expect("external-"+kind+"-without-live-proof-rejected",false,Event(external));
  }
  Expect("native-family-external-label-rejected",false,Event(Good("Autodesk.Revit.DB.FamilyInstance","external_model")));
  Expect("native-directshape-external-label-rejected",false,Event(Good("Autodesk.Revit.DB.DirectShape","external_model")));
  var tag=Good("Autodesk.Revit.DB.IndependentTag");tag["ViewSpecific"]=true;Expect("view-specific-real-Revit-tag-accepted",true,Event(tag));
  var roundtrip=JsonConvert.DeserializeObject<ElementHistoryEvent>(JsonConvert.SerializeObject(Event(Good())));
  Expect("production-event-DTO-and-live-metadata-JSON-roundtrip",true,roundtrip);
  Expect("null-event-rejected",false,null);
  Expect("null-delta-rejected",false,new ElementHistoryEvent{ElementId=123,Delta=null});
  Expect("null-recipe-alone-never-proves-exclusion",false,Event(null,false));
  var missingRecipe=Event(null,false);missingRecipe.Delta.Remove("recipe");Expect("missing-recipe-alone-never-proves-exclusion",false,missingRecipe);
  var physical=Event(null,false);physical.Category="Murs";physical.Delta["recipe"]=new JObject{{"Kind","wall"}};
  Expect("physical-wall-with-recipe-is-not-excluded",false,physical);
  physical.Delta["recipe"]=null;Expect("physical-wall-without-recipe-is-not-excluded",false,physical);
  Expect("null-excluded-metadata-rejected",false,Event(null));
  Mutate("mismatched-source-element-ID-rejected",m=>m["SourceElementId"]=124);
  Mutate("missing-source-element-ID-rejected",m=>m.Remove("SourceElementId"));
  Mutate("missing-source-UID-rejected",m=>m.Remove("SourceUniqueId"));
  Mutate("blank-source-UID-rejected",m=>m["SourceUniqueId"]=" ");
  Mutate("mismatched-source-UID-rejected",m=>m["SourceUniqueId"]="different-live-element");
  var noUid=Event(Good());noUid.Delta.Remove("deletedUniqueId");Expect("missing-deleted-UID-cannot-prove-metadata-match",false,noUid);
  Mutate("missing-live-proof-rejected",m=>m.Remove("CapturedWhileAlive"));
  Mutate("captured-after-deletion-rejected",m=>m["CapturedWhileAlive"]=false);
  Mutate("missing-class-rejected",m=>m.Remove("Class"));
  Mutate("non-Revit-class-rejected",m=>m["Class"]="Fixture.LinearDimension");
  Mutate("missing-reason-rejected",m=>m.Remove("Reason"));
  Mutate("unknown-exclusion-reason-rejected",m=>m["Reason"]="unavailable-recipe");
  Mutate("physical-wall-with-annotation-label-rejected",m=>m["Class"]="Autodesk.Revit.DB.Wall");
  Mutate("physical-pipe-with-annotation-label-rejected",m=>m["Class"]="Autodesk.Revit.DB.Plumbing.Pipe");
  Expect("physical-wall-with-axis-label-rejected",false,Event(Good("Autodesk.Revit.DB.Wall","axis")));
  Expect("physical-family-with-insulation-label-rejected",false,Event(Good("Autodesk.Revit.DB.FamilyInstance","insulation")));
  var noAxisCategory=Good("Autodesk.Revit.DB.Element","axis");noAxisCategory.Remove("CategoryId");Expect("axis-without-exact-category-proof-rejected",false,Event(noAxisCategory));
  var wrongAxisCategory=Good("Autodesk.Revit.DB.Element","axis");wrongAxisCategory["CategoryId"]=-2000011;Expect("model-wall-category-not-a-center-line",false,Event(wrongAxisCategory));
  var namedAxis=Good("Autodesk.Revit.DB.Element","axis");namedAxis["CategoryId"]=0;namedAxis["Category"]="Ligne d'axe";Expect("axis-name-without-category-ID-proof-rejected",false,Event(namedAxis));
  var fakeTag=Good("Autodesk.Revit.DB.IndependentTag");fakeTag["ViewSpecific"]=false;Expect("tag-class-alone-without-annotation-proof-rejected",false,Event(fakeTag));
  var noRecipe=Event(Good());noRecipe.Delta.Remove("recipe");Expect("valid-live-metadata-still-works-without-recipe",true,noRecipe);
  var failures=Cases.Where(item=>!(bool)item.GetType().GetProperty("Passed").GetValue(item)).ToArray();
  File.WriteAllText(args[0],JsonConvert.SerializeObject(new{Passed=failures.Length==0,Assertions=Cases.Count,Failures=failures,Cases,
   RevitInvoked=false,Scope="Exact production HasLiveExclusion predicate, exact Event/Audit/ExcludedElementMetadata DTO extracts, real Newtonsoft JObject and JSON roundtrip. This does not prove live Revit metadata capture or the conversion queue."},Formatting.Indented));
  foreach(var item in Cases)Console.WriteLine(JsonConvert.SerializeObject(item));
  return failures.Length==0?0:1;
 }
}}
'@
[IO.File]::WriteAllText((Join-Path $folder 'Fixtures.cs'),$fixture)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
if(!(Test-Path -LiteralPath $json)){throw 'Build dependency Newtonsoft.Json.dll is missing; no fake JSON substitute is allowed.'}
$project=@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="ProductionExtracts.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>
"@
$projectPath=Join-Path $folder 'LiveExclusion.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
@{AtUtc=[DateTime]::UtcNow.ToString('o');RevitInvoked=$false;SourceHashes=@('ElementHistoryTracker.cs','ElementHistoryDurableBridge.cs','ElementHistoryAudit.cs','ElementHistoryExternalModels.cs') | ForEach-Object {@{Name=$_;Sha256=(Get-FileHash -LiteralPath (Join-Path $productionFolder $_) -Algorithm SHA256).Hash}};ExtractSha256=(Get-FileHash -LiteralPath (Join-Path $folder 'ProductionExtracts.cs') -Algorithm SHA256).Hash} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $folder 'compiled-source.json') -Encoding UTF8
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $folder 'build.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $folder 'build.log') -Tail 40;throw ('Live exclusion compilation failed: '+$folder)}
& dotnet (Join-Path $folder 'bin/Debug/net9.0/LiveExclusion.Tests.dll') (Join-Path $folder 'results.json') *> (Join-Path $folder 'checks.log')
$code=$LASTEXITCODE
$result=Get-Content -LiteralPath (Join-Path $folder 'results.json') -Raw | ConvertFrom-Json
$result | Select-Object Passed,Assertions,Failures | ConvertTo-Json -Depth 6
Write-Output $folder
if($code -ne 0){throw ('Live exclusion checks rejected current production behavior; inspect results.json in '+$folder)}
