#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Method([string]$file,[string]$signature){
 $text=[IO.File]::ReadAllText((Join-Path $repo $file));$start=$text.IndexOf($signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw $signature};$brace=$text.IndexOf('{',$start);$depth=0
 for($i=$brace;$i -lt $text.Length;$i++){if($text[$i] -eq '{'){$depth++}elseif($text[$i] -eq '}'){$depth--;if($depth -eq 0){return $text.Substring($start,$i-$start+1)}}}
 throw 'Unbalanced method'
}
$validate=(Method 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.Durable.cs' 'internal static void ValidateDurableTypeCompatibility(').Replace('internal static void','public static void')
$presentation=Method 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.Durable.cs' 'internal static bool IsPresentationDefinition('
$same=Method 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.Durable.cs' 'private static bool SameDurableDefinition('
$fixture=@"
using System;using System.Collections;using System.Collections.Generic;using System.Linq;
namespace DurableCollisionFixture{
enum CategoryType {Model,Annotation,Internal}
class Category {public CategoryType CategoryType;}
class Element {public Category Category;public string Name,Version,Source;public List<string> ExtraOrigins=new List<string>();}
class ElementType:Element{public string FamilyName="F";}
class DimensionType:ElementType{}
class SpotDimensionType:DimensionType{}
class Family:Element{public Category FamilyCategory;}
class Material:Element{}
class Level:Element{}
class MEPSystemType:ElementType{}
class View:Element{}
class LineAndTextAttrSymbol:ElementType{}
class TextNoteType:LineAndTextAttrSymbol{}
class ViewFamilyType:ElementType{}
class ModelTextType:ElementType{}
class Document{public List<Element> Elements=new List<Element>();}
class FilteredElementCollector:IEnumerable<Element>{
 IEnumerable<Element> values;public FilteredElementCollector(Document d){values=d.Elements;}
 public FilteredElementCollector WhereElementIsElementType(){values=values.Where(e=>e is ElementType);return this;}
 public FilteredElementCollector OfClass(Type t){values=values.Where(e=>e.GetType()==t);return this;}
 public IEnumerator<Element> GetEnumerator(){return values.GetEnumerator();}IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
}
static class ElementHistoryRestoration{public static List<string> GetOrigins(Element e){return e.ExtraOrigins.Concat(e.Source==null?new string[0]:new[]{UnderTest.Prefix+e.Source}).ToList();}}
static class UnderTest{
 public const string Prefix="BIMaestroHistoryDefinitionSource:";const string HistoricalDefinitionSourcePrefix=Prefix;
 static string ReadHistoricalTypeVersion(Element e)=>e.Version;
 static string ReadHistoricalDefinitionSource(Element e)=>e.Source;
 static string DurableTypeNameKey(ElementType t)=>t.GetType().FullName+"|"+t.FamilyName+"|"+t.Name;
$presentation
$validate
$same
}
"@
$tests=@'
public static class Tests{
 static int count;
 static void Assert(bool ok,string n){count++;if(!ok)throw new Exception(n);}
 static void Reject(Action a,string n){bool failed=false;try{a();}catch(InvalidOperationException){failed=true;}Assert(failed,n);}
 public static int Run(){
 var c=new Document();var d=new Document();var stock=new DimensionType{Name="Style de cotation lineaire",Version="v",Source=null};c.Elements.Add(stock);d.Elements.Add(new DimensionType{Name=stock.Name});
 UnderTest.ValidateDurableTypeCompatibility(c,d,"v");Assert(true,"Version-tagged unused stock ignored");
 stock.Source="stock-origin";stock.Version="different";UnderTest.ValidateDurableTypeCompatibility(c,d,"v");Assert(true,"Dimension styles excluded even when source-tagged");
 c.Elements.Add(new SpotDimensionType{Name="Spot style",Version="other",Source="spot-origin"});d.Elements.Add(new SpotDimensionType{Name="Spot style"});UnderTest.ValidateDurableTypeCompatibility(c,d,"v");Assert(true,"DimensionType subclasses excluded");
 var custom=new ElementType{Name="Versioned physical type",Version="v",Source="source-type"};c.Elements.Add(custom);var collision=new ElementType{Name=custom.Name};d.Elements.Add(collision);
 Reject(()=>UnderTest.ValidateDurableTypeCompatibility(c,d,"v"),"Unproven destination collision refused");
 collision.Version="v";collision.Source="source-type";UnderTest.ValidateDurableTypeCompatibility(c,d,"v");Assert(true,"Same proven version accepted");
 collision.Source="other-source";Reject(()=>UnderTest.ValidateDurableTypeCompatibility(c,d,"v"),"Wrong source refused");collision.Source="source-type";
 collision.Version="other";Reject(()=>UnderTest.ValidateDurableTypeCompatibility(c,d,"v"),"Wrong destination version refused");collision.Version="v";
 custom.Version=null;Reject(()=>UnderTest.ValidateDurableTypeCompatibility(c,d,"v"),"Proven source missing version refused");custom.Version="v";
 c.Elements.Add(new Family{Name="Unused family"});d.Elements.Add(new Family{Name="Unused family"});UnderTest.ValidateDurableTypeCompatibility(c,d,"v");Assert(true,"Unproven stock family ignored");
 var family=new Family{Name="Versioned family",Version="v",Source="source-family"};c.Elements.Add(family);d.Elements.Add(new Family{Name=family.Name});
 Reject(()=>UnderTest.ValidateDurableTypeCompatibility(c,d,"v"),"Proven family collision refused");d.Elements.RemoveAt(d.Elements.Count-1);
 var material=new Material{Name="Versioned material",Version="v",Source="source-material"};c.Elements.Add(material);d.Elements.Add(new Material{Name=material.Name});
 Reject(()=>UnderTest.ValidateDurableTypeCompatibility(c,d,"v"),"Proven material collision refused");
 Assert(UnderTest.IsPresentationDefinition(new TextNoteType()),"Category-null text style excluded");
 Assert(UnderTest.IsPresentationDefinition(new ViewFamilyType()),"Category-null view style excluded");
 Assert(UnderTest.IsPresentationDefinition(new Family{FamilyCategory=new Category{CategoryType=CategoryType.Annotation}}),"Annotation family excluded");
 Assert(!UnderTest.IsPresentationDefinition(new ModelTextType{Category=new Category{CategoryType=CategoryType.Model}}),"3D model text preserved");
 Assert(!UnderTest.IsPresentationDefinition(new Level{Category=new Category{CategoryType=CategoryType.Annotation}}),"Required Level preserved");
 Assert(!UnderTest.IsPresentationDefinition(new MEPSystemType{Category=new Category{CategoryType=CategoryType.Internal}}),"Required MEP system preserved");
 return count;
 }
}
}
'@
# Only DTOs/collector are doubles; the collision checks above are extracted from the product source.
Add-Type -TypeDefinition ($fixture+$tests) -WarningAction SilentlyContinue
[pscustomobject]@{Passed=$true;Assertions=[DurableCollisionFixture.Tests]::Run();Scope='Extracted production collision guards with DTO/collector doubles; no Revit import validation'}|ConvertTo-Json -Compress
