param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName,
    [ValidateSet('early','prepared','diagnostic','durable')][string]$Scenario = 'early',
    [int]$PreparedTimeoutSeconds = 300,
    [int]$JournalTimeoutSeconds = 180,
    [string]$SourceFile,
    [string]$ReferenceInventoryFile,
    [string]$ReferenceOwnershipFile,
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$DurableCaptureRunName,
    [ValidateRange(0,3600)][int]$DeletionDelaySeconds = 0,
    [ValidateRange(0.01,1)][double]$PhysicalTargetFraction = 1,
    [switch]$ApplyDeterministicEdits,
    [string[]]$SkipPhysicalCategories = @(),
    [long[]]$SkipPhysicalIds = @(),
    [string]$WitnessExclusionReason,
    [switch]$AllowPartialDurableRestore,
    [switch]$BusinessWorkflow,
    [switch]$RapidModifiedDelete,
    [ValidateRange(30,60)][int]$WorkAfterEditsSeconds = 45,
    [ValidateRange(10,30)][int]$BusinessSelectionCount = 16,
    [long[]]$DiagnosticPhysicalIds = @(),
    [switch]$IsolatedRestoreWorkspace
)
$ErrorActionPreference = 'Stop'
function Get-SharedSha256([string]$Path) {
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/history-stress/' + $RunName)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh RunName. Existing evidence is preserved.' }
New-Item -ItemType Directory -Path $output | Out-Null
$source = if ($SourceFile) { [IO.Path]::GetFullPath($SourceFile) } else { Join-Path $repo 'tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt' }
if($DurableCaptureRunName -and ($Scenario -ne 'durable' -or $SourceFile)) { throw 'DurableCaptureRunName requires Scenario=durable and uses the existing deleted-state checkpoint; do not supply SourceFile.' }
$captureRun = if($DurableCaptureRunName) { Join-Path $repo ('tmp/history-stress/' + $DurableCaptureRunName) } else { $null }
if($IsolatedRestoreWorkspace -and (!$DurableCaptureRunName -or !$AllowPartialDurableRestore -or !$DiagnosticPhysicalIds.Count)){throw 'Isolated diagnostic lanes require capture run, partial restore and explicit physical IDs.'}
$durablePhase = if($captureRun) { 'restore' } else { 'capture' }
if($AllowPartialDurableRestore -and !$captureRun) { throw 'Partial restoration requires an explicit capture run; it cannot be enabled for capture.' }
if(($BusinessWorkflow -or $DeletionDelaySeconds -gt 0 -or $ApplyDeterministicEdits) -and $Scenario -ne 'durable') { throw 'Delayed capture/edits require Scenario=durable.' }
if($RapidModifiedDelete -and ($Scenario -ne 'durable' -or $BusinessWorkflow -or $DeletionDelaySeconds -gt 0 -or $ApplyDeterministicEdits -or $AllowPartialDurableRestore -or $DiagnosticPhysicalIds.Count)) { throw 'Rapid modified/delete cycle requires Scenario=durable without delayed/business/partial/diagnostic options.' }
if($ApplyDeterministicEdits -and $DeletionDelaySeconds -lt 30) { throw 'Deterministic edits require a delay of at least30 seconds.' }
foreach($category in $SkipPhysicalCategories) { if($category -notmatch '^(?:-?[0-9]+|OST_[A-Za-z0-9_]+)$') { throw ('Invalid explicit category name/ID: '+$category) } }
if($Scenario -ne 'diagnostic' -and ($ReferenceInventoryFile -or $ReferenceOwnershipFile)) { throw 'Reference diagnostics require Scenario=diagnostic.' }
if(($ReferenceInventoryFile -and !$ReferenceOwnershipFile) -or ($ReferenceOwnershipFile -and !$ReferenceInventoryFile)) { throw 'Provide both the source inventory and ownership diagnostic references.' }
foreach($reference in @($ReferenceInventoryFile,$ReferenceOwnershipFile) | Where-Object { $_ }) { if(!(Test-Path -LiteralPath $reference -PathType Leaf)){ throw ('Missing diagnostic reference: '+$reference) } }
if($captureRun) {
    $closedName=if($AllowPartialDurableRestore) { 'partial-session-closed.json' } else { 'durable-session-closed.json' }
    $closedPath = Join-Path $captureRun $closedName
    if(!(Test-Path -LiteralPath $closedPath -PathType Leaf)) { throw 'Capture phase must finish conversion, closure and bridge removal before preparing phase two.' }
    $closed = Get-Content -LiteralPath $closedPath -Raw | ConvertFrom-Json
    if(!$closed.SessionClosed -or !$closed.Closed -or !$closed.OldDocumentInvalid -or !$closed.AbsentFromApplicationDocuments -or !$closed.AllBridgeFilesAbsent -or !$closed.SourceAndInitialCopyUnchanged) { throw 'Capture closure/integrity proof is incomplete.' }
    if(!$AllowPartialDurableRestore -and !$closed.ConversionComplete) { throw 'The full restore path requires complete conversion.' }
    if($AllowPartialDurableRestore -and (!$closed.ManualTestBridgeRemoval -or $closed.AutomaticCleanupVerified -or
        (Get-FileHash -LiteralPath (Join-Path $captureRun 'partial-subset.json') -Algorithm SHA256).Hash -ne $closed.SubsetManifestSha256)) { throw 'Partial mode requires matching subset and guarded manual bridge-removal proof.' }
    foreach($bridge in $closed.BridgeFiles) { if(Test-Path -LiteralPath $bridge) { throw ('Temporary bridge still exists: '+$bridge) } }
    $deletedProject = Join-Path $captureRun 'Antibes-supprime.rvt'
    if(!(Test-Path -LiteralPath $deletedProject -PathType Leaf) -or (Get-SharedSha256 $deletedProject) -ne $closed.DeletedProjectSha256) { throw 'Deleted-state project is missing or changed.' }
    $source = [IO.File]::ReadAllText((Join-Path $captureRun 'source-path.txt')).Trim()
    $sourceHash = [IO.File]::ReadAllText((Join-Path $captureRun 'source-sha256.txt')).Trim()
    if((Get-SharedSha256 $source) -ne $sourceHash) { throw 'Canonical test source changed after capture.' }
    if($IsolatedRestoreWorkspace) {
        $subset=Get-Content -LiteralPath (Join-Path $captureRun 'partial-subset.json') -Raw | ConvertFrom-Json
        if(@($DiagnosticPhysicalIds | Sort-Object -Unique).Count -ne $DiagnosticPhysicalIds.Count -or @($DiagnosticPhysicalIds | Where-Object {$_ -notin $subset.SelectedPhysicalIds}).Count){throw 'Diagnostic lane IDs must be a distinct subset of the frozen converted physical IDs.'}
        Copy-Item -LiteralPath $deletedProject -Destination (Join-Path $output 'Antibes-supprime.rvt')
        Copy-Item -LiteralPath (Join-Path $captureRun 'history') -Destination (Join-Path $output 'history') -Recurse
        if((Get-SharedSha256 (Join-Path $output 'Antibes-supprime.rvt')) -ne $closed.DeletedProjectSha256){throw 'Isolated lane checkpoint copy mismatch.'}
        $laneHistoryRoot = (Join-Path $output 'history').TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        @{AtUtc=[DateTime]::UtcNow.ToString('o');CaptureDirectory=$captureRun;CheckpointSha256=$closed.DeletedProjectSha256;HistoryFiles=@(Get-ChildItem -LiteralPath $laneHistoryRoot -Recurse -File | ForEach-Object {@{Path=$_.FullName.Substring($laneHistoryRoot.Length);Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'isolated-workspace-copy.json') -Encoding UTF8
    }
    # Capture baseline metadata stays read-only; each isolated lane owns its RVT/history copies.
    # No second full RVT copy is created for phase two.
    $captureConfig=Get-Content -LiteralPath (Join-Path $captureRun 'config.json') -Raw | ConvertFrom-Json
    $DeletionDelaySeconds=[int]$captureConfig.DeletionDelaySeconds
    $PhysicalTargetFraction=if($null -ne $captureConfig.PhysicalTargetFraction) { [double]$captureConfig.PhysicalTargetFraction } else { 1 }
    $ApplyDeterministicEdits=[bool]$captureConfig.ApplyDeterministicEdits
    $SkipPhysicalCategories=@($captureConfig.SkipPhysicalCategories | Where-Object { $_ })
    $SkipPhysicalIds=@($captureConfig.SkipPhysicalIds | Where-Object { $null -ne $_ })
    $WitnessExclusionReason=$captureConfig.WitnessExclusionReason
    $BusinessWorkflow=[bool]$captureConfig.BusinessWorkflow
    $RapidModifiedDelete=[bool]$captureConfig.RapidModifiedDelete
    if($BusinessWorkflow) { $WorkAfterEditsSeconds=[int]$captureConfig.WorkAfterEditsSeconds; $BusinessSelectionCount=[int]$captureConfig.BusinessSelectionCount }
} else {
    Copy-Item -LiteralPath $source -Destination (Join-Path $output 'Antibes-stress.rvt')
    $sourceHash = Get-SharedSha256 $source
    $copyHash = Get-SharedSha256 (Join-Path $output 'Antibes-stress.rvt')
    if ($sourceHash -ne $copyHash) { throw 'Copied file differs from source; preserve this incomplete run and prepare a fresh copy.' }
}
$sourceHash | Set-Content -LiteralPath (Join-Path $output 'source-sha256.txt')
[IO.File]::WriteAllText((Join-Path $output 'source-path.txt'), $source)
@{ Scenario=$Scenario; RunName=$RunName; PreparedTimeoutSeconds=$PreparedTimeoutSeconds; JournalTimeoutSeconds=$JournalTimeoutSeconds; ReferenceInventoryPath=if($ReferenceInventoryFile){[IO.Path]::GetFullPath($ReferenceInventoryFile)}else{$null}; ReferenceOwnershipPath=if($ReferenceOwnershipFile){[IO.Path]::GetFullPath($ReferenceOwnershipFile)}else{$null}; DurablePhase=$durablePhase; DurableCaptureDirectory=$captureRun; DeletionDelaySeconds=$DeletionDelaySeconds; PhysicalTargetFraction=$PhysicalTargetFraction; ApplyDeterministicEdits=[bool]$ApplyDeterministicEdits; SkipPhysicalCategories=$SkipPhysicalCategories; SkipPhysicalIds=$SkipPhysicalIds; WitnessExclusionReason=$WitnessExclusionReason; AllowPartialDurableRestore=[bool]$AllowPartialDurableRestore; BusinessWorkflow=[bool]$BusinessWorkflow; RapidModifiedDelete=[bool]$RapidModifiedDelete; WorkAfterEditsSeconds=$WorkAfterEditsSeconds; BusinessSelectionCount=$BusinessSelectionCount; DiagnosticPhysicalIds=$DiagnosticPhysicalIds; IsolatedRestoreWorkspace=[bool]$IsolatedRestoreWorkspace } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'config.json') -Encoding UTF8
$buildLog = Join-Path $output 'build.log'
$frozenSource = Join-Path $output 'frozen-source'
New-Item -ItemType Directory -Path $frozenSource | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cs','.csproj','.ps1','.md' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $frozenSource $_.Name) }
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe' (Join-Path $PSScriptRoot 'HistoryStress.csproj') /restore /t:Build "/p:ObserverOutput=$output/" /p:RestoreIgnoreFailedSources=true /p:UseSharedCompilation=false /verbosity:quiet *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 28; throw 'Stress observer compilation failed.' }
@{ AtUtc=[DateTime]::UtcNow.ToString('o'); AssemblySha256=(Get-FileHash -LiteralPath (Join-Path $output 'BIMaestro.History.Stress.dll') -Algorithm SHA256).Hash; SourceFiles=@(Get-ChildItem -LiteralPath $frozenSource -File | ForEach-Object { @{ Name=$_.Name; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'compiled-evidence.json') -Encoding UTF8
$assembly = [Security.SecurityElement]::Escape((Join-Path $output 'BIMaestro.History.Stress.dll'))
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro Antibes mass stress observer</Name><Assembly>' + $assembly + '</Assembly><AddInId>04A837C0-4FAD-426C-9F51-8EF8F521940C</AddInId><FullClassName>BIMaestro.StressTests.StressObserver</FullClassName><VendorId>BMTS</VendorId><VendorDescription>Isolated-copy mass deletion validation</VendorDescription></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'BIMaestro.History.Stress.addin'), $manifest)
Write-Output $output
