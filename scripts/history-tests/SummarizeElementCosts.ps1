param([Parameter(Mandatory=$true)][string]$LogDirectory, [string]$ModelKey='', [string]$OutputCsv='')
$ErrorActionPreference='Stop'
$rows=@(Get-ChildItem -LiteralPath $LogDirectory -Filter 'performance-*.jsonl' -File | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Encoding UTF8 | ForEach-Object {
        try {
            $record=$_ | ConvertFrom-Json
            if($record.ElementClass -and (!$ModelKey -or $record.ModelKey -eq $ModelKey)) { $record }
        } catch { Write-Warning 'Ligne incomplète ignorée.' }
    }
})
$summary=@($rows | Group-Object ModelKey,Phase,ElementClass,Category,ElementFamily,ElementTypeId,ElementType | ForEach-Object {
    $samples=@($_.Group)
    $times=@($samples.ElapsedMilliseconds | Sort-Object)
    $worst=$samples | Sort-Object ElapsedMilliseconds -Descending | Select-Object -First 1
    [pscustomobject]@{
        Model=$worst.ModelName; ModelKey=$worst.ModelKey; Phase=$worst.Phase
        Class=$worst.ElementClass; Category=$worst.Category; Family=$worst.ElementFamily
        TypeId=$worst.ElementTypeId; Type=$worst.ElementType
        Samples=$samples.Count
        MeanMs=[math]::Round(($times | Measure-Object -Average).Average,2)
        P95Ms=[math]::Round($times[[math]::Max(0,[math]::Ceiling($times.Count*0.95)-1)],2)
        MaxMs=[math]::Round($times[-1],2)
        Over100Ms=@($times | Where-Object { $_ -gt 100 }).Count
        Over1000Ms=@($times | Where-Object { $_ -gt 1000 }).Count
        WorstElementId=$worst.ElementId; WorstUniqueId=$worst.ElementUniqueId; WorstName=$worst.ElementName
    }
} | Sort-Object MaxMs -Descending)
if($OutputCsv) { $summary | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding UTF8 }
$summary
