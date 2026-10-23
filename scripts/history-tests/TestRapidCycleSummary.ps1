#Requires -Version 7.0
param(
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$FailedCaptureRunName,
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$FailedRestoreRunName
)
$ErrorActionPreference='Stop'
if([bool]$FailedCaptureRunName -ne [bool]$FailedRestoreRunName){throw 'Provide both failed native run names, or neither. Synthetic contract checks always run.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo ('tmp/history-rapid-summary-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $folder 'TestRapidCycleSummary.ps1')
$summaryPath=Join-Path $repo 'scripts/history-stress-tests/SummarizeRapidCycle.ps1'
$text=[IO.File]::ReadAllText($summaryPath)
$start=$text.IndexOf('$checks =');$end=$text.IndexOf('$gap = Read-Evidence')
if($start -lt 0 -or $end -le $start){throw 'Summary helper extraction failed.'}
Invoke-Expression $text.Substring($start,$end-$start)
$results=[Collections.Generic.List[object]]::new()
function New-GoodFixture {
    $rows=@(1,2,3)|ForEach-Object {
        [pscustomobject]@{SourceId=$_;Class='FamilyInstance';Passed=$true;MatchesIdentity=$true;IdentityInjective=$true;ClassMatches=$true;TypeMatches=$true;GeometryChecked=$true;GeometryMatches=$true;BBoxMatches=$true;DimensionsMatch=$true;HostMatches=$true;GroupMatches=$true;JoinsMatch=$true;WallJoinsMatch=$true;ConnectionsMatch=$true;SystemTypeMatches=$true;PortSystemTypesMatch=$true;ExcavationsMatch=$true;InstanceParametersMatch=$true;ExpectedProbeErrors=@();ActualProbeErrors=@();UsesExistingModelDefinitions=$true;ExistingModelDefinitionsUsed=$true;TypeUidMatchesSource=$true;FamilyUidMatchesSource=$true;TypeDefinitionParametersMatch=$true;Expected=[pscustomobject]@{TypeUid=('type-'+$_)};Actual=[pscustomobject]@{TypeUid=('type-'+$_)};TypeDefinitionEvidence=[pscustomobject]@{UsesExistingModelDefinitions=$true;ExistingModelDefinitionsUsed=$true;SourceTypeUid=('type-'+$_);ActualTypeUid=('type-'+$_);SourceFamilyUid=('family-'+$_);ActualFamilyUid=('family-'+$_);ParameterDifferences=@()};InstanceParameterEvidence=[pscustomobject]@{Passed=$true;Differences=@();ExpectedCount=5;ActualCount=5}}
    }
    [pscustomobject]@{AllDeletedPhysicalVerified=$true;DeletedPhysicalCount=3;Missing=@();Duplicates=@();Failed=@();ReverseOriginConflicts=@();UnexpectedModelObjects=@();GeneratedModelRecords=@();Records=$rows}
}
function Set-Field([object]$Object,[string]$Path,[object]$Value,[switch]$Remove) {
    $segments=$Path.Split('.');$parent=$Object
    for($index=0;$index -lt $segments.Length-1;$index++){$parent=$parent.($segments[$index])}
    $leaf=$segments[-1]
    if($Remove){$parent.PSObject.Properties.Remove($leaf)}else{$parent.$leaf=$Value}
}
function Evaluate([object]$Proof) {
    $checks.Clear();$witnessTypeEvidence.Clear();$observerEvidenceGaps.Clear();$independentFidelityEvidence.Clear()
    foreach($phase in @('fixture-restoration','fixture-reopen')) {
        Check-IndependentFidelity $Proof $phase @(1,2,3) @(1,2,3)
        Check-WitnessExistingDefinitions $Proof $phase @(1,2,3)
    }
    @($checks|Where-Object{!$_.Passed}).Count -eq 0
}
function Expect([string]$Name,[object]$Proof,[bool]$Wanted) {
    $actual=Evaluate $Proof
    $results.Add(@{Name=$Name;Expected=$Wanted;Actual=$actual;Passed=($actual -eq $Wanted)})
    if($actual -ne $Wanted){throw ('Strict summary fixture failed: '+$Name)}
}
Expect 'Complete explicit evidence accepted in both phases' (New-GoodFixture) $true
if($witnessTypeEvidence.Count -ne 42 -or $independentFidelityEvidence.Count -ne 8){throw 'Both independent verification phases were not exercised.'}
$booleanPaths=@('Passed','MatchesIdentity','IdentityInjective','ClassMatches','TypeMatches','GeometryChecked','GeometryMatches','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','WallJoinsMatch','ConnectionsMatch','SystemTypeMatches','PortSystemTypesMatch','ExcavationsMatch','InstanceParametersMatch','InstanceParameterEvidence.Passed')
foreach($path in $booleanPaths) {
    foreach($mutation in @('missing','false','null','string')) {
        $proof=New-GoodFixture
        switch($mutation){'missing'{Set-Field $proof.Records[0] $path $null -Remove}'false'{Set-Field $proof.Records[0] $path $false}'null'{Set-Field $proof.Records[0] $path $null}'string'{Set-Field $proof.Records[0] $path 'true'}}
        Expect ($path+':'+$mutation) $proof $false
    }
}
foreach($contract in @(@{Path='UsesExistingModelDefinitions';Wrong=$false},@{Path='ExistingModelDefinitionsUsed';Wrong=$false},@{Path='TypeUidMatchesSource';Wrong=$false},@{Path='FamilyUidMatchesSource';Wrong=$false},@{Path='TypeDefinitionParametersMatch';Wrong=$false},@{Path='TypeDefinitionEvidence.UsesExistingModelDefinitions';Wrong=$false},@{Path='TypeDefinitionEvidence.ExistingModelDefinitionsUsed';Wrong=$false})) {
    foreach($mutation in @('missing','wrong','null','string')) {
        $proof=New-GoodFixture
        switch($mutation){'missing'{Set-Field $proof.Records[1] $contract.Path $null -Remove}'wrong'{Set-Field $proof.Records[1] $contract.Path $contract.Wrong}'null'{Set-Field $proof.Records[1] $contract.Path $null}'string'{Set-Field $proof.Records[1] $contract.Path 'true'}}
        Expect ($contract.Path+':'+$mutation) $proof $false
    }
}
foreach($path in @('Expected.TypeUid','Actual.TypeUid','TypeDefinitionEvidence.SourceTypeUid','TypeDefinitionEvidence.ActualTypeUid','TypeDefinitionEvidence.SourceFamilyUid','TypeDefinitionEvidence.ActualFamilyUid')) {
    foreach($mutation in @('missing','null','different','empty')) {
        $proof=New-GoodFixture
        switch($mutation){'missing'{Set-Field $proof.Records[0] $path $null -Remove}'null'{Set-Field $proof.Records[0] $path $null}'different'{Set-Field $proof.Records[0] $path 'homonym-other-uid'}'empty'{Set-Field $proof.Records[0] $path ''}}
        Expect ($path+':'+$mutation) $proof $false
    }
}
foreach($path in @('ExpectedProbeErrors','ActualProbeErrors','InstanceParameterEvidence.Differences','TypeDefinitionEvidence.ParameterDifferences')) {
    foreach($mutation in @('missing','null','nonempty')) {
        $proof=New-GoodFixture
        switch($mutation){'missing'{Set-Field $proof.Records[2] $path $null -Remove}'null'{Set-Field $proof.Records[2] $path $null}'nonempty'{Set-Field $proof.Records[2] $path @('real-difference')}}
        Expect ($path+':'+$mutation) $proof $false
    }
}
foreach($path in @('Missing','Duplicates','Failed','ReverseOriginConflicts','UnexpectedModelObjects','GeneratedModelRecords')) {
    foreach($mutation in @('missing','null','nonempty')) {
        $proof=New-GoodFixture
        switch($mutation){'missing'{Set-Field $proof $path $null -Remove}'null'{Set-Field $proof $path $null}'nonempty'{Set-Field $proof $path @('unverified-object')}}
        Expect ('phase.'+$path+':'+$mutation) $proof $false
    }
}
foreach($mutation in @('missing','false','null','string')) {
    $proof=New-GoodFixture
    switch($mutation){'missing'{Set-Field $proof 'AllDeletedPhysicalVerified' $null -Remove}'false'{$proof.AllDeletedPhysicalVerified=$false}'null'{$proof.AllDeletedPhysicalVerified=$null}'string'{$proof.AllDeletedPhysicalVerified='true'}}
    Expect ('phase.aggregate:'+$mutation) $proof $false
}
foreach($mutation in @('missing','wrong','string')) {
    $proof=New-GoodFixture
    switch($mutation){'missing'{Set-Field $proof 'DeletedPhysicalCount' $null -Remove}'wrong'{$proof.DeletedPhysicalCount=2}'string'{$proof.DeletedPhysicalCount='3'}}
    Expect ('phase.denominator:'+$mutation) $proof $false
}
foreach($mutation in @('missing','empty','duplicate','wrong-ID','string-ID')) {
    $proof=New-GoodFixture
    switch($mutation){'missing'{Set-Field $proof 'Records' $null -Remove}'empty'{$proof.Records=@()}'duplicate'{$proof.Records[2].SourceId=2}'wrong-ID'{$proof.Records[2].SourceId=4}'string-ID'{$proof.Records[2].SourceId='3'}}
    Expect ('phase.records:'+$mutation) $proof $false
}
foreach($path in @('InstanceParameterEvidence.ExpectedCount','InstanceParameterEvidence.ActualCount')) {
    foreach($mutation in @('missing','zero','string','mismatch')) {
        $proof=New-GoodFixture
        switch($mutation){'missing'{Set-Field $proof.Records[0] $path $null -Remove}'zero'{Set-Field $proof.Records[0] $path 0}'string'{Set-Field $proof.Records[0] $path '5'}'mismatch'{Set-Field $proof.Records[0] $path 4}}
        Expect ($path+':'+$mutation) $proof $false
    }
}
function New-InputFixture {
    [pscustomobject]@{Passed=$true;UsesExistingModelDefinitions=$true;Records=@(1,2,3|ForEach-Object{[pscustomobject]@{SourceId=$_;SourceUid=('source-'+$_);RequestCount=1;UsesExistingModelDefinitions=$true;NoNativeOrOpeningFallback=$true;RecursiveNativeArtifactCount=0}})}
}
function New-ArchiveFixture {
    [pscustomobject]@{UsesExistingModelDefinitions=$true;NoNativePayloads=$true;NoNewNativePayloads=$true;NativePayloadCount=0;NewNativePayloadCount=0;NativePayloadFiles=@();NewNativePayloadFiles=@();JournalBytes=200;JournalFiles=@([pscustomobject]@{Path='history/element-history-test.jsonl.gz';Bytes=200});Files=@([pscustomobject]@{Path='history/element-history-test.jsonl.gz';Bytes=200});WitnessRecipes=@(1,2,3|ForEach-Object{[pscustomobject]@{SourceId=$_;UsesExistingModelDefinitions=$true;NoNativeOrOpeningFallback=$true;JsonUtf8Bytes=50}})}
}
function New-PurgeFixture {
    [pscustomobject]@{Passed=$true;UsesExistingModelDefinitions=$true;OpenedUnderHistoricalScope=$true;OwnedBeforePurge=$true;ClosedWithoutSaving=$true;PositiveChecksCompletedBeforePurge=$true;OriginalTypeAndFamilyMissingBeforeRestore=$true;ExplicitDefinitionMissingRefusal=$true;OriginalTypeAndFamilyStillMissingAfterRestore=$true;NoModelElementsAddedOrRemovedByRestore=$true;NoNewNativePayloads=$true;PositiveSavedModelAndDeletedCheckpointUnchanged=$true;PreparedSourcesStillDenied=$true;OldOpeningBridgesAbsent=$true;SeparateCopyClosed=$true;SourceUid='source-1';SourceTypeUid='type-1';RestoreResult=[pscustomobject]@{Created=0;Existing=0;Failed=1;Items=@([pscustomobject]@{SourceUniqueId='source-1';Reason='definition_missing';Detail='ExistingModelDefinitionMissing: UID=type-1; famille/type supprimé ou purgé du modele courant.'})}}
}
function Expect-Aux([string]$Kind,[string]$Name,[object]$Proof,[bool]$Wanted) {
    $checks.Clear()
    switch($Kind){'input'{Check-ExistingDefinitionInputs $Proof @(1,2,3)}'archive'{Check-RapidArchive $Proof 'fixture' @(1,2,3)}'purge'{Check-PurgeRefusal $Proof}default{throw 'Unknown helper fixture.'}}
    $actual=@($checks|Where-Object{!$_.Passed}).Count -eq 0
    $results.Add(@{Name=($Kind+':'+$Name);Expected=$Wanted;Actual=$actual;Passed=($actual -eq $Wanted)})
    if($actual -ne $Wanted){throw ('Strict auxiliary summary fixture failed: '+$Kind+':'+$Name)}
}
foreach($kind in @('input','archive','purge')) {
    $fixture=switch($kind){'input'{New-InputFixture}'archive'{New-ArchiveFixture}'purge'{New-PurgeFixture}}
    Expect-Aux $kind 'Complete explicit evidence accepted' $fixture $true
}
foreach($kind in @('input','archive','purge')) {
    $paths=switch($kind){'input'{@('Passed','UsesExistingModelDefinitions')}'archive'{@('UsesExistingModelDefinitions','NoNativePayloads','NoNewNativePayloads')}'purge'{@('Passed','UsesExistingModelDefinitions','OpenedUnderHistoricalScope','OwnedBeforePurge','ClosedWithoutSaving','PositiveChecksCompletedBeforePurge','OriginalTypeAndFamilyMissingBeforeRestore','ExplicitDefinitionMissingRefusal','OriginalTypeAndFamilyStillMissingAfterRestore','NoModelElementsAddedOrRemovedByRestore','NoNewNativePayloads','PositiveSavedModelAndDeletedCheckpointUnchanged','PreparedSourcesStillDenied','OldOpeningBridgesAbsent','SeparateCopyClosed')}}
    foreach($path in $paths) {
        foreach($mutation in @('missing','false','null','string')) {
            $proof=switch($kind){'input'{New-InputFixture}'archive'{New-ArchiveFixture}'purge'{New-PurgeFixture}}
            switch($mutation){'missing'{Set-Field $proof $path $null -Remove}'false'{Set-Field $proof $path $false}'null'{Set-Field $proof $path $null}'string'{Set-Field $proof $path 'true'}}
            Expect-Aux $kind ($path+':'+$mutation) $proof $false
        }
    }
}
foreach($kind in @('input','archive')) {
    foreach($mutation in @('missing','empty','duplicate','wrong-ID')) {
        $proof=if($kind -eq 'input'){New-InputFixture}else{New-ArchiveFixture};$path=if($kind -eq 'input'){'Records'}else{'WitnessRecipes'}
        switch($mutation){'missing'{Set-Field $proof $path $null -Remove}'empty'{Set-Field $proof $path @()}'duplicate'{$proof.$path[2].SourceId=2}'wrong-ID'{$proof.$path[2].SourceId=4}}
        Expect-Aux $kind ($path+':'+$mutation) $proof $false
    }
    foreach($path in @('UsesExistingModelDefinitions','NoNativeOrOpeningFallback')) {
        foreach($mutation in @('missing','false','null','string')) {
            $proof=if($kind -eq 'input'){New-InputFixture}else{New-ArchiveFixture};$row=if($kind -eq 'input'){$proof.Records[0]}else{$proof.WitnessRecipes[0]}
            switch($mutation){'missing'{Set-Field $row $path $null -Remove}'false'{Set-Field $row $path $false}'null'{Set-Field $row $path $null}'string'{Set-Field $row $path 'true'}}
            Expect-Aux $kind ('row.'+$path+':'+$mutation) $proof $false
        }
    }
}
foreach($path in @('RequestCount','RecursiveNativeArtifactCount')) {
    foreach($mutation in @('missing','null','wrong','string')) {
        $proof=New-InputFixture
        switch($mutation){'missing'{Set-Field $proof.Records[0] $path $null -Remove}'null'{Set-Field $proof.Records[0] $path $null}'wrong'{Set-Field $proof.Records[0] $path 2}'string'{Set-Field $proof.Records[0] $path '0'}}
        Expect-Aux 'input' ($path+':'+$mutation) $proof $false
    }
}
foreach($path in @('NativePayloadCount','NewNativePayloadCount','JournalBytes')) {
    foreach($mutation in @('missing','null','wrong','string')) {
        $proof=New-ArchiveFixture
        switch($mutation){'missing'{Set-Field $proof $path $null -Remove}'null'{Set-Field $proof $path $null}'wrong'{Set-Field $proof $path 1}'string'{Set-Field $proof $path '0'}}
        Expect-Aux 'archive' ($path+':'+$mutation) $proof $false
    }
}
foreach($path in @('NativePayloadFiles','NewNativePayloadFiles','Files','JournalFiles')) {
    foreach($mutation in @('missing','null','native-file')) {
        $proof=New-ArchiveFixture
        switch($mutation){'missing'{Set-Field $proof $path $null -Remove}'null'{Set-Field $proof $path $null}'native-file'{Set-Field $proof $path @([pscustomobject]@{Path='forbidden.rfa';Bytes=1})}}
        Expect-Aux 'archive' ($path+':'+$mutation) $proof $false
    }
}
foreach($mutation in @('missing','zero','string')) {
    $proof=New-ArchiveFixture
    switch($mutation){'missing'{Set-Field $proof.WitnessRecipes[0] 'JsonUtf8Bytes' $null -Remove}'zero'{$proof.WitnessRecipes[0].JsonUtf8Bytes=0}'string'{$proof.WitnessRecipes[0].JsonUtf8Bytes='50'}}
    Expect-Aux 'archive' ('JsonUtf8Bytes:'+$mutation) $proof $false
}
foreach($mutation in @('missing-items','no-items','two-items','generic-reason','no-detail','no-prefix','wrong-UID','wrong-source','created','existing','no-failure')) {
    $proof=New-PurgeFixture
    switch($mutation){'missing-items'{Set-Field $proof.RestoreResult 'Items' $null -Remove}'no-items'{$proof.RestoreResult.Items=@()}'two-items'{$proof.RestoreResult.Items=@($proof.RestoreResult.Items[0],$proof.RestoreResult.Items[0])}'generic-reason'{$proof.RestoreResult.Items[0].Reason='restore_failed'}'no-detail'{$proof.RestoreResult.Items[0].Detail=$null}'no-prefix'{$proof.RestoreResult.Items[0].Detail='generic error type-1'}'wrong-UID'{$proof.RestoreResult.Items[0].Detail='ExistingModelDefinitionMissing: UID=homonym'}'wrong-source'{$proof.RestoreResult.Items[0].SourceUniqueId='source-other'}'created'{$proof.RestoreResult.Created=1}'existing'{$proof.RestoreResult.Existing=1}'no-failure'{$proof.RestoreResult.Failed=0}}
    Expect-Aux 'purge' $mutation $proof $false
}
function New-ScopeFixture {
    $catalog=@(
        [pscustomobject]@{Id=1;Uid='source-1';Class='FamilyInstance';CategoryId=-2000151;ProtectionReason=$null;ElementType=$false},
        [pscustomobject]@{Id=2;Uid='source-2';Class='Wall';CategoryId=-2000011;ProtectionReason=$null;ElementType=$false},
        [pscustomobject]@{Id=3;Uid='source-3';Class='Pipe';CategoryId=-2008044;ProtectionReason=$null;ElementType=$false},
        [pscustomobject]@{Id=4;Uid='source-4';Class='LinearDimension';CategoryId=-2000261;ProtectionReason='not-model-category';ElementType=$false},
        [pscustomobject]@{Id=5;Uid='source-5';Class='LinearDimension';CategoryId=-2000261;ProtectionReason='not-model-category';ElementType=$false},
        [pscustomobject]@{Id=6;Uid='source-6';Class='LinearDimension';CategoryId=-2000261;ProtectionReason='not-model-category';ElementType=$false},
        [pscustomobject]@{Id=7;Uid='source-7';Class='SpotDimension';CategoryId=-2000263;ProtectionReason='view-or-view-specific';ElementType=$false},
        [pscustomobject]@{Id=8;Uid='source-8';Class='Element';CategoryId=-2008045;ProtectionReason='generated-model-category';ElementType=$false},
        [pscustomobject]@{Id=9;Uid='source-9';Class='IndependentTag';CategoryId=-2008047;ProtectionReason='view-or-view-specific';ElementType=$false},
        [pscustomobject]@{Id=10;Uid='source-10';Class='DataStorage';CategoryId=$null;ProtectionReason='definition-or-system';ElementType=$false}
    )
    $rows=@($catalog|ForEach-Object {
        $physical=$_.Id -le 3;$live=$_.Id -in 4,7,8
        [pscustomobject]@{SourceId=$_.Id;Baseline=($_|ConvertTo-Json|ConvertFrom-Json);EventElementId=$_.Id;RequestAuditElementId=$_.Id;RequestSourceUid=$(if($physical -or $live){$_.Uid}else{''});RequestRecipeNull=!$physical;CaptureFailure=$(if($live){'Hors périmètre de restauration : objet exclu identifié avant sa suppression (annotation, axe, isolant ou lien/import externe).'}elseif(!$physical){'État non récupérable : Catégorie non reconstructible directement avec les types encore présents dans la maquette.'}else{$null});Physical=$physical;EvidenceKind=$(if($physical){'PhysicalRestoration'}elseif($live){'LiveExcludedMetadata'}else{'ProtectedBaselineExplicitUnsupported'});LiveExclusionValidated=$live;LiveSourceUid=$(if($live){$_.Uid}else{$null});LiveClass=$(if($live){'Autodesk.Revit.DB.'+$_.Class}else{$null});LiveCategoryId=$(if($live){$_.CategoryId}else{$null})}
    })
    [pscustomobject]@{Catalog=$catalog;Proof=[pscustomobject]@{Passed=$true;FullRequestsCount=10;RestorationRequestsCount=3;ExcludedNonPhysicalCount=7;Records=$rows}}
}
function Expect-Scope([string]$Name,[object]$Fixture,[bool]$Wanted) {
    $checks.Clear();Check-RapidRequestSelection $Fixture.Proof @(1..10) @(1,2,3) $Fixture.Catalog
    $actual=@($checks|Where-Object{!$_.Passed}).Count -eq 0
    $results.Add(@{Name=('scope:'+ $Name);Expected=$Wanted;Actual=$actual;Passed=($actual -eq $Wanted)})
    if($actual -ne $Wanted){throw ('Request scope fixture failed: '+$Name)}
}
Expect-Scope 'All 10 conversions, 3 physical requests, 3 live exclusions and 4 baseline refusals' (New-ScopeFixture) $true
foreach($mutation in @('missing-event','physical-missing-recipe','physical-classified-excluded','no-ACK','generic-reason','no-live-proof','forged-live-UID','forged-live-class','forged-baseline','wrong-audit-ID','extra-family-named-axis','extra-curve-named-axis','wrong-full-count','wrong-physical-count')) {
    $fixture=New-ScopeFixture;$proof=$fixture.Proof
    switch($mutation){
        'missing-event'{$proof.Records=@($proof.Records|Where-Object SourceId -NE 10)}
        'physical-missing-recipe'{$proof.Records[0].RequestRecipeNull=$true}
        'physical-classified-excluded'{$proof.Records[0].Physical=$false;$proof.Records[0].EvidenceKind='ProtectedBaselineExplicitUnsupported'}
        'no-ACK'{$proof.Records[4].CaptureFailure=$null}
        'generic-reason'{$proof.Records[4].CaptureFailure='identity'}
        'no-live-proof'{$proof.Records[3].LiveExclusionValidated=$false}
        'forged-live-UID'{$proof.Records[3].LiveSourceUid='different'}
        'forged-live-class'{$proof.Records[3].LiveClass='Autodesk.Revit.DB.FamilyInstance'}
        'forged-baseline'{$proof.Records[4].Baseline.Uid='forged'}
        'wrong-audit-ID'{$proof.Records[4].RequestAuditElementId=4}
        'extra-family-named-axis'{$fixture.Catalog[4].Class='FamilyInstance';$proof.Records[4].Baseline.Class='FamilyInstance';$proof.Records[4].Baseline|Add-Member Name 'Axe'}
        'extra-curve-named-axis'{$fixture.Catalog[4].Class='ModelCurve';$proof.Records[4].Baseline.Class='ModelCurve';$proof.Records[4].Baseline|Add-Member Name 'Axe'}
        'wrong-full-count'{$proof.FullRequestsCount=9}
        'wrong-physical-count'{$proof.RestorationRequestsCount=2}
    }
    Expect-Scope $mutation $fixture $false
}
foreach($mutation in @('none','failed','missing','string')) {
    $checks.Clear();$result=[pscustomobject]@{Failed=0}
    switch($mutation){'failed'{$result.Failed=1}'missing'{$result.PSObject.Properties.Remove('Failed')}'string'{$result.Failed='0'}}
    Check-PhysicalRestoreResult $result;$actual=@($checks|Where-Object{!$_.Passed}).Count -eq 0;$wanted=$mutation -eq 'none'
    $results.Add(@{Name=('physical-result:'+ $mutation);Expected=$wanted;Actual=$actual;Passed=$actual -eq $wanted})
    if($actual -ne $wanted){throw ('Physical failure fixture failed: '+$mutation)}
}
foreach($mutation in @('none','family','curve','wrong-category','forged-baseline','missing-baseline','too-many','duplicate-baseline','not-deleted')) {
    $checks.Clear();$proof=New-GoodFixture;$fixture=New-ScopeFixture;$entry=$fixture.Catalog[7]
    $generated=[pscustomobject]@{Id=108;Uid='generated-108';Class='Autodesk.Revit.DB.Element';CategoryId=-2008045;ExactNativeElementClass=$true;NativeCategoryWhitelisted=$true;BaselineDeletedRecords=@(($entry|ConvertTo-Json|ConvertFrom-Json))}
    $proof.GeneratedModelRecords=@($generated);$eventIds=@(1..10)
    switch($mutation){'family'{$generated.Class='Autodesk.Revit.DB.FamilyInstance'}'curve'{$generated.Class='Autodesk.Revit.DB.ModelCurve'}'wrong-category'{$generated.CategoryId=-2000011}'forged-baseline'{$generated.BaselineDeletedRecords[0].Uid='forged'}'missing-baseline'{$generated.BaselineDeletedRecords=@()}'too-many'{$proof.GeneratedModelRecords=@($generated,$generated)}'duplicate-baseline'{$generated.BaselineDeletedRecords=@($generated.BaselineDeletedRecords[0],$generated.BaselineDeletedRecords[0]);$proof.GeneratedModelRecords=@($generated,$generated)}'not-deleted'{$eventIds=@(1,2,3)}}
    Check-IndependentFidelity $proof 'generated-fixture' @(1,2,3) @(1,2,3) $eventIds $fixture.Catalog
    $actual=@($checks|Where-Object{!$_.Passed}).Count -eq 0;$wanted=$mutation -eq 'none'
    $results.Add(@{Name=('generated:'+ $mutation);Expected=$wanted;Actual=$actual;Passed=$actual -eq $wanted})
    if($actual -ne $wanted){throw ('Generated record fixture failed: '+$mutation)}
}
$nativeFailures=@();$wholeRunReason=$null
if($FailedCaptureRunName) {
    # Optional existing native evidence is read without launching Revit or
    # manufacturing a successful model proof. Missing requested files fail.
    $nativeRoot=Join-Path $repo ('tmp/history-stress/'+$FailedRestoreRunName)
    $captureRoot=Join-Path $repo ('tmp/history-stress/'+$FailedCaptureRunName)
    $native=Get-Content -LiteralPath (Join-Path $nativeRoot 'verification.json') -Raw|ConvertFrom-Json
    $gap=Get-Content -LiteralPath (Join-Path $captureRoot 'rapid-deletion-gap.json') -Raw|ConvertFrom-Json
    $edits=Get-Content -LiteralPath (Join-Path $captureRoot 'rapid-edits.json') -Raw|ConvertFrom-Json
    $checks.Clear();$independentFidelityEvidence.Clear();$witnessTypeEvidence.Clear();$observerEvidenceGaps.Clear()
    Check-IndependentFidelity $native 'actual-failed-native-verification' @($gap.DeletedPhysicalIds) @($edits.ModifiedIds)
    Check-WitnessExistingDefinitions $native 'actual-failed-native-verification' @($edits.ModifiedIds)
    $nativeFailures=@($checks|Where-Object{!$_.Passed})
    if($nativeFailures.Count -eq 0){throw 'The supplied supposedly failed native verification was accepted; it cannot serve as a negative fixture.'}
    $results.Add(@{Name='Actual failed native independent evidence rejected';Passed=$true;ActualFailedChecks=$nativeFailures.Count})
    $wholeRunRejected=$false
    try { & $summaryPath -CaptureRunName $FailedCaptureRunName -RestoreRunName $FailedRestoreRunName *> (Join-Path $folder 'native-run-rejection.log') }
    catch {$wholeRunRejected=$true;$wholeRunReason=$_.Exception.Message}
    if(!$wholeRunRejected){throw 'The supplied failed native whole-run proof was incorrectly accepted.'}
    $results.Add(@{Name='Actual failed native whole-run proof rejected';Passed=$true;Reason=$wholeRunReason})
}
@{AtUtc=[DateTime]::UtcNow.ToString('o');Passed=$true;Assertions=$results.Count;SummarySha256=(Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash;RevitInvoked=$false;ProductModified=$false;FailedNativeEvidenceChecked=[bool]$FailedCaptureRunName;FailedCaptureRunName=$FailedCaptureRunName;FailedRestoreRunName=$FailedRestoreRunName;Scope='Strict summary helper contract checks using explicitly synthetic success evidence, plus existing failed native evidence only when requested. No fabricated native success proof, and no Revit execution.';Cases=$results;ActualNativeIndependentFailures=$nativeFailures;ActualNativeWholeRunRejection=$wholeRunReason}|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $folder 'results.json') -Encoding UTF8
Write-Output ('PASS: '+$results.Count+' strict summary acceptance/rejection checks; existing failed native evidence checked='+[bool]$FailedCaptureRunName+'.')
Write-Output $folder
