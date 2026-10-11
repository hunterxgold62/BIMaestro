param([ValidateSet('2025','2026','2027')][string]$RevitVersion = '2025', [string]$SourceDirectory = '', [switch]$Preview)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$SourceDirectory) { $SourceDirectory = "BIMaestro/bin/Revit$RevitVersion/Release" }
$source = if ([IO.Path]::IsPathRooted($SourceDirectory)) { [IO.Path]::GetFullPath($SourceDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $SourceDirectory)) }
$dll = Join-Path $source 'BIMaestro.dll'
if (!(Test-Path -LiteralPath $dll)) { throw "Compiler la version Revit $RevitVersion avant installation." }
$runtimeConfig = Join-Path "C:\Program Files\Autodesk\Revit $RevitVersion" 'RevitAPI.runtimeconfig.json'
if (!(Test-Path -LiteralPath $runtimeConfig)) { throw "Revit $RevitVersion ou sa configuration .NET est introuvable." }
$runtimeMajor = [int](([string]((Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.tfm) -split '\.')[0] -replace '^net','')
# Inspect PE metadata without loading BIMaestro or any Revit assembly into this process.
Add-Type -AssemblyName System.Reflection.Metadata
$stream = [IO.File]::OpenRead($dll)
$pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $references = @{}
    foreach ($handle in $metadata.AssemblyReferences) {
        $reference = $metadata.GetAssemblyReference($handle)
        $references[$metadata.GetString($reference.Name)] = $reference.Version
    }
} finally { $pe.Dispose(); $stream.Dispose() }
$apiMajor = [int]$RevitVersion - 2000
if ($references['RevitAPI'].Major -ne $apiMajor -or $references['RevitAPIUI'].Major -ne $apiMajor -or $references['System.Runtime'].Major -ne $runtimeMajor) {
    throw "DLL incompatible : Revit $RevitVersion exige les API Revit $apiMajor et .NET $runtimeMajor sur ce poste. Aucun fichier modifié."
}
$binRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BIMaestro/Bin'))
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$destination = [IO.Path]::GetFullPath((Join-Path $binRoot ("Revit$RevitVersion-Clash3D-" + $hash.Substring(0, 12))))
if (!$destination.StartsWith($binRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination hors du dossier BIMaestro.' }
$manifest = Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.addin"
if (!(Test-Path -LiteralPath $manifest)) { throw "Le manifeste BIMaestro pour Revit $RevitVersion est introuvable." }
[xml]$xml = [IO.File]::ReadAllText($manifest)
$entry = @($xml.RevitAddIns.AddIn | Where-Object { $_.FullClassName -eq 'BIMaestroApp' })
if ($entry.Count -ne 1) { throw 'Le manifeste ne contient pas une seule entrée BIMaestro attendue.' }
$oldAssembly = [string]$entry[0].Assembly
$newAssembly = Join-Path $destination 'BIMaestro.dll'
$entry[0].Assembly = $newAssembly
$backup = $manifest + ".before-clash$RevitVersion-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
$files = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object Extension -ne '.pdb')
$plan = [pscustomobject]@{Revit=$RevitVersion;Runtime="net$runtimeMajor";Source=$source;Destination=$destination;Manifest=$manifest;PreviousAssembly=$oldAssembly;
    Assembly=$newAssembly;Sha256=$hash;Files=$files.Count;Bytes=($files | Measure-Object Length -Sum).Sum;Backup=$backup;Preview=[bool]$Preview}
if ($Preview) { $plan | ConvertTo-Json; return }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in $files) {
    $relative = $file.FullName.Substring($source.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar).Length).TrimStart([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fichier hors de la destination.' }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
if ((Get-FileHash -LiteralPath $newAssembly -Algorithm SHA256).Hash -ne $hash) { throw 'La DLL copiée ne correspond pas à la compilation validée.' }
$temporary = $manifest + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try { $xml.Save($temporary); [IO.File]::Replace($temporary, $manifest, $backup) }
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
[xml]$applied = [IO.File]::ReadAllText($manifest)
if ([string]$applied.RevitAddIns.AddIn.Assembly -ne $newAssembly) { throw 'Le manifeste ne référence pas la version attendue.' }
$plan | ConvertTo-Json
Write-Output "Installation Revit $RevitVersion mise à jour. Redémarrer Revit pour charger la nouvelle DLL."
