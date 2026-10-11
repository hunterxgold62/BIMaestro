param([switch]$RegisterProbe)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $repo 'BIMaestro/bin/Debug'
$dll = Join-Path $source 'BIMaestro.dll'
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$metadata = [Reflection.AssemblyName]::GetAssemblyName($dll)
if ($metadata.Name -ne 'BIMaestro') { throw 'Assembly inattendue.' }
$destination = Join-Path $env:LOCALAPPDATA ('BIMaestro/Bin/Revit2023-RibbonBadges-' + $hash.Substring(0,12))
$manifest = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2023/BIMaestro.addin'
[xml]$xml = [IO.File]::ReadAllText($manifest)
$entry = @($xml.RevitAddIns.AddIn | Where-Object FullClassName -eq 'BIMaestroApp')
if ($entry.Count -ne 1) { throw 'Manifeste inattendu.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
$newDll = Join-Path $destination 'BIMaestro.dll'
if ((Get-FileHash -LiteralPath $newDll -Algorithm SHA256).Hash -ne $hash) { throw 'Copie incorrecte.' }
$entry[0].Assembly = $newDll
$temporary = $manifest + '.ribbon.tmp'
$backup = $manifest + '.before-ribbon-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
$xml.Save($temporary)
[IO.File]::Replace($temporary, $manifest, $backup)
if ($RegisterProbe) {
    $probe = Join-Path $repo 'tmp/ribbon-badge-probe-2023-final/BIMaestro.RibbonBadgeProbe.dll'
    if (!(Test-Path -LiteralPath $probe)) { throw 'Diagnostic non compilé.' }
    Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($probe)) 'enabled.flag') -Value 'test'
    $probeXml = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro RibbonBadgeProbe</Name><Assembly>' + $probe + '</Assembly><AddInId>9D640519-9154-4FAB-B587-4ECEE3583D6E</AddInId><FullClassName>RibbonBadgeProbe</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
    Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($manifest)) 'ZZ.BIMaestro.RibbonBadgeProbe.addin') -Value $probeXml
}
Write-Output "Revit 2023 : $newDll"
