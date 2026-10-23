#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $repo ('tmp/history-summary-partial-tests/'+[guid]::NewGuid().ToString('N'))
$capture=Join-Path $root 'capture';$restore=Join-Path $root 'restore'
$null=[IO.Directory]::CreateDirectory((Join-Path $capture 'history'));$null=[IO.Directory]::CreateDirectory($restore)
$summaryScript=Join-Path $PSScriptRoot 'Summarize.ps1'
function Write-Report([string]$Path,$Value) { $Value|ConvertTo-Json -Depth 32|Set-Content -LiteralPath $Path -Encoding utf8 }
function Clone($Value) { $Value|ConvertTo-Json -Depth 32|ConvertFrom-Json -AsHashtable }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$script:checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Condition) { $script:checks.Add([pscustomobject]@{Name=$Name;Passed=$Condition});if(!$Condition){throw $Name} }
function Summarize { & $summaryScript -RunDirectory $restore -Disposition Completed | Out-Null;Get-Content -LiteralPath (Join-Path $restore 'independent-summary.json') -Raw|ConvertFrom-Json -AsHashtable }
$sourceHash='A'*64;$deletedHash='B'*64;$payload='C'*64;$typeVersion='D'*64
$bridge=Join-Path $root 'deliberately-absent-opening-bridge.rvt'
$config=@{Scenario='durable';DurablePhase='restore';DurableCaptureDirectory=$capture;AllowPartialDurableRestore=$true;PhysicalTargetFraction=.5;DeletionDelaySeconds=60;ApplyDeterministicEdits=$true}
Write-Report (Join-Path $restore 'config.json') $config
Write-Report (Join-Path $restore 'ready.json') @{Pid=11;RuntimeDetailed=$false}
$specs=@(1..8|ForEach-Object {@{Id=$_;Uid=('source'+$_);Class='FamilyInstance';Category='Physical';Point=@(1,2,3);Selected=($_ -le 4);ProtectionReason=$(if($_ -le 4){$null}else{'sampling plan'})}})
Write-Report (Join-Path $capture 'inventory.json') @{CapturedUtc='2026-10-23T08:01:02Z';Specs=$specs;Catalog=$specs;PhysicalEligibleIds=@(1..8);SelectedRootIds=@(1..4);ExplicitlyExcludedPhysicalIds=@()}
Write-Report (Join-Path $capture 'deletion.json') @{DocumentChangedDeletedIds=@(1,2,3,4,101,102);DeletedPhysicalIds=@(1..4)}
Write-Report (Join-Path $capture 'journal-summary.json') @{DeletionBatchIds=@('batch');FullCount=6;MissingChangedIds=@()}
$raw=@(1,2,3,4,101,102|ForEach-Object { @{EventId=('event'+$_);ElementId=$_;Ts='2026-10-23T08:01:03Z';Action='delete';UniqueId=('deleted:'+$_);DeletionAudit=@{BatchId='batch'};Delta=@{recipe=@{Kind='native_archive';Native=@{WholeModel=$true;Ready=$true}}}}|ConvertTo-Json -Depth 10 -Compress })
[IO.File]::WriteAllLines((Join-Path $capture 'history/element-history-fixture.jsonl'),$raw)
$sampling=@{PhysicalEligibleBeforeSamplingCount=8;AllowedPhysicalCount=8;SelectedPhysicalIds=@(1..4);ForcedModifiedPhysicalIds=@(1,2);SelectedFractionOfAllowed=.5}
Write-Report (Join-Path $capture 'sampling-plan.json') $sampling
$delay=@{RealCaptureObserved=$true;ActualDelayAfterOpenReturnSeconds=61;FirstRealCaptureUtc='2026-10-23T08:00:10Z';OpenReturnedUtc='2026-10-23T08:00:00Z';AtUtc='2026-10-23T08:01:01Z';EditAttemptsCompleted=$true;CompletedEditsCount=2;SkippedEdits=@();EditsPassed=$true;Edits=@(@{Kind='plant_translation';AtUtc='2026-10-23T08:00:20Z';Passed=$true;Skipped=$false},@{Kind='wall_disallow_join';AtUtc='2026-10-23T08:00:40Z';Passed=$true;Skipped=$false})}
Write-Report (Join-Path $capture 'delayed-before-delete.json') $delay
$records=@(1..4|ForEach-Object { @{ElementId=$_;SourceUid=('source'+$_);EventId=('event'+$_);Included=($_ -le 2);PayloadIds=$(if($_ -le 2){@($payload)}else{@()});Reason=$(if($_ -le 2){$null}else{'No immutable conversion for this original physical object'})} })
$manifest=@{FullDeletionEventCount=6;FullDeletedPhysicalIds=@(1..4);SelectedPhysicalIds=@(1,2);SelectedEventIds=@('event1','event2');Records=$records;FullConversionComplete=$false}
$manifestPath=Join-Path $capture 'partial-subset.json';Write-Report $manifestPath $manifest
$closeReady=@{CapturePid=10;ProjectClosed=$true;OldDocumentInvalid=$true;AbsentFromApplicationDocuments=$true;SourcesDisposed=$true;ConverterPaused=$true;RequestsFrozen=$true;BridgeFile=$bridge;SubsetManifestSha256=(Hash $manifestPath)}
$proofPath=Join-Path $capture 'partial-close-ready.json';Write-Report $proofPath $closeReady
$removal=@{BridgeFile=$bridge;Removed=$true;AbsentAfter=$true;ManualTestBridgeRemoval=$true;AutomaticCleanupVerified=$false;FullRestoreVerified=$false;ProofSha256=(Hash $proofPath)}
$removalPath=Join-Path $capture 'partial-bridge-removal.json';Write-Report $removalPath $removal
$closure=@{CapturePid=10;SessionClosed=$true;Closed=$true;OldDocumentInvalid=$true;AbsentFromApplicationDocuments=$true;ConversionComplete=$false;
    ManualTestBridgeRemoval=$true;AutomaticCleanupVerified=$false;FullRestoreVerified=$false;AllBridgeFilesAbsent=$true;BridgeFiles=@($bridge);DeletedBridgeFiles=@($bridge);
    SourceAndInitialCopyUnchanged=$true;SourceSha256Before=$sourceHash;SourceSha256After=$sourceHash;InitialCopySha256=$sourceHash;
    DeletedProjectSha256=$deletedHash;ExpectedDeletionEvents=6;FullDeletedPhysicalCount=4;SubsetPhysicalCount=2;SubsetManifestSha256=(Hash $manifestPath);RemovalProofSha256=(Hash $removalPath)}
