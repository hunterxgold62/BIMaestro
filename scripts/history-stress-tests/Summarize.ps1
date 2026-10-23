#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$OutputPath,
    [ValidateSet('Unspecified','Completed','Interrupted','RestoreCompletedAfterInterruptionAnnounced')][string]$Disposition='Unspecified',
    [string]$DispositionReason
)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath $run -PathType Container)) { throw "Run directory not found: $run" }
if (!$OutputPath) { $OutputPath=Join-Path $run 'independent-summary.json' }
$missingFiles=[Collections.Generic.List[string]]::new()
$parseErrors=[Collections.Generic.List[object]]::new()
function Read-Report([string]$Name,[string]$Directory=$run) {
    $path=Join-Path $Directory $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $missingFiles.Add($Name); return $null }
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable }
    catch { $parseErrors.Add([pscustomobject]@{File=$Name;Error=$_.Exception.Message}); return $null }
}
function Id-Set($Values) {
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($value in $Values) { if($null -ne $value) { $null=$set.Add(([long]$value).ToString([Globalization.CultureInfo]::InvariantCulture)) } }
    return ,$set
}
function Difference($Expected,$Actual) { return @($Expected | Where-Object { !$Actual.Contains($_) } | Sort-Object { [long]$_ }) }
function Check-Verification($Verification,$ExpectedIds,[bool]$RequireDurableTypes=$false,[bool]$RequireBusinessParameters=$false) {
    $records=@($Verification.Records | Where-Object { $null -ne $_ })
    $recordIds=Id-Set ($records | ForEach-Object SourceId)
    $missing=@(Difference $ExpectedIds $recordIds)
    $extra=@(Difference $recordIds $ExpectedIds)
    $duplicateRecords=@($records | Group-Object SourceId | Where-Object Count -gt 1 | ForEach-Object Name)
    $unchecked=[Collections.Generic.List[object]]::new()
    $required=@('MatchesIdentity','ClassMatches','TypeMatches','GeometryMatches','GeometryChecked','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','ConnectionsMatch','SystemTypeMatches','Passed')
    # Old runs predate the excavation probe. New runs must not pass if even
    # one record advertises the probe while another leaves it unverified.
    if (@($records | Where-Object { $_.ContainsKey('ExcavationsMatch') }).Count) { $required+='ExcavationsMatch' }
    if (@($records | Where-Object { $_.ContainsKey('WallJoinsMatch') }).Count) { $required+='WallJoinsMatch' }
    $versionedTypeChecks=$RequireDurableTypes -or @($records | Where-Object { $null -ne $_.TypeDefinitionParametersMatch }).Count -gt 0
    if($versionedTypeChecks) { $required+='TypeDefinitionParametersMatch' }
    $businessParameterChecks=$RequireBusinessParameters -or @($records | Where-Object { $null -ne $_.InstanceParametersMatch }).Count -gt 0
    if($businessParameterChecks) { $required+='InstanceParametersMatch' }
    if($RequireDurableTypes) { $required+='PortSystemTypesMatch' }
    foreach($record in $records) {
        $failed=@($required | Where-Object { $record[$_] -ne $true })
        if (@($record.ActualUids).Count -ne 1) { $failed+= 'ExactlyOneActualUid' }
        if($businessParameterChecks -and ($record.InstanceParameterEvidence.Passed -ne $true -or
            !$record.InstanceParameterEvidence.ContainsKey('Differences') -or @($record.InstanceParameterEvidence.Differences | Where-Object { $null -ne $_ }).Count -gt 0 -or
            $null -eq $record.InstanceParameterEvidence.ExpectedCount -or $null -eq $record.InstanceParameterEvidence.ActualCount -or
            $record.InstanceParameterEvidence.ExpectedCount -ne @($record.InstanceParameterEvidence.Expected | Where-Object { $null -ne $_ }).Count -or
            $record.InstanceParameterEvidence.ActualCount -ne @($record.InstanceParameterEvidence.Actual | Where-Object { $null -ne $_ }).Count)) { $failed+='InstanceParameterEvidence' }
        if($versionedTypeChecks -and $record.TypeUidMatchesSource -ne $true -and
            ($record.HistoricalTypeProvenance -ne $true -or $record.TypeDefinitionEvidence.TypeVersionMatchesResolvedPayload -ne $true -or
             $record.TypeDefinitionEvidence.SourceTypeOriginPresent -ne $true -or $record.TypeDefinitionEvidence.FamilyIdentityOrProvenanceMatches -ne $true -or
             $record.TypeDefinitionEvidence.TypeClassAndCategoryMatch -ne $true -or $record.TypeDefinitionEvidence.TypeVersionProbeError)) { $failed+='HistoricalTypeVersionProof' }
        if($RequireDurableTypes) {
            if($record.SystemTypeEvidence.Passed -ne $true) { $failed+='SystemTypeDefinitionProof' }
            if($record.SystemTypeUidMatchesSource -ne $true -and
                ($record.SystemTypeEvidence.HistoricalTypeProvenance -ne $true -or $record.SystemTypeEvidence.TypeVersionMatchesResolvedPayload -ne $true -or
                 $record.SystemTypeEvidence.SourceTypeOriginPresent -ne $true -or $record.SystemTypeEvidence.FamilyIdentityOrProvenanceMatches -ne $true -or
                 $record.SystemTypeEvidence.TypeClassAndCategoryMatch -ne $true -or $record.SystemTypeEvidence.TypeDefinitionParametersMatch -ne $true)) { $failed+='HistoricalSystemVersionProof' }
            $portEvidence=@($record.PortSystemTypeEvidence | Where-Object { $null -ne $_ })
            $expectedPortEvidence=0
            foreach($port in @($record.Expected.Ports | Where-Object { $null -ne $_ })) { $expectedPortEvidence+=1+@($port.Peers | Where-Object { $null -ne $_ }).Count }
            if($portEvidence.Count -ne $expectedPortEvidence) { $failed+='PortSystemTypeEvidenceCoverage' }
            foreach($trace in $portEvidence) {
                $evidence=$trace.Evidence
                if($evidence.Passed -ne $true -or ($evidence.TypeUidMatchesSource -ne $true -and
                    ($evidence.HistoricalTypeProvenance -ne $true -or $evidence.TypeVersionMatchesResolvedPayload -ne $true -or
                     $evidence.SourceTypeOriginPresent -ne $true -or $evidence.FamilyIdentityOrProvenanceMatches -ne $true -or
                     $evidence.TypeClassAndCategoryMatch -ne $true -or $evidence.TypeDefinitionParametersMatch -ne $true))) { $failed+='PortSystemTypeVersionProof';break }
            }
        }
        foreach($probe in @('ExpectedProbeErrors','ActualProbeErrors')) {
            if(!$record.ContainsKey($probe)) { $failed+= $probe+'Unverified' }
            elseif(@($record[$probe] | Where-Object { $null -ne $_ }).Count -gt 0) { $failed+= $probe }
        }
        if($failed.Count) { $unchecked.Add([pscustomobject]@{SourceId=$record.SourceId;SourceUid=$record.SourceUid;FailedOrUnverifiedChecks=$failed;ActualUids=$record.ActualUids;Note=$record.Note}) }
    }
    $originDuplicates=@($Verification.Duplicates | Where-Object { $null -ne $_ })
    $missingObjects=@($Verification.Missing | Where-Object { $null -ne $_ })
    $declaredFailed=@($Verification.Failed | Where-Object { $null -ne $_ })
    $unexpectedObjects=@($Verification.UnexpectedModelObjects | Where-Object { $null -ne $_ })
    $mapping=@{}
    foreach($record in $records) {
        foreach($actualUid in @($record.ActualUids | Where-Object { ![string]::IsNullOrWhiteSpace([string]$_) })) {
            if(!$mapping.ContainsKey([string]$actualUid)) { $mapping[[string]$actualUid]=[Collections.Generic.List[object]]::new() }
            $mapping[[string]$actualUid].Add([pscustomobject]@{SourceId=$record.SourceId;SourceUid=$record.SourceUid;Class=$record.Class;Category=$record.Category;Name=$record.Name})
        }
    }
    $nonInjective=@(foreach($actualUid in $mapping.Keys) {
        $sources=@($mapping[$actualUid] | Group-Object SourceId | ForEach-Object { $_.Group[0] })
        if($sources.Count -gt 1) { [pscustomobject]@{ActualUid=$actualUid;DistinctSourceCount=$sources.Count;Sources=$sources;
            Classification='Unexplained; a shared native root alone does not prove that distinct original physical objects may collapse to one live object.'} }
    })
    return [pscustomobject]@{
        Passed=($null -ne $Verification -and $ExpectedIds.Count -gt 0 -and !$missing.Count -and !$extra.Count -and !$duplicateRecords.Count -and !$unchecked.Count -and !$originDuplicates.Count -and !$missingObjects.Count -and !$declaredFailed.Count -and !$nonInjective.Count)
        ExpectedPhysicalObjects=$ExpectedIds.Count;VerifiedRecords=$records.Count;MissingRecordIds=$missing;UnexpectedRecordIds=$extra
        DuplicateRecordIds=$duplicateRecords;OriginDuplicates=$originDuplicates;MissingObjects=$missingObjects
        DeclaredFailures=$declaredFailed;FailedOrUnverifiedRecords=@($unchecked)
        NoUnexpectedModelObjects=($null -ne $Verification -and $Verification.ContainsKey('UnexpectedModelObjects') -and !$unexpectedObjects.Count)
        UnexpectedModelObjects=$unexpectedObjects
        NonInjectiveMappings=$nonInjective
    }
}
function Summarize-VerificationRecords($Records) {
    $records=@($Records | Where-Object { $null -ne $_ })
    $single=@($records | Where-Object { @($_.ActualUids).Count -eq 1 })
    $mismatches=[ordered]@{}
    $unverified=[ordered]@{}
    $checks=@('ClassMatches','TypeMatches','GeometryMatches','GeometryChecked','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','ConnectionsMatch','SystemTypeMatches')
    if (@($records | Where-Object { $_.ContainsKey('ExcavationsMatch') }).Count) { $checks+='ExcavationsMatch' }
    if (@($records | Where-Object { $_.ContainsKey('WallJoinsMatch') }).Count) { $checks+='WallJoinsMatch' }
    if (@($records | Where-Object { $null -ne $_.TypeDefinitionParametersMatch }).Count) { $checks+='TypeDefinitionParametersMatch' }
    if (@($records | Where-Object { $null -ne $_.InstanceParametersMatch }).Count) { $checks+='InstanceParametersMatch' }
    foreach($check in $checks) {
        $mismatches[$check]=@($single | Where-Object { $_[$check] -eq $false }).Count
        $unverified[$check]=@($single | Where-Object { !$_.ContainsKey($check) }).Count
    }
    return [pscustomobject]@{Expected=$records.Count;ExactlyOneMappedObject=$single.Count;
        Missing=@($records | Where-Object { @($_.ActualUids).Count -eq 0 }).Count;
        DuplicateOrigins=@($records | Where-Object { @($_.ActualUids).Count -gt 1 }).Count;
        Passed=@($records | Where-Object Passed -eq $true).Count;
        PresentButFailed=@($single | Where-Object Passed -ne $true).Count;
        MismatchesAmongPresent=$mismatches;UnverifiedChecksAmongPresent=$unverified;
        WithExpectedProbeErrors=@($single | Where-Object { @($_.ExpectedProbeErrors | Where-Object { $null -ne $_ }).Count -gt 0 }).Count;
        WithActualProbeErrors=@($single | Where-Object { @($_.ActualProbeErrors | Where-Object { $null -ne $_ }).Count -gt 0 }).Count}
}
function Check-DurableEvidence($Closure,$Conversion,$Baseline,$Inputs,$Ready,$Integrity) {
    $bridges=@($Closure.BridgeFiles | Where-Object { $_ })
    $removed=@($Closure.DeletedBridgeFiles | Where-Object { $_ })
    $existing=@($bridges | Where-Object { Test-Path -LiteralPath $_ })
    $artifacts=@($Inputs.Artifacts | Where-Object { $null -ne $_ })
    $badArtifacts=@($artifacts | Where-Object { $_.WholeModel -eq $true -or $_.File -in $bridges -or [string]$_.PayloadId -notmatch '^[a-fA-F0-9]{64}$' })
    $checks=[ordered]@{
        CaptureConversionComplete=($Conversion.Complete -eq $true -and $Conversion.Status.Complete -eq $true -and
            $Conversion.Status.PendingJobs -eq 0 -and $Conversion.Status.FailedJobs -eq 0 -and
            $Closure.ExpectedDeletionEvents -gt 0 -and $Conversion.Status.ConvertedEvents -ge $Closure.ExpectedDeletionEvents -and
            @($Conversion.Status.Failures | Where-Object { $_ }).Count -eq 0)
        CaptureProjectActuallyClosed=($Closure.Closed -eq $true -and $Closure.OldDocumentInvalid -eq $true -and
            $Closure.AbsentFromApplicationDocuments -eq $true -and $Closure.SessionClosed -eq $true -and $Closure.ConversionComplete -eq $true)
        BridgeRemovalProven=($bridges.Count -gt 0 -and $Closure.AllBridgeFilesAbsent -eq $true -and !$existing.Count -and
            @($bridges | Where-Object { $_ -notin $removed }).Count -eq 0)
        CaptureSourceAndInitialCopyUnchanged=($Closure.SourceAndInitialCopyUnchanged -eq $true -and
            $Closure.SourceSha256Before -and $Closure.SourceSha256Before -eq $Closure.SourceSha256After -and
            $Closure.SourceSha256Before -eq $Closure.InitialCopySha256)
        DistinctProcessAfterCaptureExited=($Baseline.DistinctProcess -eq $true -and $Baseline.CaptureProcessExited -eq $true -and
            $Closure.CapturePid -gt 0 -and $Baseline.CapturePid -eq $Closure.CapturePid -and $Baseline.RestorePid -eq $Ready.Pid -and
            $Baseline.RestorePid -ne $Baseline.CapturePid -and $Inputs.RestorePid -eq $Ready.Pid)
        ExactDurableInputCoverage=($Inputs.ExactEventCoverage -eq $true -and $Inputs.ExpectedEvents -gt 0 -and
            $Inputs.ExpectedEvents -eq $Closure.ExpectedDeletionEvents -and $Inputs.Requests -eq $Inputs.ExpectedEvents -and
            $Inputs.LoadedEvents -eq $Inputs.ExpectedEvents)
        NoTemporaryReceiptInResolvedInputs=($Inputs.AllBridgeFilesAbsent -eq $true -and $Inputs.WholeModelOrBridgeReferences -eq 0 -and !$badArtifacts.Count)
        DeletedCheckpointUnchanged=($Closure.DeletedProjectSha256 -and $Baseline.DeletedProjectSha256 -eq $Closure.DeletedProjectSha256 -and
            $Integrity.DeletedProjectSha256AfterRestore -eq $Closure.DeletedProjectSha256)
        BridgeStillAbsentAtFinish=($Integrity.AllOpeningBridgeFilesStillAbsent -eq $true -and !$existing.Count)
    }
    $failed=@($checks.Keys | Where-Object { $checks[$_] -ne $true })
    return [pscustomobject]@{Passed=(!$failed.Count);Checks=[pscustomobject]$checks;FailedOrUnverifiedChecks=$failed;
        ExistingBridgeFiles=$existing;InvalidResolvedArtifacts=$badArtifacts;CapturePid=$Closure.CapturePid;RestorePid=$Ready.Pid;
        Warning='Conversion and bridge removal are prerequisites only. Full object fidelity and distinct-document save/reopen are evaluated separately; source policy exclusions remain in the original denominator.'}
}
function Check-PartialSubsetEvidence($Manifest,$Closure,$CloseReady,$Removal,$Baseline,$Inputs,$Ready,$Integrity,$FullPhysical,$Changed,$Raw,$Specs,$Load,[string]$Directory) {
    $selected=Id-Set $Manifest.SelectedPhysicalIds
    $manifestFull=Id-Set $Manifest.FullDeletedPhysicalIds
    $records=@($Manifest.Records | Where-Object { $null -ne $_ })
    $included=@($records | Where-Object Included -eq $true)
    $excluded=@($records | Where-Object Included -ne $true)
    $includedIds=Id-Set ($included | ForEach-Object ElementId)
    $recordIds=Id-Set ($records | ForEach-Object ElementId)
    $eventIds=@($Manifest.SelectedEventIds | Where-Object { ![string]::IsNullOrWhiteSpace([string]$_) })
    $recordEventIds=@($included | ForEach-Object EventId)
    $requestIds=Id-Set $Load.RequestEventIds
    $rawIds=Id-Set ($Raw | ForEach-Object Id)
    $specById=@{};foreach($spec in $Specs) { $specById[([long]$spec.Id).ToString()]=$spec }
    $identityErrors=@(foreach($record in $records) {
        $id=([long]$record.ElementId).ToString()
        if(!$FullPhysical.Contains($id) -or !$specById.ContainsKey($id) -or $record.SourceUid -cne $specById[$id].Uid) { $id }
        elseif($record.Included -eq $true) {
            $events=@($Raw | Where-Object Id -eq $id)
            if($events.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$record.EventId) -or $events[0].EventId -cne $record.EventId -or
                @($record.PayloadIds | Where-Object { $_ -notmatch '^[a-fA-F0-9]{64}$' }).Count -or !@($record.PayloadIds).Count) { $id }
        }
    })
    $bridges=@($Closure.BridgeFiles | Where-Object { $_ })
    $existing=@($bridges | Where-Object { Test-Path -LiteralPath $_ })
    $artifacts=@($Inputs.Artifacts | Where-Object { $null -ne $_ })
    $badArtifacts=@($artifacts | Where-Object { $_.WholeModel -eq $true -or ![string]::IsNullOrWhiteSpace([string]$_.File) -or
        [string]$_.PayloadId -notmatch '^[a-fA-F0-9]{64}$' -or [string]$_.TypeVersion -notmatch '^(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{64})$' })
    $subsetPath=Join-Path $Directory 'partial-subset.json';$proofPath=Join-Path $Directory 'partial-close-ready.json';$removalPath=Join-Path $Directory 'partial-bridge-removal.json'
    $hashChain=($null -ne $Manifest -and $null -ne $Closure -and $null -ne $CloseReady -and $null -ne $Removal -and
        (Test-Path -LiteralPath $subsetPath -PathType Leaf) -and (Test-Path -LiteralPath $proofPath -PathType Leaf) -and (Test-Path -LiteralPath $removalPath -PathType Leaf))
    if($hashChain) { $hashChain=($Closure.SubsetManifestSha256 -eq (Get-FileHash -LiteralPath $subsetPath -Algorithm SHA256).Hash -and
        $CloseReady.SubsetManifestSha256 -eq $Closure.SubsetManifestSha256 -and
        $Closure.RemovalProofSha256 -eq (Get-FileHash -LiteralPath $removalPath -Algorithm SHA256).Hash -and
        $Removal.ProofSha256 -eq (Get-FileHash -LiteralPath $proofPath -Algorithm SHA256).Hash) }
    $checks=[ordered]@{
        FullOriginalPhysicalDenominatorPreserved=($manifestFull.Count -eq $FullPhysical.Count -and $manifestFull.Count -eq @($Manifest.FullDeletedPhysicalIds).Count -and
            @(Difference $FullPhysical $manifestFull).Count -eq 0 -and $Closure.FullDeletedPhysicalCount -eq $FullPhysical.Count -and
            $Inputs.FullDeletedPhysicalCount -eq $FullPhysical.Count -and $Baseline.FullDeletedPhysicalCount -eq $FullPhysical.Count)
        FullOriginalJournalDenominatorPreserved=($Changed.Count -gt 0 -and $Manifest.FullDeletionEventCount -eq $Changed.Count -and
            $Closure.ExpectedDeletionEvents -eq $Changed.Count -and $Inputs.FullDeletionEventCount -eq $Changed.Count -and
            $Baseline.FullDeletionEventCount -eq $Changed.Count -and $rawIds.Count -eq $Changed.Count -and @(Difference $Changed $rawIds).Count -eq 0)
        ExactPhysicalSubsetManifest=($selected.Count -gt 0 -and $selected.Count -eq @($Manifest.SelectedPhysicalIds).Count -and
            $selected.Count -eq $includedIds.Count -and $selected.Count -eq $included.Count -and @(Difference $selected $includedIds).Count -eq 0 -and
            @(Difference $selected $FullPhysical).Count -eq 0 -and $recordIds.Count -eq $FullPhysical.Count -and $records.Count -eq $FullPhysical.Count -and
            @(Difference $FullPhysical $recordIds).Count -eq 0 -and !$identityErrors.Count)
        ExactSubsetEventIdentities=($eventIds.Count -eq $selected.Count -and @($eventIds | Sort-Object -Unique).Count -eq $eventIds.Count -and
            $recordEventIds.Count -eq $eventIds.Count -and @($recordEventIds | Where-Object { $_ -cnotin $eventIds }).Count -eq 0)
        EveryExcludedPhysicalObjectHasAReason=(@($excluded | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Reason) }).Count -eq 0)
        ImmutableSubsetCloseAndRemovalChain=$hashChain
        CaptureProjectAndReadersActuallyClosed=($Closure.Closed -eq $true -and $Closure.OldDocumentInvalid -eq $true -and
            $Closure.AbsentFromApplicationDocuments -eq $true -and $Closure.SessionClosed -eq $true -and $CloseReady.ProjectClosed -eq $true -and
            $CloseReady.OldDocumentInvalid -eq $true -and $CloseReady.AbsentFromApplicationDocuments -eq $true -and $CloseReady.SourcesDisposed -eq $true -and
            $CloseReady.ConverterPaused -eq $true -and $CloseReady.RequestsFrozen -eq $true -and $CloseReady.CapturePid -eq $Closure.CapturePid)
        ExplicitManualRemovalNotAutomaticSuccess=($Closure.ManualTestBridgeRemoval -eq $true -and $Closure.AutomaticCleanupVerified -eq $false -and
            $Closure.FullRestoreVerified -eq $false -and $Removal.Removed -eq $true -and $Removal.AbsentAfter -eq $true -and
            $Removal.ManualTestBridgeRemoval -eq $true -and $Removal.AutomaticCleanupVerified -eq $false -and $Removal.FullRestoreVerified -eq $false -and
            $Removal.BridgeFile -ceq $CloseReady.BridgeFile -and $bridges.Count -eq 1 -and $bridges[0] -ceq $Removal.BridgeFile -and
            $Closure.AllBridgeFilesAbsent -eq $true -and !$existing.Count)
        CaptureSourceAndInitialCopyUnchanged=($Closure.SourceAndInitialCopyUnchanged -eq $true -and $Closure.SourceSha256Before -and
            $Closure.SourceSha256Before -eq $Closure.SourceSha256After -and $Closure.SourceSha256Before -eq $Closure.InitialCopySha256)
        DistinctProcessAfterCaptureExited=($Baseline.DistinctProcess -eq $true -and $Baseline.CaptureProcessExited -eq $true -and
            $Closure.CapturePid -gt 0 -and $Baseline.CapturePid -eq $Closure.CapturePid -and $Baseline.RestorePid -eq $Ready.Pid -and
            $Baseline.RestorePid -ne $Baseline.CapturePid -and $Inputs.RestorePid -eq $Ready.Pid -and $Baseline.PartialOnly -eq $true -and $Inputs.PartialOnly -eq $true)
        ExactSelectedInputCoverage=($Inputs.ExactEventCoverage -eq $true -and $Inputs.ExpectedEvents -eq $selected.Count -and
            $Inputs.Requests -eq $selected.Count -and $Inputs.LoadedEvents -eq $selected.Count -and $requestIds.Count -eq $selected.Count -and
            @(Difference $selected $requestIds).Count -eq 0 -and $Inputs.RequestsWithoutRecipe -eq 0)
        NoTemporaryReceiptInResolvedSubset=($Inputs.AllBridgeFilesAbsent -eq $true -and $Inputs.WholeModelOrBridgeReferences -eq 0 -and $artifacts.Count -gt 0 -and !$badArtifacts.Count)
        DeletedCheckpointUnchanged=($Closure.DeletedProjectSha256 -and $Baseline.DeletedProjectSha256 -eq $Closure.DeletedProjectSha256 -and
            $Integrity.DeletedProjectSha256AfterRestore -eq $Closure.DeletedProjectSha256)
        BridgeStillAbsentAtFinish=($Integrity.AllOpeningBridgeFilesStillAbsent -eq $true -and !$existing.Count)
    }
    $failed=@($checks.Keys | Where-Object { $checks[$_] -ne $true })
    return [pscustomobject]@{Passed=(!$failed.Count);Checks=[pscustomobject]$checks;FailedOrUnverifiedChecks=$failed;
        FullDeletedPhysicalCount=$FullPhysical.Count;FullDeletionEventCount=$Changed.Count;SelectedPhysicalCount=$selected.Count;
        SelectedPhysicalIds=@($selected);SelectedEventIds=$eventIds;ExcludedPhysicalCount=($FullPhysical.Count-$selected.Count);
        ExcludedPhysicalRecords=$excluded;ManifestIdentityErrors=$identityErrors;InvalidResolvedArtifacts=$badArtifacts;
        ManualTestBridgeRemoval=$true;AutomaticCleanupVerified=$false;FullRestoreVerified=$false;
        Warning='Only the explicitly selected converted physical subset can pass restoration/persistence. Full capture denominators, excluded IDs and reasons remain unchanged; manual bridge removal does not validate automatic cleanup.'}
}
function Resolve-RestorationVerdicts([bool]$Partial,[bool]$RestorationChecks,[bool]$ProtocolComplete,[bool]$CaptureComplete,[bool]$SamplingProven,[bool]$DelayProven,[bool]$StrictChecks) {
    return [pscustomobject]@{
        PhysicalRestoreAndPersistencePassed=(!$Partial -and $RestorationChecks -and $SamplingProven -and $DelayProven)
        SelectedSubsetRestoreAndPersistencePassed=($Partial -and $RestorationChecks -and $ProtocolComplete -and $CaptureComplete -and $SamplingProven -and $DelayProven)
        StrictPassed=(!$Partial -and $StrictChecks -and $RestorationChecks -and $SamplingProven -and $DelayProven)
    }
}
function Check-DelayedCaptureEvidence($Config,$Evidence,$Inventory) {
    $edits=@($Evidence.Edits | Where-Object { $null -ne $_ })
    $completed=@($edits | Where-Object { $_.Passed -eq $true -and $_.Skipped -ne $true })
    $skipped=@($edits | Where-Object Skipped -eq $true)
    $minuteProven=($Config.DeletionDelaySeconds -le 0 -or ($Evidence.RealCaptureObserved -eq $true -and
        $Evidence.ActualDelayAfterOpenReturnSeconds -ge $Config.DeletionDelaySeconds -and $Evidence.FirstRealCaptureUtc -and
        [DateTime]$Evidence.FirstRealCaptureUtc -ge [DateTime]$Evidence.OpenReturnedUtc -and
        [DateTime]$Evidence.FirstRealCaptureUtc -le [DateTime]$Evidence.AtUtc -and [DateTime]$Inventory.CapturedUtc -ge [DateTime]$Evidence.AtUtc))
    $attemptsProven=$true
    if($Config.DeletionDelaySeconds -gt 0 -and $Config.ApplyDeterministicEdits) {
        # Legacy successful edit evidence did not contain the explicit attempts
        # flag. It is accepted only when both edits actually passed, never when
        # a skipped/failed row is present.
        $attemptsComplete=if($null -ne $Evidence -and $Evidence.ContainsKey('EditAttemptsCompleted')) { $Evidence.EditAttemptsCompleted -eq $true } else { $Evidence.EditsPassed -eq $true -and !$skipped.Count }
        $invalid=@($edits | Where-Object {
            !(($_.Passed -eq $true -and $_.Skipped -ne $true) -or
                ($_.Skipped -eq $true -and $_.Passed -eq $false -and ![string]::IsNullOrWhiteSpace([string]$_.Reason))) -or
            !($_.AtUtc) -or [DateTime]$_.AtUtc -lt [DateTime]$Evidence.OpenReturnedUtc -or [DateTime]$_.AtUtc -gt [DateTime]$Inventory.CapturedUtc
        })
        $attemptsProven=$attemptsComplete -and $edits.Count -eq 2 -and !$invalid.Count
        if($null -ne $Evidence -and $Evidence.ContainsKey('CompletedEditsCount')) { $attemptsProven=$attemptsProven -and $Evidence.CompletedEditsCount -eq $completed.Count }
        if($null -ne $Evidence -and $Evidence.ContainsKey('SkippedEdits')) { $attemptsProven=$attemptsProven -and @($Evidence.SkippedEdits).Count -eq $skipped.Count }
    }
    return [pscustomobject]@{Passed=($minuteProven -and $attemptsProven);MinuteAndRealCaptureProven=$minuteProven;EditAttemptsProven=$attemptsProven;
        ArtificialEditsFullyVerified=($Config.ApplyDeterministicEdits -eq $true -and $attemptsProven -and $completed.Count -eq 2 -and !$skipped.Count -and $Evidence.EditsPassed -eq $true);
        CompletedEditsCount=$completed.Count;SkippedEdits=$skipped;
        Warning='Explicitly skipped artificial edits can complete the waiting protocol but do not prove a geometry/join modification. DocumentChanged modified IDs alone do not establish manual edits or geometric changes.'}
}

