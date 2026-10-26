param([string]$Run='2025-r4',[Parameter(Mandatory=$true)][string]$Command,[string]$Evidence='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/properties-custom-group-tests/$Run"
$control=Join-Path $out 'control.txt'
if(Test-Path -LiteralPath $control){throw 'Previous command still pending'}
$started=[DateTime]::UtcNow
$pending=Join-Path $out ('control-'+[guid]::NewGuid().ToString('N')+'.pending')
[IO.File]::WriteAllText($pending,$Command)
[IO.File]::Move($pending,$control)
$report=Join-Path $out "report-$Command.json"
if($Command -eq 'fixture'){$report=Join-Path $out 'fixture.json'}
elseif($Command -eq 'shutdown-fixture'){$report=Join-Path $out 'shutdown-requested.json'}
for($attempt=0;$attempt -lt 600;$attempt++){
 Start-Sleep -Milliseconds 200
 if((Test-Path -LiteralPath $report) -and (Get-Item -LiteralPath $report).LastWriteTimeUtc -ge $started){
  try{$data=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json}catch{continue}
  if($Evidence){Copy-Item -LiteralPath $report -Destination (Join-Path $out "$Evidence.json")}
  $data | Select-Object command,version,status,layoutStatus,hasApplied,hash | ConvertTo-Json
  exit 0
 }
 $errorFile=Join-Path $out 'error.json'
 if((Test-Path -LiteralPath $errorFile) -and (Get-Item -LiteralPath $errorFile).LastWriteTimeUtc -ge $started){Get-Content -LiteralPath $errorFile;exit 1}
}
throw "Command timed out: $Run $Command"
