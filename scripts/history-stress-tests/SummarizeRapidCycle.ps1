param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$CaptureRunName,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RestoreRunName
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$capture = Join-Path $repo ('tmp/history-stress/' + $CaptureRunName)
$restore = Join-Path $repo ('tmp/history-stress/' + $RestoreRunName)
function Read-Evidence([string]$Root,[string]$Name) {
    $path = Join-Path $Root $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Required evidence is absent: ' + $path) }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed,[string]$Details='') { $checks.Add(@{Name=$Name;Passed=$Passed;Details=$Details}) }
$witnessTypeEvidence = [Collections.Generic.List[object]]::new()
$observerEvidenceGaps = [Collections.Generic.List[object]]::new()
$independentFidelityEvidence = [Collections.Generic.List[object]]::new()
function Get-RequiredField([object]$Object,[string]$Path) {
    $value=$Object
    foreach($segment in $Path.Split('.')) {
        $property=if($null -ne $value){$value.PSObject.Properties[$segment]}else{$null}
        if($null -eq $property){return [pscustomobject]@{Present=$false;Value=$null}}
        $value=$property.Value
    }
    [pscustomobject]@{Present=$true;Value=$value}
}
function Check-IndependentFidelity([object]$Verification,[string]$EvidenceName,[long[]]$DeletedIds,[long[]]$WitnessIds,[long[]]$DeletedEventIds=@(),[object[]]$Catalog=@()) {
    $phaseIssues=[Collections.Generic.List[string]]::new()
    $aggregate=Get-RequiredField $Verification 'AllDeletedPhysicalVerified'
    if(!$aggregate.Present -or $aggregate.Value -isnot [bool] -or $aggregate.Value -ne $true){$phaseIssues.Add('AllDeletedPhysicalVerified must explicitly be true.')}
    $denominator=Get-RequiredField $Verification 'DeletedPhysicalCount'
    if(!$denominator.Present -or ($denominator.Value -isnot [long] -and $denominator.Value -isnot [int]) -or $denominator.Value -ne $DeletedIds.Count){$phaseIssues.Add('DeletedPhysicalCount must equal the actual physical deletion denominator.')}
    foreach($path in @('Missing','Duplicates','Failed','ReverseOriginConflicts','UnexpectedModelObjects')) {
        $field=Get-RequiredField $Verification $path
        if(!$field.Present -or $field.Value -isnot [Array] -or $field.Value.Count -ne 0){$phaseIssues.Add($path+' must be an explicitly present empty JSON array.')}
    }
    $recordsField=Get-RequiredField $Verification 'Records'
    $records=if($recordsField.Present -and $recordsField.Value -is [Array]){@($recordsField.Value)}else{@()}
    $actualIds=@($records | ForEach-Object { $id=Get-RequiredField $_ 'SourceId'; if($id.Present -and ($id.Value -is [long] -or $id.Value -is [int])){$id.Value} })
    if(!$recordsField.Present -or $recordsField.Value -isnot [Array] -or $records.Count -ne $DeletedIds.Count -or $actualIds.Count -ne $records.Count -or @($actualIds|Sort-Object -Unique).Count -ne $actualIds.Count -or @(Compare-Object @($DeletedIds|Sort-Object) @($actualIds|Sort-Object)).Count -ne 0){$phaseIssues.Add('Records must cover every actual deleted physical ID exactly once with numeric SourceId values.')}
    Check ($EvidenceName+': independent complete physical fidelity') ($phaseIssues.Count -eq 0) ($phaseIssues -join ' ')
    $independentFidelityEvidence.Add(@{Evidence=$EvidenceName;Scope='phase';Passed=($phaseIssues.Count -eq 0);Issues=@($phaseIssues);ExpectedDeletedIds=$DeletedIds;ActualRecordIds=$actualIds})
    $generatedField=Get-RequiredField $Verification 'GeneratedModelRecords'
    $generatedOK=$generatedField.Present -and $generatedField.Value -is [Array]
    $generated=if($generatedOK){@($generatedField.Value)}else{@()}
    foreach($record in $generated) {
        $generatedOK=$generatedOK -and $record.Class -ceq 'Autodesk.Revit.DB.Element' -and ($record.CategoryId -is [int] -or $record.CategoryId -is [long]) -and $record.CategoryId -eq -2008045
        foreach($path in @('ExactNativeElementClass','NativeCategoryWhitelisted')){$field=Get-RequiredField $record $path;$generatedOK=$generatedOK -and $field.Present -and $field.Value -is [bool] -and $field.Value}
        $baseline=Get-RequiredField $record 'BaselineDeletedRecords'
        $generatedOK=$generatedOK -and $baseline.Present -and $baseline.Value -is [Array] -and $baseline.Value.Count -gt 0
        $baselineDistinctIds=@($baseline.Value.Id|Sort-Object -Unique)
        $generatedOK=$generatedOK -and $baselineDistinctIds.Count -eq @($baseline.Value).Count
        foreach($entry in @($baseline.Value)){
            $source=@($Catalog|Where-Object Id -EQ $entry.Id)
            $generatedOK=$generatedOK -and $source.Count -eq 1 -and $entry.Class -ceq 'Element' -and $entry.CategoryId -eq $record.CategoryId -and $entry.ProtectionReason -ceq 'generated-model-category' -and $entry.Id -in $DeletedEventIds
            if($source.Count -eq 1){foreach($path in @('Id','Uid','Class','CategoryId','ProtectionReason','ElementType')){$actual=Get-RequiredField $entry $path;$expected=Get-RequiredField $source[0] $path;$generatedOK=$generatedOK -and $actual.Present -and $expected.Present -and $actual.Value -ceq $expected.Value}}
        }
        $generatedOK=$generatedOK -and @($generated|Where-Object CategoryId -EQ $record.CategoryId).Count -le $baselineDistinctIds.Count
    }
    Check ($EvidenceName+': exact native generated records with deleted protected baseline and bounded count') $generatedOK
    foreach($sourceId in $WitnessIds) {
        $matching=@($records | Where-Object { $_.SourceId -eq $sourceId })
        $issues=[Collections.Generic.List[string]]::new()
        $fields=@{}
        if($matching.Count -ne 1){$issues.Add('Expected exactly one complete witness record; observed '+$matching.Count+'.')}
        else {
            $record=$matching[0]
            foreach($path in @('Passed','MatchesIdentity','IdentityInjective','ClassMatches','TypeMatches','GeometryChecked','GeometryMatches','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','WallJoinsMatch','ConnectionsMatch','SystemTypeMatches','PortSystemTypesMatch','ExcavationsMatch','InstanceParametersMatch','InstanceParameterEvidence.Passed')) {
                $field=Get-RequiredField $record $path
                $fields[$path]=@{Present=$field.Present;Actual=$field.Value}
                if(!$field.Present -or $field.Value -isnot [bool] -or $field.Value -ne $true){$issues.Add($path+' must explicitly be Boolean true.')}
            }
            foreach($path in @('ExpectedProbeErrors','ActualProbeErrors','InstanceParameterEvidence.Differences','TypeDefinitionEvidence.ParameterDifferences')) {
                $field=Get-RequiredField $record $path
                if(!$field.Present -or $field.Value -isnot [Array] -or $field.Value.Count -ne 0){$issues.Add($path+' must be an explicitly present empty JSON array.')}
            }
            $expectedCount=Get-RequiredField $record 'InstanceParameterEvidence.ExpectedCount'
            $actualCount=Get-RequiredField $record 'InstanceParameterEvidence.ActualCount'
            if(!$expectedCount.Present -or !$actualCount.Present -or ($expectedCount.Value -isnot [long] -and $expectedCount.Value -isnot [int]) -or ($actualCount.Value -isnot [long] -and $actualCount.Value -isnot [int]) -or $expectedCount.Value -le 0 -or $expectedCount.Value -ne $actualCount.Value){$issues.Add('Instance parameter counts must be present positive matching integers.')}
        }
        Check ($EvidenceName+': witness '+$sourceId+' independent geometry, relations and parameters') ($issues.Count -eq 0) ($issues -join ' ')
        $independentFidelityEvidence.Add(@{Evidence=$EvidenceName;Scope='witness';SourceId=$sourceId;Passed=($issues.Count -eq 0);Issues=@($issues);BooleanFields=$fields})
    }
}
function Test-RapidProtectedCatalog([object]$Entry) {
    if($null -eq $Entry -or $Entry.ElementType -isnot [bool] -or $Entry.ElementType -or [string]::IsNullOrWhiteSpace($Entry.Uid)){return $false}
    switch -CaseSensitive ($Entry.Class) {
        'LinearDimension' {return $Entry.CategoryId -eq -2000261 -and $Entry.ProtectionReason -ceq 'not-model-category'}
        'SpotDimension' {return $Entry.CategoryId -eq -2000263 -and $Entry.ProtectionReason -ceq 'view-or-view-specific'}
        'IndependentTag' {return $Entry.CategoryId -eq -2008047 -and $Entry.ProtectionReason -ceq 'view-or-view-specific'}
        'DataStorage' {return $null -eq $Entry.CategoryId -and $Entry.ProtectionReason -ceq 'definition-or-system'}
        'Element' {return $Entry.CategoryId -eq -2008045 -and $Entry.ProtectionReason -ceq 'generated-model-category'}
        default {return $false}
    }
}
function Check-RapidRequestSelection([object]$Evidence,[long[]]$DeletedEventIds,[long[]]$PhysicalIds,[object[]]$Catalog) {
    $ok=$Evidence.Passed -is [bool] -and $Evidence.Passed
    foreach($contract in @(@{Path='FullRequestsCount';Value=$DeletedEventIds.Count},@{Path='RestorationRequestsCount';Value=$PhysicalIds.Count},@{Path='ExcludedNonPhysicalCount';Value=($DeletedEventIds.Count-$PhysicalIds.Count)})) {
        $field=Get-RequiredField $Evidence $contract.Path;$ok=$ok -and $field.Present -and ($field.Value -is [int] -or $field.Value -is [long]) -and $field.Value -eq $contract.Value
    }
    $field=Get-RequiredField $Evidence 'Records';$rows=if($field.Present -and $field.Value -is [Array]){@($field.Value)}else{@()}
    $ok=$ok -and $rows.Count -eq $DeletedEventIds.Count -and @($rows.SourceId|Sort-Object -Unique).Count -eq $DeletedEventIds.Count -and @(Compare-Object @($rows.SourceId|Sort-Object) @($DeletedEventIds|Sort-Object)).Count -eq 0
    Check 'Request selection: every natural deletion event and UI conversion retained, exact physical-only denominator' $ok
    foreach($id in $DeletedEventIds) {
        $matching=@($rows|Where-Object SourceId -EQ $id);$source=@($Catalog|Where-Object Id -EQ $id);$rowOK=$matching.Count -eq 1 -and $source.Count -eq 1
        if($rowOK) {
            $row=$matching[0];$entry=$source[0];$physical=$id -in $PhysicalIds
            $rowOK=$row.SourceId -eq $id -and $row.EventElementId -eq $id -and $row.RequestAuditElementId -eq $id -and $row.Physical -is [bool] -and $row.Physical -eq $physical
            foreach($path in @('Id','Uid','Class','CategoryId','ProtectionReason','ElementType')){$actual=Get-RequiredField $row.Baseline $path;$expected=Get-RequiredField $entry $path;$rowOK=$rowOK -and $actual.Present -and $expected.Present -and $actual.Value -ceq $expected.Value}
            if($physical) {
                $rowOK=$rowOK -and $row.EvidenceKind -ceq 'PhysicalRestoration' -and $null -eq $entry.ProtectionReason -and $row.RequestSourceUid -ceq $entry.Uid -and $row.RequestRecipeNull -is [bool] -and !$row.RequestRecipeNull
            } else {
                $rowOK=$rowOK -and (Test-RapidProtectedCatalog $entry) -and $row.RequestRecipeNull -is [bool] -and $row.RequestRecipeNull
                if($row.EvidenceKind -ceq 'LiveExcludedMetadata') {
                    $rowOK=$rowOK -and $row.LiveExclusionValidated -is [bool] -and $row.LiveExclusionValidated -and $row.RequestSourceUid -ceq $entry.Uid -and $row.LiveSourceUid -ceq $entry.Uid -and $row.LiveClass -ceq ('Autodesk.Revit.DB.'+$entry.Class) -and $row.LiveCategoryId -eq $entry.CategoryId -and $row.CaptureFailure -ceq 'Hors périmètre de restauration : objet exclu identifié avant sa suppression (annotation, axe, isolant ou lien/import externe).'
                } elseif($row.EvidenceKind -ceq 'ProtectedBaselineExplicitUnsupported') {
                    $rowOK=$rowOK -and $row.LiveExclusionValidated -is [bool] -and !$row.LiveExclusionValidated -and [string]::IsNullOrEmpty($row.RequestSourceUid) -and $row.CaptureFailure -ceq 'État non récupérable : Catégorie non reconstructible directement avec les types encore présents dans la maquette.'
                } else {$rowOK=$false}
            }
        }
        Check ('Request selection: event '+$id+' independently proven physical or explicitly unavailable protected record') $rowOK
    }
}
function Check-WitnessExistingDefinitions([object]$Verification,[string]$EvidenceName,[long[]]$WitnessIds) {
    foreach($sourceId in $WitnessIds) {
        $records = @($Verification.Records | Where-Object { $null -ne $_ -and $_.PSObject.Properties['SourceId'] -and $_.SourceId -eq $sourceId })
        if($records.Count -ne 1) {
            $detail = 'Expected exactly one record for edited SourceId ' + $sourceId + '; observed ' + $records.Count + '. Complete StressObserver.Verify in scripts/history-stress-tests/StressObserver.cs for a new run if this observer omits witness records.'
            Check ($EvidenceName + ': witness ' + $sourceId + ' has one verification record') $false $detail
            $observerEvidenceGaps.Add(@{Evidence=$EvidenceName;SourceId=$sourceId;Path='Records[SourceId]';Details=$detail})
            continue
        }
        $record = $records[0]
        foreach($contract in @(
            @{Path='UsesExistingModelDefinitions';Expected=$true},
            @{Path='ExistingModelDefinitionsUsed';Expected=$true},
            @{Path='TypeUidMatchesSource';Expected=$true},
            @{Path='FamilyUidMatchesSource';Expected=$true},
            @{Path='TypeDefinitionParametersMatch';Expected=$true},
            @{Path='TypeDefinitionEvidence.UsesExistingModelDefinitions';Expected=$true},
            @{Path='TypeDefinitionEvidence.ExistingModelDefinitionsUsed';Expected=$true}
        )) {
            $value = $record
            $present = $true
            foreach($segment in $contract.Path.Split('.')) {
                $property = if($null -ne $value){$value.PSObject.Properties[$segment]}else{$null}
                if($null -eq $property) { $present=$false; $value=$null; break }
                $value=$property.Value
            }
            $passed = $present -and $value -is [bool] -and $value -eq $contract.Expected
            $detail = if(!$present) {
                'Required field absent. Complete StressTypeDefinitions.CompareTypeDefinition in scripts/history-stress-tests/StressTypeDefinitions.cs and its TypeDefinitionEvidence serialization in StressObserver.Verify (scripts/history-stress-tests/StressObserver.cs), then run a new observer. Missing evidence never defaults to success.'
            } elseif($value -isnot [bool]) {
                'Required field is not a JSON Boolean; null or inferred values do not prove reuse of the original project definitions.'
            } elseif(!$passed) {
                'Expected ' + $contract.Expected + '; observed ' + $value + '. Exact original type/family UIDs and unchanged type parameters are required; imported replacements cannot satisfy this contract.'
            } else { 'Explicit Boolean evidence from this verification record.' }
            Check ($EvidenceName + ': witness ' + $sourceId + ' ' + $contract.Path + '=' + $contract.Expected) $passed $detail
            $row = @{Evidence=$EvidenceName;SourceId=$sourceId;Path=$contract.Path;Expected=$contract.Expected;Present=$present;Actual=$value;Passed=$passed;Details=$detail}
            $witnessTypeEvidence.Add($row)
            if(!$present) { $observerEvidenceGaps.Add($row) }
        }
        $sourceType=Get-RequiredField $record 'TypeDefinitionEvidence.SourceTypeUid'
        $actualType=Get-RequiredField $record 'TypeDefinitionEvidence.ActualTypeUid'
        $expectedType=Get-RequiredField $record 'Expected.TypeUid'
        $currentType=Get-RequiredField $record 'Actual.TypeUid'
        $typeExact=$sourceType.Present -and $actualType.Present -and $expectedType.Present -and $currentType.Present -and $sourceType.Value -is [string] -and ![string]::IsNullOrWhiteSpace($sourceType.Value) -and $sourceType.Value -ceq $actualType.Value -and $sourceType.Value -ceq $expectedType.Value -and $sourceType.Value -ceq $currentType.Value
        Check ($EvidenceName+': witness '+$sourceId+' exact nonempty original type UID') $typeExact 'Both independent specs and type-definition evidence must identify the same original project type UID.'
        $sourceFamily=Get-RequiredField $record 'TypeDefinitionEvidence.SourceFamilyUid'
        $actualFamily=Get-RequiredField $record 'TypeDefinitionEvidence.ActualFamilyUid'
        $familyExact=$sourceFamily.Present -and $actualFamily.Present -and $sourceFamily.Value -ceq $actualFamily.Value
        if($record.Class -ceq 'FamilyInstance'){$familyExact=$familyExact -and $sourceFamily.Value -is [string] -and ![string]::IsNullOrWhiteSpace($sourceFamily.Value)}
        Check ($EvidenceName+': witness '+$sourceId+' exact original family UID') $familyExact 'A family witness requires its nonempty original family UID; system types must explicitly expose the matching null family UIDs.'
    }
}
function Check-ExistingDefinitionInputs([object]$Evidence,[long[]]$WitnessIds) {
    foreach($path in @('Passed','UsesExistingModelDefinitions')) {
        $field=Get-RequiredField $Evidence $path
        Check ('Actual loader requests: '+$path) ($field.Present -and $field.Value -is [bool] -and $field.Value)
    }
    $records=Get-RequiredField $Evidence 'Records'
    $rows=if($records.Present -and $records.Value -is [Array]){@($records.Value)}else{@()}
    Check 'Actual loader requests: exact three witness IDs' ($rows.Count -eq $WitnessIds.Count -and @($rows.SourceId|Sort-Object -Unique).Count -eq $WitnessIds.Count -and @(Compare-Object @($WitnessIds|Sort-Object) @($rows.SourceId|Sort-Object)).Count -eq 0)
    foreach($id in $WitnessIds) {
        $matching=@($rows|Where-Object SourceId -EQ $id)
        $ok=$matching.Count -eq 1
        if($ok) {
            foreach($path in @('UsesExistingModelDefinitions','NoNativeOrOpeningFallback')){$field=Get-RequiredField $matching[0] $path;$ok=$ok -and $field.Present -and $field.Value -is [bool] -and $field.Value}
            $count=Get-RequiredField $matching[0] 'RequestCount';$ok=$ok -and $count.Present -and ($count.Value -is [int] -or $count.Value -is [long]) -and $count.Value -eq 1
            $artifactCount=Get-RequiredField $matching[0] 'RecursiveNativeArtifactCount';$ok=$ok -and $artifactCount.Present -and ($artifactCount.Value -is [int] -or $artifactCount.Value -is [long]) -and $artifactCount.Value -eq 0
        }
        Check ('Actual loader requests: witness '+$id+' explicit existing definitions without native/opening artifacts') $ok
    }
}
function Check-RapidArchive([object]$Evidence,[string]$Name,[long[]]$WitnessIds) {
    foreach($path in @('UsesExistingModelDefinitions','NoNativePayloads','NoNewNativePayloads')){$field=Get-RequiredField $Evidence $path;Check ($Name+': '+$path) ($field.Present -and $field.Value -is [bool] -and $field.Value)}
    foreach($path in @('NativePayloadCount','NewNativePayloadCount')){$field=Get-RequiredField $Evidence $path;Check ($Name+': '+$path+'=0') ($field.Present -and ($field.Value -is [int] -or $field.Value -is [long]) -and $field.Value -eq 0)}
    foreach($path in @('NativePayloadFiles','NewNativePayloadFiles')){$field=Get-RequiredField $Evidence $path;Check ($Name+': '+$path+' explicitly empty') ($field.Present -and $field.Value -is [Array] -and $field.Value.Count -eq 0)}
    $journalBytes=Get-RequiredField $Evidence 'JournalBytes';$journalFiles=Get-RequiredField $Evidence 'JournalFiles'
    $measured=$journalBytes.Present -and ($journalBytes.Value -is [int] -or $journalBytes.Value -is [long]) -and $journalBytes.Value -gt 0 -and $journalFiles.Present -and $journalFiles.Value -is [Array] -and $journalFiles.Value.Count -gt 0
    Check ($Name+': actual positive journal byte measurement') $measured
    $files=Get-RequiredField $Evidence 'Files';$filesExplicit=$files.Present -and $files.Value -is [Array]
    Check ($Name+': independent file inventory contains no RVT/RFA payload') ($filesExplicit -and @($files.Value|Where-Object {[IO.Path]::GetExtension($_.Path) -in '.rvt','.rfa'}).Count -eq 0)
    $journalSum=if($journalFiles.Present -and $journalFiles.Value -is [Array]){($journalFiles.Value|Measure-Object -Property Bytes -Sum).Sum}else{$null}
    Check ($Name+': journal total agrees with per-file byte evidence') ($measured -and $journalSum -eq $journalBytes.Value)
    $recipes=Get-RequiredField $Evidence 'WitnessRecipes';$rows=if($recipes.Present -and $recipes.Value -is [Array]){@($recipes.Value)}else{@()}
    Check ($Name+': exact three measured witness recipes') ($rows.Count -eq $WitnessIds.Count -and @($rows.SourceId|Sort-Object -Unique).Count -eq $WitnessIds.Count -and @(Compare-Object @($WitnessIds|Sort-Object) @($rows.SourceId|Sort-Object)).Count -eq 0)
    foreach($id in $WitnessIds) {
        $matching=@($rows|Where-Object SourceId -EQ $id);$ok=$matching.Count -eq 1
        if($ok) {
            foreach($path in @('UsesExistingModelDefinitions','NoNativeOrOpeningFallback')){$field=Get-RequiredField $matching[0] $path;$ok=$ok -and $field.Present -and $field.Value -is [bool] -and $field.Value}
            $bytes=Get-RequiredField $matching[0] 'JsonUtf8Bytes';$ok=$ok -and $bytes.Present -and ($bytes.Value -is [int] -or $bytes.Value -is [long]) -and $bytes.Value -gt 0
        }
        Check ($Name+': witness '+$id+' actual JSON recipe bytes without payload') $ok
    }
}
function Check-PurgeRefusal([object]$Evidence) {
    foreach($path in @('Passed','UsesExistingModelDefinitions','OpenedUnderHistoricalScope','OwnedBeforePurge','ClosedWithoutSaving','PositiveChecksCompletedBeforePurge','OriginalTypeAndFamilyMissingBeforeRestore','ExplicitDefinitionMissingRefusal','OriginalTypeAndFamilyStillMissingAfterRestore','NoModelElementsAddedOrRemovedByRestore','NoNewNativePayloads','PositiveSavedModelAndDeletedCheckpointUnchanged','PreparedSourcesStillDenied','OldOpeningBridgesAbsent','SeparateCopyClosed')) {
        $field=Get-RequiredField $Evidence $path;Check ('Separate-copy actual purge: '+$path) ($field.Present -and $field.Value -is [bool] -and $field.Value)
    }
    $items=Get-RequiredField $Evidence 'RestoreResult.Items';$rows=if($items.Present -and $items.Value -is [Array]){@($items.Value)}else{@()}
    $ok=$rows.Count -eq 1 -and $Evidence.SourceTypeUid -is [string] -and ![string]::IsNullOrWhiteSpace($Evidence.SourceTypeUid)
    if($ok){$ok=$rows[0].Reason -ceq 'definition_missing' -and $rows[0].SourceUniqueId -ceq $Evidence.SourceUid -and $rows[0].Detail -is [string] -and $rows[0].Detail.StartsWith('ExistingModelDefinitionMissing:',[StringComparison]::Ordinal) -and $rows[0].Detail.Contains($Evidence.SourceTypeUid,[StringComparison]::Ordinal)}
    Check 'Separate-copy actual purge: structured original-definition missing reason with UID' $ok
    foreach($contract in @(@{Path='RestoreResult.Created';Value=0},@{Path='RestoreResult.Existing';Value=0},@{Path='RestoreResult.Failed';Value=1})) {
        $field=Get-RequiredField $Evidence $contract.Path;Check ('Separate-copy actual purge: '+$contract.Path+'='+$contract.Value) ($field.Present -and ($field.Value -is [int] -or $field.Value -is [long]) -and $field.Value -eq $contract.Value)
    }
}
function Check-PhysicalRestoreResult([object]$Evidence) {
    $field=Get-RequiredField $Evidence 'Failed'
    Check 'No physical restoration request failed' ($field.Present -and ($field.Value -is [int] -or $field.Value -is [long]) -and $field.Value -eq 0)
}
$gap = Read-Evidence $capture 'rapid-deletion-gap.json'
$edits = Read-Evidence $capture 'rapid-edits.json'
$journal = Read-Evidence $capture 'journal-summary.json'
$ready = Read-Evidence $capture 'rapid-readiness.json'
$closed = Read-Evidence $capture 'durable-session-closed.json'
$phaseTwo = Read-Evidence $restore 'durable-phase-two-baseline.json'
$denial = Read-Evidence $restore 'rapid-source-denial.json'
$j14 = Read-Evidence $restore 'rapid-j14-simulation.json'
$load = Read-Evidence $restore 'load.json'
$cycle = Read-Evidence $restore 'rapid-cycle-result.json'
$persistence = Read-Evidence $restore 'persistence.json'
$integrity = Read-Evidence $restore 'integrity.json'
$captureReady = Read-Evidence $capture 'ready.json'
$restoreReady = Read-Evidence $restore 'ready.json'
$verification = Read-Evidence $restore 'verification.json'
$reopenedVerification = Read-Evidence $restore 'reopened-verification.json'
$existingInputs = Read-Evidence $restore 'rapid-existing-definition-inputs.json'
$storageCapture = Read-Evidence $capture 'rapid-storage-capture.json'
$storageFinal = Read-Evidence $restore 'rapid-storage-final.json'
$purge = Read-Evidence $restore 'rapid-purge-refusal.json'
$selection = Read-Evidence $restore 'rapid-request-selection.json'
$inventory = Read-Evidence $capture 'inventory.json'
$restoreResult = Read-Evidence $restore 'restore-result.json'
Check 'Three actual geometry and parameter edits' (@($edits.ModifiedIds).Count -eq 3 -and @($edits.Edits | Where-Object { !$_.Passed -or !$_.GeometricChangeVerified -or !$_.Comment }).Count -eq 0 -and @($edits.Edits.Kind | Sort-Object) -join ',' -eq 'family,mep,wall')
Check 'Deletion in next observer callback with deferred capture paused' ($gap.NextObserverCallback -and $gap.DeferredPreparationPreventedThroughDeletion -and $gap.DocumentChangedCaptureWasEnabled -and $gap.AllThreeEditedObjectsActuallyDeleted)
$pending=Get-RequiredField $gap 'NativePendingRootsAfterDeletion';Check 'No per-object native export queued after immediate deletion' ($pending.Present -and ($pending.Value -is [int] -or $pending.Value -is [long]) -and $pending.Value -eq 0)
Check 'Exact natural deletion journal' (!$journal.TimedOut -and @($journal.MissingChangedIds).Count -eq 0 -and $journal.FullCount -gt 0 -and $journal.FullCount -eq @($journal.ExpectedChangedIds).Count)
Check 'All edited witnesses durably Ready' ($ready.AllThreeReady -and $ready.PreparationWasForcedOnlyAfterDeletion -and $ready.Status.FailedJobs -eq 0)
Check 'Actual closure and automatic old bridge removal' ($closed.SessionClosed -and $closed.ConversionComplete -and $closed.Closed -and $closed.OldDocumentInvalid -and $closed.AbsentFromApplicationDocuments -and $closed.AllBridgeFilesAbsent -and $closed.SourceAndInitialCopyUnchanged)
Check 'New Revit process and stopped capture process' ($phaseTwo.DistinctProcess -and $phaseTwo.CaptureProcessExited -and $phaseTwo.CapturePid -ne $phaseTwo.RestorePid)
Check 'Same product assembly in both processes' ($captureReady.Sha256 -eq $restoreReady.Sha256)
Check 'Original prepared sources inaccessible during restore' (@($denial.Sources).Count -ge 2 -and @($denial.Sources | Where-Object { !$_.ExclusiveReadHandleHeld -or !$_.IndependentReadDenied }).Count -eq 0 -and @($denial.OldOpeningBridges | Where-Object Exists).Count -eq 0)
Check 'Explicit J14 simulation without timestamp falsification' ($j14.SimulationOnly -and $j14.SimulatedElapsedDays -eq 14 -and !$j14.WindowsClockChanged -and !$j14.EventTimestampsModified -and !$j14.FileTimestampsModified)
Check 'Actual production compression and exact event preservation' ($j14.MaintenanceInvoked -eq 'ElementHistoryTracker.CompressOldFiles' -and $j14.OriginalDailyJournalsRemoved -and @($j14.CompressedJournals).Count -gt 0 -and $j14.ExactDeletionEventsUnchanged)
Check 'Actual loader and UI restore conversion after compression' ($load.RangeDeletionCount -eq $journal.FullCount -and $load.RequestsCount -eq $journal.FullCount)
Check 'Loader preserves all conversions and restores exactly the physical denominator' ($load.FullRequestsCount -eq $journal.FullCount -and $load.RestorationRequestsCount -eq @($gap.DeletedPhysicalIds).Count)
Check-RapidRequestSelection $selection @($journal.ExpectedChangedIds) @($gap.DeletedPhysicalIds) @($inventory.Catalog)
Check-PhysicalRestoreResult $restoreResult
Check 'Restored and reopened fidelity' ($cycle.Passed -and $cycle.SimulationOnly -and !$cycle.RealFourteenDayStorageTest -and $cycle.SourcesDeniedThroughSaveAndReopen -and $cycle.OldOpeningBridgesAbsent -and $cycle.ReopenedVerification.AllDeletedPhysicalVerified -and @($cycle.ReopenedVerification.UnexpectedModelObjects).Count -eq 0)
Check-IndependentFidelity $verification 'verification.json' @($gap.DeletedPhysicalIds) @($edits.ModifiedIds) @($journal.ExpectedChangedIds) @($inventory.Catalog)
Check-IndependentFidelity $reopenedVerification 'reopened-verification.json' @($gap.DeletedPhysicalIds) @($edits.ModifiedIds) @($journal.ExpectedChangedIds) @($inventory.Catalog)
Check-WitnessExistingDefinitions $verification 'verification.json' @($edits.ModifiedIds)
Check-WitnessExistingDefinitions $reopenedVerification 'reopened-verification.json' @($edits.ModifiedIds)
foreach($evidence in @(@{Name='rapid-edits.json';Value=$edits},@{Name='rapid-cycle-result.json';Value=$cycle})){$field=Get-RequiredField $evidence.Value 'UsesExistingModelDefinitions';Check ($evidence.Name+': explicit existing-definition contract') ($field.Present -and $field.Value -is [bool] -and $field.Value)}
Check-ExistingDefinitionInputs $existingInputs @($edits.ModifiedIds)
Check-RapidArchive $storageCapture 'Closed capture archive' @($edits.ModifiedIds)
Check-RapidArchive $storageFinal 'Final J14 archive' @($edits.ModifiedIds)
Check-PurgeRefusal $purge
Check 'Real SaveAs closure and distinct reopened document' ($persistence.Closed -and $persistence.OldDocumentInvalid -and $persistence.AbsentFromApplicationDocuments -and $persistence.ReopenedDistinctDocument -and $persistence.FileExists)
Check 'Canonical sources and deleted checkpoint unchanged' ($integrity.SourceUnchanged -and $integrity.CopyAndSourceUnchanged -and $integrity.AllOpeningBridgeFilesStillAbsent -and $integrity.DeletedProjectSha256AfterRestore -eq $closed.DeletedProjectSha256)
$errors = foreach($root in @($capture,$restore)) { foreach($name in @('error.txt','last-error.json','rapid-error.json','rapid-time-error.json','durable-error.json','observer-errors.txt')) { $path = Join-Path $root $name; if((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 0) { $path } } }
Check 'No concealed observer or production conversion error' (@($errors).Count -eq 0)
$callbacks = Get-Content -LiteralPath (Join-Path $capture 'observer.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Kind -eq 'diagnostic_external_event_span' -and $_.Data.Action -in 'rapid-edit','delete' } | ForEach-Object { $_.Data }
$passed = @($checks | Where-Object { !$_.Passed }).Count -eq 0
$summary = @{AtUtc=[DateTime]::UtcNow.ToString('o');Passed=$passed;UsesExistingModelDefinitions=$true;SimulationOnly=$true;RealFourteenDayStorageTest=$false;CaptureDirectory=$capture;RestoreDirectory=$restore;WitnessCount=3;ActualDeletedPhysicalCount=@($gap.DeletedPhysicalIds).Count;GapMilliseconds=$gap.GapMilliseconds;EditAndCommitMeasurements=$edits.Edits | Select-Object Kind,SourceId,CommitAndProductionDocumentChangedCallbacksMilliseconds,TransactionAndIndependentVerificationMilliseconds;CallbackMeasurements=$callbacks;Checks=$checks;WitnessTypeEvidence=$witnessTypeEvidence;IndependentFidelityEvidence=$independentFidelityEvidence;MissingObserverEvidence=$observerEvidenceGaps;ArchiveCapture=$storageCapture;ArchiveFinal=$storageFinal;PurgeRefusal=$purge;ErrorFiles=@($errors);Scope='Three edited witnesses reuse the exact original type/family UIDs still present, with unchanged type parameters and no persistent RVT/RFA payload; purged definitions are explicitly unavailable. J+14 is injected, not real elapsed retention; no universal performance/category guarantee.'}
$summary | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $restore 'rapid-cycle-summary.json') -Encoding UTF8
$checks | ForEach-Object { '{0}: {1}{2}' -f $(if($_.Passed){'PASS'}else{'FAIL'}),$_.Name,$(if(!$_.Passed -and $_.Details){' — '+$_.Details}else{''}) }
if(!$passed) { throw 'Rapid cycle has failed checks. Keep the complete evidence; no success is asserted.' }
