#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs'))
function Extract([string] $signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Production member missing: $signature" }
    $brace = $source.IndexOf('{', $start); $depth = 0
    for ($i = $brace; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ } elseif ($source[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $source.Substring($start, $i - $start + 1).Replace('private static', 'public static').Replace('private sealed', 'public sealed') } }
    }
    throw 'Unbalanced production member.'
}
$members = @(
    (Extract 'private static bool HasHistoricalDefinitionProof('),
    (Extract 'private static void StampNewHistoricalFamily('),
    (Extract 'private sealed class RejectHistoricalFamilyOverwrite')
) -join "`n"
$fixture = @'
using System; using System.Collections.Generic; using System.Linq;
namespace FamilyProofFixture {
public enum BuiltInParameter {FAMILY_SHARED} public enum FamilySource {Project,Family}
public interface IFamilyLoadOptions {bool OnFamilyFound(bool use,out bool overwrite);bool OnSharedFamilyFound(Family shared,bool use,out FamilySource source,out bool overwrite);}
public class Parameter {public int Value;public int AsInteger()=>Value;}
public class Category {public int Id;}
public class Element {public int Id;public string UniqueId,Name,SourceUid,Version;public List<string> Origins=new List<string>();}
public class ElementType:Element {}
public class Family:Element {public bool IsInPlace;public int FamilyPlacementType;public Category FamilyCategory=new Category{Id=-2000151};public Parameter Shared=new Parameter();public List<int> SymbolIds=new List<int>();public IEnumerable<int> GetFamilySymbolIds()=>SymbolIds;public Parameter get_Parameter(BuiltInParameter id)=>Shared;}
public class FamilySymbol:ElementType {public Family Family;}
public class Document {public Dictionary<int,Element> Elements=new Dictionary<int,Element>();public Element GetElement(int id)=>Elements[id];}
public static class ElementHistoryNativeArchive {public static string ReadHistoricalTypeVersion(Element e)=>e.Version;public static string ReadHistoricalDefinitionSource(Element e)=>e.SourceUid;}
public static class Production {
 public static List<string> GetOrigins(Element e)=>e.Origins;
 public static void SetOrigins(Element e,IEnumerable<string> origins){e.Origins=origins.ToList();e.SourceUid=e.Origins.Where(x=>x.StartsWith("source:")).Select(x=>x.Substring(7)).SingleOrDefault();e.Version=e.Origins.Where(x=>x.StartsWith("version:")).Select(x=>x.Substring(8)).SingleOrDefault();}
__MEMBERS__
}
public static class Tests {
 static int count;static void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
 static void Reject(Action action,string why){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,why);}
 static void Proof(Element e,string source,string version="v1"){e.SourceUid=source;e.Version=version;e.Origins=new List<string>{source,"source:"+source,"version:"+version};}
 public static int Run(){
  var old=new Family{Id=1,UniqueId="user-family",Name="Family current"};var expected=new Family{Id=2,UniqueId="capsule-family",Name="Family__BH_version"};Proof(expected,"original-family");
  var historicalSymbol=new FamilySymbol{Id=3,UniqueId="capsule-symbol",Name="Type__BH_version",Family=expected};Proof(historicalSymbol,"original-symbol");
  var pasted=new FamilySymbol{Id=4,UniqueId="pasted-symbol",Name=historicalSymbol.Name,Family=old};Proof(pasted,"original-symbol");
  Check(!Production.HasHistoricalDefinitionProof(historicalSymbol,pasted,"original-symbol","v1"),"V3 regression: proven symbol under current unproven parent rejected");
  var currentNamesake=new Family{Id=9,UniqueId="namesake",Name=expected.Name};Check(!Production.HasHistoricalDefinitionProof(expected,currentNamesake,"original-family","v1"),"Name-only parent substitution rejected");
  var loaded=new Family{Id=5,UniqueId="new-family",Name=expected.Name};var loadedSymbol=new FamilySymbol{Id=6,UniqueId="new-symbol",Name=historicalSymbol.Name,Family=loaded};loaded.SymbolIds.Add(6);var doc=new Document();doc.Elements.Add(6,loadedSymbol);
  var existingFamilies=new HashSet<string>{old.UniqueId};var existingDefinitions=new HashSet<string>{old.UniqueId};
  Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions);
  Check(Production.HasHistoricalDefinitionProof(expected,loaded,"original-family","v1"),"New directly loaded family gets exact source proof");
  Check(Production.HasHistoricalDefinitionProof(historicalSymbol,loadedSymbol,"original-symbol","v1"),"New symbol proves both type and parent");
  Check(old.Origins.Count==0&&old.Version==null&&old.Name=="Family current","User family untouched");
  Reject(()=>Production.StampNewHistoricalFamily(expected,old,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Existing user parent never restamped");
  Check(old.Origins.Count==0&&old.Version==null,"Rejected existing family still untouched");
  Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v2",existingFamilies,existingDefinitions),"Wrong historical source version rejected");
  loaded.Name="Other";Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Loaded family rename mismatch rejected");loaded.Name=expected.Name;
  loaded.FamilyCategory.Id=-2008049;Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Loaded family category mismatch rejected");loaded.FamilyCategory.Id=-2000151;
  loaded.FamilyPlacementType=1;Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Loaded placement mismatch rejected");loaded.FamilyPlacementType=0;
  loaded.Shared.Value=1;Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Loaded shared-family flag mismatch rejected");loaded.Shared.Value=0;
  existingDefinitions.Add(loadedSymbol.UniqueId);Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Preexisting symbol cannot acquire provenance");existingDefinitions.Remove(loadedSymbol.UniqueId);
  var duplicate=new FamilySymbol{Id=7,UniqueId="duplicate",Name=loadedSymbol.Name,Family=loaded};doc.Elements.Add(7,duplicate);loaded.SymbolIds.Add(7);Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Ambiguous imported symbols rejected");loaded.SymbolIds.Remove(7);
  loadedSymbol.Family=old;Reject(()=>Production.StampNewHistoricalFamily(expected,loaded,new List<FamilySymbol>{historicalSymbol},doc,"v1",existingFamilies,existingDefinitions),"Symbol redirected to existing parent rejected");loadedSymbol.Family=loaded;
  loaded.SourceUid="other";Check(!Production.HasHistoricalDefinitionProof(historicalSymbol,loadedSymbol,"original-symbol","v1"),"Changed family provenance invalidates symbol proof");
  var options=new Production.RejectHistoricalFamilyOverwrite();bool overwrite;FamilySource familySource;
  Check(!options.OnFamilyFound(true,out overwrite)&&!overwrite,"Ordinary family overwrite refused");
  Check(!options.OnSharedFamilyFound(old,true,out familySource,out overwrite)&&!overwrite&&familySource==FamilySource.Project,"Shared family replacement refused");
  Check(expected.SourceUid=="original-family"&&historicalSymbol.SourceUid=="original-symbol","Frozen source remains unchanged");return count;
 }
}
}
'@
Add-Type -TypeDefinition ($fixture.Replace('__MEMBERS__', $members))
[pscustomobject]@{Passed=$true;Assertions=[FamilyProofFixture.Tests]::Run();Scope='Extracted production provenance/stamping and overwrite guards with API doubles; no claim that EditFamily/SaveAs/LoadFamily succeeds in Revit.'}|ConvertTo-Json -Compress
