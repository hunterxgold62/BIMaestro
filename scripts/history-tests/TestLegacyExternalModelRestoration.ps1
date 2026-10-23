#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$native = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs'))
$restoration = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs'))
function Method([string] $source, [string] $signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Production method missing: $signature" }
    $brace = $source.IndexOf('{', $start); $depth = 0
    for ($i = $brace; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ } elseif ($source[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $source.Substring($start, $i - $start + 1) } }
    }
    throw 'Unbalanced production method.'
}
$guard = Method $native 'internal static void RequireRestorableNativeRoot('
$validate = Method $native 'internal static void ValidateNoExternalModelsAdded('
$start = $native.IndexOf('internal static HashSet<ElementId> SnapshotExternalModelIds(', [StringComparison]::Ordinal)
$end = $native.IndexOf('internal static void ValidateNoExternalModelsAdded(', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'Production external ID collector missing.' }
$snapshot = $native.Substring($start, $end - $start)
$restore = Method $native 'internal static Element Restore('
$firstCopy = [Math]::Min($restore.IndexOf('CopyRoot(', [StringComparison]::Ordinal), $restore.IndexOf('ElementTransformUtils.CopyElements(', [StringComparison]::Ordinal))
if ($restore.IndexOf('RequireRestorableNativeRoot(source, root)', [StringComparison]::Ordinal) -lt 0 -or
    $restore.IndexOf('RequireRestorableNativeRoot(source, root)', [StringComparison]::Ordinal) -ge $firstCopy -or
    $restore.IndexOf('ValidateNoExternalModelsAdded(destination, externalBefore)', [StringComparison]::Ordinal) -le $firstCopy) { throw 'Legacy/ready native restore no longer enforces pre/post copy policy.' }
$import = Method $restoration 'private static void ImportDurableDefinition('
if ($import.IndexOf('definitions.Any(ElementHistoryExternalModels.IsExcluded)', [StringComparison]::Ordinal) -lt 0 -or
    $import.IndexOf('ValidateNoExternalModelsAdded(doc, externalBefore)', [StringComparison]::Ordinal) -lt 0) { throw 'Definition-only native import policy missing.' }
$family = Method $restoration 'private static void ImportNewHistoricalFamily('
if ($family.IndexOf('SnapshotExternalModelIds(familyDocument)', [StringComparison]::Ordinal) -lt 0 -or
    $family.IndexOf('SnapshotExternalModelIds(familyDocument)', [StringComparison]::Ordinal) -ge $family.IndexOf('familyDocument.SaveAs(', [StringComparison]::Ordinal)) { throw 'Historical RFA does not reject embedded external objects before export.' }