function Check-PartialPreparationSafety($CloseReady,$Closure,$Removal,[string]$Directory) {
    $paused=($CloseReady.AutomaticPreparationPaused -eq $true -and $Removal.AutomaticPreparationPaused -eq $true)
    $exitProofPath=Join-Path $Directory 'isolated-process-stopped.json';$proofPath=Join-Path $Directory 'partial-close-ready.json'
    $exitProof=$null;$captureReady=$null;$errors=[Collections.Generic.List[string]]::new();$exitMatched=$false;$processAbsent=$false
    if(!$paused) {
        try {
            $exitProof=[IO.File]::ReadAllText($exitProofPath) | ConvertFrom-Json -AsHashtable
            $captureReady=[IO.File]::ReadAllText((Join-Path $Directory 'ready.json')) | ConvertFrom-Json -AsHashtable
            $exitHash=(Get-FileHash -LiteralPath $exitProofPath -Algorithm SHA256).Hash
            $exitMatched=($CloseReady.CapturePid -gt 0 -and $exitProof.GuardedStop -eq $true -and $exitProof.Pid -eq $CloseReady.CapturePid -and
                $captureReady.Pid -eq $CloseReady.CapturePid -and $Closure.CapturePid -eq $CloseReady.CapturePid -and $Removal.CapturePid -eq $CloseReady.CapturePid -and
                $Closure.CaptureProcessExited -eq $true -and $Removal.CaptureProcessExited -eq $true -and $Closure.ExternalFinalizer -eq $true -and
                $Closure.AutomaticPreparationPaused -eq $false -and $Removal.AutomaticPreparationPaused -eq $false -and
                [string]::Equals([IO.Path]::GetFullPath([string]$exitProof.ClosureProof),[IO.Path]::GetFullPath($proofPath),[StringComparison]::OrdinalIgnoreCase) -and
                $Removal.ProcessExitProofSha256 -eq $exitHash -and $Closure.ProcessExitProofSha256 -eq $exitHash -and
                $Removal.ProofSha256 -eq (Get-FileHash -LiteralPath $proofPath -Algorithm SHA256).Hash)
            $processAbsent=(@([Diagnostics.Process]::GetProcesses() | Where-Object Id -eq $CloseReady.CapturePid).Count -eq 0)
        } catch { $errors.Add($_.Exception.Message) }
    }
    return [pscustomobject]@{Passed=($paused -or ($exitMatched -and $processAbsent -and !$errors.Count));AutomaticPreparationPausedProven=$paused;
        MatchingProcessExitProof=$exitMatched;CaptureProcessCurrentlyAbsent=$processAbsent;Errors=@($errors);
        AutomaticCleanupVerified=$false;FullRestoreVerified=$false}
}
function Check-BusinessWorkflowEvidence($Config,$Selection,$Edits,$BeforeDelete,$Readiness,$Inventory,$SelectedIds,$FullPhysical,$Changed,$Raw,$RequiredReadyPhysical=$null) {
    if($null -eq $RequiredReadyPhysical) { $RequiredReadyPhysical=$FullPhysical }
    $dates=@{};$dateErrors=[Collections.Generic.List[string]]::new()
    $dateValues=@{OpenReturnedUtc=$BeforeDelete.OpenReturnedUtc;FirstRealCaptureUtc=$BeforeDelete.FirstRealCaptureUtc;EditsCompletedUtc=$BeforeDelete.EditsCompletedUtc;
        BeforeDeleteUtc=$BeforeDelete.AtUtc;InventoryUtc=$Inventory.CapturedUtc;EditsUtc=$Edits.AtUtc;SelectionUtc=$Selection.AtUtc;ReadinessUtc=$Readiness.AtUtc}
    foreach($key in $dateValues.Keys) {
        $parsed=[DateTimeOffset]::MinValue
        if([string]::IsNullOrWhiteSpace([string]$dateValues[$key]) -or ![DateTimeOffset]::TryParse([string]$dateValues[$key],[ref]$parsed)) { $dateErrors.Add($key) }
        $dates[$key]=$parsed.UtcDateTime
    }
    $requestedWarmup=[Math]::Max(60,[double]$Config.DeletionDelaySeconds)
    $requestedWork=[Math]::Max(45,[double]$Config.WorkAfterEditsSeconds)
    $selected=Id-Set $Selection.SelectedPhysicalIds
    $selectionSpecs=@($Selection.Selected | Where-Object { $null -ne $_ });$selectionSpecIds=Id-Set ($selectionSpecs | ForEach-Object Id)
    $modified=Id-Set $Edits.ModifiedIds
    $attempts=@($Edits.Edits | Where-Object { $null -ne $_ })
    $completed=@($attempts | Where-Object { $_.Passed -eq $true -and $_.Skipped -ne $true });$completedIds=Id-Set ($completed | ForEach-Object SourceId)
    $invalidAttempts=[Collections.Generic.List[object]]::new()
    foreach($attempt in $attempts) {
        $stamp=[DateTimeOffset]::MinValue
        $validTime=([DateTimeOffset]::TryParse([string]$attempt.AtUtc,[ref]$stamp) -and $stamp.UtcDateTime -ge $dates.OpenReturnedUtc.AddSeconds($requestedWarmup) -and
            $stamp.UtcDateTime -le $dates.EditsCompletedUtc -and $stamp.UtcDateTime -ge $dates.FirstRealCaptureUtc)
        $skipped=($attempt.Skipped -eq $true -and $attempt.Passed -eq $false -and ![string]::IsNullOrWhiteSpace([string]$attempt.Reason))
        $commentAfter=@($attempt.After.InstanceParameters | Where-Object { $_.Storage -eq 'String' -and $_.Text -ceq $attempt.Comment -and ![string]::IsNullOrWhiteSpace([string]$_.Key) -and !$_.Error -and !$_.ExclusionReason })
        $commentChanged=(@($commentAfter | Where-Object { $commentKey=$_.Key;@($attempt.Before.InstanceParameters | Where-Object { $_.Key -eq $commentKey -and $_.Text -cne $attempt.Comment -and !$_.Error }).Count -eq 1 }).Count -eq 1)
        $edited=($attempt.Passed -eq $true -and $attempt.Skipped -ne $true -and $attempt.SourceId -gt 0 -and ![string]::IsNullOrWhiteSpace([string]$attempt.SourceUid) -and
            $attempt.Before.Id -eq $attempt.SourceId -and $attempt.After.Id -eq $attempt.SourceId -and $attempt.Before.Uid -ceq $attempt.SourceUid -and $attempt.After.Uid -ceq $attempt.SourceUid -and
            ![string]::IsNullOrWhiteSpace([string]$attempt.Comment) -and $commentChanged)
        if(!$validTime -or !($skipped -or $edited)) { $invalidAttempts.Add([pscustomobject]@{Kind=$attempt.Kind;SourceId=$attempt.SourceId;ValidTime=$validTime;ExplicitSkip=$skipped;ActualCommentChange=$edited}) }
    }
    $beforeModified=Id-Set $BeforeDelete.ModifiedIds;$selectionModified=Id-Set $Selection.ModifiedIds
    $beforeCompleted=Id-Set (@($BeforeDelete.Edits | Where-Object { $_.Passed -eq $true -and $_.Skipped -ne $true }) | ForEach-Object SourceId)
    $records=@($Readiness.Records | Where-Object { $null -ne $_ });$recordIds=Id-Set ($records | ForEach-Object ElementId)
    $readinessModified=Id-Set (@($Readiness.Modified | Where-Object { $null -ne $_ }) | ForEach-Object ElementId)
    $rawById=@{};foreach($row in $Raw) { $rawById[[string]$row.Id]=$row }
    $invalidReadiness=@($records | Where-Object { $id=([long]$_.ElementId).ToString();$source=$rawById[$id];
        $_.Legacy -ne $false -or [string]::IsNullOrWhiteSpace([string]$_.EventId) -or !$source -or $_.EventId -cne $source.EventId -or $_.State -notin @('Ready','Unavailable') })
    $notReadyPhysical=@($records | Where-Object { $RequiredReadyPhysical.Contains(([long]$_.ElementId).ToString()) -and $_.State -ne 'Ready' } | ForEach-Object ElementId)
    $notReadyModified=@($Readiness.Modified | Where-Object { $_.State -ne 'Ready' } | ForEach-Object ElementId)
    $datesValid=(!$dateErrors.Count);$warmup=($dates.EditsCompletedUtc-$dates.OpenReturnedUtc).TotalSeconds;$work=($dates.BeforeDeleteUtc-$dates.EditsCompletedUtc).TotalSeconds
    $checks=[ordered]@{
        AllRequiredEvidencePresent=($null -ne $Selection -and $null -ne $Edits -and $null -ne $BeforeDelete -and $null -ne $Readiness)
        ChronologyProven=($datesValid -and $dates.FirstRealCaptureUtc -ge $dates.OpenReturnedUtc -and $dates.FirstRealCaptureUtc -le $dates.EditsCompletedUtc -and
            $dates.EditsCompletedUtc -eq $dates.EditsUtc -and $dates.BeforeDeleteUtc -ge $dates.EditsCompletedUtc -and $dates.SelectionUtc -ge $dates.BeforeDeleteUtc -and
            $dates.InventoryUtc -ge $dates.SelectionUtc -and $dates.ReadinessUtc -ge $dates.InventoryUtc)
        NaturalMinuteAndWorkInterval=($datesValid -and $Config.DeletionDelaySeconds -ge 60 -and $Config.WorkAfterEditsSeconds -ge 45 -and
            $warmup -ge $requestedWarmup -and $Edits.WarmupSeconds -ge $requestedWarmup -and $work -ge $requestedWork -and $BeforeDelete.NaturalWorkAfterEditsSeconds -ge $requestedWork -and
            $BeforeDelete.ForcedPreparation -eq $false -and $Readiness.ForcedPreparation -eq $false)
        ExplicitTenToThirtyTargets=($selected.Count -ge 10 -and $selected.Count -le 30 -and $Config.BusinessSelectionCount -ge 10 -and $Config.BusinessSelectionCount -le 30 -and
            $Selection.RequestedCount -eq $Config.BusinessSelectionCount -and $selected.Count -eq $Selection.RequestedCount -and $selected.Count -eq $SelectedIds.Count -and
            !(Difference $selected $SelectedIds).Count -and $selectionSpecs.Count -eq $selected.Count -and !(Difference $selected $selectionSpecIds).Count)
        AtLeastTwoActualModifiedObjects=($completed.Count -ge 2 -and $completed.Count -eq $completedIds.Count -and $Edits.MinimumRealEdits -ge 2 -and $modified.Count -eq $completedIds.Count -and
            !(Difference $modified $completedIds).Count -and !$invalidAttempts.Count -and $beforeModified.Count -eq $modified.Count -and !(Difference $modified $beforeModified).Count -and
            $beforeCompleted.Count -eq $modified.Count -and !(Difference $modified $beforeCompleted).Count)
        ModifiedTargetsIncludedAndDeleted=($Selection.ModifiedObjectsAllIncluded -eq $true -and $selectionModified.Count -eq $modified.Count -and !(Difference $modified $selectionModified).Count -and
            !(Difference $modified $selected).Count -and !(Difference $modified $FullPhysical).Count -and !(Difference $modified $RequiredReadyPhysical).Count)
        ReadinessCoversEntireChangedBatch=($Changed.Count -gt 0 -and $Readiness.ExactDeletedEventCoverage -eq $true -and $records.Count -eq $recordIds.Count -and
            $recordIds.Count -eq $Changed.Count -and !(Difference $Changed $recordIds).Count -and !$invalidReadiness.Count -and $Readiness.AllTerminal -eq $true)
        AllVerificationPhysicalObjectsReady=($RequiredReadyPhysical.Count -gt 0 -and !(Difference $RequiredReadyPhysical $recordIds).Count -and !$notReadyPhysical.Count -and $Readiness.PhysicalReadyCount -eq @($records | Where-Object { $FullPhysical.Contains(([long]$_.ElementId).ToString()) -and $_.State -eq 'Ready' }).Count)
        ModifiedObjectsReady=($modified.Count -ge 2 -and $Readiness.ModifiedReady -eq $true -and $readinessModified.Count -eq $modified.Count -and
            !(Difference $modified $readinessModified).Count -and !$notReadyModified.Count)
    }
    $failed=@($checks.Keys | Where-Object { $checks[$_] -ne $true })
    return [pscustomobject]@{Passed=(!$failed.Count);Checks=[pscustomobject]$checks;FailedOrUnverifiedChecks=$failed;DateErrors=@($dateErrors);
        SelectedPhysicalCount=$selected.Count;FullDeletedPhysicalCount=$FullPhysical.Count;RequiredReadyPhysicalCount=$RequiredReadyPhysical.Count;FullChangedCount=$Changed.Count;
        MinimumWarmupSeconds=$requestedWarmup;ActualWarmupSeconds=$warmup;MinimumWorkAfterEditsSeconds=$requestedWork;ActualWorkAfterEditsSeconds=$work;
        CompletedEditsCount=$completedIds.Count;CompletedEditIds=@($completedIds);SkippedEdits=@($attempts | Where-Object Skipped -eq $true);InvalidEditAttempts=@($invalidAttempts);
        ReadinessNotReadyPhysicalIds=$notReadyPhysical;ReadinessInvalidRecords=$invalidReadiness;
        Scope='Explicit business selection before deletion; every actual physical cascade victim remains in verification. No whole original model restoration claim.';
        ParameterProofLimitation='Instance parameters and primary type definition probes are required. Reference identity/provenance alone does not prove every referenced definition version, binding or unsupported constraint.'}
}

