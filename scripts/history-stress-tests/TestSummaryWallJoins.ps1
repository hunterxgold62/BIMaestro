$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$tree=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Summarize.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){ throw 'Summary parser errors.' }
foreach($name in @('Id-Set','Difference','Check-Verification','Summarize-VerificationRecords')){
    $definition=$tree.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if(!$definition){throw ('Missing summary function '+$name)}
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
function Record([long]$Id){
    $value=@{SourceId=$Id;SourceUid=('source'+$Id);ActualUids=@('actual'+$Id);ExpectedProbeErrors=@();ActualProbeErrors=@()}
    foreach($field in @('MatchesIdentity','ClassMatches','TypeMatches','GeometryMatches','GeometryChecked','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','ConnectionsMatch','SystemTypeMatches','Passed')){$value[$field]=$true}
    return $value
}
$records=@((Record 1),(Record 2));$expected=Id-Set @(1,2)
$report=@{Records=$records;Missing=@();Duplicates=@();Failed=@();UnexpectedModelObjects=@()}
if(!(Check-Verification $report $expected).Passed){throw 'Legacy records without wall-order field must remain compatible.'}
$records[0].WallJoinsMatch=$false
if((Check-Verification $report $expected).Passed){throw 'A false wall-order check must reject the verification even when Passed=true.'}
$counts=Summarize-VerificationRecords $records
if($counts.MismatchesAmongPresent.WallJoinsMatch -ne 1 -or $counts.UnverifiedChecksAmongPresent.WallJoinsMatch -ne 1){throw 'Mismatch and missing wall-order checks must be reported separately.'}
$records[0].WallJoinsMatch=$true
if((Check-Verification $report $expected).Passed){throw 'A missing wall-order field on one new record must reject the verification.'}
$records[1].WallJoinsMatch=$true
if(!(Check-Verification $report $expected).Passed){throw 'All exact wall-order checks should pass with otherwise complete evidence.'}
[pscustomobject]@{Passed=$true;Cases=4;Coverage='Legacy compatibility; false wall-order rejection; partial field rejection; complete field acceptance.'}|ConvertTo-Json
