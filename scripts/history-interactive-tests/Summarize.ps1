param(
    [Parameter(Mandatory = $true)][string]$RunName
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$run = Join-Path $root ('tmp/history-interactive/' + $RunName)
$rows = @(Get-Content -LiteralPath (Join-Path $run 'observer.jsonl') | ForEach-Object {
    try { $_ | ConvertFrom-Json } catch { }
})
function Stats($values) {
    $sorted = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    [ordered]@{
        Count = $sorted.Count
        P50Milliseconds = $sorted[[Math]::Ceiling($sorted.Count * .50) - 1]
        P95Milliseconds = $sorted[[Math]::Ceiling($sorted.Count * .95) - 1]
        MaximumMilliseconds = $sorted[-1]
        Over100Milliseconds = @($sorted | Where-Object { $_ -gt 100 }).Count
        Over1000Milliseconds = @($sorted | Where-Object { $_ -gt 1000 }).Count
    }
}
$ready = $rows | Where-Object Kind -eq 'observer_ready' | Select-Object -First 1
$opened = $rows | Where-Object Kind -eq 'document_opened' | Select-Object -First 1
$idle = $rows | Where-Object Kind -eq 'first_active_document_idle' | Select-Object -First 1
$complete = $rows | Where-Object { $_.Kind -eq 'production_state' -and $_.Data.Preload.Complete } | Select-Object -First 1
$last = $rows | Where-Object Kind -eq 'production_state' | Select-Object -Last 1
$journal = @($rows | Where-Object Kind -eq 'journal_delete_first_seen')
$phaseRows = @(Get-ChildItem -LiteralPath (Join-Path $run 'history/history-performance') -Filter '*.jsonl' -ErrorAction SilentlyContinue | ForEach-Object {
    Get-Content -LiteralPath $_.FullName | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } }
})
$stages = @($rows | Where-Object Kind -eq 'dispatcher_tick' | Group-Object Stage | ForEach-Object {
    [ordered]@{
        Stage = $_.Name
        FirstUtc = $_.Group[0].AtUtc
        LastUtc = $_.Group[-1].AtUtc
        Dispatcher = Stats $_.Group.Data.GapMilliseconds
        ForegroundTicks = @($_.Group | Where-Object { $_.Data.ForegroundRevit }).Count
        ForegroundNewInputTicks = @($_.Group | Where-Object { $_.Data.ForegroundRevit -and $_.Data.NewLatestInput }).Count
    }
})
$phases = @($phaseRows | Group-Object Phase | ForEach-Object {
    [ordered]@{ Phase = $_.Name; Statistics = Stats $_.Group.ElapsedMilliseconds }
})
$deletes = @($rows | Where-Object Kind -eq 'automatic_delete_begin' | ForEach-Object {
    $entry = $_
    $event = $journal | Where-Object { $_.Data.Event.ElementId -eq [Convert]::ToInt64($entry.Data.Target.Uid.Split('-')[-1], 16) } | Select-Object -First 1
    [ordered]@{
        AtUtc = $entry.AtUtc
        Target = $entry.Data.Target
        RequestedAfterSeconds = $entry.Data.RequestedAfterSeconds
        ActualAfterOpenMilliseconds = $entry.Data.ActualAfterOpenMilliseconds
        ViewportMarkerAlreadyPresent = $entry.Data.ViewportReadyMarker
        Before = $entry.Data.Before
        PersistedAudit = $event.Data.Event.DeletionAudit
        PersistedDelta = $event.Data.Event.Delta
        JournalPollUpperBoundMilliseconds = $event.Data.PollUpperBoundMilliseconds
    }
})
$restoreResults = @($rows | Where-Object Kind -eq 'restore_result')
$lastRestore = $restoreResults | Select-Object -Last 1
$targetValidation = @(
    @{ Name = 'Générique'; Id = 474717; Uid = '2c5202a5-15c8-4179-86b7-68fe9802c14d-00073e5d' },
    @{ Name = 'Plante'; Id = 520659; Uid = 'ed00bb15-8d5c-445b-9e0c-fb4a015aaced-0007f1d3' },
    @{ Name = 'IPN'; Id = 1010815; Uid = '2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f' }
) | ForEach-Object {
    $targetSpec = $_
    $item = $lastRestore.Data.Result.Items | Where-Object SourceUniqueId -eq $targetSpec.Uid | Select-Object -Last 1
    $verification = $rows | Where-Object { $_.Kind -eq 'restore_verification' -and $_.Data.Source -eq $targetSpec.Uid } | Select-Object -Last 1
    [ordered]@{
        Target = $targetSpec.Name
        OriginalElementId = $targetSpec.Id
        OriginalUniqueId = $targetSpec.Uid
        ActualUniqueId = $item.UniqueId
        Created = $item.Created
        Existing = $item.Existing
        ClassMatches = $verification.Data.ClassMatches
        TypeMatches = $verification.Data.TypeMatches
        GeometryMatches = $verification.Data.GeometryMatches
        Passed = ($null -ne $item -and ($item.Created -or $item.Existing) -and $verification.Data.ClassMatches -and $verification.Data.TypeMatches -and $verification.Data.GeometryMatches)
    }
}
$closed = $rows | Where-Object Kind -eq 'copy_closed_without_saving' | Select-Object -Last 1
$initialSha = (Get-Content -LiteralPath (Join-Path $run 'source-sha256.txt') -Raw).Trim()
$copyPath = Join-Path $run 'Antibes-interactif.rvt'
$sourcePath = Join-Path $root 'tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt'
$copySha = if ($closed) { (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash } else { $null }
$sourceSha = if ($closed) { (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash } else { $null }
$result = [ordered]@{
    SchemaVersion = 1
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
    Run = $run
    Product = $ready.Data
    OpenedUtc = $opened.AtUtc
    OpenCommand = @($rows | Where-Object { $_.Kind -in @('open_command_begin', 'open_command_returned') })
    FirstActiveIdleUtc = $idle.AtUtc
    FirstHumanViewportMarker = $rows | Where-Object { $_.Kind -eq 'external_request' -and $_.Data.Request -eq 'viewport-ready' } | Select-Object -First 1
    BasicInventoryFirstCompleteUtc = $complete.AtUtc
    BasicInventoryMillisecondsAfterOpened = if ($complete -and $opened) { ([DateTime]$complete.AtUtc - [DateTime]$opened.AtUtc).TotalMilliseconds } else { $null }
    BasicInventoryMillisecondsAfterFirstIdle = if ($complete -and $idle) { ([DateTime]$complete.AtUtc - [DateTime]$idle.AtUtc).TotalMilliseconds } else { $null }
    FinalObservedState = $last
    AutomaticDeletions = $deletes
    AllPersistedDeletionIds = @($journal.Data.Event.ElementId)
    AdditionalPersistedDeletionIds = @($journal.Data.Event.ElementId | Where-Object { $_ -notin @(474717, 520659, 1010815) })
    Stages = $stages
    ProductPhases = $phases
    ObserverStateCost = Stats @($rows | Where-Object Kind -eq 'observer_state_cost' | ForEach-Object { $_.Data.Milliseconds })
    TargetValidationPassed = (@($targetValidation).Count -eq 3 -and @($targetValidation | Where-Object { -not $_.Passed }).Count -eq 0)
    TargetValidation = @($targetValidation)
    BatchAllItemsSucceeded = ($null -ne $lastRestore -and $lastRestore.Data.Result.Failed -eq 0)
    AdditionalFailedItems = @($lastRestore.Data.Result.Items | Where-Object { -not ($_.Created -or $_.Existing -or $_.IncludedInParent) })
    Restores = $restoreResults
    RestoreVerifications = @($rows | Where-Object Kind -eq 'restore_verification')
    CommandsErrors = @($rows | Where-Object { $_.Kind -in @('command_error', 'state_error', 'metadata_probe_error') })
    Finish = @($rows | Where-Object { $_.Kind -in @('finish_requested', 'copy_closed_without_saving') })
    ObserverFileErrors = if (Test-Path -LiteralPath (Join-Path $run 'observer-errors.txt')) { Get-Content -LiteralPath (Join-Path $run 'observer-errors.txt') -Raw } else { $null }
    Integrity = [ordered]@{
        CopyClosedWithoutSaving = ($null -ne $closed)
        ClosedUtc = $closed.AtUtc
        FinishedMarkerPresent = (Test-Path -LiteralPath (Join-Path $run 'finished.txt'))
        InitialSha256 = $initialSha
        CopyPath = $copyPath
        ClosedCopySha256ReportedByObserver = $closed.Data.ModelSha256
        FinalCopySha256 = $copySha
        TestSourcePath = $sourcePath
        FinalTestSourceSha256 = $sourceSha
        CopyAndTestSourceUnchanged = ($null -ne $closed -and $initialSha -eq $copySha -and $initialSha -eq $sourceSha)
    }
    Limitations = @(
        'Dispatcher background gaps are not painted frame times or plugin-only stalls.',
        'A Windows latest-input timestamp can omit intermediate inputs; it does not prove that Revit processed each gesture.',
        'Journal delay is an upper bound including worker queue, writes and 100 ms polling.',
        'Product phases can be nested and must not be added together.',
        'Automatic deletions execute only when Revit services their ExternalEvent; actual delays are reported.',
        'The three requested targets have no MEP connection; geometry verification covers point or axis endpoints and midpoint only.'
    )
}
$target = Join-Path $run 'interactive-summary.json'
$result | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $target -Encoding UTF8
Write-Output $target
