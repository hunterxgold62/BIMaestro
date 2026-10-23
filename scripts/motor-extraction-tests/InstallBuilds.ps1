param([switch]$Preview,[string]$Revit2025Source='')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/Bin'))
$addinRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Autodesk/Revit/Addins'
Add-Type -AssemblyName System.Reflection.Metadata
$plans = foreach ($year in 2023..2027) {
    $relative = switch ($year) { 2023 { 'Release' } 2024 { 'Release2024' } default { "Revit$year/Release" } }
    $source = Join-Path $repo "BIMaestro/bin/$relative"
    if ($year -eq 2025 -and $Revit2025Source) { $source = [IO.Path]::GetFullPath($Revit2025Source) }
    $dll = Join-Path $source 'BIMaestro.dll'
    $stream = [IO.File]::OpenRead($dll)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $loadedId=$metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('N').Substring(0,8).ToUpperInvariant()
        $references = @{}
        foreach ($handle in $metadata.AssemblyReferences) {
            $reference = $metadata.GetAssemblyReference($handle)
            $references[$metadata.GetString($reference.Name)] = $reference.Version
        }
        $hasCommand = $false
        $hasMaintenance = $false
        foreach ($handle in $metadata.TypeDefinitions) {
            $type = $metadata.GetTypeDefinition($handle)
            if ($metadata.GetString($type.Name) -eq 'MepMotorExtractionCommand') { $hasCommand = $true }
            if ($metadata.GetString($type.Name) -eq 'MaintenanceWorkbenchCommand') { $hasMaintenance = $true }
        }
        if (!$hasCommand) { throw "Prototype absent de la DLL $year" }
        if (!$hasMaintenance) { throw "Maintenance MEP absente de la DLL $year" }
        if ($references['RevitAPI'].Major -ne ($year - 2000) -or $references['RevitAPIUI'].Major -ne ($year - 2000)) {
            throw "Références API incorrectes pour $year"
        }
        if ($year -ge 2025) {
            $runtime = (Get-Content -LiteralPath "C:/Program Files/Autodesk/Revit $year/RevitAPI.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.tfm
            $runtimeMajor = [int](($runtime -replace '^net', '') -split '\.')[0]
            if ($references['System.Runtime'].Major -ne $runtimeMajor) { throw "Runtime incorrect pour $year" }
        }
    } finally { $pe.Dispose(); $stream.Dispose() }
    $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    $destination = [IO.Path]::GetFullPath((Join-Path $binRoot ("Revit$year-Maintenance-" + $hash.Substring(0, 12))))
    if (!$destination.StartsWith($binRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination invalide' }
    $manifest = Join-Path $addinRoot "$year/BIMaestro.addin"
    [xml]$xml = Get-Content -LiteralPath $manifest -Raw
    $entry = @($xml.RevitAddIns.AddIn | Where-Object FullClassName -eq 'BIMaestroApp')
    if ($entry.Count -ne 1) { throw "Manifeste inattendu : $manifest" }
    [pscustomobject]@{ Year=$year; Source=$source; Destination=$destination; Manifest=$manifest; Hash=$hash; LoadedId=$loadedId; PreviousAssembly=[string]$entry[0].Assembly }
}
if ($Preview) { $plans | ConvertTo-Json; return }
# Each build is staged in a new versioned directory. Running Revit processes keep
# their existing assembly; only subsequent launches follow the updated manifest.
# Stage every version before switching any registration.
foreach ($plan in $plans) {
    New-Item -ItemType Directory -Path $plan.Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $plan.Source -Recurse -File | Where-Object Extension -ne '.pdb') {
        $relative = $file.FullName.Substring($plan.Source.TrimEnd('\','/').Length).TrimStart('\','/')
        $target = [IO.Path]::GetFullPath((Join-Path $plan.Destination $relative))
        if (!$target.StartsWith($plan.Destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fichier hors destination' }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    if ((Get-FileHash -LiteralPath (Join-Path $plan.Destination 'BIMaestro.dll')).Hash -ne $plan.Hash) { throw "Copie incorrecte : $($plan.Year)" }
}
foreach ($plan in $plans) {
    [xml]$xml = Get-Content -LiteralPath $plan.Manifest -Raw
    $entry = @($xml.RevitAddIns.AddIn | Where-Object FullClassName -eq 'BIMaestroApp')
    if ($entry.Count -ne 1) { throw 'Manifeste modifié pendant installation' }
    $entry[0].Assembly = Join-Path $plan.Destination 'BIMaestro.dll'
    $backup = $plan.Manifest + '.before-motor-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
    $temporary = $plan.Manifest + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $xml.Save($temporary)
    [IO.File]::Replace($temporary, $plan.Manifest, $backup)
    [xml]$verified = Get-Content -LiteralPath $plan.Manifest -Raw
    $installed = [string](@($verified.RevitAddIns.AddIn | Where-Object FullClassName -eq 'BIMaestroApp')[0].Assembly)
    if ($installed -ne (Join-Path $plan.Destination 'BIMaestro.dll') -or (Get-FileHash -LiteralPath $installed).Hash -ne $plan.Hash) { throw 'Vérification après installation échouée' }
    Write-Output "Revit $($plan.Year) : installé, SHA-256 vérifié — $installed"
}
$plans | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'tmp/motor-extraction-builds/installed.json')
