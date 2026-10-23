#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scope = [IO.File]::ReadAllText((Join-Path $repo 'scripts/history-stress-tests/StressRequestScope.cs'))
$observer = [IO.File]::ReadAllText((Join-Path $repo 'scripts/history-stress-tests/StressObserver.cs'))
function Member([string]$source,[string]$signature,[switch]$Expression) {
    $start=$source.IndexOf($signature,[StringComparison]::Ordinal)
    if($start -lt 0){throw "Production member missing: $signature"}
    if($Expression){$end=$source.IndexOf(';',$start);return $source.Substring($start,$end-$start+1)}
    $brace=$source.IndexOf('{',$start);$depth=0
    for($i=$brace;$i-lt$source.Length;$i++) {
        if($source[$i]-eq'{'){$depth++}elseif($source[$i]-eq'}'){$depth--;if($depth-eq0){return $source.Substring($start,$i-$start+1)}}
    }
    throw 'Unbalanced production member.'
}
$members = @(
    (Member $scope 'static bool IsRapidGeneratedNativeClass(' -Expression),
    (Member $scope 'static bool IsRapidProtectedBaseline('),
    (Member $scope 'static string RapidExcludedRequestKind(')
) -join "`n"
$catalog = Member $observer 'public sealed class CatalogEntry'
if(!$observer.Contains('(long)BuiltInCategory.OST_PipeCurvesCenterLine')){throw 'Actual generated category whitelist omits the native pipe centerline.'}
$fixture=@'
using System;using System.Collections.Generic;using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {public class Element {}public class FamilyInstance:Element {}public class Wall:Element {}public class ModelCurve:Element {}}
public static class RapidScopeTests {
static readonly HashSet<long> ProtectedGeneratedCategories=new HashSet<long>{-2008045};
__CATALOG__
__MEMBERS__
static int count;static void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
const string Live="Hors périmètre de restauration : objet exclu identifié avant sa suppression (annotation, axe, isolant ou lien/import externe).";
const string Unsupported="État non récupérable : Catégorie non reconstructible directement avec les types encore présents dans la maquette.";
static CatalogEntry Baseline(long id,string cls,long? category,string protection)=>new CatalogEntry{Id=id,Uid="uid-"+id,Class=cls,CategoryId=category,ProtectionReason=protection};
static string Kind(CatalogEntry baseline,string failure=Unsupported,bool live=false,string requestUid=null,string liveUid=null,string liveClass=null,long? liveCategory=null,bool physical=false,long? eventId=null,long? auditId=null,bool recipeNull=true)=>RapidExcludedRequestKind(baseline,physical,eventId??baseline.Id,auditId??baseline.Id,requestUid,recipeNull,failure,live,liveUid,liveClass,liveCategory);
public static int Run(){
 var dimension=Baseline(558614,"LinearDimension",-2000261,"not-model-category");
 var spot=Baseline(791451,"SpotDimension",-2000263,"view-or-view-specific");
 var axis=Baseline(913246,"Element",-2008045,"generated-model-category");
 var tag=Baseline(1091326,"IndependentTag",-2008047,"view-or-view-specific");
 var storage=Baseline(1110420,"DataStorage",null,"definition-or-system");
 foreach(var entry in new[]{dimension,spot,axis})Check(Kind(entry,Live,true,entry.Uid,entry.Uid,"Autodesk.Revit.DB."+entry.Class,entry.CategoryId)=="LiveExcludedMetadata","Exact live exclusion needs production acknowledgement plus independent baseline: "+entry.Class);
 foreach(var entry in new[]{Baseline(558615,"LinearDimension",-2000261,"not-model-category"),Baseline(558616,"LinearDimension",-2000261,"not-model-category"),tag,storage})Check(Kind(entry)=="ProtectedBaselineExplicitUnsupported","Unprepared protected dependent remains explicitly unsupported, not live-excluded: "+entry.Id);
 Check(Kind(axis,physical:true)==null,"An independently physical victim can never be filtered by ACK.");
 Check(Kind(axis,eventId:913247)==null,"Wrong event ID is refused.");Check(Kind(axis,auditId:913247)==null,"Wrong request audit ID is refused.");
 Check(Kind(axis,recipeNull:false)==null,"A real recipe cannot be silently discarded as ACK.");Check(Kind(axis,failure:null)==null,"Missing recipe without explicit unsupported reason is refused.");Check(Kind(axis,failure:"Unknown capture failure")==null,"Unknown failure never implies exclusion.");
 Check(Kind(axis,failure:Live,live:false,requestUid:axis.Uid)==null,"Claimed live exclusion without validated metadata is refused.");
 Check(Kind(axis,Live,true,axis.Uid,"wrong-uid","Autodesk.Revit.DB.Element",axis.CategoryId)==null,"Wrong live UID is refused.");
 Check(Kind(axis,Live,true,"wrong-request-uid",axis.Uid,"Autodesk.Revit.DB.Element",axis.CategoryId)==null,"Wrong request UID is refused.");
 Check(Kind(axis,Live,true,axis.Uid,axis.Uid,"Autodesk.Revit.DB.FamilyInstance",axis.CategoryId)==null,"Wrong live runtime class is refused.");
 Check(Kind(axis,Live,true,axis.Uid,axis.Uid,"Autodesk.Revit.DB.Element",-2008044)==null,"Wrong live category is refused.");
 Check(Kind(axis,Unsupported,true)==null,"Live/unsupported proof kinds cannot be conflated.");Check(Kind(axis,Unsupported,false,axis.Uid)==null,"Unsupported baseline record with a source UID cannot hide an unexplained identity.");
 foreach(var cls in new[]{"FamilyInstance","Wall","ModelCurve"}){var real=Baseline(7,cls,-2008045,"generated-model-category");real.Name="Axe";Check(!IsRapidProtectedBaseline(real)&&Kind(real)==null,"Real model class named Axe cannot become ACK: "+cls);}
 var mismatch=Baseline(8,"LinearDimension",-2000011,"not-model-category");Check(!IsRapidProtectedBaseline(mismatch),"Class alone never proves a protected dimension.");
 mismatch=Baseline(9,"Element",-2008045,null);Check(!IsRapidProtectedBaseline(mismatch),"Baseline must explicitly prove protection.");
 mismatch=Baseline(10,"Element",-2008044,"generated-model-category");Check(!IsRapidProtectedBaseline(mismatch),"Arbitrary base Element model category stays in scope.");
 mismatch=Baseline(11,"DataStorage",null,"definition-or-system");mismatch.ElementType=true;Check(!IsRapidProtectedBaseline(mismatch),"Type definitions cannot masquerade as nonphysical deleted instances.");
 Check(!IsRapidProtectedBaseline(null),"Missing independent baseline is refused.");
 Check(IsRapidGeneratedNativeClass(typeof(Element),-2008045),"Native pipe centerline exact Element runtime class is recognized.");
 foreach(var type in new[]{typeof(FamilyInstance),typeof(Wall),typeof(ModelCurve)})Check(!IsRapidGeneratedNativeClass(type,-2008045),"Generated runtime exception never hides derived model classes: "+type.Name);
 Check(!IsRapidGeneratedNativeClass(typeof(Element),-2008044)&&!IsRapidGeneratedNativeClass(typeof(Element),null),"Native base Element still needs an exact generated category.");
 return count;
}
}
'@
Add-Type -TypeDefinition ($fixture.Replace('__CATALOG__',$catalog).Replace('__MEMBERS__',$members))
[pscustomobject]@{Passed=$true;Assertions=[RapidScopeTests]::Run();Scope='Extracted production protected-baseline/live-ACK/generated-class predicates with API doubles. All seven actual v10 baseline dependent shapes are covered; full Revit selection and persistence remain native-cycle checks.'}|ConvertTo-Json -Compress
