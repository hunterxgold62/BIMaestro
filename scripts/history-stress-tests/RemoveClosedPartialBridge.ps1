#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RunDirectory,
    [switch] $Execute,
    [switch] $Preview
)
$ErrorActionPreference = 'Stop'
if ($Execute -and $Preview) { throw 'Choisir Preview ou Execute, jamais les deux.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot = Join-Path $repo 'tmp/history-stress'

function Assert-AbsolutePath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) { throw "Chemin absolu requis : $Path" }
    return [IO.Path]::GetFullPath($Path)
}
function Assert-ChildPath([string] $Path, [string] $Root) {
    $full = Assert-AbsolutePath $Path
    $boundary = (Assert-AbsolutePath $Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Chemin hors du périmètre : $full" }
    return $full
}
function Assert-NoRedirect([string] $Path) {
    $current = Assert-AbsolutePath $Path
    while (-not [string]::IsNullOrEmpty($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.LinkType -or $item.LinkTarget)) { throw "Lien ou jonction refusé : $current" }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Assert-UniqueJsonProperties([Text.Json.JsonElement] $Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw "Propriété JSON dupliquée : $($property.Name)" }
            Assert-UniqueJsonProperties $property.Value
        }
    } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($child in $Element.EnumerateArray()) { Assert-UniqueJsonProperties $child }
    }
}
function Read-StrictJson([string] $Path) {
    Assert-NoRedirect $Path
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 1048576) { throw "Preuve JSON trop volumineuse : $Path" }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xFEFF)
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 32
    $json = [Text.Json.JsonDocument]::Parse($text, $options)
    try {
        if ($json.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "Objet JSON requis : $Path" }
        Assert-UniqueJsonProperties $json.RootElement
    } finally { $json.Dispose() }
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 32
}
function Assert-TrueField($Object, [string] $Name) {
    if ($Object[$Name] -isnot [bool] -or $Object[$Name] -ne $true) { throw "Preuve booléenne absente ou fausse : $Name" }
}
function Assert-Sha([string] $Sha, [string] $Name) {
    if ($Sha -notmatch '^[a-fA-F0-9]{64}$') { throw "SHA256 invalide : $Name" }
}
function Assert-BridgeContract($Proof, $Manifest, [string] $Run, [string] $CacheRoot, [string] $ManifestSha) {
    foreach ($name in @('SessionId','BridgeFile','BridgeSha256','SessionManifestPath','SessionManifestSha256','SourcePath','InitialCopyPath','DeletedProject')) {
        if ($Proof[$name] -isnot [string]) { throw "Champ texte requis dans la preuve : $name" }
    }
    foreach ($name in @('Id','BridgeFile')) { if ($Manifest[$name] -isnot [string]) { throw "Champ texte requis dans le manifeste : $name" } }
    foreach ($flag in @('ProjectClosed','OldDocumentInvalid','AbsentFromApplicationDocuments','SourcesDisposed','ConverterPaused','RequestsFrozen')) { Assert-TrueField $Proof $flag }
    Assert-TrueField $Manifest 'Closed'
    if ($Proof.CapturePid -isnot [long] -and $Proof.CapturePid -isnot [int]) { throw 'CapturePid doit être un entier.' }
    if ($Proof.CapturePid -le 0 -or $Manifest.ProcessId -ne $Proof.CapturePid) { throw 'PID du propriétaire incohérent.' }
    if ($Manifest.ProcessId -isnot [long] -and $Manifest.ProcessId -isnot [int]) { throw 'ProcessId du manifeste doit être un entier.' }
    if ($Proof.SessionId -notmatch '^[a-fA-F0-9]{32}$' -or $Manifest.Id -cne $Proof.SessionId) { throw 'Identité de session incohérente.' }
    if (($Manifest.Version -isnot [int] -and $Manifest.Version -isnot [long]) -or $Manifest.Version -ne 1) { throw 'Version du manifeste inconnue.' }
    if ($Manifest.Referenced -isnot [bool]) { throw 'Referenced doit être un booléen du manifeste.' }
    if ($Manifest.Deleted -isnot [bool] -or $Manifest.Deleted -ne $false) { throw 'Le manifeste est déjà marqué supprimé ou invalide.' }
    Assert-Sha $Proof.BridgeSha256 'BridgeSha256'
    Assert-Sha $Proof.SessionManifestSha256 'SessionManifestSha256'
    Assert-Sha $ManifestSha 'ActualManifestSha256'
    if ($ManifestSha -ine $Proof.SessionManifestSha256) { throw 'Le manifeste diffère de la preuve gelée.' }
    $bridge = Assert-AbsolutePath $Proof.BridgeFile
    $expectedCache = (Assert-AbsolutePath $CacheRoot).TrimEnd('\','/')
    if (-not [string]::Equals((Split-Path -Parent $bridge), $expectedCache, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($bridge) -cne ('snapshot-opening-' + $Proof.SessionId + '.rvt')) { throw 'Passerelle hors du cache local exact ou nom UUID invalide.' }
    if (-not [string]::Equals((Assert-AbsolutePath $Manifest.BridgeFile), $bridge, [StringComparison]::OrdinalIgnoreCase)) { throw 'Chemin du manifeste différent de la passerelle.' }
    $expectedManifest = Join-Path $Run ('history/history-data/sessions/' + $Proof.SessionId + '.json')
    $manifestPath = Assert-ChildPath (Assert-AbsolutePath $Proof.SessionManifestPath) $Run
    if (-not [string]::Equals($manifestPath, $expectedManifest, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifeste hors du dossier exact de la session de test.' }
    foreach ($name in @('SourcePath','InitialCopyPath','DeletedProject')) {
        $protected = Assert-AbsolutePath $Proof[$name]
        if ([string]::Equals($protected, $bridge, [StringComparison]::OrdinalIgnoreCase)) { throw "La passerelle correspond à un fichier protégé : $name" }
    }
    return [pscustomobject]@{ BridgeFile=$bridge; SessionId=$Proof.SessionId; CapturePid=$Proof.CapturePid; ManifestPath=$manifestPath }
}
function Get-Sha([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Write-JsonAtomic([string] $Path, $Value) {
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.partial'
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 32))
    try {
        $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, $null) } else { [IO.File]::Move($temp, $Path) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function Read-VerifiedState([string] $Run, [string] $CacheRoot) {
    Assert-NoRedirect $Run
    $proofPath = Join-Path $Run 'partial-close-ready.json'
    $proof = Read-StrictJson $proofPath
    $manifestPath = Assert-ChildPath (Assert-AbsolutePath $proof.SessionManifestPath) $Run
    $manifest = Read-StrictJson $manifestPath
    $manifestSha = Get-Sha $manifestPath
    $contract = Assert-BridgeContract $proof $manifest $Run $CacheRoot $manifestSha
    Assert-NoRedirect $contract.BridgeFile
    $sourceText = Join-Path $Run 'source-path.txt'
    if ([IO.File]::Exists($sourceText)) {
        Assert-NoRedirect $sourceText
        $protected = Assert-AbsolutePath ([IO.File]::ReadAllText($sourceText).Trim())
        if ([string]::Equals($protected, $contract.BridgeFile, [StringComparison]::OrdinalIgnoreCase)) { throw 'La passerelle est le source-path.txt protégé.' }
    }
    $configPath = Join-Path $Run 'config.json'
    if ([IO.File]::Exists($configPath)) {
        $config = Read-StrictJson $configPath
        $automaticPaused = $false; $processExited = $false; $processExitProofSha = $null
        if ($config.BusinessWorkflow -eq $true) {
            $automaticPaused = $proof.AutomaticPreparationPaused -is [bool] -and $proof.AutomaticPreparationPaused -eq $true
            if (-not $automaticPaused) {
                # Loaded observer v1 predates automatic preparation pause. Only a
                # guarded, externally confirmed process exit can replace it.
                $exitPath = Join-Path $Run 'isolated-process-stopped.json'
                $exitProof = Read-StrictJson $exitPath
                $ready = Read-StrictJson (Join-Path $Run 'ready.json')
                if ($exitProof.GuardedStop -ne $true -or $exitProof.Pid -ne $proof.CapturePid -or $ready.Pid -ne $proof.CapturePid -or
                    -not [string]::Equals([IO.Path]::GetFullPath($exitProof.ClosureProof),$proofPath,[StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Business automatique non pause : preuve de fin du processus exact requise apres partial-close-ready.'
                }
                $live = [Diagnostics.Process]::GetProcesses() | Where-Object Id -eq $proof.CapturePid
                if ($live) { throw 'Le processus de capture est encore vivant ; retrait Business interdit sans vraie pause automatique.' }
                $processExited = $true; $processExitProofSha = Get-Sha $exitPath
            }
        }
        foreach ($key in @('SourcePath','InitialCopyPath','DeletedProject','ModelPath','RestoredPath','model','restored')) {
            if ($config[$key] -is [string] -and -not [string]::IsNullOrWhiteSpace($config[$key])) {
                $candidate = if ([IO.Path]::IsPathFullyQualified($config[$key])) { [IO.Path]::GetFullPath($config[$key]) } else { [IO.Path]::GetFullPath((Join-Path $Run $config[$key])) }
                if ([string]::Equals($candidate, $contract.BridgeFile, [StringComparison]::OrdinalIgnoreCase)) { throw "La passerelle est protégée par config.json : $key" }
            }
        }
    }
    $exists = [IO.File]::Exists($contract.BridgeFile)
    $length = $null; $hash = $null; $lastWrite = $null
    if ($exists) {
        $item = Get-Item -LiteralPath $contract.BridgeFile -Force
        if ($item.PSIsContainer -or $item.Length -le 0) { throw 'Passerelle vide ou non fichier.' }
        $length = $item.Length; $lastWrite = $item.LastWriteTimeUtc.ToString('O'); $hash = Get-Sha $contract.BridgeFile
        if ($hash -ine $proof.BridgeSha256) { throw 'SHA256 de la passerelle différent de la preuve de fermeture.' }
        $again = Get-Item -LiteralPath $contract.BridgeFile -Force
        if ($again.Length -ne $length -or $again.LastWriteTimeUtc.ToString('O') -cne $lastWrite) { throw 'Passerelle modifiée pendant la vérification.' }
    }
    return [pscustomobject]@{
        Contract=$contract; ProofSha256=(Get-Sha $proofPath); ManifestSha256=$manifestSha; ExpectedBridgeSha256=$proof.BridgeSha256
        BridgeExists=$exists; BridgeLength=$length; BridgeSha256=$hash; BridgeLastWriteUtc=$lastWrite; AutomaticPreparationPaused=$automaticPaused; CaptureProcessExited=$processExited; ProcessExitProofSha256=$processExitProofSha
    }
}

$run = if ([IO.Path]::IsPathFullyQualified($RunDirectory)) { [IO.Path]::GetFullPath($RunDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $RunDirectory)) }
$run = Assert-ChildPath $run $runRoot
if (-not [IO.Directory]::Exists($run)) { throw 'Dossier de run introuvable.' }
$cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/HistoryBridge'
$started = [DateTime]::UtcNow.ToString('O')
$state = Read-VerifiedState $run $cache
$scriptSha = Get-Sha $PSCommandPath
$previewPath = Join-Path $run 'partial-bridge-removal-preview.json'
$resultPath = Join-Path $run 'partial-bridge-removal.json'
$record = [ordered]@{
    Mode= $(if ($Execute) { 'Execute' } else { 'Preview' }); StartedUtc=$started; CompletedUtc=$null
    RunDirectory=$run; CapturePid=$state.Contract.CapturePid; SessionId=$state.Contract.SessionId; BridgeFile=$state.Contract.BridgeFile
    ExpectedBridgeSha256=$state.ExpectedBridgeSha256; BridgeSha256Before=$state.BridgeSha256; BridgeLengthBefore=$state.BridgeLength
    ProofSha256=$state.ProofSha256; SessionManifestPath=$state.Contract.ManifestPath; SessionManifestSha256=$state.ManifestSha256; ScriptSha256=$scriptSha
    ProjectClosed=$true; OldDocumentInvalid=$true; AbsentFromApplicationDocuments=$true; SourcesDisposed=$true; ConverterPaused=$true; RequestsFrozen=$true
    AutomaticPreparationPaused=$state.AutomaticPreparationPaused; CaptureProcessExited=$state.CaptureProcessExited; ProcessExitProofSha256=$state.ProcessExitProofSha256
    ManualTestBridgeRemoval=$true; AutomaticCleanupVerified=$false; FullRestoreVerified=$false
    Removed=$false; AlreadyAbsent=(-not $state.BridgeExists); AbsentAfter=(-not $state.BridgeExists); Error=$null
}
if (-not $Execute) {
    $record.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    Write-JsonAtomic $previewPath $record
    Write-Output ('PREVIEW : ' + $state.Contract.BridgeFile + '; présent=' + $state.BridgeExists + '; octets=' + $state.BridgeLength)
    Write-Output ('Aucun retrait. Preuve : ' + $previewPath)
    return
}
if ([IO.File]::Exists($resultPath)) { throw 'Un résultat de retrait existe déjà ; refus de réécrire cette preuve.' }
if ([IO.File]::Exists($previewPath)) {
    $reviewed = Read-StrictJson $previewPath
    foreach ($key in @('BridgeFile','ProofSha256','SessionManifestSha256','ExpectedBridgeSha256','BridgeLengthBefore','ScriptSha256')) {
        if ([string]$reviewed[$key] -cne [string]$record[$key]) { throw "Preuve différente de l’aperçu : $key. Refaire Preview." }
    }
}
try {
    if ($state.BridgeExists) {
        # The lease prevents a cooperating cleaner from racing this one-file
        # operation. It is never created in Preview. The bridge itself is the
        # only file passed to Remove-Item; no recursion or process termination.
        $leasePath = $state.Contract.BridgeFile + '.lease'
        Assert-NoRedirect $leasePath
        $lease = [IO.FileStream]::new($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $checked = Read-VerifiedState $run $cache
            if ($checked.Contract.BridgeFile -cne $state.Contract.BridgeFile -or $checked.Contract.SessionId -cne $state.Contract.SessionId -or
                $checked.Contract.CapturePid -ne $state.Contract.CapturePid -or $checked.Contract.ManifestPath -cne $state.Contract.ManifestPath) { throw 'La cible de retrait a changé.' }
            foreach ($key in @('ProofSha256','ManifestSha256','ExpectedBridgeSha256','BridgeLength','BridgeSha256','BridgeLastWriteUtc','AutomaticPreparationPaused','CaptureProcessExited','ProcessExitProofSha256')) {
                if ([string]$checked.$key -cne [string]$state.$key) { throw "Preuve modifiée avant retrait : $key" }
            }
            if (-not $checked.BridgeExists) { throw 'Passerelle disparue avant le retrait ; aucun retrait attribuable à ce script.' }
            Remove-Item -LiteralPath $checked.Contract.BridgeFile -ErrorAction Stop
            $record.Removed = $true
            $record.AbsentAfter = -not [IO.File]::Exists($checked.Contract.BridgeFile)
            if (-not $record.AbsentAfter) { throw 'Passerelle encore présente après Remove-Item.' }
        } finally { $lease.Dispose() }
        # Keep the zero-byte lease marker and every manifest/job/sidecar intact.
    }
} catch {
    $record.Error = $_.Exception.ToString()
    throw
} finally {
    $record.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    Write-JsonAtomic $resultPath $record
}
Write-Output ('RETRAIT MANUEL DE TEST : Removed=' + $record.Removed + '; AlreadyAbsent=' + $record.AlreadyAbsent + '; AbsentAfter=' + $record.AbsentAfter)
Write-Output ('Ce résultat ne valide ni le nettoyage automatique ni la restauration complète. Preuve : ' + $resultPath)
