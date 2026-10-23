#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo ('tmp/history-generated-axis-tests/' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
function ProductionMethod([string] $file, [string] $signature) {
    $source = [IO.File]::ReadAllText((Join-Path $repo $file))
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production method: $signature" }
    $brace = $source.IndexOf('{', $start); $depth = 0
    for ($i = $brace; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ } elseif ($source[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $source.Substring($start, $i - $start + 1).Replace('private static bool', 'public static bool') } }
    }
    throw 'Unbalanced production method.'
}
$tracker = 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'
$live = ProductionMethod $tracker 'private static bool IsAxisLineElement('
$snapshot = ProductionMethod $tracker 'private static bool IsAxisLineSnapshot('
$text = [IO.File]::ReadAllText((Join-Path $repo $tracker))
$start = $text.IndexOf('private static bool IsGeneratedAxisCategory(', [StringComparison]::Ordinal)
$end = $text.IndexOf('private static bool IsAxisLineText(', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'Production generated-axis category helper missing.' }
$category = $text.Substring($start, $end - $start).Replace('private static bool', 'public static bool')
$evidence = ProductionMethod 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.cs' 'private static bool HasLiveExclusion('
$fixture = @'
using System; using System.Collections.Generic; using System.IO; using System.Linq; using Newtonsoft.Json; using Newtonsoft.Json.Linq; using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public class ElementId {public long Value;public ElementId(long value){Value=value;}}
 public class Category {public string Name;public ElementId Id;}
 public class Element {public string Name;public Category Category;}
 public class Wall:Element {} public class Family:Element {} public class FamilyInstance:Element {} public class ModelCurve:Element {}
}
namespace AxisFixture {
 public static class IdExtensions {public static long GetIdLongValue(this ElementId id)=>id.Value;}
 public class ElementSnapshot {public string Name,Family,Category;public long CategoryId;}
 public class ElementHistoryEvent {public int ElementId;public Dictionary<string,object> Delta;}
 public static class Production {
__METHODS__
 }
 public static class Tests {
  static int count;static void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
  static Category Category(long id,string name)=>new Category{Id=new ElementId(id),Name=name};
  static ElementHistoryEvent Evidence(string kind,long category,string uid="source-a",string deleted="source-a",int elementId=7,int capturedId=7)=>new ElementHistoryEvent{ElementId=elementId,Delta=new Dictionary<string,object>{{"deletedUniqueId",deleted},{"excluded3D",new JObject{{"Reason","axis"},{"Class",kind},{"CategoryId",category},{"SourceUniqueId",uid},{"SourceElementId",capturedId},{"CapturedWhileAlive",true},{"ViewSpecific",false}}}}};
  public static int Main(string[] args){try{
   var generated=new long[]{-2008214,-2008210,-2008196,-2008141,-2008140,-2008139,-2008136,-2008072,-2008066,-2008051,-2008045,-2008021,-2008001,-2000288};
   var names=new[]{"Axe","Axes","Axis","Centerline","Ligne d'axe","Porte axe",null,""};
   foreach(var name in names){
    var ordinary=new Element{Name=name,Category=Category(-2000151,name)};Check(!Production.IsAxisLineElement(ordinary),"Free name must never establish generated-axis identity: "+name);
    Check(!Production.IsAxisLineSnapshot(new ElementSnapshot{Name=name,Family=name,Category=name,CategoryId=-2000151}),"Snapshot free name must never establish generated-axis identity");
    Check(!Production.HasLiveExclusion(Evidence(typeof(Element).FullName,-2000151)),"Free category label must never acknowledge an ordinary deletion");
    foreach(var element in new Element[]{new Wall(),new Family(),new FamilyInstance(),new ModelCurve()}){
     element.Name=name;element.Category=Category(generated[0],"Centerline");Check(!Production.IsAxisLineElement(element),"Derived physical class must stay in scope even with Centerline category");
     Check(!Production.HasLiveExclusion(Evidence(element.GetType().FullName,generated[0])),"Derived physical class must never receive axis acknowledgement");
    }
   }
   foreach(var id in generated){
    Check(Production.IsGeneratedAxisCategory(id),"Explicit generated category recognised");
    Check(Production.IsAxisLineElement(new Element{Name="unrelated free name",Category=Category(id,"unrelated free label")}),"Exact native Element class and generated category recognised");
    Check(Production.IsAxisLineSnapshot(new ElementSnapshot{Name="ordinary name",Category="ordinary label",CategoryId=id}),"Snapshot generated category recognised independently of translated label");
    Check(Production.HasLiveExclusion(Evidence(typeof(Element).FullName,id)),"Matching live evidence acknowledges native generated axis");
   }
   Check(!Production.IsAxisLineElement(null)&&!Production.IsAxisLineSnapshot(null),"Missing objects not classified as generated axes");
   Check(!Production.IsAxisLineElement(new Element{Name="Axe"}),"Missing category not inferred from name");
   Check(!Production.IsGeneratedAxisCategory(0)&&!Production.IsGeneratedAxisCategory(-2000151),"Unknown categories rejected");
   Check(!Production.HasLiveExclusion(Evidence(typeof(Element).FullName,generated[0],deleted:"other")),"UID mismatch rejects acknowledgement");
   Check(!Production.HasLiveExclusion(Evidence(typeof(Element).FullName,generated[0],capturedId:8)),"ElementId mismatch rejects acknowledgement");
   var notAlive=Evidence(typeof(Element).FullName,generated[0]);((JObject)notAlive.Delta["excluded3D"])["CapturedWhileAlive"]=false;Check(!Production.HasLiveExclusion(notAlive),"Historical name metadata alone is insufficient");
   File.WriteAllText(args[0],JsonConvert.SerializeObject(new{Passed=true,Assertions=count,RevitInvoked=false,Scope="Extracted production axis identity and durable exclusion evidence; API metadata doubles only."},Formatting.Indented));Console.WriteLine(count+" generated-axis identity assertions PASS");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
$fixture = $fixture.Replace('__METHODS__', ($live, $snapshot, $category, $evidence -join "`n"))
[IO.File]::WriteAllText((Join-Path $root 'Fixtures.cs'), $fixture)
$json = Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include="Fixtures.cs"/></ItemGroup></Project>
"@
$projectPath = Join-Path $root 'GeneratedAxis.Tests.csproj'
[IO.File]::WriteAllText($projectPath, $project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $root 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $root 'build.log') -Tail 60; throw 'Generated-axis fixture compilation failed.' }
& dotnet (Join-Path $root 'bin/Debug/net9.0/GeneratedAxis.Tests.dll') (Join-Path $root 'results.json')
if ($LASTEXITCODE -ne 0) { throw 'Generated-axis identity tests failed.' }
Write-Output $root
