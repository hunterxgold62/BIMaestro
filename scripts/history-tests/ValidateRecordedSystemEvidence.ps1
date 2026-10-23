param(
    [string]$ReportPath = '',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$ReportPath) { $ReportPath = Join-Path $repo 'tmp/codex-native-validation/2025-history-antibes-opening-v7/opening-benchmark.json' }
if (!$OutputPath) { $OutputPath = Join-Path ([IO.Path]::GetDirectoryName($ReportPath)) 'recorded-system-validation.json' }
$rawHash = (Get-FileHash -LiteralPath $ReportPath -Algorithm SHA256).Hash
$rawResultPath = Join-Path ([IO.Path]::GetDirectoryName($ReportPath)) 'result.json'
$rawResultHash = (Get-FileHash -LiteralPath $rawResultPath -Algorithm SHA256).Hash
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$checks = [ordered]@{}
function Check([string]$Name, [bool]$Value) { $checks[$Name] = $Value }
function HvacPorts($Network) { @($Network.ConnectorClassifications | Where-Object Domain -eq 'DomainHvac') }
function SameHvacPorts($Source, $Actual) {
    $saved = @(HvacPorts $Source)
    $current = @(HvacPorts $Actual)
    if (!$saved.Count -or $saved.Count -ne $current.Count) { return $false }
    foreach ($port in $saved) {
        $match = @($current | Where-Object Id -eq $port.Id)
        if ($match.Count -ne 1 -or !$port.Classification -or $port.Classification -ne $match[0].Classification -or $port.Connected -ne $match[0].Connected) { return $false }
        if ($port.SystemTypeId -gt 0) {
            # Valid historical types always require their exact UID. No such HVAC
            # type exists in the recorded v7 source; piping ports are excluded.
            if (!$port.SystemTypeUid -or $port.SystemTypeUid -ne $match[0].SystemTypeUid) { return $false }
        }
    }
    return $true
}
$targets = @($report.TargetedFallbackRegressions)
$duct = @($targets | Where-Object Uid -eq 'ee974953-383f-4838-bbc8-67cddf0cf5e4-000939c6')[0]
$natural = @($targets | Where-Object { $_.Uid -eq '2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f' -and !$_.FaultInjectedMissingType })[0]
$fault = @($targets | Where-Object FaultInjectedMissingType)[0]
Check 'ThreeRecordedCasesPresent' ($targets.Count -eq 3 -and $null -ne $duct -and $null -ne $natural -and $null -ne $fault)
foreach ($case in @(@{Name='Duct';Value=$duct}, @{Name='IPNDirect';Value=$natural}, @{Name='IPNBackupFault';Value=$fault})) {
    $value = $case.Value
    Check ($case.Name + 'RestoredClassAndType') ($value.Restore.Created -eq 1 -and $value.Restore.Failed -eq 0 -and $value.OriginalTypeUid -eq $value.ActualTypeUid -and !!$value.OriginalTypeUid)
    Check ($case.Name + 'ExactGeometry') ($value.PositionMatches -and @($value.CurveDistances | Where-Object { $_ -ge 1e-5 }).Count -eq 0 -and $null -ne $value.MidpointDistance -and $value.MidpointDistance -lt 1e-5)
    Check ($case.Name + 'ExpectedArchiveOpens') ($value.SourcesOpened -eq $value.ExpectedSourcesOpened -and $value.SourcesOwned)
}
Check 'DuctClassRetained' ($duct.Class -eq 'Duct' -and $duct.FinalNetwork.Class -eq 'Duct')
Check 'IPNDirectHasNoBackupOrArchiveOpen' ($natural.OpeningBackupRemovedForDirectCheck -and $natural.SourcesOpened -eq 0)
Check 'FaultUsesOneArchiveAndReportsTrigger' ($fault.FaultInjectedMissingType -and $fault.SourcesOpened -eq 1 -and !!$fault.Restore.Items[0].OpeningFallbackTrigger)

