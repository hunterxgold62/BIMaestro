#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/history-stress')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
$run=[IO.Path]::GetFullPath($RunDirectory)
if(!$run.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Run outside exact workspace stress root.'}
$walk=$run
while($walk){$item=Get-Item -LiteralPath $walk -Force; if($item.LinkType -or $item.LinkTarget){throw 'Redirected run ancestor refused.'}; $parent=Split-Path -Parent $walk; if($parent -eq $walk){break}; $walk=$parent}
function Read([string]$name){$path=Join-Path $run $name; $item=Get-Item -LiteralPath $path; if($item.LinkType -or $item.LinkTarget){throw 'Redirected evidence refused.'}; return [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -Depth 100}
function Sha([string]$path){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function True($o,[string]$name){if($o[$name] -isnot [bool] -or !$o[$name]){throw ('Missing true proof: '+$name)}}
$proof=Read 'partial-close-ready.json'; $removal=Read 'partial-bridge-removal.json'; $exitProof=Read 'isolated-process-stopped.json'; $ready=Read 'ready.json'; $subset=Read 'partial-subset.json'; $deletion=Read 'deletion.json'; $inventory=Read 'inventory.json'
foreach($flag in 'ProjectClosed','OldDocumentInvalid','AbsentFromApplicationDocuments','SourcesDisposed','ConverterPaused','RequestsFrozen','SourceAndInitialCopyUnchanged'){True $proof $flag}
foreach($flag in 'Removed','AbsentAfter','ManualTestBridgeRemoval','CaptureProcessExited'){True $removal $flag}
True $exitProof 'GuardedStop'
if($removal.AutomaticCleanupVerified -ne $false -or $removal.FullRestoreVerified -ne $false){throw 'Manual removal cannot claim automatic cleanup or full fidelity.'}
$proofPath=Join-Path $run 'partial-close-ready.json'
if($proof.CapturePid -ne $ready.Pid -or $exitProof.Pid -ne $proof.CapturePid -or $removal.CapturePid -ne $proof.CapturePid -or
    ![string]::Equals([IO.Path]::GetFullPath($exitProof.ClosureProof),$proofPath,[StringComparison]::OrdinalIgnoreCase)){throw 'Capture PID/closure mismatch.'}
if([Diagnostics.Process]::GetProcesses() | Where-Object Id -eq $proof.CapturePid){throw 'Capture process still exists; external finalization refused.'}
if($removal.ProcessExitProofSha256 -ne (Sha (Join-Path $run 'isolated-process-stopped.json')) -or $removal.ProofSha256 -ne (Sha $proofPath) -or
    $proof.SubsetManifestSha256 -ne (Sha (Join-Path $run 'partial-subset.json')) -or $removal.BridgeFile -ne $proof.BridgeFile -or
    [IO.File]::Exists($proof.BridgeFile)){throw 'Removal, frozen subset or absent-bridge evidence mismatch.'}
foreach($field in 'SourcePath','InitialCopyPath','DeletedProject'){if(![IO.Path]::IsPathFullyQualified($proof[$field])){throw 'Absolute protected paths required.'}}
if($proof.InitialCopyPath -ne (Join-Path $run 'Antibes-stress.rvt') -or $proof.DeletedProject -ne (Join-Path $run 'Antibes-supprime.rvt')){throw 'Checkpoint paths outside exact capture run.'}
if((Sha $proof.SourcePath) -ne $proof.SourceSha256Before -or (Sha $proof.InitialCopyPath) -ne $proof.SourceSha256Before -or (Sha $proof.DeletedProject) -ne $proof.DeletedProjectSha256){throw 'Protected model bytes changed.'}
if($ready.Sha256 -ne $proof.ProductSha256){throw 'Product identity differs from the actual loaded assembly.'}
$events=@($subset.Records | Where-Object Included -eq $true)
if(!$events.Count -or @($events.ElementId | Sort-Object -Unique).Count -ne $events.Count -or @($events.EventId | Sort-Object -Unique).Count -ne $events.Count){throw 'Subset is empty or non-injective.'}
if($subset.FullDeletedPhysicalIds.Count -ne $deletion.DeletedPhysicalIds.Count -or @($subset.FullDeletedPhysicalIds | Where-Object {$_ -notin $deletion.DeletedPhysicalIds}).Count){throw 'Original physical denominator changed.'}
$opened=$null
foreach($line in [IO.File]::ReadLines((Join-Path $run 'observer.jsonl'))){$row=$line | ConvertFrom-Json -AsHashtable -Depth 16; if($row.Kind -eq 'document_opened' -and $row.Data.HistoryDocumentKey -eq $inventory.StateBefore.Key){$opened=$row; break}}
if(!$opened -or !$opened.Data.HistoryDocumentKey -or $opened.Data.HistoryDocumentKey -ne $inventory.StateBefore.Key -or $ready.Model -ne $proof.InitialCopyPath){throw 'Original document-open/key evidence unavailable.'}
$result=[ordered]@{
    AtUtc=[DateTime]::UtcNow.ToString('O'); CapturePid=$proof.CapturePid; ProductSha256=$proof.ProductSha256
    DocumentKey=$opened.Data.HistoryDocumentKey; OpenedUtc=$opened.AtUtc; Transaction=$deletion.Transaction
    Closed=$true; OldDocumentInvalid=$true; AbsentFromApplicationDocuments=$true; SessionClosed=$true
    ConversionComplete=$false; ManualTestBridgeRemoval=$true; AutomaticCleanupVerified=$false; FullRestoreVerified=$false
    AutomaticPreparationPaused=$false; CaptureProcessExited=$true; ExternalFinalizer=$true
    BridgeFiles=@($proof.BridgeFile); DeletedBridgeFiles=@($proof.BridgeFile); AllBridgeFilesAbsent=$true
    DeletedProject=$proof.DeletedProject; DeletedProjectSha256=$proof.DeletedProjectSha256
    SourceSha256Before=$proof.SourceSha256Before; SourceSha256After=(Sha $proof.SourcePath); InitialCopySha256=(Sha $proof.InitialCopyPath); SourceAndInitialCopyUnchanged=$true
    ExpectedDeletionEvents=$subset.FullDeletionEventCount; FullDeletedPhysicalCount=$subset.FullDeletedPhysicalIds.Count; SubsetPhysicalCount=$events.Count
    SubsetManifestSha256=(Sha (Join-Path $run 'partial-subset.json')); RemovalProofSha256=(Sha (Join-Path $run 'partial-bridge-removal.json'))
    ProcessExitProofSha256=(Sha (Join-Path $run 'isolated-process-stopped.json')); Status=$subset.ConversionStatus
    Note='Loaded observer v1 cannot pause automatic preparation. The exact capture process exited after actual project closure and before guarded manual bridge removal. Full conversion and automatic cleanup remain unverified.'
}
$out=Join-Path $run 'partial-session-closed.json'
if([IO.File]::Exists($out)){throw 'Existing closure evidence preserved; no overwrite.'}
$temp=$out+'.partial'; [IO.File]::WriteAllText($temp,($result | ConvertTo-Json -Depth 32),[Text.UTF8Encoding]::new($false)); [IO.File]::Move($temp,$out)
Write-Output $out
