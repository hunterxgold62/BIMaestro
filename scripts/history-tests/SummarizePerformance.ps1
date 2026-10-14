param([Parameter(Mandatory = $true)][string]$LogDirectory,
    [string]$OutputCsv = '')
$ErrorActionPreference = 'Stop'
$records = @(Get-ChildItem -LiteralPath $LogDirectory -Filter 'performance-*.jsonl' -File | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Encoding UTF8 | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { Write-Warning 'Ligne incomplète ignorée.' }
    }
})
$summary = @($records | Group-Object Session, RuntimeDocumentId, ModelKey, Phase | ForEach-Object {
    $rows = @($_.Group)
    $times = @($rows.ElapsedMilliseconds | Sort-Object)
    $sum = ($times | Measure-Object -Sum).Sum
    [pscustomobject]@{
        Model = $rows[0].ModelName
        ModelKey = $rows[0].ModelKey
        Session = $rows[0].Session
        RuntimeDocumentId = $rows[0].RuntimeDocumentId
        Phase = $rows[0].Phase
        Samples = $rows.Count
        Elements = ($rows.Elements | Measure-Object -Sum).Sum
        ActiveSeconds = [math]::Round($sum / 1000, 3)
        MeanMs = [math]::Round($sum / $rows.Count, 2)
        P95Ms = [math]::Round($times[[math]::Max(0, [math]::Ceiling($times.Count * 0.95) - 1)], 2)
        MaxMs = [math]::Round($times[-1], 2)
        Over100Ms = @($times | Where-Object { $_ -gt 100 }).Count
        Over1000Ms = @($times | Where-Object { $_ -gt 1000 }).Count
        Remaining = $rows[-1].Remaining
        Complete = $rows[-1].Complete
        ObservedSeconds = [math]::Round((([datetime]$rows[-1].AtUtc) - ([datetime]$rows[0].AtUtc)).TotalSeconds, 2)
    }
})
if ($OutputCsv) { $summary | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding UTF8 }
$summary
