#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][int]$CapturePid,[switch]$Execute)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runs=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/history-stress')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
$run=[IO.Path]::GetFullPath($RunDirectory)
if(!$run.StartsWith($runs,[StringComparison]::OrdinalIgnoreCase)){throw 'Run outside exact stress workspace.'}
function NoRedirect([string]$path){$current=[IO.Path]::GetFullPath($path);while($current){$item=Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue;if($item.LinkType -or $item.LinkTarget){throw ('Redirected path refused: '+$current)};$parent=Split-Path -Parent $current;if($parent -eq $current){break};$current=$parent}}
function Read([string]$path){NoRedirect $path; $item=Get-Item -LiteralPath $path;if($item.Length -gt 1048576){throw 'Oversized JSON evidence.'};return [IO.File]::ReadAllText($path)|ConvertFrom-Json -AsHashtable -Depth 50}
function Sha([string]$path){NoRedirect $path;return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Bool($o,[string]$name,[bool]$value){if($o[$name] -isnot [bool] -or $o[$name] -ne $value){throw ('Required boolean '+$name+'='+$value)}}
function WriteAtomic([string]$path,$value){$tmp=$path+'.'+[guid]::NewGuid().ToString('N')+'.partial';[IO.File]::WriteAllText($tmp,($value|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false));if([IO.File]::Exists($path)){[IO.File]::Replace($tmp,$path,$null)}else{[IO.File]::Move($tmp,$path)}}
function State {
    NoRedirect $run
    $readyPath=Join-Path $run 'ready.json';$finishPath=Join-Path $run 'finished.json';$stopPath=Join-Path $run 'isolated-process-stopped.json';$configPath=Join-Path $run 'config.json'
    $ready=Read $readyPath;$finish=Read $finishPath;$stop=Read $stopPath;$config=Read $configPath
    if($CapturePid -le 0 -or $ready.Pid -ne $CapturePid -or $stop.Pid -ne $CapturePid -or $config.DurablePhase -ne 'restore' -or $config.Scenario -ne 'durable'){throw 'Exact isolated restore PID/config required.'}
    Bool $stop 'GuardedStop' $true
    if(![string]::Equals([IO.Path]::GetFullPath($stop.ClosureProof),$finishPath,[StringComparison]::OrdinalIgnoreCase)){throw 'Guarded process stop does not reference exact finished proof.'}
    if([Diagnostics.Process]::GetProcesses()|Where-Object Id -eq $CapturePid){throw 'Isolated restore process is still alive.'}
    foreach($flag in 'Closed','OldDocumentInvalid','AbsentFromApplicationDocuments','ClosedWithoutSaving','SourceUnchanged','CopyAndSourceUnchanged'){Bool $finish $flag $true}
    $capture=[IO.Path]::GetFullPath($config.DurableCaptureDirectory)
    if(!$capture.StartsWith($runs,[StringComparison]::OrdinalIgnoreCase)){throw 'Capture history outside stress workspace.'}
    NoRedirect $capture
    $sourcePathFile=Join-Path $capture 'source-path.txt';$sourceHashFile=Join-Path $capture 'source-sha256.txt'
    $source=[IO.File]::ReadAllText($sourcePathFile).Trim();$expected=[IO.File]::ReadAllText($sourceHashFile).Trim();$copy=Join-Path $capture 'Antibes-stress.rvt';$deleted=Join-Path $capture 'Antibes-supprime.rvt'
    if($expected -notmatch '^[A-Fa-f0-9]{64}$' -or $ready.Model -ne $deleted -or (Sha $source) -ne $expected -or (Sha $copy) -ne $expected -or $finish.SourceSha256Before -ne $expected -or $finish.SourceSha256After -ne $expected){throw 'Original source/initial test-copy integrity mismatch.'}
    $deletedHash=Sha $deleted;$deletedLength=(Get-Item -LiteralPath $deleted).Length
    if($deletedHash -ne $finish.DeletedProjectSha256AfterRestore){throw 'Deleted checkpoint differs from closed restore proof.'}
    $history=Join-Path $capture 'history';if($ready.HistoryDirectory -ne $history){throw 'Ready history directory mismatch.'}
    $cache=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/HistoryBridge'))
    $targets=@();$excluded=@();$inputs=@(Sha $readyPath;Sha $finishPath;Sha $stopPath;Sha $configPath;Sha $sourcePathFile;Sha $sourceHashFile;$expected;$deletedHash)
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $history 'history-data/sessions') -File -Filter '*.json'){
        $m=Read $file.FullName;if($m.ProcessId -ne $CapturePid){continue}
        $reason=if($m.Closed -isnot [bool] -or !$m.Closed){'Session not explicitly closed'}elseif($m.Referenced -isnot [bool] -or $m.Referenced){'Session referenced or unknown'}elseif($m.Deleted -isnot [bool] -or $m.Deleted){'Session already deleted or unknown'}elseif($m.ModelKey -ne $deleted){'Different model, including empty finishing document'}else{$null}
        if($reason){$excluded+=@{Manifest=$file.FullName;SessionId=$m.Id;Reason=$reason};continue}
        if($m.Version -ne 1 -or $m.Id -notmatch '^[a-fA-F0-9]{32}$' -or $file.Name -cne ($m.Id+'.json') -or [DateTime]$m.ProcessStartedUtc -ne [DateTime]$stop.StartedUtc){throw 'Session UUID/version/process-start mismatch.'}
        $bridge=[IO.Path]::GetFullPath($m.BridgeFile)
        if((Split-Path -Parent $bridge) -ne $cache -or [IO.Path]::GetFileName($bridge) -cne ('snapshot-opening-'+$m.Id+'.rvt')){throw 'Bridge outside exact local cache/UUID name.'}
        if($bridge -in @($source,$copy,$deleted)){throw 'Protected RVT targeted.'}
        NoRedirect $bridge;$item=Get-Item -LiteralPath $bridge
        if($item.PSIsContainer -or $item.Length -ne $deletedLength -or (Sha $bridge) -ne $deletedHash){throw 'Unused bridge does not exactly match deleted-state checkpoint bytes.'}
        $manifestHash=Sha $file.FullName
        $targets+=@{Manifest=$file.FullName;ManifestSha256=$manifestHash;SessionId=$m.Id;BridgeFile=$bridge;BridgeSha256=$deletedHash;BridgeBytes=$item.Length;ProcessId=$CapturePid;Referenced=$false;Closed=$true}
        $inputs+=@($file.FullName,$manifestHash,$bridge,$deletedHash,[string]$item.Length)
    }
    if($targets.Count -gt 1){throw 'More than one owned restore copy requires a separate reviewed cleanup.'}
    $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($inputs -join '|'))))
    return @{CapturePid=$CapturePid;RunDirectory=$run;SourcePath=$source;InitialCopy=$copy;DeletedProject=$deleted;SourceSha256=$expected;DeletedProjectSha256=$deletedHash;Targets=$targets;ExcludedSessions=$excluded;ProofFingerprint=$fingerprint;CacheRoot=$cache;ProcessExited=$true}
}
$state=State;$previewPath=Join-Path $run 'unused-restore-bridges-preview.json';$resultPath=Join-Path $run 'unused-restore-bridges-cleanup.json'
if(!$Execute){WriteAtomic $previewPath ([ordered]@{AtUtc=[DateTime]::UtcNow.ToString('O');Mode='Preview';Removed=$false;State=$state;ScriptSha256=(Sha $PSCommandPath)});$state|ConvertTo-Json -Depth 8;return}
if([IO.File]::Exists($resultPath)){throw 'Existing cleanup result preserved.'}
$preview=Read $previewPath
if($preview.Mode -ne 'Preview' -or $preview.State.ProofFingerprint -ne $state.ProofFingerprint -or $preview.ScriptSha256 -ne (Sha $PSCommandPath)){throw 'Exact reviewed Preview and unchanged script/evidence required.'}
$removed=@()
foreach($target in $state.Targets){
    $leasePath=$target.BridgeFile+'.lease';NoRedirect $leasePath;$lease=[IO.FileStream]::new($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $again=State
        if($again.ProofFingerprint -ne $state.ProofFingerprint){throw 'Evidence changed before cleanup.'}
        # One exact run-owned, non-referenced, closed temporary bridge only.
        Remove-Item -LiteralPath $target.BridgeFile -ErrorAction Stop
        if([IO.File]::Exists($target.BridgeFile)){throw 'Target still exists after removal.'}
        $removed+=@{BridgeFile=$target.BridgeFile;ManifestSha256=$target.ManifestSha256;BridgeSha256=$target.BridgeSha256}
    }finally{$lease.Dispose()}
}
WriteAtomic $resultPath ([ordered]@{AtUtc=[DateTime]::UtcNow.ToString('O');Mode='Execute';CapturePid=$CapturePid;ProofFingerprint=$state.ProofFingerprint;Removed=$removed;ExcludedSessions=$state.ExcludedSessions;ManualOwnedScratchCleanup=$true;AutomaticCleanupVerified=$false;FullRestoreVerified=$false})
$resultPath