$config=Read-Report 'config.json'
$isBusiness=$config.BusinessWorkflow -eq $true
$isDurable=$config.Scenario -eq 'durable'
$isDurableRestore=$isDurable -and $config.DurablePhase -eq 'restore'
$isPartialDurableRestore=$isDurableRestore -and $config.AllowPartialDurableRestore -eq $true
$captureDirectory=if($isDurableRestore) { [IO.Path]::GetFullPath($config.DurableCaptureDirectory) } else { $run }
$ready=Read-Report 'ready.json'
$inventory=Read-Report 'inventory.json' $captureDirectory
$deletion=Read-Report 'deletion.json' $captureDirectory
$journal=Read-Report 'journal-summary.json' $captureDirectory
$load=Read-Report 'load.json'
$restore=Read-Report 'restore-result.json'
$verification=Read-Report 'verification.json'
$reopened=Read-Report 'reopened-verification.json'
$persistence=Read-Report 'persistence.json'
$integrity=Read-Report 'integrity.json'
$finished=Read-Report 'finished.json'
$durableEvidence=$null
$partialEvidence=$null
if($isDurableRestore) {
    if($isPartialDurableRestore) {
        $partialManifest=Read-Report 'partial-subset.json' $captureDirectory
        $durableClosure=Read-Report 'partial-session-closed.json' $captureDirectory
        $partialCloseReady=Read-Report 'partial-close-ready.json' $captureDirectory
        $partialRemoval=Read-Report 'partial-bridge-removal.json' $captureDirectory
    } else {
        $durableClosure=Read-Report 'durable-session-closed.json' $captureDirectory
        $durableConversion=Read-Report 'durable-conversion.json' $captureDirectory
    }
    $durableBaseline=Read-Report 'durable-phase-two-baseline.json'
    $durableInputs=Read-Report 'durable-restoration-inputs.json'
    if(!$isPartialDurableRestore) { $durableEvidence=Check-DurableEvidence $durableClosure $durableConversion $durableBaseline $durableInputs $ready $integrity }
}
$durableProven=(!$isDurable -or ($isDurableRestore -and $durableEvidence.Passed))
$diskIntegrity=if(Test-Path -LiteralPath (Join-Path $run 'disk-integrity-after-interruption.json') -PathType Leaf) { Read-Report 'disk-integrity-after-interruption.json' } else { $null }
$changed=Id-Set $deletion.DocumentChangedDeletedIds
$physical=Id-Set $deletion.DeletedPhysicalIds
$specs=@($inventory.Specs | Where-Object { $null -ne $_ })
$selectedSpecs=@($specs | Where-Object Selected -eq $true)
$selectedWithoutGeometry=@($selectedSpecs | Where-Object { !$_.Point -and !$_.Curve -and !$_.BBox -and !$_.Transform } | ForEach-Object Id)
$unexplainedExcluded=@($specs | Where-Object { $_.Selected -ne $true -and [string]::IsNullOrWhiteSpace([string]$_.ProtectionReason) -and [string]::IsNullOrWhiteSpace([string]$_.SelectionExclusionReason) } | ForEach-Object Id)
# These are factual subgroups only. Never remove them from the harness's
# original denominator to manufacture a passing broad-scope result.
$allKnownInternal=@($specs | Where-Object {
    $_.Class -in @('PropertySetElement','SunAndShadowSettings') -or
    ($_.Class -eq 'Element' -and ([string]$_.Category -match '(?i)axe|legend|légend|camera|caméra'))
})
$allPolicyExcluded=@($specs | Where-Object {
    $_.Class -in @('PipeInsulation','DuctInsulation') -or
    ([string]$_.Category -match '(?i)calorifuge|insulation|isolant.+canalisation|isolant.+gaine') -or
    (([string]$_.Name + ' ' + [string]$_.FamilyName) -match '(?i)calorifuge')
})
$knownInternal=@($allKnownInternal | Where-Object Selected -eq $true)
$policyExcluded=@($allPolicyExcluded | Where-Object Selected -eq $true)
$internalIds=Id-Set ($allKnownInternal | ForEach-Object Id)
$policyIds=Id-Set ($allPolicyExcluded | ForEach-Object Id)
$specIdByUid=@{}
foreach($spec in $specs) { if($spec.Uid) { $specIdByUid[[string]$spec.Uid]=([long]$spec.Id).ToString() } }
$catalogById=@{}
foreach($entry in $inventory.Catalog) { $catalogById[([long]$entry.Id).ToString()]=$entry }
$selectedIds=Id-Set ($selectedSpecs | ForEach-Object Id)
$physicalUniverse=if(@($inventory.PhysicalEligibleIds).Count) { Id-Set $inventory.PhysicalEligibleIds } else { $selectedIds }
$independentPhysical=Id-Set ($physicalUniverse | Where-Object { $changed.Contains($_) })
$explicitlyExcluded=Id-Set $inventory.ExplicitlyExcludedPhysicalIds
$excludedCascadeVictims=@($physical | Where-Object { $explicitlyExcluded.Contains($_) })
$missingPhysicalClassification=@(Difference $independentPhysical $physical)
$unexpectedPhysicalClassification=@(Difference $physical $independentPhysical)
$survivingSelected=@(Difference $selectedIds $changed)
$batchIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($batchId in $journal.DeletionBatchIds) { if($batchId) { $null=$batchIds.Add([string]$batchId) } }
$raw=[Collections.Generic.List[object]]::new()
$journalDirectory=Join-Path $captureDirectory 'history'
$files=if(Test-Path -LiteralPath $journalDirectory) { @(Get-ChildItem -LiteralPath $journalDirectory -File | Where-Object Name -match '^element-history-.*\.jsonl(?:\.gz)?$') } else { @() }
if(!$files.Count) { $missingFiles.Add('history/element-history-*.jsonl') }
foreach($file in $files) {
    $stream=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $payload=$stream
    if($file.Extension -eq '.gz') { $payload=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress) }
    $reader=[IO.StreamReader]::new($payload,[Text.Encoding]::UTF8)
    $lineNumber=0
    try {
        while($null -ne ($line=$reader.ReadLine())) {
            $lineNumber++
            try {
                $event=$line | ConvertFrom-Json -AsHashtable
                if($event.Action -ne 'delete' -or !$batchIds.Contains([string]$event.DeletionAudit.BatchId)) { continue }
                # Keep only evidence required for this independent count; avoid
                # retaining thousands of full geometry/parameter payloads in RAM.
                $raw.Add([pscustomobject]@{Id=([long]$event.ElementId).ToString([Globalization.CultureInfo]::InvariantCulture);
                    EventId=$event.EventId;
                    BatchId=$event.DeletionAudit.BatchId;Category=$event.Category;SnapshotMissing=($event.Delta.snapshotMissing -eq $true);
                    HasRecipe=($null -ne $event.Delta.recipe);RecipeKind=$event.Delta.recipe.Kind;
                    PrimaryNativeReady=($event.Delta.recipe.Native.Ready -eq $true);PrimaryNativeWholeModel=($event.Delta.recipe.Native.WholeModel -eq $true);
                    OpeningFallbackReady=($event.Delta.recipe.OpeningFallback.Ready -eq $true);
                    SourceUid=$event.UniqueId;Ts=$event.Ts})
            } catch { $parseErrors.Add([pscustomobject]@{File=$file.Name;Line=$lineNumber;Error=$_.Exception.Message}) }
        }
    } finally { $reader.Dispose(); if($payload -ne $stream) { $payload.Dispose() };$stream.Dispose() }
}
$persisted=Id-Set ($raw | ForEach-Object Id)
$missingChanged=@(Difference $changed $persisted)
$unexpectedEvents=@(Difference $persisted $changed)
$missingPhysical=@(Difference $physical $persisted)
$duplicateEvents=@($raw | Group-Object Id | Where-Object Count -gt 1 | Select-Object Name,Count)
$withoutRecipe=@($raw | Where-Object HasRecipe -eq $false)
$recipeMissingRecords=@($withoutRecipe | ForEach-Object {
    $entry=$catalogById[$_.Id]
    [pscustomobject]@{Id=$_.Id;Category=$_.Category;Class=$entry.Class;Name=$entry.Name;ProtectionReason=$entry.ProtectionReason;
        SelectedDeleted=$physical.Contains($_.Id)}
})
$coverage=@($inventory.SnapshotCoverage | Where-Object { $null -ne $_ })
$nativePendingCandidates=@($coverage | Where-Object { $_.Kind -eq 'native_archive' -and $_.DetailCaptureAttempted -eq $false } | ForEach-Object {
    $coverageEntry=$_;$id=([long]$coverageEntry.Id).ToString();$entry=$catalogById[$id]
    $event=$raw | Where-Object Id -eq $id | Select-Object -First 1
    [pscustomobject]@{Id=$coverageEntry.Id;Class=$entry.Class;Category=$entry.Category;Name=$entry.Name;
        SelectedDeleted=$physical.Contains($id);PersistedNativeWholeModelReady=($event.PrimaryNativeWholeModel -and $event.PrimaryNativeReady)}
})
$range=Id-Set $load.FullEventIds
$recent=Id-Set $load.DefaultEventIds
$requestIds=Id-Set $load.RequestEventIds
if($isPartialDurableRestore) {
    $partialEvidence=Check-PartialSubsetEvidence $partialManifest $durableClosure $partialCloseReady $partialRemoval $durableBaseline $durableInputs $ready $integrity $physical $changed $raw $specs $load $captureDirectory
    $durableProven=$partialEvidence.Passed
}
$verificationPhysical=if($isPartialDurableRestore) { Id-Set $partialManifest.SelectedPhysicalIds } else { $physical }
$missingRangePhysical=@(Difference $physical $range)
$missingDefaultPhysical=@(Difference $physical $recent)
$missingRequestPhysical=@(Difference $physical $requestIds)
$rangeMissingChanged=@(Difference $changed $range)
$physicalUids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($spec in $specs) { if($physical.Contains(([long]$spec.Id).ToString()) -and $spec.Uid) { $null=$physicalUids.Add([string]$spec.Uid) } }
$physicalFailures=[Collections.Generic.List[object]]::new()
$ancillaryFailures=[Collections.Generic.List[object]]::new()
foreach($item in @($restore.Items | Where-Object { $null -ne $_ })) {
    if($item.Created -eq $true -or $item.Existing -eq $true -or $item.IncludedInParent -eq $true) { continue }
    $auditId=if($null -ne $item.DeletionAudit.ElementId) { ([long]$item.DeletionAudit.ElementId).ToString() } else { '' }
    if(!$auditId -and $specIdByUid.ContainsKey([string]$item.SourceUniqueId)) { $auditId=$specIdByUid[[string]$item.SourceUniqueId] }
    $classification=if($policyIds.Contains($auditId)) {'explicit-insulation-policy'} elseif($internalIds.Contains($auditId)) {'internal-model-category-record'} elseif($physical.Contains($auditId)) {'other-declared-model-candidate'} else {'other-dependent-or-unclassified'}
    $failure=[pscustomobject]@{SourceUid=$item.SourceUniqueId;ElementId=$auditId;Category=$item.Category;Label=$item.Label;Reason=$item.Reason;Detail=$item.Detail;Classification=$classification}
    if($physicalUids.Contains([string]$item.SourceUniqueId) -or $physical.Contains($auditId)) { $physicalFailures.Add($failure) } else { $ancillaryFailures.Add($failure) }
}
$verified=Check-Verification $verification $verificationPhysical $isDurableRestore $isBusiness
$verifiedReopened=Check-Verification $reopened $verificationPhysical $isDurableRestore $isBusiness
$verificationByScope=@(foreach($scope in @('internal-model-category-record','explicit-insulation-policy','other-declared-model-candidate')) {
    $scopeRecords=@($verification.Records | Where-Object {
        $id=([long]$_.SourceId).ToString()
        $kind=if($internalIds.Contains($id)) {'internal-model-category-record'} elseif($policyIds.Contains($id)) {'explicit-insulation-policy'} else {'other-declared-model-candidate'}
        $kind -eq $scope
    })
    [pscustomobject]@{Scope=$scope;Checks=Summarize-VerificationRecords $scopeRecords}
})
$verificationByCategory=@($verification.Records | Group-Object Class,Category | ForEach-Object {
    [pscustomobject]@{Class=$_.Group[0].Class;Category=$_.Group[0].Category;Checks=Summarize-VerificationRecords $_.Group}
})
$noUnexpected=($verified.NoUnexpectedModelObjects -and $verifiedReopened.NoUnexpectedModelObjects)
$persistenceProven=($persistence.Closed -eq $true -and $persistence.OldDocumentInvalid -eq $true -and
    $persistence.AbsentFromApplicationDocuments -eq $true -and $persistence.ReopenedDistinctDocument -eq $true -and
    $persistence.FileExists -eq $true -and $persistence.FileLength -gt 0)
