#Requires -Version 7.0
<#
.SYNOPSIS
Read-only diagnostic of persisted deleted-element history, not a Revit restore test.
.DESCRIPTION
Reads every element-history-*.jsonl / *.jsonl.gz in the selected history store.
Compression never makes an external RVT or a project UniqueId self-contained.
The current production recipe v1 has no frozen type/family definition payload;
therefore this tool cannot certify v1 as independent of the original project.
A future durable format needs an explicitly supported verifier, not a boolean
claim or an unrecognized "Definitions" object. Unknown formats fail closed.
The only write is a separate JSON diagnostic report outside the history store.
Exit 0: autonomy demonstrated; exit 1: failed/unverified; exit 2: report could not
be written. No Revit API, product loading, conversion, cleanup or file deletion.
.EXAMPLE
./TestHistoryDurability.ps1 -RunDirectory tmp/history-stress/antibes-physical-fixed-early-v3
.EXAMPLE
./TestHistoryDurability.ps1 -HistoryDirectory C:/history -OutputPath C:/diagnostics/history.json
#>
[CmdletBinding(DefaultParameterSetName='History')]
param(
    [Parameter(Mandatory,ParameterSetName='History')][string]$HistoryDirectory,
    [Parameter(Mandatory,ParameterSetName='Run')][string]$RunDirectory,
    [string[]]$DeletionBatchId,
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$history=if($PSCmdlet.ParameterSetName -eq 'Run') { [IO.Path]::GetFullPath((Join-Path $RunDirectory 'history')) } else { [IO.Path]::GetFullPath($HistoryDirectory) }
if(!$OutputPath) { $OutputPath=Join-Path $repo ('tmp/history-durability/'+[IO.Path]::GetFileName($history.TrimEnd([IO.Path]::DirectorySeparatorChar))+'-'+[guid]::NewGuid().ToString('N')+'.json') }
$output=[IO.Path]::GetFullPath($OutputPath)
$historyPrefix=$history.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if($output.Equals($history,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($historyPrefix,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be outside the input history store. No history file will be changed.'
}
$fileIssues=[Collections.Generic.List[object]]::new()
$formatIssues=[Collections.Generic.List[object]]::new()
$records=[Collections.Generic.List[object]]::new()
$fileEvidence=[Collections.Generic.List[object]]::new()
$rvtRefs=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$projectRefs=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$identitySet=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$stats=[ordered]@{Files=0;JsonlFiles=0;GzipFiles=0;InputBytes=0L;Lines=0;BlankLines=0;KnownRecords=0;NonDeletionRecords=0;DeletionRecords=0;SelectedDeletionRecords=0;FilteredDeletionRecords=0;DuplicateDeletionRecords=0;MissingRecipes=0;RecipeOccurrences=0;DirectRecipes=0;NativeRecipes=0;DirectRecipesWithOpeningFallback=0;NestedCircuitRecipes=0;NativeReferences=0;OpeningFallbackReferences=0;WholeModelCheckpointReferences=0;ReceiptsNotReady=0;RecipesWithoutFrozenDefinitionProof=0}
$eventKeys=@('Ts','Project','ModelGuid','ModelKey','ElementId','UniqueId','Category','Family','TypeName','ThumbnailPath','Action','User','Tx','Delta','DeletionAudit')
$actions=@('create','delete','modify','modify_skip','move','type_change','param_change','geometry_change')
$recipeKeys=@('Version','Kind','Native','OpeningFallback','HistoricalPlacement','NativeFallbackReason','SystemName','SystemMembers','ElectricalSystemType','ElectricalConnectionType','ElectricalStartSlot','ElectricalCircuitNumber','ElectricalPath','ElectricalCircuits','GeometryRelations','Type','Level','Host','LevelElevation','Point','Rotation','HandFlipped','FacingFlipped','Flipped','StructuralType','Height','Offset','LocationLine','Loops','Parameters','RequiresMeshPreview','RestorationOrigins','WallStructural','FloorStructural','Network','Connections','BasisX','BasisZ','CaptureWarnings','Mirrored','Ports','SketchCurves','RoofEdges','Placement','FaceReference','FaceNormal','AdaptivePoints','AdaptiveFlipped','WorkPlaneFlipped','WallProfile','ShapePoints','ShapeCreases')
$kinds=@('native_archive','family','wall','floor','network','roof','ceiling','model_curve','pipe_system','duct_system','electrical_system')
$artifactKeys=@('File','RootUniqueId','RootSourceUniqueId','MemberSourceUniqueId','Ready','Failure','RequestedUtc','StartedUtc','ReadyUtc','WholeModel','SourceElementId','SourceRootElementId','ExcludedElementIds','SourceSystemTypeDefined','SourceSystemTypeUniqueId','SourceSystemTypeName','SourceConnectedPeerTypeUniqueIds')

function Format-Issue($Context,[string]$Code,[string]$Detail) {
    $formatIssues.Add([pscustomobject]@{File=$Context.File;Line=$Context.Line;ElementId=$Context.ElementId;Code=$Code;Detail=$Detail})
}
function Add-ProjectReference([string]$Kind,$Value,[string]$Path,$Context) {
    if($null -eq $Value -or ($Value -is [string] -and [string]::IsNullOrWhiteSpace($Value))) {return}
    if($Value -isnot [string]) { Format-Issue $Context 'UnknownReferenceFormat' $Path;return }
    $key=$Kind+[char]31+$Value
    if(!$projectRefs.ContainsKey($key)) { $projectRefs[$key]=[pscustomobject][ordered]@{Kind=$Kind;UniqueId=$Value;Occurrences=0;ExamplePath=$Path;FrozenDefinitionDemonstrated=$false} }
    $projectRefs[$key].Occurrences++
}
function Inspect-Artifact($Artifact,[string]$Role,$Context,$Reasons) {
    if($null -eq $Artifact) {return}
    if($Artifact -isnot [Collections.IDictionary]) {Format-Issue $Context 'UnknownArtifactFormat' $Role;return}
    foreach($key in $Artifact.Keys) {if($key -notin $artifactKeys) {Format-Issue $Context 'UnknownArtifactField' ($Role+'.'+$key)}}
    if($Role.EndsWith('OpeningFallback',[StringComparison]::Ordinal)) {$stats.OpeningFallbackReferences++} else {$stats.NativeReferences++}
    $whole=$Artifact.WholeModel -eq $true
    if($whole) {$stats.WholeModelCheckpointReferences++;$Reasons.Add('CompleteModelCheckpointRequired')}
    if($Artifact.Ready -ne $true) {$stats.ReceiptsNotReady++;$Reasons.Add('NativeReceiptNotReady')}
    $path=$Artifact.File
    if($path -isnot [string] -or [string]::IsNullOrWhiteSpace($path)) {Format-Issue $Context 'ArtifactFileMissing' $Role;return}
    if([IO.Path]::GetExtension($path) -ine '.rvt') {Format-Issue $Context 'UnknownArtifactFileFormat' $path;return}
    $Reasons.Add('ExternalRvtRequired')
    if(!$rvtRefs.ContainsKey($path)) {
        $rvtRefs[$path]=[pscustomobject][ordered]@{PersistedPath=$path;CurrentStorePath=$null;Occurrences=0;WholeModelOccurrences=0;NativeOccurrences=0;OpeningFallbackOccurrences=0;CurrentStoreAccessible=$false;CurrentStoreBytes=$null;PersistedPathAccessible=$false;PersistedPathBytes=$null;MetadataErrors=[Collections.Generic.List[string]]::new()}
        $entry=$rvtRefs[$path]
        try {
            $entry.CurrentStorePath=[IO.Path]::GetFullPath((Join-Path $history ('native-history/'+[IO.Path]::GetFileName($path))))
            if([IO.File]::Exists($entry.CurrentStorePath)) {$info=[IO.FileInfo]::new($entry.CurrentStorePath);$entry.CurrentStoreBytes=$info.Length;$entry.CurrentStoreAccessible=$true}
        } catch {$entry.MetadataErrors.Add($_.Exception.Message)}
        try {
            if([IO.Path]::IsPathRooted($path) -and [IO.File]::Exists($path)) {$info=[IO.FileInfo]::new($path);$entry.PersistedPathBytes=$info.Length;$entry.PersistedPathAccessible=$true}
        } catch {$entry.MetadataErrors.Add($_.Exception.Message)}
    }
    $entry=$rvtRefs[$path];$entry.Occurrences++
    if($whole) {$entry.WholeModelOccurrences++}
    if($Role.EndsWith('OpeningFallback',[StringComparison]::Ordinal)) {$entry.OpeningFallbackOccurrences++}else{$entry.NativeOccurrences++}
    Add-ProjectReference 'SystemType' $Artifact.SourceSystemTypeUniqueId ($Role+'.SourceSystemTypeUniqueId') $Context
    foreach($uid in $Artifact.SourceConnectedPeerTypeUniqueIds) {Add-ProjectReference 'SystemType' $uid ($Role+'.SourceConnectedPeerTypeUniqueIds') $Context}
}
function Inspect-Recipe($Recipe,[string]$Path,$Context,$Reasons,[int]$Depth=0) {
    if($Depth -gt 12) {Format-Issue $Context 'RecipeDepthExceeded' $Path;return}
    if($Recipe -isnot [Collections.IDictionary]) {Format-Issue $Context 'UnknownRecipeFormat' $Path;return}
    $stats.RecipeOccurrences++
    foreach($key in $Recipe.Keys) {if($key -notin $recipeKeys) {Format-Issue $Context 'UnknownRecipeField' ($Path+'.'+$key)}}
    if($Recipe.Version -ne 1 -or $Recipe.Kind -notin $kinds) {Format-Issue $Context 'UnknownRecipeVersionOrKind' ($Path+': version='+$Recipe.Version+', kind='+$Recipe.Kind);return}
    # Current production v1 does not serialize/import frozen family/type/material
    # definitions. Geometry and instance parameter values do not prove that gap closed.
    $stats.RecipesWithoutFrozenDefinitionProof++;$Reasons.Add('FrozenDefinitionProofAbsentInRecipeV1')
    if($null -eq $Recipe.Native -and $Recipe.Kind -ne 'native_archive') {
        $stats.DirectRecipes++
        if($null -ne $Recipe.OpeningFallback) {$stats.DirectRecipesWithOpeningFallback++}
    } else {$stats.NativeRecipes++}
    if($Recipe.Kind -eq 'native_archive' -and $null -eq $Recipe.Native) {Format-Issue $Context 'NativeRecipeWithoutArtifact' $Path}
    Inspect-Artifact $Recipe.Native ($Path+'.Native') $Context $Reasons
    Inspect-Artifact $Recipe.OpeningFallback ($Path+'.OpeningFallback') $Context $Reasons
    foreach($kind in @('Type','Level','Host')) {Add-ProjectReference $kind $Recipe[$kind] ($Path+'.'+$kind) $Context}
    Add-ProjectReference 'SystemType' $Recipe.Network.SystemType ($Path+'.Network.SystemType') $Context
    foreach($port in $Recipe.Ports) {Add-ProjectReference 'SystemType' $port.SystemType ($Path+'.Ports.SystemType') $Context}
    foreach($link in $Recipe.Connections) {
        Add-ProjectReference 'ConnectedElement' $link.Peer ($Path+'.Connections.Peer') $Context
        Add-ProjectReference 'SystemType' $link.Port.SystemType ($Path+'.Connections.Port.SystemType') $Context
        Add-ProjectReference 'SystemType' $link.PeerPort.SystemType ($Path+'.Connections.PeerPort.SystemType') $Context
    }
    foreach($parameter in $Recipe.Parameters) {
        Add-ProjectReference 'ParameterReference' $parameter.Reference ($Path+'.Parameters.Reference') $Context
        Add-ProjectReference 'ParameterDefinition' $parameter.Definition ($Path+'.Parameters.Definition') $Context
        Add-ProjectReference 'SharedParameterDefinition' $parameter.Shared ($Path+'.Parameters.Shared') $Context
    }
    foreach($uid in $Recipe.SystemMembers) {Add-ProjectReference 'SystemMember' $uid ($Path+'.SystemMembers') $Context}
    Add-ProjectReference 'FaceReference' $Recipe.FaceReference ($Path+'.FaceReference') $Context
    foreach($relation in $Recipe.GeometryRelations) {Add-ProjectReference 'RelatedElement' $relation.Second ($Path+'.GeometryRelations.Second') $Context}
    foreach($circuit in $Recipe.ElectricalCircuits) {
        if($null -ne $circuit.Recipe) {$stats.NestedCircuitRecipes++;Inspect-Recipe $circuit.Recipe ($Path+'.ElectricalCircuits.Recipe') $Context $Reasons ($Depth+1)}
    }
}
function Inspect-Line([string]$Line,[string]$File,[int]$LineNumber) {
    $stats.Lines++
    if([string]::IsNullOrWhiteSpace($Line)) {$stats.BlankLines++;return}
    $context=@{File=$File;Line=$LineNumber;ElementId=$null}
    try {$event=$Line|ConvertFrom-Json -AsHashtable -ErrorAction Stop} catch {Format-Issue $context 'InvalidJson' $_.Exception.Message;return}
    if($event -isnot [Collections.IDictionary]) {Format-Issue $context 'UnknownRecordFormat' 'Expected a production ElementHistoryEvent object.';return}
    $context.ElementId=$event.ElementId
    $elementNumber=0L
    $time=[DateTimeOffset]::MinValue
    # ConvertFrom-Json can already produce DateTime. Casting it to string first
    # loses fractions and parses MM/dd text using the user's French culture.
    $validTime=$false
    if($event.Ts -is [DateTimeOffset]) {$time=$event.Ts;$validTime=$true}
    elseif($event.Ts -is [DateTime]) {$time=[DateTimeOffset]::new($event.Ts);$validTime=$true}
    elseif($event.Ts -is [string]) {$validTime=[DateTimeOffset]::TryParse($event.Ts,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$time)}
    if(!$event.Contains('Ts') -or !$validTime -or
        $event.Action -isnot [string] -or $event.Action -notin $actions -or !$event.Contains('ElementId') -or
        ![long]::TryParse([string]$event.ElementId,[ref]$elementNumber)) {
        Format-Issue $context 'UnknownRecordSchema' 'Required production keys Ts/Action/ElementId are missing or invalid.';return
    }
    foreach($key in $event.Keys) {if($key -notin $eventKeys) {Format-Issue $context 'UnknownRecordField' $key}}
    $stats.KnownRecords++
    if($event.Action -ne 'delete') {$stats.NonDeletionRecords++;return}
    $stats.DeletionRecords++
    if($DeletionBatchId.Count -gt 0 -and $event.DeletionAudit.BatchId -notin $DeletionBatchId) {$stats.FilteredDeletionRecords++;return}
    $stats.SelectedDeletionRecords++
    $identity=(@($time.ToUniversalTime().ToString('o'),$event.ModelKey,$event.ModelGuid,$event.UniqueId,$event.ElementId,$event.Action,$event.Tx)|ConvertTo-Json -Compress)
    if(!$identitySet.Add($identity)) {$stats.DuplicateDeletionRecords++}
    $reasons=[Collections.Generic.List[string]]::new()
    if($event.Delta -isnot [Collections.IDictionary] -or !$event.Delta.Contains('recipe')) {Format-Issue $context 'UnknownDeletionDelta' 'Production deletion payload Delta.recipe is absent.'}
    if($null -eq $event.Delta.recipe) {$stats.MissingRecipes++;$reasons.Add('RecipeMissing')}
    else {Inspect-Recipe $event.Delta.recipe 'Delta.recipe' $context $reasons}
    $records.Add([pscustomobject]@{File=$File;Line=$LineNumber;Ts=$event.Ts;ElementId=$event.ElementId;UniqueId=$event.UniqueId;BatchId=$event.DeletionAudit.BatchId;RecipeKind=$event.Delta.recipe.Kind;CaptureFailure=$event.Delta.captureFailure;AutonomyDemonstrated=$false;FailureReasons=@($reasons|Sort-Object -Unique)})
}

try {
    if(![IO.Directory]::Exists($history)) {throw "History directory does not exist: $history"}
    $files=@([IO.Directory]::GetFiles($history,'element-history-*')|Sort-Object)
    foreach($path in $files) {
        $gzip=$path.EndsWith('.jsonl.gz',[StringComparison]::OrdinalIgnoreCase)
        if(!$gzip -and !$path.EndsWith('.jsonl',[StringComparison]::OrdinalIgnoreCase)) {$fileIssues.Add([pscustomobject]@{File=$path;Code='UnsupportedHistoryFile';Error='Unrecognized journal extension; no input file was changed.'});continue}
        $stream=$null;$compressed=$null;$reader=$null
        try {
            $before=[IO.FileInfo]::new($path);$beforeLength=$before.Length;$beforeWrite=$before.LastWriteTimeUtc
            $stats.Files++;$stats.InputBytes+=$beforeLength
            if($gzip) {$stats.GzipFiles++}else{$stats.JsonlFiles++}
            $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $data=$stream
            if($gzip) {$compressed=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress,$true);$data=$compressed}
            $reader=[IO.StreamReader]::new($data,[Text.UTF8Encoding]::new($false,$true),$true,65536,$true)
            $number=0
            while($null -ne ($line=$reader.ReadLine())) {$number++;Inspect-Line $line $path $number}
            $after=[IO.FileInfo]::new($path)
            $stable=$beforeLength -eq $after.Length -and $beforeWrite -eq $after.LastWriteTimeUtc
            $fileEvidence.Add([pscustomobject]@{File=$path;Gzip=$gzip;Bytes=$beforeLength;LastWriteUtc=$beforeWrite;Lines=$number;StableDuringRead=$stable})
            if(!$stable) {$fileIssues.Add([pscustomobject]@{File=$path;Code='InputChangedDuringRead';Error='A changing journal cannot prove complete durability.'})}
        } catch {$fileIssues.Add([pscustomobject]@{File=$path;Code='JournalReadFailure';Error=$_.Exception.Message})}
        finally {if($reader){$reader.Dispose()};if($compressed){$compressed.Dispose()};if($stream){$stream.Dispose()}}
    }
} catch {$fileIssues.Add([pscustomobject]@{File=$history;Code='HistoryEnumerationFailure';Error=$_.Exception.Message})}

$references=@($projectRefs.Values|Sort-Object Kind,UniqueId)
$referenceStats=@(foreach($group in $references|Group-Object Kind) {[pscustomobject]@{Kind=$group.Name;DistinctReferences=$group.Count;Occurrences=($group.Group|Measure-Object Occurrences -Sum).Sum;FrozenDefinitionsDemonstrated=0}})
$rvt=@($rvtRefs.Values|Sort-Object PersistedPath)
$accessible=@($rvt|Where-Object CurrentStoreAccessible|Group-Object CurrentStorePath|ForEach-Object {$_.Group[0]})
$batchSummary=@(foreach($group in $records|Group-Object BatchId) {[pscustomobject]@{BatchId=$group.Name;RawDeletionRecords=$group.Count;RecipeMissing=@($group.Group|Where-Object {'RecipeMissing' -in $_.FailureReasons}).Count;CompleteModelCheckpointRequired=@($group.Group|Where-Object {'CompleteModelCheckpointRequired' -in $_.FailureReasons}).Count;ExternalRvtRequired=@($group.Group|Where-Object {'ExternalRvtRequired' -in $_.FailureReasons}).Count}})
$failed=$fileIssues.Count -gt 0 -or $formatIssues.Count -gt 0 -or $stats.SelectedDeletionRecords -eq 0 -or $stats.MissingRecipes -gt 0 -or $stats.RecipesWithoutFrozenDefinitionProof -gt 0 -or $rvt.Count -gt 0
$report=[ordered]@{
    DiagnosticSchemaVersion=1;AtUtc=[DateTime]::UtcNow.ToString('o');Failed=$failed;AutonomyDemonstrated=(!$failed)
    Status=if($failed){'FailedOrNotDemonstrated'}else{'AutonomyDemonstrated'}
    Scope=[ordered]@{HistoryDirectory=$history;RunDirectory=$RunDirectory;DeletionBatchId=@($DeletionBatchId|Where-Object {![string]::IsNullOrWhiteSpace($_)});ReadOnlyInputs=$true;RevitInvoked=$false;InputFilesModified=$false;RawRecordCounts=$true;NoRecentEventLimit=$true}
    Criterion=[ordered]@{RequiresNoCompleteModelCheckpoint=$true;RequiresIndependenceFromCurrentProjectDefinitions=$true;GzipCompressionIsConversion=$false;UnknownOrUnreadableFormatsBlockPass=$true;KnownRecipeSchema='Production v1: no frozen definition decoder/payload; autonomy is not demonstrated by coordinates, parameters or UniqueIds alone.';FutureSchemaPolicy='A future payload needs a supported verifier. SelfContained=true, hashes or unknown definition fields alone cannot confer PASS.';PassIsNotRevitReplayValidation=$true}
    Summary=$stats;UniqueSelectedDeletionIdentities=$identitySet.Count;DeletionBatchSummary=$batchSummary
    RvtSummary=[ordered]@{DistinctPersistedReferences=$rvt.Count;AccessibleDistinctCurrentStoreFiles=$accessible.Count;AccessibleCurrentStoreBytes=[long](($accessible|Measure-Object CurrentStoreBytes -Sum).Sum);MissingCurrentStoreReferences=@($rvt|Where-Object {!$_.CurrentStoreAccessible}).Count;ByteMeasurement='File metadata only; no RVT content was opened, hashed or modified.'}
    ProjectReferenceSummary=$referenceStats;ProjectReferencesWithoutFrozenDefinitions=$references
    RvtReferences=$rvt;JournalFiles=@($fileEvidence);FileIssues=@($fileIssues);FormatIssues=@($formatIssues);DeletionRecords=@($records)
    Limitations=@('No Revit document or current project types were inspected. References are unproven payload dependencies, not a claim that those elements are presently absent.','RVT contents and their type/family dependencies were not inspected. Zero explicit JSON Type/Level/Host references does not mean an opaque native recipe has no such dependencies.','All selected persisted deletion records, including internal/dependent objects, are counted; this is not the physical restoration denominator.','No external RVT is deleted or converted. GZ records retain exactly the same external dependencies.','This diagnostic assesses the persisted history provided, not unseen in-memory queues or outboxes. No deletion records means insufficient evidence, never PASS.')
}
try {
    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    [IO.File]::WriteAllText($output,($report|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
} catch {Write-Error ('Unable to write separate diagnostic report: '+$_.Exception.Message);exit 2}
Write-Output (([ordered]@{Failed=$failed;SelectedDeletionRecords=$stats.SelectedDeletionRecords;DirectRecipes=$stats.DirectRecipes;NativeReferences=$stats.NativeReferences;OpeningFallbackReferences=$stats.OpeningFallbackReferences;WholeModelCheckpointReferences=$stats.WholeModelCheckpointReferences;DistinctRvtReferences=$rvt.Count;CurrentStoreRvtBytes=$report.RvtSummary.AccessibleCurrentStoreBytes;ProjectReferenceOccurrencesWithoutFrozenDefinitions=[long](($references|Measure-Object Occurrences -Sum).Sum);FileIssues=$fileIssues.Count;FormatIssues=$formatIssues.Count;OutputPath=$output})|ConvertTo-Json -Compress)
if($failed){exit 1}
exit 0