$closurePath=Join-Path $capture 'partial-session-closed.json';Write-Report $closurePath $closure
Write-Report (Join-Path $restore 'durable-phase-two-baseline.json') @{CapturePid=10;RestorePid=11;DistinctProcess=$true;CaptureProcessExited=$true;DeletedProjectSha256=$deletedHash;PartialOnly=$true;FullDeletedPhysicalCount=4;FullDeletionEventCount=6}
$inputs=@{RestorePid=11;PartialOnly=$true;FullDeletedPhysicalCount=4;FullDeletionEventCount=6;ExactEventCoverage=$true;ExpectedEvents=2;Requests=2;LoadedEvents=2;RequestsWithoutRecipe=0;
    AllBridgeFilesAbsent=$true;WholeModelOrBridgeReferences=0;Artifacts=@(@{WholeModel=$false;File=$null;PayloadId=$payload;TypeVersion=$typeVersion})}
$inputsPath=Join-Path $restore 'durable-restoration-inputs.json';Write-Report $inputsPath $inputs
Write-Report (Join-Path $restore 'load.json') @{FullEventIds=@(1,2);DefaultEventIds=@(1,2);RequestEventIds=@(1,2);RequestsCount=2;DisplayableDeletionCount=2}
Write-Report (Join-Path $restore 'restore-result.json') @{Created=2;Existing=0;IncludedInParent=0;Failed=0;Items=@()}
$verified=@(foreach($id in 1..2) {
    $record=@{SourceId=$id;SourceUid=('source'+$id);ActualUids=@('actual'+$id);Class='FamilyInstance';Category='Physical';ExpectedProbeErrors=@();ActualProbeErrors=@();
        TypeUidMatchesSource=$false;HistoricalTypeProvenance=$true;TypeDefinitionParametersMatch=$true;Expected=@{Ports=@()};PortSystemTypesMatch=$true;PortSystemTypeEvidence=@();
        SystemTypeUidMatchesSource=$true;SystemTypeEvidence=@{Passed=$true};TypeDefinitionEvidence=@{TypeVersionMatchesResolvedPayload=$true;SourceTypeOriginPresent=$true;FamilyIdentityOrProvenanceMatches=$true;TypeClassAndCategoryMatch=$true}}
    foreach($flag in @('MatchesIdentity','ClassMatches','TypeMatches','GeometryMatches','GeometryChecked','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','ConnectionsMatch','SystemTypeMatches','Passed','WallJoinsMatch','ExcavationsMatch')) {$record[$flag]=$true}
    $record
})
$verification=@{Records=$verified;Missing=@();Duplicates=@();Failed=@();UnexpectedModelObjects=@();SelectedSubsetVerified=$true;AllDeletedPhysicalVerified=$false;FullDeletedPhysicalCount=4}
Write-Report (Join-Path $restore 'verification.json') $verification;Write-Report (Join-Path $restore 'reopened-verification.json') $verification
Write-Report (Join-Path $restore 'persistence.json') @{Closed=$true;OldDocumentInvalid=$true;AbsentFromApplicationDocuments=$true;ReopenedDistinctDocument=$true;FileExists=$true;FileLength=10}
$finish=@{Closed=$true;OldDocumentInvalid=$true;AbsentFromApplicationDocuments=$true;SourceUnchanged=$true;CopyAndSourceUnchanged=$true;SourceSha256Before=$sourceHash;SourceSha256After=$sourceHash;AllOpeningBridgeFilesStillAbsent=$true;DeletedProjectSha256AfterRestore=$deletedHash}
Write-Report (Join-Path $restore 'integrity.json') $finish;Write-Report (Join-Path $restore 'finished.json') $finish
$summary=Summarize
Check 'validated selected subset can pass its own restoration/persistence gate' $summary.SelectedSubsetRestoreAndPersistencePassed
Check 'partial run never passes global strict or full physical restoration' (!$summary.StrictPassed -and !$summary.PhysicalRestoreAndPersistencePassed)
Check 'raw journal and full physical denominators stay unchanged' ($summary.Journal.RawBatchLines -eq 6 -and $summary.Journal.UniqueEventIds -eq 6 -and $summary.Selection.DocumentChangedDeletedCount -eq 6 -and $summary.FullDeletedPhysicalCount -eq 4 -and $summary.Selection.DeletedDeclaredPhysicalCount -eq 4)
Check 'only verification denominator becomes the selected subset' ($summary.Verification.ExpectedPhysicalObjects -eq 2 -and $summary.ReopenedVerification.ExpectedPhysicalObjects -eq 2 -and $summary.RestorationVerificationPhysicalCount -eq 2)
Check 'excluded physical IDs and reasons remain visible' ($summary.PartialSubsetEvidence.ExcludedPhysicalCount -eq 2 -and @($summary.PartialSubsetEvidence.ExcludedPhysicalRecords).Count -eq 2 -and @($summary.PartialSubsetEvidence.ExcludedPhysicalRecords|Where-Object {![string]::IsNullOrWhiteSpace($_.Reason)}).Count -eq 2)
Check 'missing full-scope request IDs remain visible' (@($summary.Loading.PhysicalIdsMissingFromActualRestoreRequests).Count -eq 2)
Check 'summary does not fabricate whole-model or automatic-cleanup success' (!$summary.PartialSubsetEvidence.FullRestoreVerified -and !$summary.PartialSubsetEvidence.AutomaticCleanupVerified -and !$summary.PlannedPhysicalSubset.ContainsKey('WholeOriginalPhysicalModelRestorationClaimed'))
Check 'two successful artificial edits are reported separately' ($summary.DelayedCapture.ArtificialEditsFullyVerified -and $summary.DelayedCapture.CompletedEditsCount -eq 2 -and @($summary.DelayedCapture.SkippedEdits).Count -eq 0)
$skippedDelay=Clone $delay;$skippedDelay.CompletedEditsCount=0;$skippedDelay.EditsPassed=$false
foreach($edit in $skippedDelay.Edits) {$edit.Passed=$false;$edit.Skipped=$true;$edit.Reason='No eligible candidate; no modification attempted.'}
$skippedDelay.SkippedEdits=@($skippedDelay.Edits)
Write-Report (Join-Path $capture 'delayed-before-delete.json') $skippedDelay
$summary=Summarize
Check 'explicit skips can complete the minute protocol without false mutation success' ($summary.SelectedSubsetRestoreAndPersistencePassed -and $summary.DelayedCapture.Proven -and !$summary.DelayedCapture.ArtificialEditsFullyVerified -and $summary.DelayedCapture.CompletedEditsCount -eq 0 -and @($summary.DelayedCapture.SkippedEdits).Count -eq 2)
$bad=Clone $skippedDelay;$bad.Edits[0].Reason=$null;Write-Report (Join-Path $capture 'delayed-before-delete.json') $bad
$summary=Summarize;Check 'skipped artificial edit without explanation blocks protocol' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.DelayedCapture.Proven)
$bad=Clone $delay;$bad.Edits[0].Passed=$false;Write-Report (Join-Path $capture 'delayed-before-delete.json') $bad
$summary=Summarize;Check 'real failed unskipped edit remains blocking' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.DelayedCapture.Proven)
Write-Report (Join-Path $capture 'delayed-before-delete.json') $delay
$bad=Clone $sampling;$bad.SelectedFractionOfAllowed=.2;Write-Report (Join-Path $capture 'sampling-plan.json') $bad
$summary=Summarize;Check 'missing sampling proof blocks subset success' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.PlannedPhysicalSubset.Proven)
Write-Report (Join-Path $capture 'sampling-plan.json') $sampling
$bad=Clone $delay;$bad.RealCaptureObserved=$false;Write-Report (Join-Path $capture 'delayed-before-delete.json') $bad
$summary=Summarize;Check 'missing real delay/capture proof blocks subset success' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.DelayedCapture.Proven)
Write-Report (Join-Path $capture 'delayed-before-delete.json') $delay
$bad=Clone $manifest;$bad.FullDeletedPhysicalIds=@(1,2);Write-Report $manifestPath $bad
$summary=Summarize;Check 'truncated manifest denominator cannot manufacture subset pass' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.PartialSubsetEvidence.Checks.FullOriginalPhysicalDenominatorPreserved)
Write-Report $manifestPath $manifest
$bad=Clone $manifest;$bad.Records[2].Reason=$null;Write-Report $manifestPath $bad
$summary=Summarize;Check 'an exclusion without a reason blocks subset success' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.PartialSubsetEvidence.Checks.EveryExcludedPhysicalObjectHasAReason)
Write-Report $manifestPath $manifest
$bad=Clone $inputs;$bad.Artifacts[0].File='old-external.rvt';Write-Report $inputsPath $bad
$summary=Summarize;Check 'external RVT reference blocks subset despite a valid payload ID' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.PartialSubsetEvidence.Checks.NoTemporaryReceiptInResolvedSubset)
Write-Report $inputsPath $inputs
$bad=Clone $closeReady;$bad.RequestsFrozen=$false;Write-Report $proofPath $bad
$summary=Summarize;Check 'unfrozen readers cannot pass manual bridge-removal prerequisites' (!$summary.SelectedSubsetRestoreAndPersistencePassed -and !$summary.PartialSubsetEvidence.Checks.CaptureProjectAndReadersActuallyClosed)
Write-Report $proofPath $closeReady
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($summaryScript,[ref]$tokens,[ref]$errors)
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-RestorationVerdicts'},$true)
Invoke-Expression $definition.Extent.Text
foreach($partial in @($false,$true)) {
    foreach($gate in @('sampling','delay')) {
        $v=Resolve-RestorationVerdicts $partial $true $true $true ($gate -ne 'sampling') ($gate -ne 'delay') $true
        Check ("$gate failure blocks every restoration success; partial=$partial") (!$v.StrictPassed -and !$v.PhysicalRestoreAndPersistencePassed -and !$v.SelectedSubsetRestoreAndPersistencePassed)
    }
}
$v=Resolve-RestorationVerdicts $true $true $true $true $true $true $true
Check 'even perfect selected subset evidence cannot enable global success' (!$v.StrictPassed -and !$v.PhysicalRestoreAndPersistencePassed -and $v.SelectedSubsetRestoreAndPersistencePassed)
Write-Report (Join-Path $root 'results.json') @{Passed=$true;Assertions=$script:checks.Count;Scope='Analysis fixtures only. No Revit, product build, request, native file or real deletion/removal.';Checks=$script:checks}
Write-Output ('PASS: '+$script:checks.Count+' partial durable summary assertions; '+(Join-Path $root 'results.json'))
