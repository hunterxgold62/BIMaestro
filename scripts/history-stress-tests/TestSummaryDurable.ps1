#Requires -Version 7.0
$ErrorActionPreference='Stop'
$parseProblems=$null;$tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Summarize.ps1'),[ref]$tokens,[ref]$parseProblems)
if($parseProblems.Count) { throw ($parseProblems.Message -join '; ') }
foreach($name in @('Id-Set','Difference','Check-Verification','Check-DurableEvidence')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if(!$definition) { throw ('Missing production analysis function '+$name) }
    Invoke-Expression $definition.Extent.Text
}
$checks=0
function Assert-Check([bool]$Condition,[string]$Message) { if(!$Condition) { throw $Message };$script:checks++ }
function Clone($Value) { return ($Value | ConvertTo-Json -Depth 24 | ConvertFrom-Json -AsHashtable) }
$missingBridge=Join-Path ([IO.Path]::GetTempPath()) ('BIMaestro-missing-bridge-'+[guid]::NewGuid().ToString('N')+'.rvt')
$sourceHash='A'*64;$deletedHash='B'*64
$closure=@{CapturePid=10;SessionClosed=$true;ConversionComplete=$true;Closed=$true;OldDocumentInvalid=$true;AbsentFromApplicationDocuments=$true;
    AllBridgeFilesAbsent=$true;BridgeFiles=@($missingBridge);DeletedBridgeFiles=@($missingBridge);SourceAndInitialCopyUnchanged=$true;
    SourceSha256Before=$sourceHash;SourceSha256After=$sourceHash;InitialCopySha256=$sourceHash;DeletedProjectSha256=$deletedHash;ExpectedDeletionEvents=3600}
$conversion=@{Complete=$true;Status=@{Complete=$true;PendingJobs=0;ConvertedEvents=3600;FailedJobs=0;Failures=@()}}
$baseline=@{CapturePid=10;RestorePid=11;DistinctProcess=$true;CaptureProcessExited=$true;DeletedProjectSha256=$deletedHash}
$inputs=@{RestorePid=11;ExactEventCoverage=$true;AllBridgeFilesAbsent=$true;WholeModelOrBridgeReferences=0;ExpectedEvents=3600;Requests=3600;LoadedEvents=3600;
    Artifacts=@(@{WholeModel=$false;File=$null;PayloadId=('C'*64);TypeVersion=('D'*64)})}
$ready=@{Pid=11};$integrity=@{AllOpeningBridgeFilesStillAbsent=$true;DeletedProjectSha256AfterRestore=$deletedHash}
Assert-Check (Check-DurableEvidence $closure $conversion $baseline $inputs $ready $integrity).Passed 'Complete durable prerequisites should pass without declaring model fidelity.'
$changed=Clone $closure;$changed.SessionClosed=$false
Assert-Check (!(Check-DurableEvidence $changed $conversion $baseline $inputs $ready $integrity).Passed) 'A live capture session cannot pass.'
$changed=Clone $baseline;$changed.CaptureProcessExited=$false
Assert-Check (!(Check-DurableEvidence $closure $conversion $changed $inputs $ready $integrity).Passed) 'A capture process still running cannot pass.'
$changed=Clone $inputs;$changed.Artifacts[0].WholeModel=$true
Assert-Check (!(Check-DurableEvidence $closure $conversion $baseline $changed $ready $integrity).Passed) 'An old WholeModel receipt must fail even when the aggregate counter claims zero.'
$changed=Clone $inputs;$changed.Artifacts[0].PayloadId='invalid'
Assert-Check (!(Check-DurableEvidence $closure $conversion $baseline $changed $ready $integrity).Passed) 'An unverified payload identity must fail.'
$changed=Clone $closure;$changed.BridgeFiles=@($PSCommandPath);$changed.DeletedBridgeFiles=@($PSCommandPath)
Assert-Check (!(Check-DurableEvidence $changed $conversion $baseline $inputs $ready $integrity).Passed) 'A physically existing bridge must fail despite claimed removal.'
$changed=Clone $integrity;$changed.DeletedProjectSha256AfterRestore='changed'
Assert-Check (!(Check-DurableEvidence $closure $conversion $baseline $inputs $ready $changed).Passed) 'The deleted checkpoint must remain unchanged.'
$changed=Clone $inputs;$changed.LoadedEvents=3599
Assert-Check (!(Check-DurableEvidence $closure $conversion $baseline $changed $ready $integrity).Passed) 'A truncated resolved set must fail.'
$changed=Clone $conversion;$changed.Status.PendingJobs=1
Assert-Check (!(Check-DurableEvidence $closure $changed $baseline $inputs $ready $integrity).Passed) 'Pending conversion cannot pass.'
$changed=Clone $conversion;$changed.Status.ConvertedEvents=0
Assert-Check (!(Check-DurableEvidence $closure $changed $baseline $inputs $ready $integrity).Passed) 'An empty service queue is not evidence that the batch was converted.'
$record=@{SourceId=1;SourceUid='source';ActualUids=@('actual');ExpectedProbeErrors=@();ActualProbeErrors=@();
    TypeUidMatchesSource=$false;HistoricalTypeProvenance=$true;TypeDefinitionParametersMatch=$true;Expected=@{Ports=@()};PortSystemTypesMatch=$true;PortSystemTypeEvidence=@();
    SystemTypeUidMatchesSource=$true;SystemTypeEvidence=@{Passed=$true};TypeDefinitionEvidence=@{TypeVersionMatchesResolvedPayload=$true;SourceTypeOriginPresent=$true;
    FamilyIdentityOrProvenanceMatches=$true;TypeClassAndCategoryMatch=$true;TypeVersionProbeError=$null}}
foreach($flag in @('MatchesIdentity','ClassMatches','TypeMatches','GeometryMatches','GeometryChecked','BBoxMatches','DimensionsMatch','HostMatches','GroupMatches','JoinsMatch','ConnectionsMatch','SystemTypeMatches','Passed')) { $record[$flag]=$true }
$verification=@{Records=@($record);Missing=@();Duplicates=@();Failed=@();UnexpectedModelObjects=@()};$expectedIds=Id-Set @(1)
Assert-Check (Check-Verification $verification $expectedIds $true).Passed 'A fully proven versioned type may pass the durable verification gate.'
$changed=Clone $verification;$changed.Records[0].HistoricalTypeProvenance=$false
Assert-Check (!(Check-Verification $changed $expectedIds $true).Passed) 'A versioned UID without source provenance cannot pass.'
$changed=Clone $verification;$changed.Records[0].TypeDefinitionParametersMatch=$false
Assert-Check (!(Check-Verification $changed $expectedIds $true).Passed) 'A changed type definition cannot pass despite valid lineage.'
$changed=Clone $verification;$changed.Records[0].Remove('TypeDefinitionParametersMatch') | Out-Null
Assert-Check (!(Check-Verification $changed $expectedIds $true).Passed) 'A wholly missing type probe is not legacy-compatible in durable mode.'
$changed=Clone $verification;$changed.Records[0].SystemTypeUidMatchesSource=$false
Assert-Check (!(Check-Verification $changed $expectedIds $true).Passed) 'A new system UID requires its own historical version proof.'
$changed=Clone $verification;$changed.Records[0].Expected.Ports=@(@{Peers=@()})
Assert-Check (!(Check-Verification $changed $expectedIds $true).Passed) 'Missing port system evidence cannot pass.'
Write-Output ('PASS: '+$checks+' durable summary assertions; analysis functions only, no Revit, build, RVT copy or request file.')
