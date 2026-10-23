param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunDirectory)
$rawPath = Join-Path $run 'regressions.json'
$rawHash = (Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash
$raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json -Depth 100
$toleranceFeet = 1e-5

function Same-Point($First,$Second) {
    if ($null -eq $First -or $null -eq $Second) { return $null -eq $First -and $null -eq $Second }
    if (@($First).Count -ne @($Second).Count) { return $false }
    $sum=0.0
    for($index=0;$index -lt @($First).Count;$index++){ $difference=[double]$First[$index]-[double]$Second[$index]; $sum+=$difference*$difference }
    return [Math]::Sqrt($sum) -le $toleranceFeet
}
function Same-Points($First,$Second) {
    if($null -eq $First -or $null -eq $Second){ return $null -eq $First -and $null -eq $Second }
    if(@($First).Count -ne @($Second).Count){ return $false }
    for($index=0;$index -lt @($First).Count;$index++){ if(!(Same-Point $First[$index] $Second[$index])){ return $false } }
    return $true
}
function Same-Geometry($First,$Second) {
    return (Same-Point $First.Point $Second.Point) -and (Same-Points $First.Curve $Second.Curve) -and (Same-Points $First.Transform $Second.Transform) -and (Same-Points $First.BBox $Second.BBox)
}
function Same-PortShape($First,$Second,[switch]$Classification) {
    if(@($First).Count -ne @($Second).Count){ return $false }
    foreach($port in $First){
        $matches=@($Second | Where-Object { $_.Id -eq $port.Id -and $_.Domain -eq $port.Domain -and $_.Type -eq $port.Type -and $_.Shape -eq $port.Shape -and (Same-Point $_.Point $port.Point) })
        if($matches.Count -ne 1 -or ($Classification -and $matches[0].Classification -cne $port.Classification)){ return $false }
    }
    return $true
}

$results=foreach($case in $raw.Cases){
    if($case.Name -in @('plant-or-foundation-offset','rotation-and-ports')){
        $geometry=Same-Geometry $case.Expected $case.Actual
        $perturbationDetected=!(Same-Geometry $case.Expected $case.Perturbed)
        $ports=Same-PortShape $case.Expected.Ports $case.Actual.Ports
        $sourceClassification=Same-PortShape $case.Expected.Ports $case.Actual.Ports -Classification
        $fixtureClassification=@($case.Perturbed.Ports).Count -eq @($case.Actual.Ports).Count
        foreach($port in $case.Perturbed.Ports){
            $matching=@($case.Actual.Ports | Where-Object { $_.Id -eq $port.Id -and $_.Domain -eq $port.Domain -and $_.Type -eq $port.Type -and $_.Shape -eq $port.Shape -and $_.Classification -ceq $port.Classification })
            if($matching.Count -ne 1){ $fixtureClassification=$false }
        }
        $type=$case.Expected.Class -ceq $case.Actual.Class -and $case.Expected.FamilyName -ceq $case.Actual.FamilyName -and $case.Expected.TypeName -ceq $case.Actual.TypeName
        $unconnected=@($case.Actual.Ports | ForEach-Object Peers).Count -eq 0
        $passed=$geometry -and $ports -and $type -and $fixtureClassification -and $perturbationDetected -and $case.PerturbationRejected -and $case.FinalMatches -and $null -eq $case.FinalFailure
        [ordered]@{
            Name=$case.Name;SourceId=$case.SourceId;RawPassed=$case.Passed;PlacementAndPortPositionsPassed=$passed
            IndependentGeometryMatches=$geometry;PortIdentityDomainTypeShapeAndPointMatches=$ports;FamilyAndTypeNamesMatch=$type
            DeliberateFaultRejected=$case.PerturbationRejected;DeliberateFaultIndependentlyDetected=$perturbationDetected;FixtureClassificationUnchangedDuringRepair=$fixtureClassification
            SourceClassificationMatches=$sourceClassification;ActualFixtureDisconnected=$unconnected
            SourceClassifications=@($case.Expected.Ports | Select-Object Id,Classification);ActualClassifications=@($case.Actual.Ports | Select-Object Id,Classification)
            InitialFixtureGeometryExported=$false
            Limit='Independent final geometry and port coordinates are verified. The isolated fixture intentionally has no neighbors; this does not prove source classification or reconnection fidelity. The mass test still requires exact source classifications and system UID, including null.'
        }
    } elseif($case.Name -eq 'stairs-generated-coincident-balusters'){
        $distinct=@($case.Records.ActualUid | Sort-Object -Unique).Count -eq 6
        $all=($case.RootMatches -and $case.ActualBalusters -eq 6 -and $distinct)
        foreach($record in $case.Records){
            $all=$all -and (Same-Geometry $record.Expected $record.Actual) -and $record.OwnerMatches -and $record.IdentityInjective -and $record.TypeMatches
        }
        [ordered]@{Name=$case.Name;RawPassed=$case.Passed;PlacementAndPortPositionsPassed=$all;DistinctActualUids=$distinct;ActualBalusters=$case.ActualBalusters;RootMatches=$case.RootMatches}
    } else {
        $distinct=@($case.Mapping.ActualUid | Sort-Object -Unique).Count -eq @($case.Mapping).Count
        [ordered]@{Name=$case.Name;RawPassed=$case.Passed;PlacementAndPortPositionsPassed=($case.Passed -and $distinct);DistinctActualUids=$distinct;RawAssertionsRetained=$true}
    }
}
$result=[ordered]@{
    AtUtc=[DateTime]::UtcNow.ToString('o');Scope='Identity and geometric placement helper regressions; disconnected fixture source classification is reported separately.'
    ProductSha256=$raw.ProductSha256;RawFile='regressions.json';RawSha256=$rawHash;RawGlobalPassed=$raw.Passed
    Cases=@($results);RawErrors=@($raw.Errors);CaseCount=@($results).Count
    IdentityAndPlacementPassed=(@($raw.Errors).Count -eq 0 -and @($results).Count -eq 10 -and @($results | Where-Object {!$_.PlacementAndPortPositionsPassed}).Count -eq 0)
    FullSourceFidelityPassed=$raw.Passed;ConnectedNetworkReconciliationVerified=$false;WholeModelRestorationVerified=$false
    ToleranceFeet=$toleranceFeet;InitialFixtureSemanticBaselineMissing=$true
    Limit='The raw global result remains false for the two disconnected fittings whose dynamic source classification disappears on copying. This separate result does not rewrite that result or weaken the strict whole-model validator.'
}
$destination=Join-Path $run 'regressions-placement-validation.json'
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $destination -Encoding UTF8
if((Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash -ne $rawHash){ throw 'Raw regression evidence changed while validating.' }
[PSCustomObject]$result | Select-Object RawGlobalPassed,IdentityAndPlacementPassed,FullSourceFidelityPassed,ConnectedNetworkReconciliationVerified,CaseCount | ConvertTo-Json