$captureComplete=($changed.Count -gt 0 -and $batchIds.Count -gt 0 -and !$missingChanged.Count -and !$unexpectedEvents.Count -and !$duplicateEvents.Count)
$classificationComplete=(!$missingPhysicalClassification.Count -and !$unexpectedPhysicalClassification.Count -and $physical.Count -gt 0)
$missingVerificationRange=@(Difference $verificationPhysical $range)
$missingVerificationRequests=@(Difference $verificationPhysical $requestIds)
$restorationChecks=($Disposition -ne 'Interrupted' -and $durableProven -and $classificationComplete -and !$missingPhysical.Count -and !$missingVerificationRange.Count -and !$missingVerificationRequests.Count -and !$physicalFailures.Count -and $verified.Passed -and $verifiedReopened.Passed -and $persistenceProven)
$hashVerified=($integrity.SourceUnchanged -eq $true -and $integrity.SourceSha256Before -and $integrity.SourceSha256Before -eq $integrity.SourceSha256After)
$finishProven=($finished.Closed -eq $true -and $finished.OldDocumentInvalid -eq $true -and $finished.AbsentFromApplicationDocuments -eq $true -and
    $finished.SourceUnchanged -eq $true -and $finished.CopyAndSourceUnchanged -eq $true -and $finished.SourceSha256Before -and
    $finished.SourceSha256Before -eq $finished.SourceSha256After)
