#Requires -Version 7.0
[CmdletBinding()]
param([string]$RevitApi='C:/Program Files/Autodesk/Revit 2025/RevitAPI.dll')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$paths=@('ElementHistoryNetwork.cs','ElementHistoryNetwork.Guard.cs') | ForEach-Object { Join-Path $repo ('BIMaestro/commands/qui est le coupable/'+$_) }
$sources=@($paths | ForEach-Object { [IO.File]::ReadAllText($_) })
$using=@($sources | ForEach-Object { [regex]::Matches($_,'(?m)^using [^;]+;') | ForEach-Object Value } | Sort-Object -Unique)
$stubs=@'
namespace Analyse {
internal sealed class HistoryNativeArtifact { public bool? SourceSystemTypeDefined {get;set;} }
internal sealed class HistoryParameter { public int BuiltIn {get;set;} }
internal sealed class HistoryRecipe {
    public string Kind,Type,Level;
    public double LevelElevation;
    public double[] Point,BasisX,BasisZ;
    public HistoryNativeArtifact Native,OpeningFallback;
    public List<HistoryPort> Ports;
    public List<HistoryConnection> Connections;
    public List<string> RestorationOrigins,CaptureWarnings;
    public List<HistoryParameter> Parameters;
    public HistoryNetwork Network;
}
internal static class ElementHistoryReconstruction {
    internal static bool VerifyHistoricalPlacement(Element element,HistoryRecipe recipe,out string failure,bool includeBoundingBox=false) {failure=null;return true;}
    internal static void RestoreHistoricalPlacement(Document doc,Element element,HistoryRecipe recipe) { }
    internal static void ApplyParameters(Document doc,Element element,List<HistoryParameter> values) { }
    internal static List<HistoryParameter> CaptureParameters(Element element) {return new List<HistoryParameter>();}
    internal static Level FindLevel(Element element) {return null;}
    internal static Element FindOriginal(Document doc,string uid) {return doc.GetElement(uid);}
}
internal static class ElementHistoryRestoration {
    internal static List<string> GetOrigins(Element element) {return new List<string>();}
    internal static bool IsCalorifuge(string category,string name) {return false;}
}
internal static class ElementHistoryNativeArchive {
    internal static Element ReadMepSystemType(Element element) {return null;}
    internal static string ReadHistoricalTypeVersion(Element element) {return null;}
    internal static bool HasMepSystemType(Element element) {return element is MEPCurve;}
}
}
'@
$output=Join-Path $repo ('tmp/history-network-api-check/'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $output -Force
$newtonsoft=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
[IO.File]::WriteAllText((Join-Path $output 'Dependencies.cs'),(($using -join "`n")+"`n"+$stubs))
$includes=@($paths | ForEach-Object { '<Compile Include="'+[Security.SecurityElement]::Escape($_)+'" />' }) -join "`n"
$project=@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><OutputType>Library</OutputType><PlatformTarget>x64</PlatformTarget><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup>
    <Reference Include="RevitAPI"><HintPath>$([Security.SecurityElement]::Escape($RevitApi))</HintPath><Private>false</Private></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>$([Security.SecurityElement]::Escape($newtonsoft))</HintPath><Private>false</Private></Reference>
    <Compile Include="Dependencies.cs" />
    $includes
  </ItemGroup>
</Project>
"@
$projectPath=Join-Path $output 'HistoryNetwork.ApiCheck.csproj'
[IO.File]::WriteAllText($projectPath,$project)
$buildLog=Join-Path $output 'build.log'
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> $buildLog
if($LASTEXITCODE -ne 0) { Get-Content $buildLog -Tail 50; throw 'Targeted production Network API compilation failed.' }
Write-Output ('PASS: production Network and Guard compile against installed Revit 2025 API; no Revit execution or product build. '+$output)
