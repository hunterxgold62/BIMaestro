$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/app et excel/BIMaestroApp.cs'))
$start = $source.IndexOf('    private void ProcessHistoryBackground(UIApplication app)')
$end = $source.IndexOf('    private DateTime _lastRibbonApplyUtc', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Production history dispatcher was not found.' }
$dispatcher = $source.Substring($start, $end - $start)
$fixtures = @'
using System;
namespace HistoryBackgroundSchedulingTests {
class Document { }
class UIDocument { internal Document Document=new Document(); }
class UIApplication { internal UIDocument ActiveUIDocument=new UIDocument(); }
// Controlled elapsed API time; no real sleeps, model access or Windows calls.
class Stopwatch {
    static long Work;
    long start;
    internal static Stopwatch StartNew() { return new Stopwatch { start=Work }; }
    internal long ElapsedMilliseconds { get { return Work-start; } }
    internal static void Spend(long ms) { Work+=ms; }
}
namespace Analyse {
static class HistoryBackgroundWork {
    internal static bool SelectionRequested, SelectionAllowed, InventoryAllowed, IdleAllowed;
    internal static bool SelectionCanRun() { return SelectionAllowed; }
    internal static bool InventoryCanRun() { return InventoryAllowed; }
    internal static bool UserIsIdle() { return IdleAllowed; }
}
class SelectionPreparationStatus { internal int Pending; }
static class ElementHistoryTracker {
    internal static bool PrimeRemaining,LightRemaining;
    internal static int Schedules,PrimeCalls,LightCalls,EnrichmentCalls,SelectionPending,PrimeMs;
    internal static SelectionPreparationStatus GetSelectionPreparationStatus(Document doc) {
        return new SelectionPreparationStatus { Pending=SelectionPending };
    }
    internal static void ScheduleDeferredPrime(Document doc) { Schedules++; }
    internal static bool HasPendingBasicInventory(Document doc) { return PrimeRemaining || LightRemaining; }
    internal static void ProcessDeferredPrime(Document doc) { PrimeCalls++; Stopwatch.Spend(PrimeMs); }
    internal static void ProcessDeferredRestorationCapture(Document doc,bool selectionOnly=false,bool inventoryOnly=false) {
        bool allowed=selectionOnly ? HistoryBackgroundWork.SelectionCanRun()
            : inventoryOnly ? HistoryBackgroundWork.InventoryCanRun() : HistoryBackgroundWork.UserIsIdle();
        if (!allowed) return;
        if (LightRemaining) { LightCalls++; LightRemaining=false; }
        if (!inventoryOnly) EnrichmentCalls++;
        if (selectionOnly) SelectionPending=0;
    }
}
static class ElementHistoryNativeArchive {
    internal static int Calls,PriorityRoots;
    internal static bool LastSelectionOnly;
    internal static void GetPriorityAudit(Document doc,out int roots,out int unused) { roots=PriorityRoots;unused=0; }
    internal static void ProcessPendingBackground(Document doc,bool selectionOnly=false) {
        if (!HistoryBackgroundWork.UserIsIdle()) return;
        Calls++;LastSelectionOnly=selectionOnly;
    }
}
}
public class SchedulerChecks {
    DateTime _nextHistoryCaptureUtc=DateTime.MinValue;
'@
$checks = @'
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static void Reset() {
        Analyse.HistoryBackgroundWork.SelectionRequested=false;
        Analyse.HistoryBackgroundWork.SelectionAllowed=false;
        Analyse.HistoryBackgroundWork.InventoryAllowed=true;
        Analyse.HistoryBackgroundWork.IdleAllowed=false;
        Analyse.ElementHistoryTracker.PrimeRemaining=false;
        Analyse.ElementHistoryTracker.LightRemaining=false;
        Analyse.ElementHistoryTracker.Schedules=Analyse.ElementHistoryTracker.PrimeCalls=0;
        Analyse.ElementHistoryTracker.LightCalls=Analyse.ElementHistoryTracker.EnrichmentCalls=0;
        Analyse.ElementHistoryTracker.SelectionPending=Analyse.ElementHistoryTracker.PrimeMs=0;
        Analyse.ElementHistoryNativeArchive.Calls=Analyse.ElementHistoryNativeArchive.PriorityRoots=0;
    }
    public static void Run() {
        Reset(); Analyse.HistoryBackgroundWork.SelectionRequested=true;
        new SchedulerChecks().ProcessHistoryBackground(new UIApplication { ActiveUIDocument=null });
        Check(!Analyse.HistoryBackgroundWork.SelectionRequested && Analyse.ElementHistoryTracker.Schedules==0,"Closing the active document must stop stale selection requests.");

        Reset(); Analyse.ElementHistoryTracker.PrimeRemaining=true;
        Analyse.HistoryBackgroundWork.InventoryAllowed=false;
        new SchedulerChecks().ProcessHistoryBackground(new UIApplication());
        Check(Analyse.ElementHistoryTracker.PrimeCalls==0,"Paused inventory must not start a batch.");

        Reset(); Analyse.ElementHistoryTracker.PrimeRemaining=true;
        Analyse.ElementHistoryTracker.LightRemaining=true;
        new SchedulerChecks().ProcessHistoryBackground(new UIApplication());
        Check(Analyse.ElementHistoryTracker.PrimeCalls==1 && Analyse.ElementHistoryTracker.LightCalls==1,"Inventory and queued light captures must progress while heavy work is paused.");
        Check(Analyse.ElementHistoryTracker.EnrichmentCalls==0 && Analyse.ElementHistoryNativeArchive.Calls==0,"Background inventory must not borrow permission for heavy work.");

        Reset(); Analyse.ElementHistoryTracker.LightRemaining=true;
        new SchedulerChecks().ProcessHistoryBackground(new UIApplication());
        Check(!Analyse.ElementHistoryTracker.LightRemaining && Analyse.ElementHistoryTracker.LightCalls==1,"Deselected light captures must drain after the baseline completes.");

        Reset(); Analyse.ElementHistoryTracker.PrimeRemaining=true;
        Analyse.ElementHistoryTracker.LightRemaining=true;
        Analyse.ElementHistoryTracker.PrimeMs=40;
        Analyse.HistoryBackgroundWork.IdleAllowed=true;
        Analyse.ElementHistoryNativeArchive.PriorityRoots=1;
        new SchedulerChecks().ProcessHistoryBackground(new UIApplication());
        Check(Analyse.ElementHistoryTracker.LightCalls==0 && Analyse.ElementHistoryNativeArchive.Calls==0,"A slow API batch must yield before stacking more captures or native copies.");

        Reset(); Analyse.HistoryBackgroundWork.SelectionRequested=true;
        Analyse.HistoryBackgroundWork.SelectionAllowed=true;
        Analyse.ElementHistoryTracker.SelectionPending=1;
        Analyse.ElementHistoryTracker.LightRemaining=true;
        new SchedulerChecks().ProcessHistoryBackground(new UIApplication());
        Check(Analyse.ElementHistoryTracker.PrimeCalls==0 && Analyse.ElementHistoryTracker.LightCalls==1,"Explicit selection must precede the general inventory.");
        Check(!Analyse.HistoryBackgroundWork.SelectionRequested,"Completed selection must release its immediate bypass.");
    }
}}
'@
Add-Type -TypeDefinition ($fixtures + $dispatcher + $checks)
[HistoryBackgroundSchedulingTests.SchedulerChecks]::Run()
Write-Output 'PASS: dispatcher handles no document/Stop, progresses inventory and deselected light captures, defers heavy work, yields after slow API batches and prioritizes selection.'