$largeBatch=($changed.Count -gt 2000)
$plannedSubset=($null -ne $config.PhysicalTargetFraction -and $config.PhysicalTargetFraction -lt 1) -or @($config.SkipPhysicalCategories | Where-Object { $_ }).Count -gt 0 -or @($config.SkipPhysicalIds | Where-Object { $null -ne $_ }).Count -gt 0
$samplingEvidence=$null;$samplingProven=$true
if($plannedSubset -and !$isBusiness) {
    $samplingEvidence=Read-Report 'sampling-plan.json' $captureDirectory
    $sampleIds=Id-Set $samplingEvidence.SelectedPhysicalIds
    $forcedIds=Id-Set $samplingEvidence.ForcedModifiedPhysicalIds
    $samplingProven=($samplingEvidence.PhysicalEligibleBeforeSamplingCount -eq $physicalUniverse.Count -and
        $sampleIds.Count -eq $selectedIds.Count -and @($selectedIds | Where-Object { !$sampleIds.Contains($_) }).Count -eq 0 -and
        @($forcedIds | Where-Object { !$selectedIds.Contains($_) }).Count -eq 0 -and !$excludedCascadeVictims.Count -and
        $samplingEvidence.AllowedPhysicalCount -gt 0 -and [Math]::Abs([double]$samplingEvidence.SelectedFractionOfAllowed-[double]$config.PhysicalTargetFraction) -le .05)
}
$delayEvidence=$null;$delayProven=$true;$businessEvidence=$null;$partialPreparationSafety=$null;$delayChecks=$null
if($isBusiness) {
    $businessSelection=Read-Report 'business-selection.json' $captureDirectory
    $businessEdits=Read-Report 'business-edits.json' $captureDirectory
    $businessBeforeDelete=Read-Report 'business-before-delete.json' $captureDirectory
    $businessReadiness=Read-Report 'business-readiness.json' $captureDirectory
    $businessEvidence=Check-BusinessWorkflowEvidence $config $businessSelection $businessEdits $businessBeforeDelete $businessReadiness $inventory $selectedIds $physical $changed $raw $verificationPhysical
    $samplingProven=$businessEvidence.Checks.ExplicitTenToThirtyTargets -and $businessEvidence.Checks.ModifiedTargetsIncludedAndDeleted
    $delayProven=$businessEvidence.Passed
    if($isPartialDurableRestore) {
        $partialPreparationSafety=Check-PartialPreparationSafety $partialCloseReady $durableClosure $partialRemoval $captureDirectory
        $durableProven=$durableProven -and $partialPreparationSafety.Passed
        $restorationChecks=$restorationChecks -and $partialPreparationSafety.Passed
    }
} else {
    if($config.DeletionDelaySeconds -gt 0) { $delayEvidence=Read-Report 'delayed-before-delete.json' $captureDirectory }
    $delayChecks=Check-DelayedCaptureEvidence $config $delayEvidence $inventory
    $delayProven=$delayChecks.Passed
}
$noInputErrors=(!$missingFiles.Count -and !$parseErrors.Count)
$protocolComplete=($noInputErrors -and $durableProven -and $samplingProven -and $delayProven -and $persistenceProven -and $finishProven -and $hashVerified -and $integrity.Closed -eq $true)
$strictSizeGate=if($isBusiness) { $businessEvidence.Checks.ExplicitTenToThirtyTargets } elseif($plannedSubset) { $physical.Count -gt 0 } else { $largeBatch }
$strictChecks=($Disposition -ne 'Interrupted' -and $protocolComplete -and $captureComplete -and !$ancillaryFailures.Count -and !$rangeMissingChanged.Count -and !$survivingSelected.Count -and $strictSizeGate -and $noUnexpected -and !$knownInternal.Count -and !$selectedWithoutGeometry.Count -and !$unexplainedExcluded.Count)
$verdicts=Resolve-RestorationVerdicts $isPartialDurableRestore $restorationChecks $protocolComplete $captureComplete $samplingProven $delayProven $strictChecks
$physicalComplete=$verdicts.PhysicalRestoreAndPersistencePassed
$strictPass=$verdicts.StrictPassed
$subsetComplete=$verdicts.SelectedSubsetRestoreAndPersistencePassed
$summary=[pscustomobject]@{
    ReportVersion=3;GeneratedUtc=[DateTime]::UtcNow;RunDirectory=$run;Scenario=$config.Scenario;CaptureDirectory=$captureDirectory
    RunStatus=if($Disposition -eq 'Interrupted') {'Interrupted'} elseif($Disposition -eq 'RestoreCompletedAfterInterruptionAnnounced') {'RestoreCompletedAfterInterruptionAnnounced'} elseif($subsetComplete) {'SelectedSubsetVerifiedOnly'} elseif($strictPass) {'CompleteVerified'} elseif($Disposition -eq 'Completed' -and $protocolComplete) {'CompletedWithRestorationFailures'} else {'IncompleteOrFailed'}
    Disposition=$Disposition;DispositionReason=$DispositionReason
    ProtocolCompleted=$protocolComplete
    StrictPassed=$strictPass;PhysicalRestoreAndPersistencePassed=$physicalComplete;LargeBatchAbove2000WasTested=$largeBatch
    SelectedSubsetRestoreAndPersistencePassed=$subsetComplete
    FullDeletedPhysicalCount=$physical.Count;RestorationVerificationPhysicalCount=$verificationPhysical.Count
    LargeBatchAbove10000WasTested=($changed.Count -gt 10000)
    NoUnexpectedModelObjects=$noUnexpected
    DurableEvidence=$durableEvidence
    PartialSubsetEvidence=$partialEvidence
    BusinessWorkflowEvidence=$businessEvidence
    PartialPreparationSafety=$partialPreparationSafety
    PlannedPhysicalSubset=[pscustomobject]@{Requested=($plannedSubset -or $isBusiness);Proven=$samplingProven;OriginalPhysicalUniverse=$physicalUniverse.Count;
        SelectedPhysical=$selectedIds.Count;ExcludedPhysicalIds=@($explicitlyExcluded);UnexpectedExcludedCascadeVictims=$excludedCascadeVictims;
        SamplingEvidence=$samplingEvidence;
        ScopeWanted=if($isBusiness) {'explicit business selection of ten to thirty physical objects'} elseif($plannedSubset) {'planned physical sample with explicit exclusions'} else {'all admissible physical objects selected for deletion'};
        Scope=if($isPartialDurableRestore) {'explicit converted subset only; original deleted denominator preserved'} elseif($isBusiness) {'business selection and all actual physical cascade victims; no whole original model claim'} elseif($plannedSubset) {'planned sampled deletion scope'} else {'declared deleted physical scope'}}
    DelayedCapture=[pscustomobject]@{RequestedSeconds=$config.DeletionDelaySeconds;Proven=$delayProven;Evidence=$delayEvidence;BusinessWorkflow=$isBusiness;
        MinuteAndRealCaptureProven=$delayChecks.MinuteAndRealCaptureProven;EditAttemptsProven=$delayChecks.EditAttemptsProven;
        ArtificialEditsFullyVerified=$delayChecks.ArtificialEditsFullyVerified;CompletedEditsCount=$delayChecks.CompletedEditsCount;SkippedEdits=$delayChecks.SkippedEdits;
        Warning='The clock starts after the opening API returns. Inventory progress is not a frame-rate measurement. Explicitly skipped artificial edits do not prove manual changes or geometry/join mutations.'}
    CaptureMode=[pscustomobject]@{OriginalDetailed=$ready.OriginalDetailed;RuntimeDetailed=$ready.RuntimeDetailed;
        Mode=if($ready.RuntimeDetailed -eq $true) {'Detailed'} elseif($ready.RuntimeDetailed -eq $false) {'Simple'} else {'Unverified'};
        Limitation='One run in one capture mode cannot establish a Simple-versus-Detailed comparison.'}
    MissingInputFiles=@($missingFiles);InputParseErrors=@($parseErrors)
    ScopeAudit=[pscustomobject]@{BroadScopeDefinitionError=($knownInternal.Count -gt 0 -or $selectedWithoutGeometry.Count -gt 0);KnownInternalSelectedCount=$knownInternal.Count;
        SelectedWithRecordedGeometryEvidence=($selectedSpecs.Count-$selectedWithoutGeometry.Count);SelectedWithoutGeometryEvidenceIds=$selectedWithoutGeometry;
        KnownInternalDeletedCount=@($knownInternal | Where-Object { $physical.Contains(([long]$_.Id).ToString()) }).Count;
        KnownInternalSelectedIds=@($knownInternal | ForEach-Object Id);PolicyExcludedSelectedCount=$policyExcluded.Count;
        PolicyExcludedDeletedCount=@($policyExcluded | Where-Object { $physical.Contains(([long]$_.Id).ToString()) }).Count;
        PolicyExcludedSelectedIds=@($policyExcluded | ForEach-Object Id);UnselectedSpecsWithoutProtectionReason=$unexplainedExcluded;
        SelectedClassAndCategoryCounts=@($selectedSpecs | Group-Object Class,Category | Select-Object Name,Count);
        Warning='Harness scope is preserved verbatim. Internal records and intentionally excluded insulation are reported separately; subgroup statistics never turn a failed original scope into PASS.'}
    Selection=[pscustomobject]@{SelectedPhysicalCount=$selectedIds.Count;SelectedRootCount=@($inventory.SelectedRootIds).Count;
        DocumentChangedDeletedCount=$changed.Count;DeletedDeclaredPhysicalCount=$physical.Count;SurvivingSelectedPhysicalIds=$survivingSelected;
        DocumentChangedModifiedIds=@($deletion.DocumentChangedModifiedIds);DocumentChangedAddedIds=@($deletion.DocumentChangedAddedIds);ModifiedCatalogRecords=@($deletion.ModifiedCatalogRecords);
        ModificationEvidenceLimitation='These are actual transaction notifications; they do not by themselves prove manual edits or geometry/join changes.';
        MissingPhysicalClassificationIds=$missingPhysicalClassification;UnexpectedPhysicalClassificationIds=$unexpectedPhysicalClassification}
    Journal=[pscustomobject]@{BatchIds=@($batchIds);CompleteChangedIdCoverage=$captureComplete;RawBatchLines=$raw.Count;UniqueEventIds=$persisted.Count;
        MissingChangedIds=$missingChanged;UnexpectedEventIds=$unexpectedEvents;MissingPhysicalIds=$missingPhysical;DuplicateEventIds=$duplicateEvents;
        SnapshotMissingCount=@($raw | Where-Object SnapshotMissing).Count;WithoutRecipeCount=@($raw | Where-Object HasRecipe -eq $false).Count;
        SelectedDeletedWithoutRecipeIds=@($withoutRecipe | Where-Object { $physical.Contains($_.Id) } | ForEach-Object Id);
        RecipeMissingRecords=$recipeMissingRecords;
        HarnessReportedFullCount=$journal.FullCount;HarnessMissingChangedIds=$journal.MissingChangedIds}
    PredeleteCoverage=[pscustomobject]@{EvidencePresent=($coverage.Count -gt 0);ReportedPendingCount=$inventory.StateBefore.Capture.Pending;
        CoverageSelectedCount=$coverage.Count;SelectedSnapshotMissingCount=@($coverage | Where-Object SnapshotPresent -ne $true).Count;
        SelectedSnapshotDetailNotAttemptedCount=@($coverage | Where-Object DetailCaptureAttempted -eq $false).Count;
        SelectedNativeWithDetailNotAttempted=$nativePendingCandidates;
        Warning='DetailCaptureAttempted=false is not the Pending membership list. Direct recipes may remain false after processing; coverage exports selected objects only. Ready persisted archives prove a receipt, not restored fidelity.'}
    Loading=[pscustomobject]@{FullRangeUniqueIds=$range.Count;DefaultRecentUniqueIds=$recent.Count;DefaultRecentLoadTruncatedPhysicalBatch=($missingDefaultPhysical.Count -gt 0);
        PhysicalIdsMissingFromDefaultLoad=$missingDefaultPhysical;PhysicalIdsMissingFromFullRange=$missingRangePhysical;ChangedIdsMissingFromFullRange=$rangeMissingChanged;
        ActualRequestUniqueIds=$requestIds.Count;PhysicalIdsMissingFromActualRestoreRequests=$missingRequestPhysical;
        VerificationPhysicalIdsMissingFromFullRange=$missingVerificationRange;VerificationPhysicalIdsMissingFromActualRequests=$missingVerificationRequests;
        HarnessRequestsCount=$load.RequestsCount;HarnessDisplayableDeletionCount=$load.DisplayableDeletionCount;
        ObservedHarnessRoute=$load.Route;UiListAndClusterSelectionObserved=($load.UiListAndClusterSelectionObserved -eq $true);
        Warning='A successful restore of a truncated recent-event subset never proves restoration of the full batch. UI request IDs and full-range load must be compared with the actual deleted set. An observed default count alone does not prove a configured cap.'}
    Restore=[pscustomobject]@{ResultObserved=($null -ne $restore);ReportedCreated=$restore.Created;ReportedExisting=$restore.Existing;ReportedIncludedInParent=$restore.IncludedInParent;ReportedFailed=$restore.Failed;
        FailuresByClassification=@(@($physicalFailures)+@($ancillaryFailures) | Group-Object Classification | Select-Object Name,Count);
        PhysicalFailures=@($physicalFailures);AncillaryFailures=@($ancillaryFailures)}
    Verification=$verified;ReopenedVerification=$verifiedReopened
    VerificationByScope=$verificationByScope;VerificationByClassAndCategory=$verificationByCategory
    UnexpectedModelObjectsByClassAndCategory=@($verification.UnexpectedModelObjects | Group-Object Class,Category | Select-Object Name,Count)
    Integrity=[pscustomobject]@{SourceHashesVerified=$hashVerified;Closed=$integrity.Closed;RestoredSavedPath=$integrity.RestoredSavedPath;
        DistinctDocumentReopenProven=$persistenceProven;PersistenceEvidence=$persistence;
        FinalDocumentClosedAndOriginalsUnchanged=$finishProven;FinishEvidence=$finished;
        DiskOnlySourceHashesVerified=($diskIntegrity.SourceUnchanged -eq $true -and $diskIntegrity.SourceSha256Before -and $diskIntegrity.SourceSha256Before -eq $diskIntegrity.SourceSha256After);
        DiskOnlyEvidence=$diskIntegrity}
}
$parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
if(!(Test-Path -LiteralPath $parent -PathType Container)) { throw "Output directory must already exist: $parent" }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($summary | ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
$summary | Select-Object RunStatus,StrictPassed,PhysicalRestoreAndPersistencePassed,SelectedSubsetRestoreAndPersistencePassed,LargeBatchAbove2000WasTested,LargeBatchAbove10000WasTested,@{Name='DeletedDeclaredPhysical';Expression={$_.Selection.DeletedDeclaredPhysicalCount}},@{Name='PersistedUniqueIds';Expression={$_.Journal.UniqueEventIds}},@{Name='DefaultLoadTruncated';Expression={$_.Loading.DefaultRecentLoadTruncatedPhysicalBatch}},@{Name='AncillaryFailures';Expression={if($_.Restore.ResultObserved) {$_.Restore.AncillaryFailures.Count} else {'Unverified'}}},@{Name='Report';Expression={[IO.Path]::GetFullPath($OutputPath)}} | ConvertTo-Json
