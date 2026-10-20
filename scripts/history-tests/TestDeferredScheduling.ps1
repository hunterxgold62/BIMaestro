$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$start = $source.IndexOf('        public static void ScheduleDeferredPrime(Document doc)')
$end = $source.IndexOf('        private static bool ShouldScheduleDeferredPrime(', $start)
$schedule = $source.Substring($start, $end-$start)
$start = $source.IndexOf('        public static void PrimeDocument(Document doc)')
$end = $source.IndexOf('        internal static void PrimeExerciseElements(', $start)
$entry = $source.Substring($start, $end-$start)
$fixtures = @'
using System;
using System.Collections.Generic;
namespace DeferredSchedulingTests {
class Document { internal string Key; internal bool Owned; }
static class ElementHistoryNativeArchive { internal static bool Owns(Document doc) => doc != null && doc.Owned; }
class DeferredPrimeState { internal DateTime NotBeforeUtc; internal int Progress; }
class HistoryPreloadAudit { internal DateTime ScheduledUtc; }
public static class SchedulerChecks {
    static readonly object SnapshotByElementId = new object();
    static readonly HashSet<string> PrimedDocumentKeys = new HashSet<string>();
    static readonly Dictionary<string,DeferredPrimeState> DeferredPrimeByDocumentKey = new Dictionary<string,DeferredPrimeState>();
    static readonly Dictionary<string,HistoryPreloadAudit> PreloadAuditByDocumentKey = new Dictionary<string,HistoryPreloadAudit>();
    static readonly TimeSpan DeferredPrimeDelay = TimeSpan.FromSeconds(1);
    static bool ShouldScheduleDeferredPrime(Document doc) => doc != null;
    static string GetDocumentKey(Document doc) => doc.Key;
'@
$checks = @'
    static void Check(bool ok, string message) { if(!ok) throw new Exception(message); }
    public static void Run() {
        var doc = new Document {Key="model"};
        PrimeDocument(doc);
        Check(DeferredPrimeByDocumentKey.Count==1, "Opening history must schedule the baseline.");
        Check(!PrimedDocumentKeys.Contains(doc.Key), "Scheduling must not mark an unprocessed model complete.");
        var state=DeferredPrimeByDocumentKey[doc.Key];
        var audit=PreloadAuditByDocumentKey[doc.Key];
        state.Progress=42;
        PrimeDocument(doc);
        Check(Object.ReferenceEquals(state,DeferredPrimeByDocumentKey[doc.Key]) && state.Progress==42,
            "Opening history again must preserve partial progress.");
        Check(Object.ReferenceEquals(audit,PreloadAuditByDocumentKey[doc.Key]), "Opening again must preserve timing audit.");
        PrimedDocumentKeys.Add(doc.Key); DeferredPrimeByDocumentKey.Remove(doc.Key);
        PrimeDocument(doc);
        Check(DeferredPrimeByDocumentKey.Count==0, "Completed baseline must not restart.");
        PrimeDocument(null); PrimeDocument(new Document {Key="archive",Owned=true});
        Check(DeferredPrimeByDocumentKey.Count==0, "Missing documents and owned archives must not be scheduled.");
        PrimeDocument(new Document {Key="second"});
        Check(DeferredPrimeByDocumentKey.Count==1, "Other models must retain independent scheduling.");
    }
}}
'@
# Actual scheduler and compatibility entry point; API metadata is represented by minimal doubles.
# Restrict visibility of extracted public API because the doubles are internal to this test assembly.
Add-Type -TypeDefinition ($fixtures + $schedule.Replace('public static','internal static') + $entry.Replace('public static','internal static') + $checks)
[DeferredSchedulingTests.SchedulerChecks]::Run()
Write-Output 'PASS: opening history schedules without early completion, preserves partial progress/audit and does not restart completed models.'
