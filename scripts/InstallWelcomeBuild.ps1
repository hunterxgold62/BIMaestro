param([ValidateSet('2024','2025')][string]$RevitVersion)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repo $(if ($RevitVersion -eq '2024') { 'BIMaestro/bin/Release2024' } else { 'BIMaestro/bin/Revit2025/Release' })
$dll = Join-Path $source 'BIMaestro.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Compiler la version attendue avant installation.' }
if (!(Test-Path -LiteralPath (Join-Path $source 'Demo/Maquette/BIMaestro_Apprentissage_2024.rvt'))) { throw 'Maquette de formation absente.' }
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$binRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/Bin'))
$destination = [IO.Path]::GetFullPath((Join-Path $binRoot ('Revit' + $RevitVersion + '-Welcome-' + $hash.Substring(0,12))))
if (!$destination.StartsWith($binRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination invalide.' }
$manifest = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.addin"
[xml]$xml = Get-Content -LiteralPath $manifest -Raw
$entry = @($xml.RevitAddIns.AddIn | Where-Object FullClassName -eq 'BIMaestroApp')
if ($entry.Count -ne 1) { throw 'Manifeste inattendu.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
$installedDll = Join-Path $destination 'BIMaestro.dll'
if ((Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash -ne $hash) { throw 'Copie incorrecte.' }
$backup = $manifest + '.before-welcome-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
Copy-Item -LiteralPath $manifest -Destination $backup
$entry[0].Assembly = $installedDll
$xml.Save($manifest)
[xml]$verified = Get-Content -LiteralPath $manifest -Raw
if ([string]$verified.RevitAddIns.AddIn.Assembly -ne $installedDll) { throw 'Inscription incorrecte.' }
[pscustomobject]@{ Revit = $RevitVersion; Assembly = $installedDll; Backup = $backup } | ConvertTo-Json