$source = $duct.OriginalNetwork
$actual = $duct.FinalNetwork
$sourcePeers = @($duct.OriginalPeers)
$actualPeers = @($duct.FinalPeerNetworks)
$sourcePeerIds = @($sourcePeers.Uid | Sort-Object)
$actualPeerIds = @($actualPeers.Uid | Sort-Object)
Check 'ExactPhysicalPeerOwnersRetained' (($sourcePeerIds -join '|') -eq ($actualPeerIds -join '|') -and $sourcePeers.Count -eq 2)
Check 'AllDuctHvacPortIdsClassificationsAndConnectionsRetained' (SameHvacPorts $source $actual)
$peerPortsPreserved = $true
foreach ($peer in $sourcePeers) {
    $match = @($actualPeers | Where-Object Uid -eq $peer.Uid)
    if ($match.Count -ne 1 -or !(SameHvacPorts $peer $match[0])) { $peerPortsPreserved = $false }
    if ($peer.SystemTypeId -gt 0 -and (!$peer.SystemTypeUid -or $peer.SystemTypeUid -ne $match[0].SystemTypeUid)) { $peerPortsPreserved = $false }
}
Check 'AllPhysicalHvacPortsOfActualNeighborsRetained' $peerPortsPreserved
$sourceHvacPorts = @((HvacPorts $source)) + @($sourcePeers | ForEach-Object { HvacPorts $_ })
$actualPeerHvacPorts = @($actualPeers | ForEach-Object { HvacPorts $_ })
$historicalHvacTypes = @($sourceHvacPorts | Where-Object SystemTypeId -gt 0 | Select-Object -ExpandProperty SystemTypeId -Unique)
$finalHvacTypes = @($actualPeerHvacPorts | Where-Object SystemTypeId -gt 0 | Select-Object -ExpandProperty SystemTypeId -Unique)
Check 'HistoricalDuctWasUndefinedWithoutMEPSystem' ($source.SystemTypeId -eq -1 -and $source.MEPSystemMissing -and !$source.SystemTypeUid)
Check 'NoValidHistoricalHvacTypeOverwritten' ($historicalHvacTypes.Count -eq 0)
Check 'SingleFinalHvacPeerTypeMatchesDuct' ($finalHvacTypes.Count -eq 1 -and $finalHvacTypes[0] -eq $actual.SystemTypeId)
$classifications = @($sourceHvacPorts | Select-Object -ExpandProperty Classification -Unique)
Check 'HistoricalHvacConnectorClassificationMatchesFinalSystem' ($classifications.Count -eq 1 -and $classifications[0] -eq $actual.MEPSystemClassification)
Check 'ExactlyTwoHistoricalPhysicalConnectionsRestored' ($duct.NormalizationCriteria.HistoricalConnections -eq 2 -and $duct.Restore.ConnectionsRestored -eq 2 -and $duct.UnsetSystemDiagnosticRollback.LinksBefore -eq 2)
$audit = $duct.Restore.Items[0].SystemTypeAdjustment
Check 'ExplicitNormalizationAuditAndVisibleNote' (!$audit.SourceDefined -and $audit.ActualDefined -and $audit.MatchesUniqueConnectedPeerType -and !$audit.ClassificationPreserved -and $audit.RevitAssignedPreviouslyUndefinedType -and !!$audit.Note -and $duct.Restore.Items[0].Detail.Contains($audit.Note))
Check 'AuditedTypeUidMatchesUniqueFinalPeerType' ($audit.ActualTypeUniqueId -eq $actual.SystemTypeUid -and @($audit.ConnectedPeerTypeUniqueIds).Count -eq 1 -and $audit.ConnectedPeerTypeUniqueIds[0] -eq $actual.SystemTypeUid)
Check 'NativeCopyAlreadyAssignedTypeBeforeReconnect' ($duct.AfterNativeCreationBeforeConnections.SystemTypeUid -eq $actual.SystemTypeUid -and @($duct.AfterNativeCreationBeforeConnections.ConnectorClassifications | Where-Object Connected).Count -eq 0)
$probe = $duct.UnsetSystemDiagnosticRollback
Check 'DiagnosticSetterRolledBack' ($probe.AfterRollback.SystemTypeUid -eq $probe.Before.SystemTypeUid -and $probe.AfterRollback.MEPSystemUid -eq $probe.Before.MEPSystemUid -and $probe.AfterRollback.MEPSystemName -eq $probe.Before.MEPSystemName -and $probe.LinksAfterRollback -eq 2)
Check 'SourceFileUnchanged' ($report.SourceUnchanged -and $report.SourceSha256Before -eq $report.SourceSha256After)
Check 'RawEvidenceAndResultUnmodified' ((Get-FileHash -LiteralPath $ReportPath -Algorithm SHA256).Hash -eq $rawHash -and (Get-FileHash -LiteralPath $rawResultPath -Algorithm SHA256).Hash -eq $rawResultHash)
$failed = @($checks.GetEnumerator() | Where-Object { !$_.Value } | ForEach-Object Key)
$result = [ordered]@{
    validationPassed = $failed.Count -eq 0
    validationKind = 'Offline interpretation of recorded native v7 evidence; no new Revit run'
    productionSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'BIMaestro/bin/Revit2025/Release/BIMaestro.dll') -Algorithm SHA256).Hash
    rawReport = [IO.Path]::GetFullPath($ReportPath)
    rawReportSha256 = $rawHash
    rawResultPassed = (Get-Content -LiteralPath $rawResultPath -Raw | ConvertFrom-Json).passed
    rawResultSha256 = $rawResultHash
    scope = 'Actual physical neighbor owners from CaptureConnections; all their physical HVAC port IDs/classifications are checked, a superset of linked ports. v7 lacks per-link peer port IDs; none are invented.'
    excludedNonHvacPorts = @($sourcePeers | ForEach-Object ConnectorClassifications | Where-Object Domain -ne 'DomainHvac').Count
    historicalHvacTypeIds = $historicalHvacTypes
    finalHvacTypeIds = $finalHvacTypes
    historicalHvacClassifications = $classifications
    systemAssignmentPreserved = $false
    normalizationExplicitlyReported = $true
    probeChangedSharedSystem = ($probe.SetReturned -and $probe.AfterAttempt.MEPSystemUid -eq $probe.Before.MEPSystemUid -and $probe.AfterAttempt.MEPSystemName -ne $probe.Before.MEPSystemName)
    checks = $checks
    failedChecks = $failed
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($result | ConvertTo-Json -Depth 12))
[pscustomobject]$result | Select-Object validationPassed,rawResultPassed,productionSha256,excludedNonHvacPorts,failedChecks | ConvertTo-Json
if ($failed.Count) { throw ('Recorded evidence validation failed: ' + ($failed -join ', ')) }
