#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$source=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryNativeArchive.Durable.cs'))
function Extract([string]$signature){$start=$source.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw "Missing production method: $signature"};$opening=$source.IndexOf('{',$start);$depth=0;for($i=$opening;$i -lt $source.Length;$i++){if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}};throw 'Unterminated production method.'}
$methods=@('private static DurableItem ResolveDurableItem(Document source, HistoryRestoreRequest request)','private static void RejectExternalModel(Element element, string context)','private static void RejectExternalModelsInCapsule(Document capsule)')|ForEach-Object{Extract $_}
$definitions=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryNativeArchive.Definitions.cs'))
foreach($text in @($source,$definitions)){
 $save=$text.IndexOf('capsule.SaveAs(outputRvt, options)',[StringComparison]::Ordinal)
 if($save -lt 0 -or $text.LastIndexOf('RejectExternalModelsInCapsule(capsule);',$save,[StringComparison]::Ordinal) -lt 0){throw 'External model capsule guard missing before SaveAs.'}
}
if(!$definitions.Contains('RejectExternalModel(definition, "frozen_definition_closure");') -or !$definitions.Contains('RejectExternalModel(definition, "frozen_definition_copy");') -or !$source.Contains('RejectExternalModel(byUid[uid], "durable_definition_dependency");')){throw 'External dependency guard missing.'}
$fixture=@'
using System;using System.Linq;using System.Collections.Generic;using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 internal sealed class ElementId{internal int Value;internal int GetIdValue()=>Value;}
 internal class Element{internal string UniqueId;internal ElementId Id;internal Element Root;internal List<Element> Members=new List<Element>();internal string OrdinaryIfcGuid;}
 internal class ElementType:Element{}internal sealed class RevitLinkInstance:Element{}internal sealed class RevitLinkType:ElementType{}internal sealed class ImportInstance:Element{}internal sealed class CADLinkType:ElementType{}internal sealed class MEPSystem:Element{}
 internal sealed class Document{internal readonly Dictionary<string,Element> Elements=new Dictionary<string,Element>();internal Element GetElement(string uid)=>Elements.TryGetValue(uid,out var element)?element:null;internal Element GetElement(ElementId id)=>Elements.Values.FirstOrDefault(e=>e.Id.Value==id.Value);}
 internal sealed class FilteredElementCollector{readonly Document document;bool? typesOnly;internal FilteredElementCollector(Document doc){document=doc;}internal FilteredElementCollector WhereElementIsElementType(){typesOnly=true;return this;}internal FilteredElementCollector WhereElementIsNotElementType(){typesOnly=false;return this;}internal IList<Element> ToElements(){if(!typesOnly.HasValue)throw new InvalidOperationException("CollectorRequiresFilter");return document.Elements.Values.Where(element=>(element is ElementType)==typesOnly.Value).ToList();}}
}
namespace Analyse {
internal sealed class HistoryNativeArtifact{internal string MemberSourceUniqueId;internal int SourceElementId;internal List<int> ExcludedElementIds;}
internal sealed class HistoryRecipe{internal HistoryNativeArtifact Native,OpeningFallback;internal string Type;}
internal sealed class HistoryRestoreRequest{internal string SourceUniqueId;internal HistoryRecipe Recipe;}
internal static class ElementIdExtensions{internal static ElementId CreateElementId(int value)=>new ElementId{Value=value};}
internal static class ElementHistoryOpeningArchive{internal static bool IsEligibleElement(Element element)=>element!=null;}
internal static class ElementHistoryReconstruction{internal static HistoryRecipe CaptureDirectForRestore(Element element)=>null;}
internal static class Checks{
 private sealed class DurableItem{internal HistoryRestoreRequest Request;internal Element Element,Root;internal HistoryRecipe DirectSystem;internal ElementType SystemDefinition;}
 static bool IsCalo(Element element)=>false;static Element Root(Element element)=>element.Root??element;static IEnumerable<Element> Aggregate(Document doc,Element root)=>new[]{root}.Concat(root.Members);
'@
$checks=@'
 static int Count;static void Check(bool ok,string why){Count++;if(!ok)throw new Exception(why);}
 static void Reject(Action action,string why){try{action();}catch(InvalidOperationException error){if(!error.Message.Contains("ExternalModelExcluded"))throw;Count++;return;}throw new Exception(why);}
 static T Add<T>(Document doc,string uid,int id) where T:Element,new(){var element=new T{UniqueId=uid,Id=new ElementId{Value=id}};doc.Elements[uid]=element;return element;}
 static HistoryRestoreRequest Request(Element element)=>new HistoryRestoreRequest{SourceUniqueId=element.UniqueId};
 static int Main(string[] args){try{
  var source=new Document();var ordinary=Add<Element>(source,"native-wall",1);ordinary.OrdinaryIfcGuid="ordinary-native-IfcGUID";Check(ResolveDurableItem(source,Request(ordinary)).Element==ordinary,"Native object with an ordinary IFC identifier was excluded.");
  bool filterRequired=false;try{new FilteredElementCollector(source).ToElements();}catch(InvalidOperationException error){filterRequired=error.Message=="CollectorRequiresFilter";}Check(filterRequired,"Fixture must enforce Revit's required collector filter.");
  var link=Add<RevitLinkInstance>(source,"rvt-link",2);var linkType=Add<RevitLinkType>(source,"rvt-link-type",3);var import=Add<ImportInstance>(source,"ifc-import",4);var cadType=Add<CADLinkType>(source,"cad-link-type",5);
  foreach(var external in new Element[]{link,linkType,import,cadType}){Reject(()=>ResolveDurableItem(source,Request(external)),"Direct external model escaped durable conversion guard.");Reject(()=>RejectExternalModel(external,"definition-test"),"External definition escaped copy/closure guard.");}
  Check(ResolveDurableItem(source,Request(ordinary)).Element==ordinary,"Support RVT containing an unrelated link was forbidden as a whole.");
  var aggregate=Add<Element>(source,"group",6);aggregate.Members.Add(import);Reject(()=>ResolveDurableItem(source,Request(aggregate)),"Aggregate containing external content must fail instead of copying a silent subset.");
  var member=Add<Element>(source,"native-member",7);member.Root=aggregate;Reject(()=>ResolveDurableItem(source,Request(member)),"Aggregate reached through ordinary member escaped guard.");
  var clean=new Document();Add<Element>(clean,"native-result",8);RejectExternalModelsInCapsule(clean);Check(true,"Clean capsule rejected.");Add<CADLinkType>(clean,"unplanned-link-type",9);Reject(()=>RejectExternalModelsInCapsule(clean),"Implicit external dependency escaped final pre-publication check.");
  var linkedCapsule=new Document();Add<RevitLinkInstance>(linkedCapsule,"unplanned-link-instance",10);Reject(()=>RejectExternalModelsInCapsule(linkedCapsule),"Non-type external object escaped filtered capsule scan.");
  System.IO.Directory.CreateDirectory(args[0]);System.IO.File.WriteAllText(System.IO.Path.Combine(args[0],"results.json"),System.Text.Json.JsonSerializer.Serialize(new{Passed=true,Assertions=Count,RevitInvoked=false,RequiredCollectorFilterEnforced=true,Scope="Production durable resolver/capsule guards and central four-class predicate with API adapters enforcing Revit's required filter. Both instance/type scans exercised. Static pre-SaveAs and definition-closure/copy integration checks. No native link import/export executed."}));
  Console.WriteLine("PASS: "+Count+" durable external-model guard assertions; support RVT allowed, selected/aggregate/implicit external objects refused.");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}}
'@
$root=Join-Path $repo ('tmp/history-durable-external-tests/'+[guid]::NewGuid().ToString('N'));$null=[IO.Directory]::CreateDirectory($root)
[IO.File]::WriteAllText((Join-Path $root 'Fixture.cs'),$fixture+($methods -join "`n")+$checks)
$project="<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include=`"$([Security.SecurityElement]::Escape((Join-Path $folder 'ElementHistoryExternalModels.cs')))`"/><Compile Include=`"Fixture.cs`"/></ItemGroup></Project>"
$projectPath=Join-Path $root 'External.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Durable external exclusion fixture build failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/External.Tests.dll') (Join-Path $root 'io')
if($LASTEXITCODE -ne 0){throw "Durable external exclusion assertions failed: $root"}
Write-Output "Results: $root"
