#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$output=Join-Path $repo ('tmp/history-autonomous-hosted-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
function Extract([string]$text,[string]$signature){
 $start=$text.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw ('Missing '+$signature)}
 $open=$text.IndexOf('{',$start);$depth=0
 for($position=$open;$position -lt $text.Length;$position++){
  if($text[$position] -eq '{'){$depth++}
  if($text[$position] -eq '}'){$depth--;if($depth -eq 0){return $text.Substring($start,$position-$start+1)}}
 }
 throw 'Unclosed production method.'
}
$capture=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryReconstruction.cs'))
$restore=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryRestoration.cs'))
$helper=Extract $capture '        private static bool CanCaptureIndependentHostedFamily'
$order=Extract $restore '        private static List<HistoryRestoreRequest> OrderByDependencies'
$resolve=Extract $restore '        private static string Resolve('
$production='using System;using System.Collections.Generic;using System.Linq;using Autodesk.Revit.DB;namespace Analyse{internal static partial class ElementHistoryReconstruction{'+$helper+' internal static bool Accepts(Element e)=>CanCaptureIndependentHostedFamily(e);internal static Element FindOriginal(Document d,string id)=>id==null?null:d.GetElement(id);}internal static partial class ElementHistoryRestoration{'+$order+$resolve+' internal static List<HistoryRestoreRequest> Order(List<HistoryRestoreRequest> requests)=>OrderByDependencies(requests);internal static string ResolveIdentity(Document d,Dictionary<string,string> index,string id)=>Resolve(d,index,id);}}'
[IO.File]::WriteAllText((Join-Path $output 'Production.cs'),$production)
$fixtures=@'
using System;using System.Collections.Generic;using System.Linq;using Autodesk.Revit.DB;using Analyse;
namespace Autodesk.Revit.DB{
 public sealed class ElementId{public long Value;public ElementId(long value){Value=value;}public static ElementId InvalidElementId=new ElementId(-1);public static bool operator==(ElementId a,ElementId b)=>ReferenceEquals(a,b)||(a is not null&&b is not null&&a.Value==b.Value);public static bool operator!=(ElementId a,ElementId b)=>!(a==b);public override bool Equals(object b)=>b is ElementId other&&other==this;public override int GetHashCode()=>Value.GetHashCode();public long GetIdLongValue()=>Value;}
 public class Category{public ElementId Id=new ElementId(-2000014);}
 public class Location{}public class LocationPoint:Location{}public class LocationCurve:Location{}
 public class Element{public ElementId GroupId=ElementId.InvalidElementId;public Category Category=new Category();public Location Location;}
 public class HostObject:Element{}public class Floor:HostObject{}public class Stairs:Element{}
 public enum WallKind{Basic,Curtain,Stacked}public class WallType{public WallKind Kind=WallKind.Basic;}public class Wall:HostObject{public WallType WallType=new WallType();}
 public enum FamilyPlacementType{OneLevelBasedHosted,OneLevelBased,WorkPlaneBased,Adaptive}
 public class Family{public bool IsInPlace;public FamilyPlacementType FamilyPlacementType=FamilyPlacementType.OneLevelBasedHosted;}public class FamilySymbol{public Family Family=new Family();}
 public class FamilyInstance:Element{public Element SuperComponent,Host;public FamilySymbol Symbol=new FamilySymbol();}
 public class Document{public readonly Dictionary<string,Element> Elements=new Dictionary<string,Element>();public Element GetElement(string id)=>Elements.TryGetValue(id,out var value)?value:null;}
}
namespace Analyse{internal class HistoryRestoreRequest{public string SourceUniqueId,SuperComponentUniqueId;public HistoryRecipe Recipe;}internal class HistoryRecipe{public string Host;public List<string> SystemMembers;public List<HistoryParameter> Parameters;public HistoryNativeArtifact Native;}internal class HistoryNativeArtifact{public string RootSourceUniqueId;}internal class HistoryParameter{public string Reference;}}
internal static class Program{
 static int count;static void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
 static FamilyInstance Door()=>new FamilyInstance{Host=new Wall(),Location=new LocationPoint()};
 static void Reject(Action<FamilyInstance> change,string message){var instance=Door();change(instance);Check(!ElementHistoryReconstruction.Accepts(instance),message);}
 public static int Main(){try{
  var door=Door();var originalHost=door.Host;var originalSymbol=door.Symbol;Check(ElementHistoryReconstruction.Accepts(door),"Ordinary hosted door is captured independently.");
  var window=Door();window.Category.Id=new ElementId(-2000014);Check(ElementHistoryReconstruction.Accepts(window),"Hosted window is captured independently.");
  var floorHosted=Door();floorHosted.Host=new Floor();Check(ElementHistoryReconstruction.Accepts(floorHosted),"Ordinary floor-hosted point family uses the same supported placement.");
  Check(!ElementHistoryReconstruction.Accepts(null),"Missing element is not independently hosted.");Check(!ElementHistoryReconstruction.Accepts(new Element()),"Non-family element does not bypass native ownership.");
  Reject(i=>i.GroupId=new ElementId(20),"Family group is preserved.");Reject(i=>i.Host.GroupId=new ElementId(21),"Grouped host remains an aggregate.");Reject(i=>i.SuperComponent=new Element(),"Nested family remains part of its parent.");
  Reject(i=>((Wall)i.Host).WallType.Kind=WallKind.Curtain,"Curtain panel remains native.");Reject(i=>((Wall)i.Host).WallType.Kind=WallKind.Stacked,"Stacked host is not admitted by this exception.");
  Reject(i=>i.Host=new Stairs(),"Stair-dependent family stays native.");Reject(i=>i.Host=new Element(),"Linked or other unsupported host does not qualify.");Reject(i=>i.Host=null,"Missing host does not qualify.");
  Reject(i=>i.Symbol.Family.IsInPlace=true,"In-place family stays native.");Reject(i=>i.Symbol.Family.FamilyPlacementType=FamilyPlacementType.WorkPlaneBased,"Face/workplane placements retain existing restrictions.");Reject(i=>i.Symbol.Family.FamilyPlacementType=FamilyPlacementType.Adaptive,"Adaptive placement does not bypass ownership.");
  Reject(i=>i.Location=new LocationCurve(),"Curve-driven family does not use point-hosted exception.");Reject(i=>i.Category.Id=new ElementId(-2000127),"Generated baluster remains part of its railing.");Reject(i=>i.Symbol=null,"Missing exact family definition is rejected.");
  Check(ReferenceEquals(originalHost,door.Host)&&ReferenceEquals(originalSymbol,door.Symbol)&&door.GroupId==ElementId.InvalidElementId,"Eligibility never mutates placement or ownership.");
  var host=new HistoryRestoreRequest{SourceUniqueId="old-host",Recipe=new HistoryRecipe()};var child=new HistoryRestoreRequest{SourceUniqueId="old-door",Recipe=new HistoryRecipe{Host="old-host"}};
  var ordered=ElementHistoryRestoration.Order(new List<HistoryRestoreRequest>{child,host});Check(ordered.Count==2&&ReferenceEquals(ordered[0],host)&&ReferenceEquals(ordered[1],child),"The actual restoration order creates a deleted host before its door.");
  var doc=new Document();doc.Elements["new-host"]=new Wall();var index=new Dictionary<string,string>{{"old-host","new-host"}};
  Check(ElementHistoryRestoration.ResolveIdentity(doc,index,"old-host")=="new-host","Historical UID resolves to the new Revit identity.");Check(ElementHistoryRestoration.ResolveIdentity(doc,index,"missing")==null,"Missing historical identity is never replaced with an arbitrary element.");
  doc.Elements["old-host"]=new Wall();Check(ElementHistoryRestoration.ResolveIdentity(doc,index,"old-host")=="old-host","A retained original host is reused instead of duplicated.");
  Console.WriteLine("PASS: "+count+" autonomous hosted-family and dependency checks.");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixtures)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
[IO.File]::WriteAllText((Join-Path $output 'Hosted.Tests.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>')
& dotnet build (Join-Path $output 'Hosted.Tests.csproj') --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'build.log');throw 'Hosted-family fixture compilation failed.'}
& dotnet (Join-Path $output 'bin/Debug/net8.0/Hosted.Tests.dll')
if($LASTEXITCODE -ne 0){throw 'Hosted-family fixture failed.'}
Write-Output $output
