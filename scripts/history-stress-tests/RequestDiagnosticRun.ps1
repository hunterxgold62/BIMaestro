param(
    [Parameter(Mandatory)][ValidatePattern('^antibes-(diag|business)-[a-zA-Z0-9-]+$')][string]$RunName,
    [Parameter(Mandatory)][ValidateSet('restore','persist','finish','durable-diagnostics','persist-subset')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run=Join-Path $repo ('tmp/history-stress/'+$RunName)
$ready=Get-Content -LiteralPath (Join-Path $run 'ready.json') -Raw | ConvertFrom-Json
$heartbeat=Get-Content -LiteralPath (Join-Path $run 'heartbeat.json') -Raw | ConvertFrom-Json
$config=Get-Content -LiteralPath (Join-Path $run 'config.json') -Raw | ConvertFrom-Json
if($ready.Sha256 -ne $ExpectedSha256){throw 'Wrong product loaded.'}
$lane=$config.IsolatedRestoreWorkspace -and @($config.DiagnosticPhysicalIds).Count -gt 0
$business=$config.BusinessWorkflow -and $config.Scenario -eq 'durable'
if(!$lane -and !$business){throw 'Not an authorized isolated diagnostic or business run.'}
$boundary=[IO.Path]::GetFullPath($run).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(![IO.Path]::GetFullPath($ready.Model).StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Model is outside the isolated run.'}
if($Action -eq 'restore' -and $heartbeat.Phase -ne 'await-restore'){throw 'Model is not ready to restore.'}
if($Action -eq 'persist' -and $heartbeat.Phase -ne 'restored'){throw 'Restoration has not completed.'}
if($Action -eq 'persist-subset'){
    if(!$business -or $config.DurablePhase -ne 'capture' -or $heartbeat.Phase -ne 'await-restore'){throw 'Business capture has not reached a stable deleted journal.'}
    $readiness=Get-Content -LiteralPath (Join-Path $run 'business-readiness.json') -Raw|ConvertFrom-Json
    if(!$readiness.ExactDeletedEventCoverage -or (!$readiness.AllTerminal -and !$readiness.TimedOut) -or $readiness.PhysicalReadyCount -eq 0){throw 'Natural preparation has not reached a testable stable subset.'}
}
if($Action -eq 'finish' -and $heartbeat.Phase -notin @('restored','reopened-verified','await-restore')){throw 'Finish is not permitted during an active operation.'}
$request=Join-Path $run ($Action+'.request')
if(Test-Path -LiteralPath $request){throw 'Request already exists; inspect the observer before retrying.'}
[DateTime]::UtcNow.ToString('o') | Set-Content -LiteralPath $request -Encoding UTF8
[ordered]@{RunName=$RunName;Pid=$ready.Pid;Action=$Action;Phase=$heartbeat.Phase} | ConvertTo-Json -Compress
