#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$restoration = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs'))
$reconstruction = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
function Member([string] $source, [string] $signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Production member missing: $signature" }
    $brace = $source.IndexOf('{', $start); $depth = 0
    for ($i = $brace; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ } elseif ($source[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $source.Substring($start, $i - $start + 1) } }
    }
    throw 'Unbalanced production member.'
}
$members = @(
    (Member $restoration 'private static void UseExistingModelDefinitionsOnly('),
    (Member $restoration 'private static void ValidateExistingModelDefinitions('),
    (Member $restoration 'private static Element RestoreWithExistingModelDefinitions(')
) -join "`n"
$dto = Member $reconstruction 'internal sealed class HistoryRecipe'
$restore = Member $restoration 'internal static HistoryRestoreBatch Restore('
$validation = $restore.IndexOf('ValidateExistingModelDefinitions(doc, recipe, index)', [StringComparison]::Ordinal)
$import = $restore.IndexOf('ImportDurableDefinition(doc, recipe, nativeSources, index)', [StringComparison]::Ordinal)
$creation = $restore.IndexOf('creationGroup.Start()', [StringComparison]::Ordinal)
if ($validation -lt 0 -or $validation -ge $import -or $validation -ge $creation -or
    !$restore.Contains('recipe.Native = null; recipe.OpeningFallback = null;') -or
    !$restore.Contains('request.Recipe?.UsesExistingModelDefinitions == true || request.Recipe?.Native?.WholeModel != true') -or
    !$restore.Contains('RestoreWithExistingModelDefinitions(doc, recipe, index)') -or
    !$restore.Contains('? "definition_missing" : "reference_missing"')) { throw 'Existing-definition restoration does not preflight before all archive/import/create paths.' }
