param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run=Join-Path $repo ('tmp/history-stress/'+$RunName)
$ready=Get-Content -LiteralPath (Join-Path $run 'ready.json') -Raw|ConvertFrom-Json
$launch=Get-Content -LiteralPath (Join-Path $run 'launch-validation.json') -Raw|ConvertFrom-Json
$heartbeat=Get-Content -LiteralPath (Join-Path $run 'heartbeat.json') -Raw|ConvertFrom-Json
if($launch.Pid -ne $ready.Pid -or $launch.ProductMatches -ne $false -or $launch.OpenAllowed -ne $false -or
    $heartbeat.Phase -ne 'waiting-open' -or (Test-Path -LiteralPath (Join-Path $run 'open.request'))){throw 'This is not an unopened rejected launch.'}
foreach($line in [IO.File]::ReadLines((Join-Path $run 'observer.jsonl'))){
    $row=$line|ConvertFrom-Json
    if($row.Kind -in 'open_begin','document_opened','restored_document_opened'){throw 'An open was attempted; normal closure proof is required.'}
}
$process=Get-Process -Id $ready.Pid
$started=$process.StartTime.ToUniversalTime();$readyAt=(Get-Item -LiteralPath (Join-Path $run 'ready.json')).LastWriteTimeUtc
if($process.ProcessName -ne 'Revit' -or $started -gt $readyAt -or ($readyAt-$started).TotalMinutes -gt 5 -or
    $process.MainWindowHandle.ToInt64() -ne $ready.MainWindowHandle -or $process.MainWindowTitle -notmatch '^Autodesk Revit 2025(?:\.[0-9]+)*(?:\s*-\s*\[(?:Début|Accueil|Home|Start)\])?$'){
    throw ('Process/window is not the exact empty isolated Revit: '+$process.MainWindowTitle)
}
$title=$process.MainWindowTitle
Stop-Process -Id $process.Id
[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');Pid=$ready.Pid;StartedUtc=$started;Title=$title;GuardedStop=$true;ModelNeverOpened=$true;RejectedProductSha256=$ready.Sha256}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'unopened-process-stopped.json') -Encoding UTF8
