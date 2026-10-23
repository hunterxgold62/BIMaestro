#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo ('tmp/history-wall-top-constraint-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$sourcePath=Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'
$restorePath=Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs'
$source=[IO.File]::ReadAllText($sourcePath)
$restore=[IO.File]::ReadAllText($restorePath)
function Read-Block([string]$Text,[string]$Signature) {
    $start=$Text.IndexOf($Signature,[StringComparison]::Ordinal)
    if($start -lt 0){throw ('Missing production block: '+$Signature)}
    $opening=$Text.IndexOf('{',$start);$depth=1
    for($index=$opening+1;$index -lt $Text.Length;$index++) {
        if($Text[$index] -eq '{'){$depth++}
        if($Text[$index] -eq '}'){$depth--}
        if(!$depth){return $Text.Substring($start,$index-$start+1)}
    }
    throw ('Unterminated production block: '+$Signature)
}
$apply=Read-Block $source 'internal static void ApplyParameters(Document doc, Element element, List<HistoryParameter> values)'
$remap=Read-Block $restore 'private static void RemapDurableDefinitionReferences(HistoryRecipe recipe, Dictionary<string, string> remap)'
$parameterDto=Read-Block $source 'internal sealed class HistoryParameter'
$start=$source.IndexOf('recipe.Parameters = CaptureParameters(wall)',[StringComparison]::Ordinal)
$end=if($start -ge 0){$source.IndexOf('.ToList();',$start,[StringComparison]::Ordinal)}else{-1}
if($start -lt 0 -or $end -le $start){throw 'Actual wall parameter capture filter is absent.'}
$wallFilter=$source.Substring($start,$end-$start+'.ToList();'.Length)
$prefix=@'
#pragma warning disable CS0649
using System;using System.Collections.Generic;using System.Linq;
namespace Autodesk.Revit.Exceptions { public class RegenerationFailedException:Exception {} }
namespace WallTopConstraintFixture {
enum StorageType { None,Integer,Double,String,ElementId }
enum BuiltInParameter {
 RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM=-1140323, WALL_HEIGHT_TYPE=-1001103,
 WALL_TOP_OFFSET=-1001109, ALL_MODEL_INSTANCE_COMMENTS=-1010106, Unsupported=-1999999
}
sealed class ElementId {
 internal long Value;internal ElementId(long value){Value=value;}internal long GetIdLongValue()=>Value;
 public static bool operator ==(ElementId a,ElementId b)=>ReferenceEquals(a,b)||!ReferenceEquals(a,null)&&!ReferenceEquals(b,null)&&a.Value==b.Value;
 public static bool operator !=(ElementId a,ElementId b)=>!(a==b);
 public override bool Equals(object other)=>other is ElementId id&&Value==id.Value;
 public override int GetHashCode()=>Value.GetHashCode();
}
static class ElementIdExtensions {internal static ElementId CreateElementId(int value)=>new ElementId(value);}
sealed class Definition {internal string Name;}
class Element {
 internal string Name="wall",UniqueId;internal ElementId Id=new ElementId(556835);
 internal readonly Dictionary<BuiltInParameter,Parameter> Values=new Dictionary<BuiltInParameter,Parameter>();
 internal Parameter get_Parameter(BuiltInParameter id)=>Values.TryGetValue(id,out var parameter)?parameter:null;
 internal Parameter get_Parameter(Guid id)=>null;internal Parameter get_Parameter(Definition definition)=>null;
 internal IEnumerable<Parameter> GetParameters(string name)=>Values.Values.Where(p=>p.Definition.Name==name);
}
sealed class ParameterElement:Element {internal Definition GetDefinition()=>new Definition{Name="project"};}
sealed class Wall:Element {internal List<HistoryParameter> SourceCapture;}
sealed class Level:Element {}
sealed class Document {
 internal Wall Wall;internal readonly Dictionary<string,Element> Elements=new Dictionary<string,Element>();
 internal readonly List<string> Trace=new List<string>();internal Exception RegenerationFailure;
 internal int Regenerations;
 internal Element GetElement(string uid)=>Elements.TryGetValue(uid,out var element)?element:null;
 internal void Regenerate(){
  Trace.Add("regenerate");Regenerations++;if(RegenerationFailure!=null)throw RegenerationFailure;
  Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).IsReadOnly=
   Wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId().Value<0;
 }
}
sealed class Parameter {
 internal Document Document;internal string TraceName;internal ElementId Id;internal Definition Definition;
 internal StorageType StorageType;internal bool IsReadOnly,Accept=true;internal double Number;internal string Text;
 internal ElementId Reference=new ElementId(-1);internal int SetCalls;
 internal double AsDouble()=>Number;internal int AsInteger()=>(int)Number;internal string AsString()=>Text;
 internal ElementId AsElementId()=>Reference;
 void Attempt(){if(IsReadOnly)throw new InvalidOperationException("Fixture refuses a readonly setter.");SetCalls++;Document.Trace.Add("set-"+TraceName);}
 internal bool Set(ElementId value){Attempt();if(Accept)Reference=value;return Accept;}
 internal bool Set(double value){Attempt();if(Accept)Number=value;return Accept;}
 internal bool Set(int value)=>Set((double)value);
 internal bool Set(string value){Attempt();if(Accept)Text=value;return Accept;}
}
sealed class HistoryPlacement {internal string LevelSourceUniqueId,HostSourceUniqueId;internal List<HistoryParameter> ElevationParameters;}
sealed class HistoryNetwork {internal string SystemType;}
sealed class HistoryPort {internal string SystemType;}
sealed class HistoryConnection {internal HistoryPort Port,PeerPort;}
sealed class HistoryCircuit {internal HistoryRecipe Recipe;}
sealed class HistoryRecipe {
 internal string Type,Level;internal List<HistoryParameter> Parameters;internal HistoryPlacement HistoricalPlacement;
 internal HistoryNetwork Network;internal List<HistoryPort> Ports;internal List<HistoryConnection> Connections;
 internal List<HistoryCircuit> ElectricalCircuits;
}
static class ProductionMethods {
 internal static List<HistoryParameter> CaptureParameters(Wall wall)=>wall.SourceCapture;
 internal static List<HistoryParameter> CaptureWall(Wall wall) {var recipe=new HistoryRecipe();
'@
$middle=@'
 return recipe.Parameters;
 }
 internal static void Remap(HistoryRecipe recipe,Dictionary<string,string> map)=>RemapDurableDefinitionReferences(recipe,map);
'@
$checks=@'
public static class Tests {
 static int count;public static string[] PrimaryTrace;
 static void Check(bool condition,string message){count++;if(!condition)throw new Exception(message);}
 static void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
 static Document NewWall() {
  var doc=new Document();doc.Wall=new Wall();
  foreach(var item in new[]{
   new Parameter{TraceName="top-level",Id=new ElementId(-1001103),Definition=new Definition{Name="Contrainte supérieure"},StorageType=StorageType.ElementId},
   new Parameter{TraceName="top-offset",Id=new ElementId(-1001109),Definition=new Definition{Name="Décalage supérieur"},StorageType=StorageType.Double,IsReadOnly=true},
   new Parameter{TraceName="comment",Id=new ElementId(-1010106),Definition=new Definition{Name="Commentaires"},StorageType=StorageType.String}}) {
   item.Document=doc;doc.Wall.Values[(BuiltInParameter)item.Id.Value]=item;
  }
  doc.Elements.Add("level-destination",new Level{UniqueId="level-destination",Id=new ElementId(99007)});
  return doc;
 }
 static HistoryRecipe SavedRecipe(Document doc) {
  // Deliberately retain the actual source order seen on the failing wall:
  // top offset precedes the top level. Raw source ID2607 must not be replayed.
  var offset=new HistoryParameter{BuiltIn=-1001109,Storage=(int)StorageType.Double,Number=3.280839895013123};
  var level=new HistoryParameter{BuiltIn=-1001103,Storage=(int)StorageType.ElementId,Number=2607,Reference="level-source"};
  var comment=new HistoryParameter{BuiltIn=-1010106,Storage=(int)StorageType.String,Text="modified before deletion"};
  doc.Wall.SourceCapture=new List<HistoryParameter>{offset,comment,level,
   new HistoryParameter{BuiltIn=-1999999,Storage=(int)StorageType.String,Text="not in the wall contract"}};
  return new HistoryRecipe{Type="type-source",Level="base-level",Parameters=ProductionMethods.CaptureWall(doc.Wall)};
 }
 public static int Run() {
  var doc=NewWall();var recipe=SavedRecipe(doc);var offset=doc.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
  Check(offset.IsReadOnly,"The test must start with offset readonly until constrained wall regenerates.");
  Check(recipe.Parameters.Count==3,"Production wall filter must retain top level, top offset and comment only in this fixture.");
  Check(recipe.Parameters[0].BuiltIn==-1001109&&recipe.Parameters[2].BuiltIn==-1001103,"Source order must challenge offset-before-level restoration.");
  Check(recipe.Parameters.Any(p=>p.BuiltIn==-1001103&&p.Reference=="level-source"),"Upper constraint UID missing from capture filter.");
  Check(recipe.Parameters.Any(p=>p.BuiltIn==-1001109&&p.Number==3.280839895013123),"One metre upper offset missing from capture filter.");
  ProductionMethods.Remap(recipe,new Dictionary<string,string>{{"level-source","level-destination"},{"type-source","type-destination"}});
  Check(recipe.Parameters.Single(p=>p.BuiltIn==-1001103).Reference=="level-destination","Production definition remap must include upper level parameter.");
  Check(recipe.Parameters.Single(p=>p.BuiltIn==-1001109).Number==3.280839895013123,"Definition remap must preserve exact saved upper offset.");
  Check(recipe.Type=="type-destination"&&recipe.Level=="base-level","Remap must preserve unrelated base context while mapping type.");
  ProductionMethods.ApplyParameters(doc,doc.Wall,recipe.Parameters);
  Check(doc.Wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId().Value==99007,"Restoration must resolve the remapped UID to destination ID, not source numeric ID2607.");
  Check(!offset.IsReadOnly,"A regeneration after top-level assignment must unlock the upper offset.");
  Check(offset.AsDouble()==3.280839895013123,"Reread upper offset differs from exact historical one metre.");
  Check(doc.Wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString()=="modified before deletion","Comment metadata must survive parameter ordering.");
  Check(doc.Trace.SequenceEqual(new[]{"set-top-level","regenerate","set-comment","set-top-offset"}),"Upper level must precede regeneration and upper offset must run last.");
  PrimaryTrace=doc.Trace.ToArray();
  int writes=doc.Wall.Values.Values.Sum(p=>p.SetCalls);ProductionMethods.ApplyParameters(doc,doc.Wall,recipe.Parameters);
  Check(doc.Wall.Values.Values.Sum(p=>p.SetCalls)==writes,"Equal historical values must not cause duplicate writes.");
  Check(offset.AsDouble()==recipe.Parameters[0].Number,"Repeated application must retain exact saved offset.");
  var missing=NewWall();var missingRecipe=SavedRecipe(missing);
  Reject(()=>ProductionMethods.ApplyParameters(missing,missing.Wall,missingRecipe.Parameters),"An unresolved upper-level UID must fail instead of producing an unconstrained wall.");
  Check(missing.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble()==0&&missing.Regenerations==0,"Missing reference must not apply offset or reach successful regeneration.");
  var refused=NewWall();var refusedRecipe=SavedRecipe(refused);ProductionMethods.Remap(refusedRecipe,new Dictionary<string,string>{{"level-source","level-destination"}});
  refused.Wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Accept=false;
  Reject(()=>ProductionMethods.ApplyParameters(refused,refused.Wall,refusedRecipe.Parameters),"Rejected top-level setter must fail strictly.");
  Check(refused.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble()==0,"Refused level must not cause an offset-only success.");
  var refusedOffset=NewWall();var offsetRecipe=SavedRecipe(refusedOffset);ProductionMethods.Remap(offsetRecipe,new Dictionary<string,string>{{"level-source","level-destination"}});
  refusedOffset.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Accept=false;
  Reject(()=>ProductionMethods.ApplyParameters(refusedOffset,refusedOffset.Wall,offsetRecipe.Parameters),"Rejected offset setter must not silently count as restored.");
  Check(refusedOffset.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble()==0,"A refused offset cannot masquerade as the saved value.");
  var fatal=NewWall();var fatalRecipe=SavedRecipe(fatal);ProductionMethods.Remap(fatalRecipe,new Dictionary<string,string>{{"level-source","level-destination"}});
  var original=new Autodesk.Revit.Exceptions.RegenerationFailedException();fatal.RegenerationFailure=original;bool propagated=false;
  try{ProductionMethods.ApplyParameters(fatal,fatal.Wall,fatalRecipe.Parameters);}catch(Autodesk.Revit.Exceptions.RegenerationFailedException ex){propagated=ReferenceEquals(ex,original);}
  Check(propagated,"Fatal regeneration failure must propagate unchanged before offset is applied.");
  Check(fatal.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble()==0,"Fatal regeneration must leave offset unapplied.");
  return count;
 }
}}
'@
$compiled=$prefix+$wallFilter+$middle+$apply+$remap+"`n}"+$parameterDto+$checks
[IO.File]::WriteAllText((Join-Path $folder 'ProductionAndFixture.cs'),$compiled)
Add-Type -TypeDefinition $compiled
$count=[WallTopConstraintFixture.Tests]::Run()
$result=@{Passed=$true;Assertions=$count;PrimaryTrace=[WallTopConstraintFixture.Tests]::PrimaryTrace;RevitInvoked=$false;ReconstructionSourceSha256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash;RestorationSourceSha256=(Get-FileHash -LiteralPath $restorePath -Algorithm SHA256).Hash;FixtureSha256=(Get-FileHash -LiteralPath (Join-Path $folder 'ProductionAndFixture.cs') -Algorithm SHA256).Hash;Scope='Exact production wall capture filter, definition remap, HistoryParameter DTO and ApplyParameters with readonly-until-regeneration API doubles. No native Revit creation, persistence or fidelity claim.'}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $folder 'results.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
Write-Output $folder
