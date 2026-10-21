[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet(2023,2024,2025,2026,2027)][int[]]$RevitVersions = @(2023..2027),
    [switch]$Install,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
if ($Install -and $Preview) { throw 'Choose -Install or -Preview.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/Bin'))
$addinRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Autodesk/Revit/Addins'
$evidenceRoot = Join-Path $repo 'tmp/properties-order-tests'
if ($Install -and $binRoot -match '\\Packages\\sandbox\.') { throw 'Installation refused from an ephemeral sandbox profile; run on the actual desktop host with filesystem authorization.' }
Add-Type -AssemblyName System.Reflection.Metadata

function Assert-ChildPath([string]$Path, [string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw "Path outside intended directory: $Path" }
    return $resolved
}
function Get-Sha([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-NotLink($Item) {
    # OneDrive's cloud placeholders carry ReparsePoint too; unlike a filesystem
    # redirection they have neither a LinkType nor a LinkTarget.
    if ($Item.LinkType -or $Item.LinkTarget) { throw "Linked payload path is not allowed: $($Item.FullName)" }
}
function Read-AssemblyMetadata([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $references = @{}
        foreach ($handle in $metadata.AssemblyReferences) {
            $reference = $metadata.GetAssemblyReference($handle)
            $references[$metadata.GetString($reference.Name)] = $reference.Version
        }
        $types = foreach ($handle in $metadata.TypeDefinitions) {
            $type = $metadata.GetTypeDefinition($handle)
            $metadata.GetString($type.Name)
        }
        return [pscustomobject]@{Version=$metadata.GetAssemblyDefinition().Version;References=$references;Types=@($types)}
    } finally { $pe.Dispose(); $stream.Dispose() }
}
function Assert-BuildEvidence {
    $sourceEvidence = Join-Path $evidenceRoot 'production-build-final-source-hashes.json'
    $buildEvidence = Join-Path $evidenceRoot 'production-build-final-results.json'
    if (!(Test-Path -LiteralPath $sourceEvidence) -or !(Test-Path -LiteralPath $buildEvidence)) { throw 'Final build evidence is missing.' }
    foreach ($record in @(Get-Content -LiteralPath $sourceEvidence -Raw | ConvertFrom-Json)) {
        $sourcePath = Assert-ChildPath $record.Path (Join-Path $repo 'BIMaestro')
        if ((Get-Sha $sourcePath) -ne $record.Hash) { throw "Source changed since the final build: $sourcePath" }
    }
    $results = @(Get-Content -LiteralPath $buildEvidence -Raw | ConvertFrom-Json)
    foreach ($year in $RevitVersions) {
        $result = @($results | Where-Object Version -eq $year)
        if ($result.Count -ne 1 -or $result[0].Exit -ne 0) { throw "No successful final production build for $year" }
        $log = Assert-ChildPath (Join-Path $repo $result[0].Log) $evidenceRoot
        if (!(Test-Path -LiteralPath $log)) { throw "Missing final build log: $log" }
    }
}
function Get-Payload([string]$Source) {
    # Refuse links/junctions in the source tree before recursive enumeration.
    Assert-NotLink (Get-Item -LiteralPath $Source)
    $queue = [Collections.Generic.Queue[string]]::new(); $queue.Enqueue($Source)
    $files = [Collections.Generic.List[object]]::new()
    while ($queue.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            Assert-NotLink $item
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
            if ($item.Extension -eq '.pdb') { continue }
            $relative = $item.FullName.Substring($Source.TrimEnd('\','/').Length).TrimStart('\','/')
            $files.Add([pscustomobject]@{Relative=$relative;Source=$item.FullName;Hash=(Get-Sha $item.FullName)})
        }
    }
    return @($files | Sort-Object Relative)
}
function Assert-Payload([string]$Destination, [object[]]$Payload) {
    $actual = @(Get-Payload $Destination)
    if ($actual.Count -ne $Payload.Count) { throw "Existing package has unexpected files: $Destination" }
    for ($index=0; $index -lt $Payload.Count; $index++) {
        if ($actual[$index].Relative -cne $Payload[$index].Relative -or $actual[$index].Hash -ne $Payload[$index].Hash) {
            throw "Package content mismatch: $Destination / $($Payload[$index].Relative)"
        }
    }
}
function Read-ManifestEntry([string]$Path) {
    Assert-NotLink (Get-Item -LiteralPath $Path)
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $entry = @($xml.RevitAddIns.AddIn | Where-Object FullClassName -eq 'BIMaestroApp')
    if ($entry.Count -ne 1 -or [string]$entry[0].Type -ne 'Application' -or [string]::IsNullOrWhiteSpace([string]$entry[0].Assembly)) {
        throw "Unexpected BIMaestro application registration: $Path"
    }
    return [pscustomobject]@{Xml=$xml;Entry=$entry[0]}
}

Assert-BuildEvidence
$artifactEvidence = @(Get-Content -LiteralPath (Join-Path $evidenceRoot 'production-build-final-artifact-hashes.json') -Raw | ConvertFrom-Json)
$plans = foreach ($year in @($RevitVersions | Sort-Object -Unique)) {
    $relative = switch ($year) { 2023 {'Release'} 2024 {'Release2024'} default {"Revit$year/Release"} }
    $source = Assert-ChildPath (Join-Path $repo "BIMaestro/bin/$relative") (Join-Path $repo 'BIMaestro/bin')
    Assert-NotLink (Get-Item -LiteralPath $source)
    $dll = Join-Path $source 'BIMaestro.dll'
    $artifact = @($artifactEvidence | Where-Object Version -eq $year)
    if ($artifact.Count -ne 1 -or [IO.Path]::GetFullPath($artifact[0].Path) -ne $dll -or (Get-Sha $dll) -ne $artifact[0].Hash) {
        throw "Production DLL differs from the recorded final artifact: $year"
    }
    $assembly = Read-AssemblyMetadata $dll
    foreach ($type in @('BIMaestroApp','NativePropertiesGrid','PropertiesPaletteLayout','PropertiesAppearanceService')) {
        if ($assembly.Types -notcontains $type) { throw "Production properties implementation absent from $year build: $type" }
    }
    foreach ($apiName in @('RevitAPI','RevitAPIUI')) {
        $installed = Read-AssemblyMetadata "C:/Program Files/Autodesk/Revit $year/$apiName.dll"
        if (!$assembly.References.ContainsKey($apiName) -or $assembly.References[$apiName].Major -ne ($year-2000) -or
            $installed.Version.Major -ne $assembly.References[$apiName].Major) { throw "Wrong $apiName reference for $year" }
    }
    if ($year -ge 2025) {
        $runtime = (Get-Content -LiteralPath "C:/Program Files/Autodesk/Revit $year/RevitAPI.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.tfm
        if ($runtime -notmatch '^net(\d+)\.') { throw "Unrecognized installed runtime: $year / $runtime" }
        $runtimeMajor = [int]$Matches[1]
        if (!$assembly.References.ContainsKey('System.Runtime') -or $assembly.References['System.Runtime'].Major -ne $runtimeMajor) { throw "Wrong runtime target for $year" }
    } else {
        $runtime = '.NETFramework4.8'
        if (!$assembly.References.ContainsKey('mscorlib') -or $assembly.References['mscorlib'].Major -ne 4) { throw "Wrong framework target for $year" }
    }
    $payload = @(Get-Payload $source)
    $hashInput = ($payload | ForEach-Object { $_.Relative.Replace('\','/') + "`t" + $_.Hash }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $packageHash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($hashInput))).Replace('-','') } finally { $sha.Dispose() }
    $destination = Assert-ChildPath (Join-Path $binRoot "Revit$year-PropertiesOrder-$packageHash") $binRoot
    if (Test-Path -LiteralPath $destination) { Assert-Payload $destination $payload }
    $manifest = Join-Path $addinRoot "$year/BIMaestro.addin"
    $registration = Read-ManifestEntry $manifest
    [pscustomobject]@{
        Year=$year;Source=$source;Destination=$destination;Manifest=$manifest;Runtime=$runtime
        AssemblyHash=(Get-Sha $dll);PackageHash=$packageHash;Payload=$payload
        ManifestHash=(Get-Sha $manifest);PreviousAssembly=[string]$registration.Entry.Assembly
        PreviousManifestBytes=[IO.File]::ReadAllBytes($manifest)
    }
}
$previewPlans = @($plans | Select-Object Year,Source,Destination,Manifest,Runtime,AssemblyHash,PackageHash,PreviousAssembly)
if (!$Install -or $Preview) { $previewPlans | ConvertTo-Json -Depth 5; return }
if (!$PSCmdlet.ShouldProcess(($plans.Year -join ', '),'Stage immutable BIMaestro packages and atomically switch production manifests')) { return }

# Stage EVERY version before touching ANY manifest. Existing packages are read-only:
# running Revit processes can keep their loaded DLLs, which are never overwritten.
foreach ($plan in $plans) {
    if (Test-Path -LiteralPath $plan.Destination) { Assert-Payload $plan.Destination $plan.Payload; continue }
    $staging = Assert-ChildPath (Join-Path $binRoot ('.properties-staging-' + [Guid]::NewGuid().ToString('N'))) $binRoot
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($file in $plan.Payload) {
        if ((Get-Sha $file.Source) -ne $file.Hash) { throw "Build changed while staging: $($file.Source)" }
        $target = Assert-ChildPath (Join-Path $staging $file.Relative) $staging
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [IO.File]::Copy($file.Source,$target,$false)
    }
    Assert-Payload $staging $plan.Payload
    # Both fully resolved paths are verified inside the explicit BIMaestro Bin root.
    $null = Assert-ChildPath $staging $binRoot
    $null = Assert-ChildPath $plan.Destination $binRoot
    [IO.Directory]::Move($staging,$plan.Destination)
    Assert-Payload $plan.Destination $plan.Payload
}
Assert-BuildEvidence
foreach ($plan in $plans) {
    if ((Get-Sha $plan.Manifest) -ne $plan.ManifestHash) { throw "Registration changed during staging: $($plan.Manifest)" }
}
$switched = [Collections.Generic.List[object]]::new()
try {
    foreach ($plan in $plans) {
        if ((Get-Sha $plan.Manifest) -ne $plan.ManifestHash) { throw "Registration changed before replacement: $($plan.Manifest)" }
        $registration = Read-ManifestEntry $plan.Manifest
        $registration.Entry.Assembly = Join-Path $plan.Destination 'BIMaestro.dll'
        $suffix = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
        $backup = $plan.Manifest + '.before-properties-order-' + $suffix + '.bak'
        $temporary = $plan.Manifest + '.' + $suffix + '.tmp'
        $registration.Xml.Save($temporary)
        $updatedHash = Get-Sha $temporary
        if ((Get-Sha $plan.Manifest) -ne $plan.ManifestHash) { throw "Registration changed while preparing replacement: $($plan.Manifest)" }
        [IO.File]::Replace($temporary,$plan.Manifest,$backup)
        $switched.Add([pscustomobject]@{Plan=$plan;Backup=$backup;UpdatedHash=$updatedHash;PreviousHash=(Get-Sha $backup);PreviousBytes=[IO.File]::ReadAllBytes($backup)})
        $verified = Read-ManifestEntry $plan.Manifest
        if ([string]$verified.Entry.Assembly -ne (Join-Path $plan.Destination 'BIMaestro.dll') -or
            (Get-Sha ([string]$verified.Entry.Assembly)) -ne $plan.AssemblyHash -or (Get-Sha $backup) -ne $plan.ManifestHash) {
            throw "Post-install verification failed: $($plan.Year)"
        }
    }
} catch {
    $installationFailure = $_
    # Roll back only manifests still equal to the exact bytes installed here.
    # An external edit is preserved, with its backup path reported for manual recovery.
    for ($index=$switched.Count-1; $index -ge 0; $index--) {
        $change = $switched[$index]
        try {
            if ((Get-Sha $change.Plan.Manifest) -ne $change.UpdatedHash) { throw "External registration edit; restore manually from $($change.Backup)" }
            $restore = $change.Plan.Manifest + '.' + [Guid]::NewGuid().ToString('N') + '.restore'
            [IO.File]::WriteAllBytes($restore,$change.PreviousBytes)
            [IO.File]::Replace($restore,$change.Plan.Manifest,$null)
            if ((Get-Sha $change.Plan.Manifest) -ne $change.PreviousHash) { throw 'Restored registration hash mismatch.' }
        } catch { Write-Warning "Rollback failed: $($_.Exception.Message). Original manifest: $($change.Backup)" }
    }
    throw $installationFailure
}
$receipt = [pscustomobject]@{
    InstalledAtUtc=[DateTime]::UtcNow.ToString('O');Plans=$previewPlans
    ManifestBackups=@($switched | Select-Object @{n='Year';e={$_.Plan.Year}},Backup)
    Note='Existing Revit sessions retain their old DLL. Updated manifests apply at the next application start.'
}
$receipt | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'installed-production.json')
$receipt | ConvertTo-Json -Depth 7