$retry = Member $restoration 'private static void RetryOpeningFailures('
if (!$retry.Contains('request.Recipe?.UsesExistingModelDefinitions != true')) { throw 'Nominal mode can retry using an archive.' }
$fixture = @'
using System;using System.Collections.Generic;using System.Linq;using Newtonsoft.Json;using Newtonsoft.Json.Linq;
namespace ExistingDefinitionFixture {
class Element {public string UniqueId,Name;public bool IsValidObject=true,Excluded;}
class ElementType:Element {} class Family:Element {} class FamilySymbol:ElementType {public Family Family;}
class GraphicsStyle:Element {} class Level:Element {} class MEPSystemType:ElementType {} class ParameterElement:Element {}
class Document {public Dictionary<string,Element> Elements=new Dictionary<string,Element>();public int Mutations,DirectCalls;}
class HistoryNativeArtifact {} class HistoryGeometryRelation {} class HistoryCurve {} class HistoryRoofEdge {}
class HistoryPlacement {public string LevelSourceUniqueId,HostSourceUniqueId;public List<HistoryParameter> ElevationParameters;}
class HistoryParameter {public string Reference,Definition,Name;}
class HistoryPort {public string SystemType;}
class HistoryConnection {public HistoryPort Port,PeerPort;}
class HistoryNetwork {public string SystemType;}
class HistoryCircuit {public HistoryRecipe Recipe;}
__DTO__
static class ElementHistoryReconstruction {
 public static Element FindOriginal(Document doc,string uid)=>uid!=null&&doc.Elements.TryGetValue(uid,out var e)?e:null;
 public static Element RestoreNative(Document doc,HistoryRecipe recipe){doc.DirectCalls++;doc.Mutations++;return new Element{UniqueId="restored-instance"};}
}
static class ElementHistoryExternalModels {public static bool IsExcluded(Element e)=>e?.Excluded==true;}
static class Production {
 static string Resolve(Document doc,Dictionary<string,string> index,string id)=>ElementHistoryReconstruction.FindOriginal(doc,id)!=null?id:id!=null&&index.TryGetValue(id,out var restored)&&ElementHistoryReconstruction.FindOriginal(doc,restored)!=null?restored:null;
__MEMBERS__
 static int count; static void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
 static HistoryRecipe Recipe()=>new HistoryRecipe{UsesExistingModelDefinitions=true,Kind="family",Type="symbol-original",Level="level-original"};
 static Document Model(){var d=new Document();d.Elements.Add("symbol-original",new FamilySymbol{UniqueId="symbol-original",Name="Type A",Family=new Family{UniqueId="family-original"}});d.Elements.Add("level-original",new Level{UniqueId="level-original"});return d;}
 static void Reject(Document doc,HistoryRecipe recipe,string prefix,string uid){var before=doc.Mutations;var calls=doc.DirectCalls;bool rejected=false;try{RestoreWithExistingModelDefinitions(doc,recipe,new Dictionary<string,string>());}catch(InvalidOperationException ex){rejected=ex.Message.StartsWith(prefix)&&ex.Message.Contains(uid);}Check(rejected,"Expected precise missing-reference failure: "+prefix+uid);Check(doc.Mutations==before&&doc.DirectCalls==calls,"Failure must precede all direct creation/mutation.");}
 public static int Run(){
  var defaults=JObject.FromObject(new HistoryRecipe());Check(defaults["UsesExistingModelDefinitions"]==null,"Legacy/default false flag must not be serialized.");
  var persisted=JObject.FromObject(Recipe());Check((bool)persisted["UsesExistingModelDefinitions"],"True flag must serialize.");Check(persisted.ToObject<HistoryRecipe>().UsesExistingModelDefinitions,"True flag must survive a real DTO round trip.");Check(!JObject.Parse("{\"Version\":1,\"Kind\":\"family\"}").ToObject<HistoryRecipe>().UsesExistingModelDefinitions,"Legacy payloads retain false default.");
  var parent=Recipe();var child=new HistoryRecipe{Kind="electrical_system",Native=new HistoryNativeArtifact(),OpeningFallback=new HistoryNativeArtifact()};parent.Native=new HistoryNativeArtifact();parent.OpeningFallback=new HistoryNativeArtifact();parent.ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit{Recipe=child}};UseExistingModelDefinitionsOnly(parent);Check(parent.Native==null&&parent.OpeningFallback==null&&child.Native==null&&child.OpeningFallback==null&&child.UsesExistingModelDefinitions,"Nominal parent excludes archive/import fallback recursively for companion circuits.");
  var doc=Model();var recipe=Recipe();var result=RestoreWithExistingModelDefinitions(doc,recipe,new Dictionary<string,string>());Check(doc.DirectCalls==1&&doc.Mutations==1&&result.UniqueId=="restored-instance","Present exact type dispatches once to native direct creator.");Check(recipe.Type=="symbol-original"&&((FamilySymbol)doc.Elements[recipe.Type]).Family.UniqueId=="family-original","Existing type/family identities remain unchanged.");
  doc=Model();doc.Elements.Remove("symbol-original");Reject(doc,Recipe(),"ExistingModelDefinitionMissing:","symbol-original");
  doc.Elements.Add("other-symbol",new FamilySymbol{UniqueId="other-symbol",Name="Type A",Family=new Family{UniqueId="other-family"}});Reject(doc,Recipe(),"ExistingModelDefinitionMissing:","symbol-original");
  doc=Model();((FamilySymbol)doc.Elements["symbol-original"]).Family=null;Reject(doc,Recipe(),"ExistingModelDefinitionMissing:","symbol-original");
  doc=Model();((FamilySymbol)doc.Elements["symbol-original"]).Family.IsValidObject=false;Reject(doc,Recipe(),"ExistingModelDefinitionMissing:","symbol-original");
  doc=Model();doc.Elements["symbol-original"].Excluded=true;Reject(doc,Recipe(),"ExistingModelDefinitionMissing:","symbol-original");
  doc=Model();doc.Elements["symbol-original"]=new Element{UniqueId="symbol-original"};Reject(doc,Recipe(),"ExistingModelDefinitionMissing:","symbol-original");
  doc=Model();doc.Elements.Remove("level-original");Reject(doc,Recipe(),"ExistingModelReferenceMissing:","level-original");
  doc=Model();recipe=Recipe();recipe.HistoricalPlacement=new HistoryPlacement{LevelSourceUniqueId="deleted-top-level"};Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-top-level");
  recipe=Recipe();recipe.Host="deleted-host";Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-host");
  doc.Elements.Add("restored-host",new Element{UniqueId="restored-host"});ValidateExistingModelDefinitions(doc,recipe,new Dictionary<string,string>{{"deleted-host","restored-host"}});Check(doc.Mutations==0,"A host restored earlier in this batch can be resolved without mutation.");
  recipe=Recipe();recipe.Network=new HistoryNetwork{SystemType="deleted-system"};Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-system");
  recipe=Recipe();recipe.Ports=new List<HistoryPort>{new HistoryPort{SystemType="deleted-port-system"}};Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-port-system");
  recipe=Recipe();recipe.Connections=new List<HistoryConnection>{new HistoryConnection{PeerPort=new HistoryPort{SystemType="deleted-peer-system"}}};Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-peer-system");
  recipe=Recipe();recipe.Parameters=new List<HistoryParameter>{new HistoryParameter{Reference="deleted-material",Name="Material"}};Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-material");
  recipe=Recipe();recipe.Parameters=new List<HistoryParameter>{new HistoryParameter{Definition="deleted-parameter-definition",Name="Comment"}};Reject(doc,recipe,"ExistingModelReferenceMissing:","deleted-parameter-definition");
  doc.Elements.Add("present-system",new MEPSystemType{UniqueId="present-system"});recipe=Recipe();recipe.Network=new HistoryNetwork{SystemType="present-system"};ValidateExistingModelDefinitions(doc,recipe,new Dictionary<string,string>());Check(doc.Mutations==0,"Exact retained system type needs no historical version/import.");
  recipe=Recipe();recipe.Kind="model_curve";recipe.Type="line-style";doc.Elements.Add(recipe.Type,new GraphicsStyle{UniqueId=recipe.Type});ValidateExistingModelDefinitions(doc,recipe,new Dictionary<string,string>());Check(doc.Mutations==0,"Model curves retain exact graphics-style reference.");
  recipe=new HistoryRecipe{UsesExistingModelDefinitions=true,Kind="electrical_system",SystemMembers=new List<string>()};ValidateExistingModelDefinitions(doc,recipe,new Dictionary<string,string>());Check(doc.Mutations==0,"Electrical systems need no fabricated type or level UID.");
  recipe=Recipe();recipe.UsesExistingModelDefinitions=false;Reject(doc,recipe,"ExistingModelReferenceMissing:","recette directe incompatible");
  return count;
 }
}
public static class Tests {public static void Main(){Console.WriteLine("PASS: "+Production.Run()+" assertions for existing model definitions.");}}
}
'@
$testRoot = Join-Path $repo ('tmp/history-existing-definition-tests/' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($testRoot)
[IO.File]::WriteAllText((Join-Path $testRoot 'Fixtures.cs'), $fixture.Replace('__DTO__', $dto).Replace('__MEMBERS__', $members))
$json = [Newtonsoft.Json.JsonConvert].Assembly.Location
$project = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>
<Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="Fixtures.cs"/>
</ItemGroup></Project>
"@
$projectPath = Join-Path $testRoot 'ExistingDefinitions.Tests.csproj'
[IO.File]::WriteAllText($projectPath, $project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $testRoot 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $testRoot 'build.log') -Tail 55; throw 'Existing-definition fixture compilation failed.' }
& dotnet (Join-Path $testRoot 'bin/Debug/net9.0/ExistingDefinitions.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Existing-definition restoration fixture failed.' }
Write-Output 'PASS: 3 restoration boundary contracts. Actual DTO serialization and production preflight/direct dispatch use API doubles; native geometry creation is validated separately in Revit.'
