$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryAudit.cs'))
$start=$source.IndexOf('    internal static class HistoryCaptureCosts')
$end=$source.IndexOf('    // Capture plain values',$start)
$costs=$source.Substring($start,$end-$start)
# Remove only the API identity adapter; exercise the actual production ordering estimates.
$costs=[regex]::Replace($costs,'(?s)        internal static string Key\(Element element\).*?;', '')
$costs=[regex]::Replace($costs,'(?s)        internal static bool KnownSlowBasic\(Element element\).*?;', '')
$checks=@'
public static class CaptureCostChecks {
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static void Run() {
        Check(HistoryCaptureCosts.Estimate("g","native_root","Group") < HistoryCaptureCosts.Estimate("t","native_root","Toposolid"), "Unknown terrain must follow a simple group");
        HistoryCaptureCosts.Observe("g","native_root",20000);
        Check(HistoryCaptureCosts.Estimate("g","native_root","Group") > HistoryCaptureCosts.Estimate("t","native_root","Toposolid"), "Measured slow group must move behind faster candidates");
        Check(HistoryCaptureCosts.Estimate("g","element_enrichment","Group")==200, "Native copying cost must not contaminate another phase");
        Check(HistoryCaptureCosts.Estimate("other-model-g","native_root","Group")==200, "One model must not contaminate another");
        HistoryCaptureCosts.Observe("g","native_root",100);
        Check(HistoryCaptureCosts.Estimate("g","native_root","Group") > 100, "One fast retry must not erase a previous severe pause");
    }
}
'@
Add-Type -TypeDefinition ("using System; using System.Collections.Generic;`n"+$costs+$checks)
[CaptureCostChecks]::Run()
Write-Output 'PASS: initial heavy-object ordering, measured-cost adaptation, model/phase isolation and outlier retention.'
