param([string]$SourceDirectory = 'BIMaestro/bin/TrainingBase2025', [switch]$Preview)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = if ([IO.Path]::IsPathRooted($SourceDirectory)) { [IO.Path]::GetFullPath($SourceDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $SourceDirectory)) }
$dll = Join-Path $source 'BIMaestro.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Compiler la version Revit 2025 avant installation.' }
$reference = Join-Path $source 'Demo/Maquette/BIMaestro_Apprentissage_2024.rvt'
$validatedReference = Join-Path $repo 'Demo/BIMaestro_Apprentissage_2024.rvt'
if (!(Test-Path -LiteralPath $reference)) { throw 'La maquette de reference manque dans la compilation.' }
if ((Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $validatedReference -Algorithm SHA256).Hash) { throw 'La maquette compilee ne correspond pas a la reference validee.' }
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
if ($references['RevitAPI'].Major -ne 25 -or $references['RevitAPIUI'].Major -ne 25 -or $references['System.Runtime'].Major -ne 8) {
    throw 'DLL incompatible : Revit 2025 exige les API Revit 25 et .NET 8. Aucun fichier modifié.'
}
$binRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BIMaestro/Bin'))
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$destination = [IO.Path]::GetFullPath((Join-Path $binRoot ('Revit2025-TrainingBase-' + $hash.Substring(0, 12))))
if (!$destination.StartsWith($binRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination hors du dossier BIMaestro.' }
$manifest = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.addin'
if (!(Test-Path -LiteralPath $manifest)) { throw 'Le manifeste BIMaestro pour Revit 2025 est introuvable.' }
[xml]$xml = [IO.File]::ReadAllText($manifest)
$entry = @($xml.RevitAddIns.AddIn | Where-Object { $_.FullClassName -eq 'BIMaestroApp' })
if ($entry.Count -ne 1) { throw 'Le manifeste ne contient pas une seule entrée BIMaestro attendue.' }
$oldAssembly = [string]$entry[0].Assembly
$newAssembly = Join-Path $destination 'BIMaestro.dll'
$entry[0].Assembly = $newAssembly
$backup = $manifest + '.before-training-base-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
$files = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object Extension -ne '.pdb')
$plan = [pscustomobject]@{Revit='2025';Source=$source;Destination=$destination;Manifest=$manifest;PreviousAssembly=$oldAssembly;
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
Write-Output 'Installation Revit 2025 mise à jour. Redémarrer Revit pour charger la nouvelle DLL.'
