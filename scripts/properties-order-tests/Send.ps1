param([string]$Run='2025-r4',[Parameter(Mandatory=$true)][string]$Command,[string]$Evidence='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo "tmp/properties-order-tests/$Run"
$control=Join-Path $out 'control.txt'
if(Test-Path -LiteralPath $control){throw 'Previous command still pending'}
$started=[DateTime]::UtcNow
[IO.File]::WriteAllText($control,$Command)
$report=Join-Path $out "report-$Command.json"
for($attempt=0;$attempt -lt 120;$attempt++){
 Start-Sleep -Milliseconds 200
 if((Test-Path -LiteralPath $report) -and (Get-Item -LiteralPath $report).LastWriteTimeUtc -ge $started){
  $data=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
  if($Evidence){Copy-Item -LiteralPath $report -Destination (Join-Path $out "$Evidence.json")}
  $data | Select-Object command,version,status,layoutStatus,hasApplied,hash | ConvertTo-Json
  exit 0
 }
 $errorFile=Join-Path $out 'error.json'
 if((Test-Path -LiteralPath $errorFile) -and (Get-Item -LiteralPath $errorFile).LastWriteTimeUtc -ge $started){Get-Content -LiteralPath $errorFile;exit 1}
}
throw "Command timed out: $Run $Command"