$fixture = @'
using System;using System.Collections.Generic;using System.Linq;using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public sealed class ElementId {public int Value;public ElementId(int value){Value=value;}public override bool Equals(object value)=>value is ElementId id&&id.Value==Value;public override int GetHashCode()=>Value;}
 public class Element {public ElementId Id,TypeId=new ElementId(-1);public string Name;public List<Element> Children=new List<Element>();public ElementId GetTypeId()=>TypeId;}
 public class Wall:Element {}public class Group:Element {}public class DirectShape:Element {public string ApplicationId,IfcGUID;}
 public class RevitLinkInstance:Element {}public class RevitLinkType:Element {}public class ImportInstance:Element {}public class CADLinkType:Element {}
 public class Document {public string PathName;public List<Element> Elements=new List<Element>();public Element GetElement(ElementId id)=>Elements.FirstOrDefault(element=>element.Id.Equals(id));}
 public sealed class FilteredElementCollector {Document doc;Type type;public FilteredElementCollector(Document value){doc=value;}public FilteredElementCollector OfClass(Type value){type=value;return this;}public ICollection<ElementId> ToElementIds()=>doc.Elements.Where(element=>type.IsAssignableFrom(element.GetType())).Select(element=>element.Id).ToList();}
}
namespace Analyse {
__CENTRAL__
internal static class ElementHistoryNativeArchive {
 internal static IEnumerable<Element> Aggregate(Document source,Element root){yield return root;foreach(var child in root.Children)yield return child;}
__GUARDS__
}
public static class Checks {
 static int count;static void Check(bool ok,string reason){count++;if(!ok)throw new Exception(reason);}
 static void Reject(Action action,string reason){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,reason);}
 public static int Run(){
  var source=new Document{PathName="snapshot-opening-original.rvt"};var native=new Wall{Id=new ElementId(1),Name="Native wall .ifc.rvt"};source.Elements.Add(native);
  ElementHistoryNativeArchive.RequireRestorableNativeRoot(source,native);Check(true,"RVT safety source file remains allowed for ordinary native objects");
  foreach(var external in new Element[]{new ImportInstance(),new RevitLinkInstance(),new CADLinkType(),new RevitLinkType()}){
   external.Id=new ElementId(source.Elements.Count+10);external.Name="External object";source.Elements.Add(external);
   Reject(()=>ElementHistoryNativeArchive.RequireRestorableNativeRoot(source,external),"Legacy/ready source native class excluded before CopyElements: "+external.GetType().Name);
  }
  var type=source.Elements.OfType<CADLinkType>().Single();native.TypeId=type.Id;Reject(()=>ElementHistoryNativeArchive.RequireRestorableNativeRoot(source,native),"Excluded historical native type also rejected");native.TypeId=new ElementId(-1);
  var group=new Group{Id=new ElementId(50)};group.Children.Add(source.Elements.OfType<ImportInstance>().Single());source.Elements.Add(group);Reject(()=>ElementHistoryNativeArchive.RequireRestorableNativeRoot(source,group),"Aggregate cannot smuggle an excluded member through native group copy");
  var shape=new DirectShape{Id=new ElementId(51),Name="ifcGUID-bearing native object",IfcGUID="ordinary native identifier",ApplicationId="native-authoring"};source.Elements.Add(shape);ElementHistoryNativeArchive.RequireRestorableNativeRoot(source,shape);Check(true,"Native IFC identifiers and free filenames never establish import provenance");
  var destination=new Document{PathName="user.rvt"};destination.Elements.Add(new RevitLinkInstance{Id=new ElementId(100),Name="User existing link"});var before=ElementHistoryNativeArchive.SnapshotExternalModelIds(destination);ElementHistoryNativeArchive.ValidateNoExternalModelsAdded(destination,before);Check(true,"Existing current-model external objects remain untouched");
  foreach(var added in new Element[]{new ImportInstance(),new RevitLinkInstance(),new CADLinkType(),new RevitLinkType()}){
   added.Id=new ElementId(200);destination.Elements.Add(added);Reject(()=>ElementHistoryNativeArchive.ValidateNoExternalModelsAdded(destination,before),"Implicit new native copy dependency rejected: "+added.GetType().Name);destination.Elements.Remove(added);
  }
  destination.Elements.Add(new Wall{Id=new ElementId(201),Name="Wall.ifc"});ElementHistoryNativeArchive.ValidateNoExternalModelsAdded(destination,before);Check(destination.Elements.OfType<RevitLinkInstance>().Single().Id.Value==100,"Allowed native copy does not remove current links");
  return count;
 }
}
}
'@
$central = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryExternalModels.cs'))
# Use the production central predicate, dropping only its imports/namespace wrapper.
$centralStart = $central.IndexOf('internal static class ElementHistoryExternalModels', [StringComparison]::Ordinal)
$centralClass = $central.Substring($centralStart, $central.LastIndexOf('}') - $centralStart).Trim()
$fixture = $fixture.Replace('__CENTRAL__', $centralClass).Replace('__GUARDS__', ($guard, $snapshot, $validate -join "`n"))
Add-Type -TypeDefinition $fixture
[pscustomobject]@{Passed=$true;Assertions=[Analyse.Checks]::Run();NativeBoundaryOrderingChecks=3;Scope='Production external-object predicate and native restoration guards with API doubles; legacy/ready policy boundaries verified, no Revit copy executed and no journal removed.'}|ConvertTo-Json -Compress
