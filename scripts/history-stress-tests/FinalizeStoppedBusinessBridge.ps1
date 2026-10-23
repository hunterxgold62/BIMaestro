param([Parameter(Mandatory)][ValidatePattern('^antibes-business-capture-[a-zA-Z0-9-]+$')][string]$RunName)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run=Join-Path $repo ('tmp/history-stress/'+$RunName)
function ReadProof([string]$name){ Get-Content -LiteralPath (Join-Path $run $name) -Raw|ConvertFrom-Json }
function Sha([string]$path){ (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function AssertTrue($o,[string]$name){ if($o.$name -isnot [bool] -or !$o.$name){throw ('Missing true proof: '+$name)} }
function NoRedirect([string]$path){
    $walk=[IO.Path]::GetFullPath($path)
    while($walk){
        $item=Get-Item -LiteralPath $walk -Force -ErrorAction SilentlyContinue
        # OneDrive cloud placeholders also carry ReparsePoint. Refuse actual
        # links/junctions, as opposed to ordinary cloud-backed workspace files.
        if($item -and ($item.LinkType -or $item.LinkTarget -or @($item.Target|Where-Object {$_}).Count)){throw 'Redirected path refused.'}
        $parent=Split-Path -Parent $walk
        if($parent -eq $walk){break};$walk=$parent
    }
}
NoRedirect $run
$proof=ReadProof 'partial-close-ready.json';$ready=ReadProof 'ready.json';$exitProof=ReadProof 'isolated-process-stopped.json'
$subset=ReadProof 'partial-subset.json';$deletion=ReadProof 'deletion.json';$inventory=ReadProof 'inventory.json'
foreach($flag in 'ProjectClosed','OldDocumentInvalid','AbsentFromApplicationDocuments','SourcesDisposed','ConverterPaused','RequestsFrozen','SourceAndInitialCopyUnchanged'){AssertTrue $proof $flag}
AssertTrue $exitProof 'GuardedStop'
$proofPath=Join-Path $run 'partial-close-ready.json'
if($ready.Pid -ne $proof.CapturePid -or $exitProof.Pid -ne $proof.CapturePid -or
    [IO.Path]::GetFullPath($exitProof.ClosureProof) -ne $proofPath){throw 'Stopped process does not match this capture.'}
if([Diagnostics.Process]::GetProcesses()|Where-Object Id -eq $proof.CapturePid){throw 'Capture process is still alive.'}
if($proof.SessionId -notmatch '^[a-fA-F0-9]{32}$'){throw 'Invalid session identity.'}
$cache=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/HistoryBridge'
$bridge=[IO.Path]::GetFullPath($proof.BridgeFile)
$expectedBridge=[IO.Path]::GetFullPath((Join-Path $cache ('snapshot-opening-'+$proof.SessionId+'.rvt')))
if($bridge -ne $expectedBridge){throw 'Bridge is outside the exact local test cache.'}
$manifestPath=Join-Path $run ('history/history-data/sessions/'+$proof.SessionId+'.json')
if([IO.Path]::GetFullPath($proof.SessionManifestPath) -ne $manifestPath){throw 'Session manifest is outside this run.'}
NoRedirect $bridge;NoRedirect $manifestPath
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
AssertTrue $manifest 'Closed'
if($manifest.Id -cne $proof.SessionId -or $manifest.ProcessId -ne $proof.CapturePid -or
    $manifest.BridgeFile -ne $bridge -or (Sha $manifestPath) -ne $proof.SessionManifestSha256){throw 'Frozen bridge session changed.'}
$canonical=Join-Path $repo 'tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt'
if($proof.SourcePath -ne $canonical -or $proof.InitialCopyPath -ne (Join-Path $run 'Antibes-stress.rvt') -or
    $proof.DeletedProject -ne (Join-Path $run 'Antibes-supprime.rvt') -or $ready.Model -ne $proof.InitialCopyPath){throw 'Protected model paths differ from the isolated test.'}
foreach($path in $canonical,$proof.InitialCopyPath,$proof.DeletedProject){NoRedirect $path}
if((Sha $canonical) -ne $proof.SourceSha256Before -or (Sha $proof.InitialCopyPath) -ne $proof.SourceSha256Before -or
    (Sha $proof.DeletedProject) -ne $proof.DeletedProjectSha256 -or
    (Sha (Join-Path $run 'partial-subset.json')) -ne $proof.SubsetManifestSha256 -or $ready.Sha256 -ne $proof.ProductSha256){throw 'Capture integrity evidence changed.'}
$events=@($subset.Records|Where-Object Included -eq $true)
if(!$events.Count -or @($events.ElementId|Sort-Object -Unique).Count -ne $events.Count -or @($events.EventId|Sort-Object -Unique).Count -ne $events.Count){throw 'Empty or non-injective subset.'}
if($subset.FullDeletedPhysicalIds.Count -ne $deletion.DeletedPhysicalIds.Count -or
    @($subset.FullDeletedPhysicalIds|Where-Object {$_ -notin $deletion.DeletedPhysicalIds}).Count){throw 'Original physical denominator changed.'}
$opened=$null
foreach($line in [IO.File]::ReadLines((Join-Path $run 'observer.jsonl'))){
    if($line -notmatch '"Kind":"document_opened"'){continue}
    $row=$line|ConvertFrom-Json
    if($row.Data.HistoryDocumentKey -eq $inventory.StateBefore.Key){$opened=$row;break}
}
if(!$opened -or !$opened.Data.HistoryDocumentKey){throw 'Original document identity is unavailable.'}
$removalPath=Join-Path $run 'partial-bridge-removal.json';$closedPath=Join-Path $run 'partial-session-closed.json'
if((Test-Path -LiteralPath $removalPath) -or (Test-Path -LiteralPath $closedPath)){throw 'Existing evidence is preserved.'}
$before=Get-Item -LiteralPath $bridge
if($before.PSIsContainer -or $before.Length -le 0 -or (Sha $bridge) -ne $proof.BridgeSha256){throw 'Bridge bytes differ from the frozen closure proof.'}
$lease=[IO.FileStream]::new($bridge+'.lease',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    if((Sha $manifestPath) -ne $proof.SessionManifestSha256 -or (Sha $bridge) -ne $proof.BridgeSha256 -or
        ([Diagnostics.Process]::GetProcesses()|Where-Object Id -eq $proof.CapturePid)){throw 'State changed before removal.'}
    Remove-Item -LiteralPath $bridge -ErrorAction Stop
}finally{$lease.Dispose()}
if(Test-Path -LiteralPath $bridge){throw 'Bridge is still present.'}
$removal=[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');CapturePid=$proof.CapturePid;SessionId=$proof.SessionId;BridgeFile=$bridge
    BridgeSha256Before=$proof.BridgeSha256;BridgeLengthBefore=$before.Length;ProofSha256=(Sha $proofPath)
    SessionManifestSha256=$proof.SessionManifestSha256;ProcessExitProofSha256=(Sha (Join-Path $run 'isolated-process-stopped.json'))
    Removed=$true;AbsentAfter=$true;CaptureProcessExited=$true;ManualTestBridgeRemoval=$true;AutomaticCleanupVerified=$false;FullRestoreVerified=$false}
$removal|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $removalPath -Encoding UTF8
$closed=[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');CapturePid=$proof.CapturePid;ProductSha256=$proof.ProductSha256
    DocumentKey=$opened.Data.HistoryDocumentKey;OpenedUtc=$opened.AtUtc;Transaction=$deletion.Transaction
    Closed=$true;OldDocumentInvalid=$true;AbsentFromApplicationDocuments=$true;SessionClosed=$true;ConversionComplete=$false
    ManualTestBridgeRemoval=$true;AutomaticCleanupVerified=$false;FullRestoreVerified=$false;CaptureProcessExited=$true;ExternalFinalizer=$true
    AutomaticPreparationPaused=$proof.AutomaticPreparationPaused;BridgeFiles=@($bridge);DeletedBridgeFiles=@($bridge);AllBridgeFilesAbsent=$true
    DeletedProject=$proof.DeletedProject;DeletedProjectSha256=$proof.DeletedProjectSha256;SourceSha256Before=$proof.SourceSha256Before
    SourceSha256After=(Sha $canonical);InitialCopySha256=(Sha $proof.InitialCopyPath);SourceAndInitialCopyUnchanged=$true
    ExpectedDeletionEvents=$subset.FullDeletionEventCount;FullDeletedPhysicalCount=$subset.FullDeletedPhysicalIds.Count;SubsetPhysicalCount=$events.Count
    SubsetManifestSha256=(Sha (Join-Path $run 'partial-subset.json'));RemovalProofSha256=(Sha $removalPath)
    ProcessExitProofSha256=(Sha (Join-Path $run 'isolated-process-stopped.json'));Status=$subset.ConversionStatus
    Note='Guarded manual removal of one local bridge after actual document closure and process exit; no automatic-cleanup or full-restoration claim.'}
$closed|ConvertTo-Json -Depth 24|Set-Content -LiteralPath $closedPath -Encoding UTF8
[ordered]@{RunName=$RunName;BridgeRemoved=$true;RestorableSubset=$events.Count;FullPhysicalDenominator=$subset.FullDeletedPhysicalIds.Count;AutomaticCleanupVerified=$false}|ConvertTo-Json -Compress
