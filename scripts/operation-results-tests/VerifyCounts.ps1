$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$organizerSource = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'BIMaestro/commands/renuméroté élément/RenameElementsCommand.cs')
$baseSource = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'BIMaestro/app et excel/Supabase/BaseTrackedCommand.cs')
$methodStart = $organizerSource.IndexOf('        private void RecordOrganizerResult(')
$methodEnd = $organizerSource.IndexOf('        private List<Viewport> SortViewportsByReadingOrder(', $methodStart)
$baseStart = $baseSource.IndexOf('        private object _operationResult;')
$baseEnd = $baseSource.IndexOf('        protected virtual object BuildContext(', $baseStart)
if ($methodStart -lt 0 -or $methodEnd -lt 0 -or $baseStart -lt 0 -or $baseEnd -lt 0) {
    throw 'Production count methods could not be located.'
}
$testSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
#pragma warning disable CS0169
public class OperationCountsTest {
    private const string ButtonId = "RenameElementsCommand";
'@
$testSource += $baseSource.Substring($baseStart, $baseEnd - $baseStart)
$testSource += $organizerSource.Substring($methodStart, $methodEnd - $methodStart)
$testSource += @'
    private void Expect(int renamed, int reset, int unchanged, int skipped) {
        var counts = (IDictionary<string,int>)_operationResult.GetType().GetProperty("counts").GetValue(_operationResult);
        if (counts["elements_renamed"] != renamed || counts["elements_reset"] != reset ||
            counts["elements_unchanged"] != unchanged || counts["elements_skipped"] != skipped)
            throw new Exception("Incorrect committed quantities");
    }
    public static string Run() {
        var test = new OperationCountsTest();
        var before = new Dictionary<long,string> { [1]="A", [2]="B", [3]="" };
        var after = new Dictionary<long,string> { [1]="B", [2]="A", [3]="" };
        test.RecordOrganizerResult(before, after, 4, false, "elements", false);
        test.Expect(2,0,1,1);
        string firstId = (string)test._operationResult.GetType().GetProperty("operation_id").GetValue(test._operationResult);
        test.RecordOrganizerResult(before, before, 3, false, "elements", false);
        test.Expect(0,0,3,0);
        string secondId = (string)test._operationResult.GetType().GetProperty("operation_id").GetValue(test._operationResult);
        if (firstId == secondId) throw new Exception("Two runs reused an operation id");
        after = new Dictionary<long,string> { [1]="", [2]="", [3]="" };
        test.RecordOrganizerResult(before, after, 3, true, "elements", false);
        test.Expect(0,2,1,0);
        test.RecordOrganizerResult(before, new Dictionary<long,string> { [1]="A" }, 3, false, "elements", false);
        test.Expect(0,0,1,2);
        test.RecordOperationResult("auto_reservation", "manual", false,
            new Dictionary<string,int> { ["reservations_created"]=10 });
        var reservationCounts = (IDictionary<string,int>)test._operationResult.GetType().GetProperty("counts").GetValue(test._operationResult);
        if (reservationCounts["reservations_created"] != 10) throw new Exception("Manual batch result failed");
        return "PASS: changed, unchanged, skipped, reset, operation IDs and one batch of ten reservations";
    }
}
'@
Add-Type -TypeDefinition $testSource
[OperationCountsTest]::Run()
