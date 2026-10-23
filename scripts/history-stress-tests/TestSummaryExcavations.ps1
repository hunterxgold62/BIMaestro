#Requires -Version 7.0
$ErrorActionPreference='Stop'
$path=Join-Path $PSScriptRoot 'Summarize.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count) { throw 'Summary source did not parse.' }
foreach($name in @('Id-Set','Difference','Check-Verification','Summarize-VerificationRecords')) {
    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if(!$function) { throw "Production summary function absent: $name" }
    Invoke-Expression $function.Extent.Text
}
function Record([int]$Id) {
    $record=@{SourceId=$Id;SourceUid="original-$Id";ActualUids=@("restored-$Id");ExpectedProbeErrors=@();ActualProbeErrors=@()}
    foreach($check in @('MatchesIdentity','ClassMatches','TypeMatches','GeometryMatches','GeometryChecked','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','ConnectionsMatch','SystemTypeMatches','Passed')) { $record[$check]=$true }
    return $record
}
function Verify($Records) {
    $report=@{Records=@($Records);Duplicates=@();Missing=@();Failed=@();UnexpectedModelObjects=@()}
    return Check-Verification $report (Id-Set @($Records | ForEach-Object SourceId))
}
$old=Record 1
if(!(Verify @($old)).Passed) { throw 'Legacy records without the excavation probe must preserve their original checks.' }
$current=Record 2;$current.ExcavationsMatch=$true
if(!(Verify @($current)).Passed) { throw 'A measured matching excavation graph must pass.' }
$current.ExcavationsMatch=$false
if((Verify @($current)).Passed) { throw 'A false excavation comparison must fail even when Passed was incorrectly true.' }
$current.ExcavationsMatch=$true
$mixed=Verify @($old,$current)
if($mixed.Passed -or 'ExcavationsMatch' -notin $mixed.FailedOrUnverifiedRecords[0].FailedOrUnverifiedChecks) { throw 'A mixed new run must reject unmeasured excavation records.' }
$stats=Summarize-VerificationRecords @($old,$current)
if($stats.MismatchesAmongPresent.ExcavationsMatch -ne 0 -or $stats.UnverifiedChecksAmongPresent.ExcavationsMatch -ne 1) { throw 'The summary must distinguish a mismatch from an unverified excavation probe.' }
Write-Output 'PASS: extracted production summary preserves legacy runs and rejects mismatched or partly unverified excavation probes.'
