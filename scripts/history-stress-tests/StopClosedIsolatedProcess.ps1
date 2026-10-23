param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run=Join-Path $repo ('tmp/history-stress/'+$RunName)
$readyPath=Join-Path $run 'ready.json'
$ready=Get-Content -LiteralPath $readyPath -Raw|ConvertFrom-Json
$proofPath=Join-Path $run 'finished.json'
if(!(Test-Path -LiteralPath $proofPath)){$proofPath=Join-Path $run 'durable-session-closed.json'}
if(!(Test-Path -LiteralPath $proofPath)){$proofPath=Join-Path $run 'partial-close-ready.json'}
$proof=Get-Content -LiteralPath $proofPath -Raw|ConvertFrom-Json
$partial=[IO.Path]::GetFileName($proofPath) -eq 'partial-close-ready.json'
$closed=if($partial){$proof.ProjectClosed}else{$proof.Closed}
if(!$closed -or !$proof.OldDocumentInvalid -or !$proof.AbsentFromApplicationDocuments -or !($proof.CopyAndSourceUnchanged -or $proof.SourceAndInitialCopyUnchanged)){throw 'No verified isolated-document closure/integrity proof.'}
if($partial -and (!$proof.SourcesDisposed -or !$proof.RequestsFrozen -or $proof.CapturePid -ne $ready.Pid -or
    (Get-FileHash -LiteralPath (Join-Path $run 'Antibes-supprime.rvt')).Hash -ne $proof.DeletedProjectSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $run 'partial-subset.json')).Hash -ne $proof.SubsetManifestSha256)){throw 'Partial checkpoint/source closure proof is incomplete or changed.'}
$process=Get-Process -Id $ready.Pid
$started=$process.StartTime.ToUniversalTime();$readyAt=(Get-Item -LiteralPath $readyPath).LastWriteTimeUtc
$pidMarker=Join-Path $run 'observer-pid.txt'
if(Test-Path -LiteralPath $pidMarker){
    $launchPid=[int][IO.File]::ReadAllText($pidMarker).Trim()
    $markedAt=(Get-Item -LiteralPath $pidMarker).LastWriteTimeUtc
    # A user may spend more than five minutes on Revit's unsigned-addin prompt.
    # Match the launch-time PID marker, not the later readiness delay.
    if($launchPid -ne $ready.Pid -or $markedAt -lt $started -or ($markedAt-$started).TotalSeconds -gt 30 -or $started -gt $readyAt){throw 'Process identity/time differs from the exact launch marker.'}
} elseif($started -gt $readyAt -or ($readyAt-$started).TotalMinutes -gt 5){throw 'Process identity/time differs from launched test.'}
if($process.MainWindowHandle.ToInt64() -ne $ready.MainWindowHandle -or $process.MainWindowTitle -match 'Projet_AntibesV2|SIGMA'){throw 'Refusing to stop a different or original-project window.'}
$title=$process.MainWindowTitle
Stop-Process -Id $process.Id
[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');Pid=$ready.Pid;StartedUtc=$started;Window=$ready.MainWindowHandle;Title=$title;GuardedStop=$true;ClosureProof=$proofPath}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'isolated-process-stopped.json') -Encoding UTF8
