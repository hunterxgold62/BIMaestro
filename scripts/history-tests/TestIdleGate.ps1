$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs'))
$start = $source.IndexOf('    internal static class HistoryBackgroundWork')
$end = $source.IndexOf('    internal sealed class HistoryNativeArtifact', $start)
$gate = $source.Substring($start, $end - $start)
$checks = @'
public static class IdleGateChecks {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() {
        HistoryBackgroundWork.SelectionRequested = true;
        Check(HistoryBackgroundWork.CanStartSelection(true, true, false), "Explicit selection starts without idle delay");
        Check(!HistoryBackgroundWork.CanStartSelection(true, true, true), "Stop blocks explicit selection");
        Check(!HistoryBackgroundWork.CanStartSelection(false, true, false), "Explicit selection pauses outside Revit");
        Check(!HistoryBackgroundWork.CanStartSelection(true, false, false), "Background work cannot borrow the selection bypass");
        HistoryBackgroundWork.Paused = true;
        Check(!HistoryBackgroundWork.UserIsIdle(), "Stop blocks ordinary automatic preparation too");
        HistoryBackgroundWork.Paused = false;
        HistoryBackgroundWork.SelectionRequested = false;
        Check(!HistoryBackgroundWork.CanRun(10000, 0, false, 2000), "AFK outside Revit must pause");
        Check(!HistoryBackgroundWork.CanRun(11000, 0, true, 2000), "Returning to Revit must not start immediately");
        Check(!HistoryBackgroundWork.CanRun(12999, 0, true, 2000), "Foreground settling delay");
        Check(HistoryBackgroundWork.CanRun(13000, 0, true, 2000), "Idle foreground can resume");
        Check(!HistoryBackgroundWork.CanRun(13001, 13000, true, 2000), "New input stops capture");
        Check(!HistoryBackgroundWork.CanRun(15000, 13000, false, 2000), "Switching away stops capture");
        Check(!HistoryBackgroundWork.CanRun(20000, 13000, true, 2000), "Return resets settling delay");
        Check(HistoryBackgroundWork.CanRun(22000, 13000, true, 2000), "Return resumes after settling");
        HistoryBackgroundWork.CanRun(uint.MaxValue-1000, 0, false, 2000);
        Check(!HistoryBackgroundWork.CanRun(uint.MaxValue-1000, uint.MaxValue-3000, true, 2000), "Rollover initial delay");
        Check(HistoryBackgroundWork.CanRun(1000, uint.MaxValue-3000, true, 2000), "Tick counter rollover");
    }
}
'@
Add-Type -TypeDefinition ("using System; using System.Diagnostics; using System.Runtime.InteropServices;`n" + $gate + $checks)
[IdleGateChecks]::Run()
Write-Output 'PASS: immediate selection, Stop, background suspension, return delay, input interruption and tick rollover.'
