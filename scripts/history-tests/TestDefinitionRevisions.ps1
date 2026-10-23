#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-definition-revision-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDefinitionRevisions.cs') -Destination (Join-Path $output 'Production.cs')
$fixture=@'
using System; using System.Collections.Generic; using System.IO;
namespace Autodesk.Revit.DB {
 public sealed class ElementId { public int Value; public ElementId(int value){Value=value;} public static readonly ElementId InvalidElementId=new ElementId(-1); }
 public class Element {public ElementId Id; public string UniqueId;}
 public class ElementType:Element{} public class Family:Element{} public class Material:Element{} public class ParameterElement:Element{} public class Level:Element{} public class Phase:Element{} public class GraphicsStyle:Element{}
 public class FamilySymbol:ElementType {public Family Family;}
 public class Document {public string Identity=Guid.NewGuid().ToString();public bool IsValidObject=true;public Dictionary<int,Element> Elements=new Dictionary<int,Element>();public Element GetElement(ElementId id){return Elements.TryGetValue(id.Value,out var e)?e:null;}public override bool Equals(object other){if(!IsValidObject)throw new Exception("closed wrapper queried");return other is Document d&&Identity==d.Identity;}public override int GetHashCode(){if(!IsValidObject)throw new Exception("closed wrapper hashed");return Identity.GetHashCode();}}
}
namespace Analyse {
 using Autodesk.Revit.DB;
 internal static class ElementIdExtensions {internal static int GetIdValue(this ElementId id)=>id.Value;}
 internal static class Checks {
  static int count;static void Check(bool ok,string name){count++;if(!ok)throw new Exception(name);}
  static ElementId[] Ids(params int[] ids)=>Array.ConvertAll(ids,id=>new ElementId(id));
  static int Main(string[] args){try{
   var doc=new Document();var type=new ElementType{Id=new ElementId(1),UniqueId="type-a"};var wall=new Element{Id=new ElementId(2),UniqueId="wall"};doc.Elements[1]=type;doc.Elements[2]=wall;
   string session=ElementHistoryDefinitionRevisions.SessionId(doc);long initial=ElementHistoryDefinitionRevisions.Revision(doc);
   Check(initial==0,"initial revision");Check(ElementHistoryDefinitionRevisions.Unchanged(session,initial,"type-a"),"initial type proof");
   ElementHistoryDefinitionRevisions.Remember(doc,type.Id);
   ElementHistoryDefinitionRevisions.Changed(doc,Ids(2),Ids());Check(ElementHistoryDefinitionRevisions.Revision(doc)==initial,"instance edit must not invent a type version");
   ElementHistoryDefinitionRevisions.Changed(doc,Ids(1),Ids());Check(!ElementHistoryDefinitionRevisions.Unchanged(session,initial,"type-a"),"type changed before conversion must reject old snapshot");
   long edited=ElementHistoryDefinitionRevisions.Revision(doc);Check(ElementHistoryDefinitionRevisions.Unchanged(session,edited,"type-a"),"new snapshot may use edited type");
   ElementHistoryDefinitionRevisions.Changed(doc,Ids(1),Ids());Check(!ElementHistoryDefinitionRevisions.Unchanged(session,edited,"type-a"),"second unsaved transaction must advance proof");
   var alias=new Document{Identity=doc.Identity,Elements=doc.Elements};Check(ElementHistoryDefinitionRevisions.SessionId(alias)==session,"equivalent live wrapper");
   Check(ElementHistoryDefinitionRevisions.TrySource(session,edited,out var source)&&ReferenceEquals(source,doc),"canonical live source");
   Check(!ElementHistoryDefinitionRevisions.TrySource(session,999,out source),"future revision rejected");
   ElementHistoryDefinitionRevisions.Changed(doc,Ids(),Ids(1));Check(!ElementHistoryDefinitionRevisions.Unchanged(session,edited,"type-a"),"remembered deleted definition invalidated");
   Check(ElementHistoryDefinitionRevisions.Version(session,initial)==ElementHistoryDefinitionRevisions.Version(session,initial),"stable version");Check(ElementHistoryDefinitionRevisions.Version(session,initial)!=ElementHistoryDefinitionRevisions.Version(session,edited),"different type states separated");
   doc.IsValidObject=false;Check(!ElementHistoryDefinitionRevisions.TrySource(session,edited,out source),"closed source cannot export definitions");
   var reopened=new Document{Identity=doc.Identity};Check(ElementHistoryDefinitionRevisions.SessionId(reopened)!=session,"reopened document does not inherit unproven revisions");
   File.WriteAllText(args[0],"{\"Passed\":true,\"Assertions\":"+count+",\"ProductionSource\":\"ElementHistoryDefinitionRevisions.cs\"}");Console.WriteLine(count+" revision assertions PASS");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
$exe=Join-Path $output 'Checks.exe'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /target:exe "/out:$exe" /r:System.Core.dll (Join-Path $output 'Production.cs') (Join-Path $output 'Fixtures.cs')
if($LASTEXITCODE -ne 0){throw 'Revision test compilation failed'}
& $exe (Join-Path $output 'results.json')
if($LASTEXITCODE -ne 0){throw 'Revision checks failed'}
Write-Output $output
