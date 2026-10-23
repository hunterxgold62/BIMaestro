#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo ('tmp/history-external-model-exclusion-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$production=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$tracker=[IO.File]::ReadAllText((Join-Path $production 'ElementHistoryTracker.cs'))
$opening=[IO.File]::ReadAllText((Join-Path $production 'ElementHistoryOpeningArchive.cs'))
$helper=[IO.File]::ReadAllText((Join-Path $production 'ElementHistoryExternalModels.cs'))
function Block([string]$Text,[string]$Signature) {
    $start=$Text.IndexOf($Signature,[StringComparison]::Ordinal)
    if($start -lt 0){throw ('Missing production block: '+$Signature)}
    $brace=$Text.IndexOf('{',$start);$depth=1
    for($index=$brace+1;$index -lt $Text.Length;$index++) {
        if($Text[$index] -eq '{'){$depth++}
        if($Text[$index] -eq '}'){$depth--}
        if(!$depth){return $Text.Substring($start,$index-$start+1)}
    }
    throw ('Unterminated production block: '+$Signature)
}
$ignore=Block $tracker 'private static bool ShouldIgnoreElement(Element element)'
$remember=Block $tracker 'private static void RememberExcludedElement(Element element)'
$dto=Block $tracker 'private sealed class ExcludedElementMetadata'
$start=$opening.IndexOf('        internal static bool IsEligibleElement(Element element)',[StringComparison]::Ordinal)
$end=$opening.IndexOf('        internal static HistoryRecipe TryCreateDeletedRecipe(',$start,[StringComparison]::Ordinal)
if($start -lt 0 -or $end -le $start){throw 'Opening eligibility expression not found.'}
$eligible=$opening.Substring($start,$end-$start)
$fixtures=@'
#pragma warning disable CS0649
using System;using System.Collections.Generic;using System.Linq;
namespace Autodesk.Revit.DB {
 internal enum CategoryType{Model,Annotation,Internal}
 internal enum BuiltInCategory{OST_IOSModelGroups=-2000095}
 internal sealed class ElementId{internal int Value=123;}
 internal sealed class Category{internal ElementId Id=new ElementId();internal CategoryType CategoryType=CategoryType.Model;internal string Name="Native model category";}
 internal sealed class Document{internal string Key=Guid.NewGuid().ToString("N");}
 internal class Element{internal ElementId Id=new ElementId();internal Category Category=new Category();internal bool ViewSpecific;internal string Name="model.ifc.rvt",UniqueId=Guid.NewGuid().ToString("N"),IfcGUID="same-native-ifc-guid";internal Document Document=new Document();}
 internal class ElementType:Element{}
 internal class RevitLinkInstance:Element{}
 internal class RevitLinkType:ElementType{}
 internal class ImportInstance:Element{}
 internal class CADLinkType:ElementType{}
 internal class Material:Element{}internal class GraphicsStyle:Element{}internal class View:Element{}
 internal class ProjectInfo:Element{}internal class Group:Element{}internal class ViewFamilyType:ElementType{}
 internal class DimensionType:ElementType{}internal class TextElementType:ElementType{}internal class Dimension:Element{}
 internal class ModelCurve:Element{}internal class FamilyInstance:Element{}internal class DirectShape:Element{}internal class Wall:Element{}
 internal class FamilySymbol:ElementType{internal string FamilyName="Native IFC-labelled family";}
}
namespace Autodesk.Revit.DB.Plumbing{internal class PipeSegment:Autodesk.Revit.DB.Element{}internal class PipeInsulation:Autodesk.Revit.DB.Element{}}
namespace Autodesk.Revit.DB.Mechanical{internal class DuctInsulation:Autodesk.Revit.DB.Element{}}
namespace Analyse {
 using Autodesk.Revit.DB;
 internal static class ElementIdExtensions{internal static int GetIdValue(this ElementId id)=>id.Value;internal static long GetIdLongValue(this ElementId id)=>id.Value;}
 internal static class TrackerFixture {
  private static readonly Dictionary<string,ExcludedElementMetadata> ExcludedElements=new Dictionary<string,ExcludedElementMetadata>();
  private static readonly object SnapshotByElementId=new object();
  private static bool IsBIMaestroPreviewElement(Element element)=>false;
  private static bool IsAxisLineElement(Element element)=>false;
  private static bool ShouldIgnoreCategory(Category category)=>false;
  private static bool IsUsefulHistoryText(string text)=>!string.IsNullOrWhiteSpace(text);
  private static string BuildElementSnapshotKey(Document doc,ElementId id)=>doc.Key+"|element|"+id.Value;
  internal static bool Ignore(Element element)=>ShouldIgnoreElement(element);
  internal static object Observe(Element element){if(!ShouldIgnoreElement(element))return null;RememberExcludedElement(element);return ExcludedElements.TryGetValue(BuildElementSnapshotKey(element.Document,element.Id),out var value)?value:null;}
'@
$checks=@'
 }
 internal static class OpeningFixture {
'@
$suffix=@'
 }
 public static class Tests {
  static int count;static void Check(bool passed,string name){count++;if(!passed)throw new Exception(name);}
  static object Field(object metadata,string name)=>metadata.GetType().GetField(name).GetValue(metadata);
  public static int Run(){
   foreach(var element in new Element[]{new RevitLinkInstance(),new RevitLinkType(),new ImportInstance(),new CADLinkType()}) {
    Check(ElementHistoryExternalModels.IsExcluded(element),"External native class not rejected: "+element.GetType());
    Check(ElementHistoryExternalModels.IsExcludedClass(element.GetType().FullName),"Exact native class identity not recognized.");
    Check(TrackerFixture.Ignore(element),"An external container entered object capture.");
    Check(!OpeningFixture.IsEligibleElement(element),"An external container acquired an opening restore receipt.");
    var metadata=TrackerFixture.Observe(element);Check(metadata!=null,"Excluded external deletion needs its minimal live audit proof.");
    Check((string)Field(metadata,"Reason")=="external_model","Deletion audit reason not explicit.");
    Check((bool)Field(metadata,"CapturedWhileAlive"),"Deletion exclusion was not proved while alive.");
    Check((int)Field(metadata,"SourceElementId")==element.Id.Value,"Wrong source ID in live proof.");
    Check((string)Field(metadata,"SourceUniqueId")==element.UniqueId,"Wrong source UID in live proof.");
    Check((string)Field(metadata,"Class")==element.GetType().FullName,"Wrong native class in live proof.");
   }
   foreach(var native in new Element[]{new Wall(),new FamilyInstance(),new DirectShape()}) {
    Check(!ElementHistoryExternalModels.IsExcluded(native),"Native IfcGUID/name must not exclude an ordinary object.");
    Check(!TrackerFixture.Ignore(native),"Native object wrongly ignored just for IFC-like filename/IfcGUID.");
    Check(OpeningFixture.IsEligibleElement(native),"Native object wrongly loses its opening checkpoint eligibility.");
    Check(TrackerFixture.Observe(native)==null,"Native object acquired a forged external exclusion proof.");
   }
   foreach(var name in new[]{null,"","Autodesk.Revit.DB.Wall","Autodesk.Revit.DB.FamilyInstance","Autodesk.Revit.DB.DirectShape","Other.RevitLinkInstance","Autodesk.Revit.DB.RevitLinkInstanceSuffix"})
    Check(!ElementHistoryExternalModels.IsExcludedClass(name),"Non-external metadata class wrongly excluded: "+name);
   Check(!ElementHistoryExternalModels.IsExcluded(null),"Missing API object is not live external-model evidence.");
   return count;
  }
 }
}
'@
# The helper is compiled unchanged against API-shaped doubles. Production
# ignore/eligibility/metadata methods are extracted, not reimplemented.
$compiled="using System.Collections.Generic;using System.Linq;`n"+$helper+"`n"+$fixtures.Replace('using System;using System.Collections.Generic;using System.Linq;','')+$dto+$ignore+$remember+$checks+$eligible+$suffix
[IO.File]::WriteAllText((Join-Path $folder 'ProductionAndFixture.cs'),$compiled)
Add-Type -TypeDefinition $compiled
$count=[Analyse.Tests]::Run()
@{AtUtc=[DateTime]::UtcNow.ToString('o');Passed=$true;Assertions=$count;RevitInvoked=$false;Scope='Exact production helper, Tracker ignore/live exclusion metadata, and opening eligibility with native class doubles. Covers all four link/import container classes and protects native IfcGUID-bearing Wall/Family/DirectShape; native source diagnostics are separate.';SourceHashes=@('ElementHistoryExternalModels.cs','ElementHistoryTracker.cs','ElementHistoryOpeningArchive.cs')|ForEach-Object{@{Name=$_;Sha256=(Get-FileHash -LiteralPath (Join-Path $production $_) -Algorithm SHA256).Hash}};FixtureSha256=(Get-FileHash -LiteralPath (Join-Path $folder 'ProductionAndFixture.cs') -Algorithm SHA256).Hash}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $folder 'results.json') -Encoding UTF8
Write-Output ('PASS: '+$count+' external model exclusion assertions.')
Write-Output $folder
