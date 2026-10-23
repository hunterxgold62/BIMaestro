#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tool=Join-Path $PSScriptRoot 'TestHistoryDurability.ps1'
$root=Join-Path $repo ('tmp/history-durability-fixtures/'+[guid]::NewGuid().ToString('N'))
$reports=Join-Path $root 'reports';$null=[IO.Directory]::CreateDirectory($reports)
$pwsh=(Get-Process -Id $PID).Path
$assertions=0
function Check([bool]$Condition,[string]$Message) {$script:assertions++;if(!$Condition){throw $Message}}
function New-History([string]$Name) {$path=Join-Path $root ($Name+'/history');$null=[IO.Directory]::CreateDirectory($path);return $path}
function Event($Recipe,[string]$Action='delete',[string]$Batch='wanted') {
    return @{Ts='2026-10-22T16:15:00.9403538Z';ElementId=7;UniqueId='original-7';ModelKey='fixture-model';ModelGuid='42';Action=$Action;Tx='fixture';Delta=@{recipe=$Recipe};DeletionAudit=@{BatchId=$Batch}}
}
function Direct-Recipe {return @{Version=1;Kind='family';Type='type-a';Level='level-a';Host='host-a';Network=@{SystemType='system-a'};Parameters=@(@{Reference='material-a';Definition='parameter-a';Shared='shared-a'})}}
function Write-Journal([string]$History,$Events,[bool]$Gzip=$false) {
    $text=(@($Events|ForEach-Object {$_|ConvertTo-Json -Compress -Depth 20}) -join "`n")+"`n"
    $path=Join-Path $History 'element-history-2026-10-01.jsonl'
    if(!$Gzip) {[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false));return $path}
    $path+='.gz';$stream=[IO.File]::Create($path)
    $zip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionLevel]::Optimal)
    $writer=[IO.StreamWriter]::new($zip,[Text.UTF8Encoding]::new($false));$writer.Write($text);$writer.Dispose();return $path
}
function Run-Case([string]$Name,[string]$History,[string[]]$Batch=@()) {
    $before=@(Get-ChildItem -LiteralPath $History -File -Recurse|ForEach-Object { [pscustomobject]@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    $out=Join-Path $reports ($Name+'.json');$log=Join-Path $reports ($Name+'.log')
    $args=@('-NoLogo','-NoProfile','-File',$tool,'-HistoryDirectory',$History,'-OutputPath',$out)
    if($Batch.Count) {$args+=@('-DeletionBatchId')+$Batch}
    & $pwsh @args *> $log
    $code=$LASTEXITCODE
    if(!(Test-Path -LiteralPath $out)) {Get-Content $log;throw "No diagnostic report was produced for $Name"}
    $result=Get-Content -LiteralPath $out -Raw|ConvertFrom-Json -AsHashtable
    Check ($code -eq 1 -and $result.Failed -eq $true -and !$result.AutonomyDemonstrated) "$Name must exit nonzero: this production schema does not prove autonomous definitions."
    $after=@(Get-ChildItem -LiteralPath $History -File -Recurse)
    Check ($before.Count -eq $after.Count) "$Name modified the input file inventory."
    foreach($file in $before) {Check ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -eq $file.Hash) "$Name modified an input journal or RVT."}
    return $result
}
$plain=New-History 'direct-plain';$null=Write-Journal $plain @((Event (Direct-Recipe)))
$one=Run-Case 'direct-plain' $plain
Check ($one.Summary.DirectRecipes -eq 1 -and $one.FormatIssues.Count -eq 0 -and $one.FileIssues.Count -eq 0) 'A real v1 direct recipe must parse cleanly, rather than failing because of an unrelated schema error.'
foreach($kind in @('Type','Level','Host','SystemType','ParameterReference','ParameterDefinition','SharedParameterDefinition')) {Check (@($one.ProjectReferenceSummary|Where-Object Kind -eq $kind).Count -eq 1) "Missing unfrozen reference category $kind"}
$gzip=New-History 'direct-gzip';$null=Write-Journal $gzip @((Event (Direct-Recipe))) $true
$compressed=Run-Case 'direct-gzip' $gzip
Check ($compressed.Summary.GzipFiles -eq 1 -and $compressed.Summary.DirectRecipes -eq $one.Summary.DirectRecipes -and $compressed.ProjectReferencesWithoutFrozenDefinitions.Count -eq $one.ProjectReferencesWithoutFrozenDefinitions.Count) 'Gzip must retain the same recipe dependencies.'
Check ($compressed.Criterion.GzipCompressionIsConversion -eq $false) 'Compression must never be called durable conversion.'
$native=New-History 'external-rvt';$nativeDir=Join-Path $native 'native-history';$null=[IO.Directory]::CreateDirectory($nativeDir)
[IO.File]::WriteAllBytes((Join-Path $nativeDir 'snapshot-fixture.rvt'),[byte[]](1,2,3,4))
$artifact=@{File='Z:/old-store/native-history/snapshot-fixture.rvt';Ready=$true}
$backup=@{File=$artifact.File;Ready=$true;WholeModel=$true}
$null=Write-Journal $native @((Event @{Version=1;Kind='native_archive';Native=$artifact;OpeningFallback=$backup}))
$external=Run-Case 'external-rvt' $native
Check ($external.Summary.NativeReferences -eq 1 -and $external.Summary.OpeningFallbackReferences -eq 1 -and $external.Summary.WholeModelCheckpointReferences -eq 1) 'Native and fallback references must both be counted.'
Check ($external.RvtSummary.DistinctPersistedReferences -eq 1 -and $external.RvtSummary.AccessibleCurrentStoreBytes -eq 4 -and $external.RvtSummary.AccessibleDistinctCurrentStoreFiles -eq 1) 'Shared references must not multiply the RVT byte count, and relocation must follow the production store/basename rule.'
$missing=New-History 'missing-rvt';$null=Write-Journal $missing @((Event @{Version=1;Kind='native_archive';Native=@{File='snapshot-absent.rvt';Ready=$true;WholeModel=$true}}))
$missingResult=Run-Case 'missing-rvt' $missing
Check ($missingResult.RvtSummary.MissingCurrentStoreReferences -eq 1) 'A ready receipt cannot make a missing RVT available.'
$unknown=New-History 'unrecognized-proof';$fake=Direct-Recipe;$fake.FrozenDefinitions=@{Type='pretend'};$fake.SelfContained=$true
$null=Write-Journal $unknown @((Event $fake))
$unknownResult=Run-Case 'unrecognized-proof' $unknown
Check ($unknownResult.FormatIssues.Count -gt 0) 'Unknown frozen-definition claims must not confer PASS.'
$version=New-History 'unknown-version-and-record';$future=Event @{Version=99;Kind='family';Type='future'}
$text=($future|ConvertTo-Json -Compress -Depth 8)+"`n"+'{"Records":[]}'
[IO.File]::WriteAllText((Join-Path $version 'element-history-2026-10-01.jsonl'),$text)
$futureResult=Run-Case 'unknown-version-and-record' $version
Check (@($futureResult.FormatIssues|Where-Object Code -eq 'UnknownRecipeVersionOrKind').Count -eq 1 -and @($futureResult.FormatIssues|Where-Object Code -eq 'UnknownRecordSchema').Count -eq 1) 'Unknown recipe versions and record envelopes must be explicitly rejected.'
$bad=New-History 'bad-json';[IO.File]::WriteAllText((Join-Path $bad 'element-history-2026-10-01.jsonl'),'{invalid json')
$badResult=Run-Case 'bad-json' $bad
Check (@($badResult.FormatIssues|Where-Object Code -eq 'InvalidJson').Count -eq 1) 'Malformed JSON must block certification.'
$badZip=New-History 'bad-gzip';[IO.File]::WriteAllBytes((Join-Path $badZip 'element-history-2026-10-01.jsonl.gz'),[byte[]](1,2,3,4))
$zipResult=Run-Case 'bad-gzip' $badZip
Check ($zipResult.FileIssues.Count -gt 0) 'Unreadable gzip must block certification.'
$empty=New-History 'no-deletions';$null=Write-Journal $empty @((Event $null 'create'))
$emptyResult=Run-Case 'no-deletions' $empty
Check ($emptyResult.Summary.KnownRecords -eq 1 -and $emptyResult.Summary.SelectedDeletionRecords -eq 0) 'An empty deletion scope must not produce a vacuous PASS.'
$filter=New-History 'batch-filter';$null=Write-Journal $filter @((Event (Direct-Recipe)),(Event @{Version=1;Kind='native_archive';Native=$backup} 'delete' 'other'))
$filtered=Run-Case 'batch-filter' $filter @('wanted')
Check ($filtered.Summary.DeletionRecords -eq 2 -and $filtered.Summary.SelectedDeletionRecords -eq 1 -and $filtered.Summary.FilteredDeletionRecords -eq 1 -and $filtered.Summary.NativeReferences -eq 0) 'Batch filtering must be explicit and must not count unrelated artifacts.'
$large=New-History 'above-10000';$line=(Event (Direct-Recipe)|ConvertTo-Json -Compress -Depth 20)+"`n"
[IO.File]::WriteAllText((Join-Path $large 'element-history-2026-10-01.jsonl'),($line*10005),[Text.UTF8Encoding]::new($false))
$largeResult=Run-Case 'above-10000' $large
Check ($largeResult.Summary.SelectedDeletionRecords -eq 10005 -and $largeResult.Summary.DirectRecipes -eq 10005 -and $largeResult.Summary.DuplicateDeletionRecords -eq 10004) 'The diagnostic must read all raw records beyond 10k; duplicate detection must not silently truncate them.'
$unsafe=Join-Path $plain 'must-not-be-written.json';$log=Join-Path $reports 'unsafe-output.log'
& $pwsh -NoLogo -NoProfile -File $tool -HistoryDirectory $plain -OutputPath $unsafe *> $log
Check ($LASTEXITCODE -ne 0 -and !(Test-Path -LiteralPath $unsafe)) 'A report inside the history input directory must be refused without writing it.'
Write-Output "PASS: $assertions IO assertions; production JSONL/GZ shape, external references, missing definitions, unknown/corrupt data, batch filtering, >10k records and unchanged input hashes. Fixtures: $root"
