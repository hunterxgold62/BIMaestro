param(
 [ValidateSet('2023','2024','2025','2026')][string[]]$RevitVersions=@('2023','2024','2025','2026'),
 [ValidateRange(1,60)][int]$TimeoutSeconds=15
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo 'tmp/home-tests/matrix-results.json'
$records=[Collections.Generic.List[object]]::new()
$failures=[Collections.Generic.List[string]]::new()
$started=[DateTime]::UtcNow.ToString('O')

function Save-Evidence {
 $report=[ordered]@{startedUtc=$started;updatedUtc=[DateTime]::UtcNow.ToString('O');versions=$RevitVersions;results=@($records.ToArray());failures=@($failures.ToArray());passed=($records.Count -eq ($RevitVersions.Count*5) -and $failures.Count -eq 0)}
 [IO.File]::WriteAllText($output,($report|ConvertTo-Json -Depth 50))
}
function Decode-Result([string]$raw) {
 $decoded=$raw
 for($level=0;$level -lt 5;$level++) {
  if($decoded -isnot [string]) { return $decoded }
  $decoded=ConvertFrom-Json -InputObject $decoded -ErrorAction Stop
 }
 if($decoded -is [string]) {throw 'Result remains JSON-encoded after five decoding passes.'}
 return $decoded
}

foreach($version in $RevitVersions) {
 if(!(Test-Path -LiteralPath (Join-Path $repo "tmp/home-tests/$version"))) {throw "Missing probe directory for Revit $version"}
}
foreach($preset in @('compact','dark','blueprint','large','restore')) {
 $batch=[Collections.Generic.List[object]]::new()
 foreach($version in $RevitVersions) {
  & (Join-Path $PSScriptRoot 'Set-Experiment.ps1') -RevitVersion $version -Preset $preset
  $control=Join-Path $repo "tmp/home-tests/$version/control.js"
  $token=[IO.File]::GetLastWriteTimeUtc($control).Ticks.ToString()
  $batch.Add([ordered]@{version=$version;preset=$preset;token=$token;resultPath=(Join-Path $repo "tmp/home-tests/$version/result-$token.txt");queuedUtc=[DateTime]::UtcNow.ToString('O')})
 }
 $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
 do {
  $missing=@($batch|Where-Object {!(Test-Path -LiteralPath $_.resultPath)})
  if($missing.Count -eq 0) {break}
  Start-Sleep -Milliseconds 200
 } while([DateTime]::UtcNow -lt $deadline)
 foreach($request in $batch) {
  $entry=[ordered]@{version=$request.version;preset=$preset;token=$request.token;resultPath=$request.resultPath;queuedUtc=$request.queuedUtc;raw=$null;decoded=$null;passed=$false;errors=@()}
  $errors=[Collections.Generic.List[string]]::new()
  try {
   if(!(Test-Path -LiteralPath $request.resultPath)) {throw "No fresh result within $TimeoutSeconds seconds"}
   $entry.raw=[IO.File]::ReadAllText($request.resultPath)
   $entry.decoded=Decode-Result $entry.raw
   $evidence=if($preset -eq 'restore'){$entry.decoded}else{$entry.decoded.evidence}
   if($null -eq $evidence) {throw 'Result has no evidence object'}
   if($preset -eq 'restore') {
    if($evidence.styles -ne $false) {$errors.Add('Restoration left styles present or styles evidence absent')}
    if($null -eq $evidence.PSObject.Properties['preset']) {$errors.Add('Restoration preset evidence absent')}
    elseif($null -ne $evidence.preset) {$errors.Add('Restoration left a preset marker')}
   } else {
    if($entry.decoded.applied -ne $true) {$errors.Add('Preset was not applied')}
    if($evidence.cards -le 0) {$errors.Add('No Home cards detected')}
    if($evidence.preset -ne $preset) {$errors.Add('Preset marker does not match requested preset')}
    if($evidence.styles -ne $true) {$errors.Add('Injected stylesheet absent')}
    if($null -eq $evidence.PSObject.Properties['titleDateOverlaps']) {$errors.Add('Missing title/date overlap evidence')}
    elseif(@($evidence.titleDateOverlaps).Count -ne 0) {$errors.Add('Title/date overlaps detected')}
   }
  } catch {$errors.Add($_.Exception.Message)}
  $entry.errors=@($errors.ToArray())
  $entry.passed=($errors.Count -eq 0)
  $records.Add($entry)
  if(!$entry.passed) {$failures.Add("Revit $($request.version) / ${preset}: $($errors -join '; ')")}
 }
 Save-Evidence
}
if($failures.Count -gt 0) {throw "Home matrix failed. See ${output}: $($failures -join ' | ')"}
Write-Output "Home matrix verified: $($records.Count) results. Evidence: $output"
