#Requires -Version 7.0
param(
    [string]$LegacyAssembly = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/Bin/BIMaestro.dll'),
    [string]$CompatibleAssembly,
    [string]$BuildAssembly = 'BIMaestro/bin/Revit2025/Release/BIMaestro.dll'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$CompatibleAssembly) {
    $manifest = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Autodesk/Revit/Addins/2025/BIMaestro.addin'
    $CompatibleAssembly = [string]([xml][IO.File]::ReadAllText($manifest)).RevitAddIns.AddIn.Assembly
}
if (![IO.Path]::IsPathRooted($BuildAssembly)) { $BuildAssembly = Join-Path $repo $BuildAssembly }
$output = Join-Path $repo ('tmp/history-api-compatibility-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/MainCommand.cs'))
$start = $source.IndexOf('    internal static class ElementHistoryCompatibility', [StringComparison]::Ordinal)
$end = if ($start -ge 0) { $source.IndexOf('    [Transaction(TransactionMode.Manual)]', $start, [StringComparison]::Ordinal) } else { -1 }
if ($start -lt 0 -or $end -le $start) { throw 'Production history API compatibility helper was not found.' }
$production = 'using System; using System.Linq; using System.Reflection; using Autodesk.Revit.DB;' + "`nnamespace Analyse {`n" + $source.Substring($start, $end - $start) + "`n}"
[IO.File]::WriteAllText((Join-Path $output 'Production.cs'), $production)
$fixture = @"
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
namespace Autodesk.Revit.DB { public class Document { } }
namespace Analyse
{
    internal static class Checks
    {
        private static int count;
        private static void Check(bool value, string reason) { count++; if (!value) throw new InvalidOperationException(reason); }
        private static Version ApiReference(string path)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(handle);
                if (md.GetString(reference.Name) == "RevitAPI") return reference.Version;
            }
            throw new InvalidOperationException("RevitAPI reference missing: " + path);
        }
        private static (Version Version, int Width) ApiMetadata(string path)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (md.GetString(type.Namespace) != "Autodesk.Revit.DB" || md.GetString(type.Name) != "BuiltInParameter") continue;
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = md.GetFieldDefinition(fieldHandle);
                    if (md.GetString(field.Name) != "value__") continue;
                    var signature = md.GetBlobBytes(field.Signature);
                    if (signature.SequenceEqual(new byte[] { 0x06, 0x08 })) return (md.GetAssemblyDefinition().Version, 4);
                    if (signature.SequenceEqual(new byte[] { 0x06, 0x0a })) return (md.GetAssemblyDefinition().Version, 8);
                    throw new InvalidOperationException("Unexpected BuiltInParameter storage signature: " + path);
                }
            }
            throw new InvalidOperationException("BuiltInParameter enum storage missing: " + path);
        }
        private static int Main(string[] args)
        {
            try
            {
                Version v23 = new Version(23, 0), v24 = new Version(24, 0), v25 = new Version(25, 0);
                Check(ElementHistoryCompatibility.GetApiIssue(v23, v24) != null, "Revit 2023 build must be blocked in Revit 2024.");
                Check(ElementHistoryCompatibility.GetApiIssue(v23, v25) != null, "Revit 2023 build must be blocked in Revit 2025.");
                Check(ElementHistoryCompatibility.GetApiIssue(v24, v25) != null, "A wrong major must be blocked even when enum widths match.");
                Check(ElementHistoryCompatibility.GetApiIssue(v25, v24) != null, "A newer wrong major must be blocked as well.");
                Check(ElementHistoryCompatibility.GetApiIssue(v23, v23) == null && ElementHistoryCompatibility.GetApiIssue(v24, v24) == null && ElementHistoryCompatibility.GetApiIssue(v25, v25) == null, "Matching API majors must remain enabled.");
                Check(ElementHistoryCompatibility.GetApiIssue(v25, new Version(25, 4, 40, 0)) == null, "A patch version within the same API major must not be blocked.");
                Check(ElementHistoryCompatibility.GetApiIssue(null, v25) != null && ElementHistoryCompatibility.GetApiIssue(v25, null) != null, "Missing compatibility metadata must not permit preparation.");
                var issue = ElementHistoryCompatibility.GetApiIssue(v23, v25);
                Check(issue.Contains("2023") && issue.Contains("2025") && issue.Contains("La préparation est bloquée") && issue.Contains("version de BIMaestro installée pour cette version de Revit"), "The compatibility issue must explain the actual versions and required action.");
                Check(ElementHistoryCompatibility.GetLoadedApiIssue() != null, "A fixture without a RevitAPI assembly reference must report unverified compatibility.");

                var legacy = ApiReference(args[0]);
                var installed = ApiReference(args[1]);
                var build = ApiReference(args[2]);
                var api23 = ApiMetadata(args[3]);
                var api24 = ApiMetadata(args[4]);
                var api25 = ApiMetadata(args[5]);
                Check(api23.Version.Major == 23 && api23.Width == 4, "Installed Revit 2023 API must declare 32-bit BuiltInParameter storage.");
                Check(api24.Version.Major == 24 && api24.Width == 8, "Installed Revit 2024 API must declare 64-bit BuiltInParameter storage.");
                Check(api25.Version.Major == 25 && api25.Width == 8, "Installed Revit 2025 API must declare 64-bit BuiltInParameter storage.");
                Check(legacy.Major == 23, "The observed base Bin legacy binary must reference RevitAPI 23.");
                Check(ElementHistoryCompatibility.GetApiIssue(legacy, api25.Version) != null, "The actual base Bin binary must be blocked against the installed Revit 2025 API.");
                Check(installed == api25.Version && ElementHistoryCompatibility.GetApiIssue(installed, api25.Version) == null, "The installed immutable binary must match the actual Revit 2025 API.");
                Check(build == api25.Version && ElementHistoryCompatibility.GetApiIssue(build, api25.Version) == null, "The current Revit 2025 build must match the actual Revit 2025 API.");
                Console.WriteLine("PE references: base=" + legacy + "; immutable=" + installed + "; build=" + build + "; runtime=" + api25.Version + ". Enum widths: 2023=" + api23.Width + "; 2024=" + api24.Width + "; 2025=" + api25.Width + " bytes.");
                Console.WriteLine("PASS: " + count + " production history API compatibility and PE metadata checks.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
"@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'), $fixture)
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
$projectPath = Join-Path $output 'HistoryApiCompatibility.Tests.csproj'
[IO.File]::WriteAllText($projectPath, $project)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>')
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.log'); throw 'History API compatibility test compilation failed.' }
& dotnet (Join-Path $output 'bin/Debug/net8.0/HistoryApiCompatibility.Tests.dll') $LegacyAssembly $CompatibleAssembly $BuildAssembly 'C:/Program Files/Autodesk/Revit 2023/RevitAPI.dll' 'C:/Program Files/Autodesk/Revit 2024/RevitAPI.dll' 'C:/Program Files/Autodesk/Revit 2025/RevitAPI.dll'
if ($LASTEXITCODE -ne 0) { throw 'History API compatibility checks failed.' }
Write-Output $output