#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('2025','2026','2027')][string]$RevitVersion = '2025',
    [string]$SourceDirectory = "BIMaestro/bin/Revit$RevitVersion/Release",
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$expectedApiMajor = [int]$RevitVersion - 2000
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = if ([IO.Path]::IsPathRooted($SourceDirectory)) { [IO.Path]::GetFullPath($SourceDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $SourceDirectory)) }
$dll = Join-Path $source 'BIMaestro.dll'
$revitDirectory = "C:/Program Files/Autodesk/Revit $RevitVersion"
$runtimeConfig = Join-Path $revitDirectory 'RevitAPI.runtimeconfig.json'
$binRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/Bin'))
$manifest = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.addin"
Add-Type -AssemblyName System.Reflection.Metadata

function Assert-ChildPath([string]$Path,[string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw "Chemin hors du dossier prévu : $Path" }
    return $resolved
}
function Assert-NotLink($Item) {
    # OneDrive placeholders are reparse points too; they are not path redirections.
    if ($Item.LinkType -or $Item.LinkTarget) { throw "Lien ou jonction refusé : $($Item.FullName)" }
}
function Get-Sha([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Read-PeMetadata([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (!$pe.HasMetadata -or !$pe.PEHeaders.CorHeader) { throw "Assembly .NET attendue : $Path" }
        if (([int]$pe.PEHeaders.CorHeader.Flags -band 2) -ne 0) { throw "Assembly 32 bits incompatible avec Revit : $Path" }
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $definition = $metadata.GetAssemblyDefinition()
        $references = @{}
        foreach ($handle in $metadata.AssemblyReferences) {
            $reference = $metadata.GetAssemblyReference($handle)
            $references[$metadata.GetString($reference.Name)] = $reference.Version
        }
        $types = foreach ($handle in $metadata.TypeDefinitions) {
            $type = $metadata.GetTypeDefinition($handle)
            $namespace = $metadata.GetString($type.Namespace)
            $name = $metadata.GetString($type.Name)
            if ($namespace) { "$namespace.$name" } else { $name }
        }
        return [pscustomobject]@{Name=$metadata.GetString($definition.Name);Version=$definition.Version;References=$references;Types=@($types)}
    } finally { $pe.Dispose(); $stream.Dispose() }
}
function Get-Payload([string]$Directory) {
    Assert-NotLink (Get-Item -LiteralPath $Directory)
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Directory)
    $files = [Collections.Generic.List[object]]::new()
    while ($queue.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            Assert-NotLink $item
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
            if ($item.Extension -eq '.pdb') { continue }
            $relative = $item.FullName.Substring($Directory.TrimEnd('\','/').Length).TrimStart('\','/')
            $files.Add([pscustomobject]@{Relative=$relative;Source=$item.FullName;Hash=(Get-Sha $item.FullName);Bytes=$item.Length})
        }
    }
    return @($files | Sort-Object Relative)
}
function Assert-Payload([string]$Directory,[object[]]$Payload) {
    $actual = @(Get-Payload $Directory)
    if ($actual.Count -ne $Payload.Count) { throw "Nombre de fichiers inattendu dans : $Directory" }
    for ($index=0; $index -lt $Payload.Count; $index++) {
        if ($actual[$index].Relative -cne $Payload[$index].Relative -or $actual[$index].Hash -ne $Payload[$index].Hash) {
            throw "Fichier différent du paquet préparé : $Directory / $($Payload[$index].Relative)"
        }
    }
}
function Read-Manifest([string]$Path) {
    Assert-NotLink (Get-Item -LiteralPath $Path)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path,$settings)
    try {
        $xml = [Xml.XmlDocument]::new()
        $xml.PreserveWhitespace = $true
        $xml.XmlResolver = $null
        $xml.Load($reader)
    } finally { $reader.Dispose() }
    $entries = @($xml.SelectNodes("/RevitAddIns/AddIn[FullClassName='BIMaestroApp']"))
    if ($entries.Count -ne 1 -or $entries[0].GetAttribute('Type') -cne 'Application') {
        throw "Le manifeste doit contenir exactement une application BIMaestroApp : $Path"
    }
    $assemblyNodes = @($entries[0].SelectNodes('Assembly'))
    if ($assemblyNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($assemblyNodes[0].InnerText)) { throw "Assembly du manifeste absente ou ambiguë : $Path" }
    return [pscustomobject]@{Xml=$xml;Assembly=$assemblyNodes[0]}
}

# Everything before the Preview return is read-only, including PE inspection:
# never load BIMaestro/Revit assemblies into this PowerShell process.
if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Compilation Revit $RevitVersion introuvable : $dll" }
foreach ($dependency in @('Newtonsoft.Json.dll','OxyPlot.dll','OxyPlot.Wpf.Shared.dll','OxyPlot.Wpf.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $dependency) -PathType Leaf)) { throw "Dépendance manquante : $dependency" }
}
if (!(Test-Path -LiteralPath $runtimeConfig -PathType Leaf)) { throw "Configuration .NET de Revit $RevitVersion introuvable." }
$runtime = [string](Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.tfm
if ($runtime -notmatch '^net(\d+)\.') { throw "Runtime Revit $RevitVersion non reconnu : $runtime" }
$runtimeMajor = [int]$Matches[1]
$assembly = Read-PeMetadata $dll
if ($assembly.Name -cne 'BIMaestro') { throw 'Le paquet ne contient pas une assembly BIMaestro attendue.' }
foreach ($type in @('BIMaestroApp','Analyse.ElementHistoryTracker','Analyse.ElementHistoryOpeningArchive')) {
    if ($assembly.Types -cnotcontains $type) { throw "Implémentation historique absente de la DLL : $type" }
}
foreach ($apiName in @('RevitAPI','RevitAPIUI')) {
    $installed = Read-PeMetadata (Join-Path $revitDirectory "$apiName.dll")
    if (!$assembly.References.ContainsKey($apiName) -or $assembly.References[$apiName].Major -ne $expectedApiMajor -or $installed.Version.Major -ne $expectedApiMajor -or
        $assembly.References[$apiName] -ne $installed.Version) { throw "Référence $apiName incompatible avec Revit $RevitVersion sur ce poste." }
}
if (!$assembly.References.ContainsKey('System.Runtime') -or $assembly.References['System.Runtime'].Major -ne $runtimeMajor) {
    throw "La DLL cible un runtime incompatible ; Revit $RevitVersion utilise $runtime sur ce poste."
}
$hash = Get-Sha $dll
if ($ExpectedSha256 -and $hash -ne $ExpectedSha256) { throw 'La DLL ne correspond pas au SHA-256 de la compilation validée.' }
$payload = @(Get-Payload $source)
$dllRecord = @($payload | Where-Object Relative -ceq 'BIMaestro.dll')
if ($dllRecord.Count -ne 1 -or $dllRecord[0].Hash -ne $hash) { throw 'La compilation a changé pendant sa vérification.' }
$destination = Assert-ChildPath (Join-Path $binRoot ('Revit' + $RevitVersion + '-History-' + $hash.Substring(0,12))) $binRoot
$newAssembly = Join-Path $destination 'BIMaestro.dll'
if (Test-Path -LiteralPath $destination) { Assert-Payload $destination $payload }
$registration = Read-Manifest $manifest
$manifestHash = Get-Sha $manifest
$previousAssembly = $registration.Assembly.InnerText
$backup = $manifest + ('.before-history-' + $RevitVersion + '-') + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.bak'
$plan = [pscustomobject]@{
    Revit=$RevitVersion;Runtime=$runtime;Source=$source;Destination=$destination;Manifest=$manifest;
    PreviousAssembly=$previousAssembly;Assembly=$newAssembly;Sha256=$hash;Files=$payload.Count;
    Bytes=($payload | Measure-Object Bytes -Sum).Sum;Backup=$backup;Preview=[bool]$Preview
}
if ($Preview) { $plan | ConvertTo-Json; return }
if ($binRoot -match '\\Packages\\sandbox\.') { throw "Exécuter l'installation sur le profil du bureau autorisé, hors profil temporaire de sandbox." }

# Stage the full package before changing registration. Existing versions are
# immutable, so running Revit processes retain their already loaded assemblies.
if (!(Test-Path -LiteralPath $destination)) {
    $staging = Assert-ChildPath (Join-Path $binRoot ('.history-staging-' + [Guid]::NewGuid().ToString('N'))) $binRoot
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($file in $payload) {
        if ((Get-Sha $file.Source) -ne $file.Hash) { throw "La compilation a changé avant copie : $($file.Source). Manifeste inchangé." }
        $target = Assert-ChildPath (Join-Path $staging $file.Relative) $staging
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [IO.File]::Copy($file.Source,$target,$false)
    }
    Assert-Payload $staging $payload
    $null = Assert-ChildPath $staging $binRoot
    $null = Assert-ChildPath $destination $binRoot
    [IO.Directory]::Move($staging,$destination)
}
Assert-Payload $destination $payload
if ((Get-Sha $newAssembly) -ne $hash) { throw 'La DLL installée diffère du paquet vérifié. Manifeste inchangé.' }
if ((Get-Sha $manifest) -ne $manifestHash) { throw 'Le manifeste a changé pendant la préparation. Aucun basculement effectué.' }
$registration.Assembly.InnerText = $newAssembly
$temporary = $manifest + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $registration.Xml.Save($temporary)
    if ((Get-Sha $manifest) -ne $manifestHash) { throw 'Le manifeste a changé avant son remplacement. Aucun basculement effectué.' }
    [IO.File]::Replace($temporary,$manifest,$backup)
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
$applied = Read-Manifest $manifest
if ($applied.Assembly.InnerText -cne $newAssembly -or (Get-Sha $newAssembly) -ne $hash) { throw 'Échec de vérification après basculement du manifeste.' }
$plan | ConvertTo-Json
Write-Output "Version historique Revit $RevitVersion installée et vérifiée. Elle sera chargée au prochain lancement de Revit."
