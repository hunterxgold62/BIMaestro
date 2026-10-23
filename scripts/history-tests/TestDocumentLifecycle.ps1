$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tracker = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$app = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/app et excel/BIMaestroApp.cs'))
function Get-ProductionSection([string]$text,[string]$from,[string]$until) {
    $start = $text.IndexOf($from)
    if ($start -lt 0) { throw "Production entry point missing: $from" }
    $end = $text.IndexOf($until,$start)
    if ($end -le $start) { throw "Production section end missing: $until" }
    return $text.Substring($start,$end-$start)
}
$invalidate = Get-ProductionSection $tracker '        internal static void InvalidateCaptureCaches(Document doc)' '        private static readonly Dictionary<string, HistoryPreloadAudit>'
$cleanupAndSchedule = Get-ProductionSection $tracker '        internal static void ForgetAudit(Document doc)' '        public static void ProcessDeferredPrime(Document doc)'
$callbacks = Get-ProductionSection $app '    private void OnDocumentClosingSafe(object sender, DocumentClosingEventArgs e)' '    private void OnDocumentChangedSafe(object sender, DocumentChangedEventArgs e)'
$fixtures = @'
using System;
using System.Collections.Generic;
using System.Linq;
namespace HistoryDocumentLifecycleTests {
class Document {
    internal string SavedKey;
    internal bool Valid=true,Owned;
    internal object Application { get { EnsureValid(); return this; } }
    internal string Key { get { EnsureValid(); return SavedKey; } }
    void EnsureValid() { if(!Valid) throw new InvalidOperationException("The closed Revit document cannot be queried."); }
}
class UIApplication { internal UIApplication(object _) { } }
enum RevitAPIEventStatus { Succeeded,Failed,Cancelled }
class DocumentClosingEventArgs { internal int DocumentId; internal Document Document; }
class DocumentClosedEventArgs { internal int DocumentId; internal RevitAPIEventStatus Status; }
static class UiLanguage { internal static string T(string a,string b) { return a; } }
static class TaskDialog { internal static void Show(string title,string text) { throw new Exception(title+": "+text); } }
static class ExcelLogger { internal static void OnDocumentClosing(Document doc,UIApplication app) { } }
namespace Couleur {
static class ProjectBrowserProjectStorage { internal static bool Forget(Document doc) { return false; } }
static class ProjectBrowserColoring { internal static void Reset() { } internal static void ForgetViewTypeMap(Document doc) { } }
}
namespace BIMaestro.ViewHover { static class ViewHoverPreviewService { internal static void ForgetDocument(Document doc) { } } }
namespace Analyse {
static class ElementHistoryHoverInfoService { internal static void Hide() { } }
static class CollaborativeModelTrackerStore { internal static void TryAutoLog(Document doc,UIApplication app) { } }
static class ElementHistoryOpeningArchive {
    internal static readonly HashSet<Document> Protected=new HashSet<Document>();
    internal static void Forget(Document doc) { Protected.Remove(doc); }
}
static class ElementHistoryNativeArchive {
    internal static readonly HashSet<Document> Protected=new HashSet<Document>();
    internal static bool Owns(Document doc) { return doc != null && doc.Owned; }
    internal static void Forget(Document doc) { Protected.Remove(doc); }
}
class DeferredPrimeState { internal DateTime NotBeforeUtc; }
class HistoryPreloadAudit { internal DateTime ScheduledUtc; }
static class ElementHistoryTracker {
    internal static readonly Dictionary<Document,HashSet<string>> DocumentCaptureKeys=new Dictionary<Document,HashSet<string>>();
    internal static readonly Dictionary<Document,object> TypeParameterCaches=new Dictionary<Document,object>();
    internal static readonly Dictionary<Document,object> ParameterDisplayCaches=new Dictionary<Document,object>();
    internal static readonly Dictionary<string,object> SnapshotByElementId=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> FamilyTypeSnapshotByKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> SketchOwnerByCurveUniqueId=new Dictionary<string,object>();
    internal static readonly HashSet<string> PrimedDocumentKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> FamilyParameterPrimedDocumentKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,DeferredPrimeState> DeferredPrimeByDocumentKey=new Dictionary<string,DeferredPrimeState>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,HistoryPreloadAudit> PreloadAuditByDocumentKey=new Dictionary<string,HistoryPreloadAudit>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> SelectionAuditByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> LastSelectionIdsByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,object> RestorationCaptureByDocumentKey=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan DeferredPrimeDelay=TimeSpan.FromSeconds(1);
    static string GetDocumentKey(Document doc) { return doc == null ? "" : doc.Key; }
    internal static string GetDocumentKeyForHistory(Document doc) { return GetDocumentKey(doc); }
'@
$appFixtures = @'
}
}
public class LifecycleChecks {
    readonly Dictionary<int,Tuple<Document,string>> _historyClosingDocuments=new Dictionary<int,Tuple<Document,string>>();
    UIApplication _uiApp;
    int _pendingProjectBrowserAppearanceRefreshes;
    static int LoggedErrors;
    static void AppendLog(string text) { LoggedErrors++; }
'@
$checks = @'
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static object Seed(Document doc,string key) {
        doc.SavedKey=key;
        Analyse.ElementHistoryTracker.ScheduleDeferredPrime(doc);
        var snapshot=new object();
        Analyse.ElementHistoryTracker.SnapshotByElementId[key+"|element|1"]=snapshot;
        Analyse.ElementHistoryTracker.FamilyTypeSnapshotByKey[key+"|type|A"]=snapshot;
        Analyse.ElementHistoryTracker.SketchOwnerByCurveUniqueId[key+"|curve|A"]=snapshot;
        Analyse.ElementHistoryTracker.PrimedDocumentKeys.Add(key);
        Analyse.ElementHistoryTracker.FamilyParameterPrimedDocumentKeys.Add(key);
        Analyse.ElementHistoryTracker.SelectionAuditByDocumentKey[key]=snapshot;
        Analyse.ElementHistoryTracker.LastSelectionIdsByDocumentKey[key]=snapshot;
        Analyse.ElementHistoryTracker.RestorationCaptureByDocumentKey[key]=snapshot;
        Analyse.ElementHistoryTracker.TypeParameterCaches[doc]=snapshot;
        Analyse.ElementHistoryTracker.ParameterDisplayCaches[doc]=snapshot;
        Analyse.ElementHistoryOpeningArchive.Protected.Add(doc);
        Analyse.ElementHistoryNativeArchive.Protected.Add(doc);
        return snapshot;
    }
    static void CheckPreserved(Document doc,string key,object original) {
        Check(Object.ReferenceEquals(Analyse.ElementHistoryTracker.SnapshotByElementId[key+"|element|1"],original),"Close attempt replaced/discarded a captured snapshot.");
        Check(Analyse.ElementHistoryTracker.PrimedDocumentKeys.Contains(key),"Close attempt discarded baseline completion.");
        Check(Analyse.ElementHistoryTracker.DocumentCaptureKeys.ContainsKey(doc),"Close attempt discarded Save As aliases.");
        Check(Analyse.ElementHistoryTracker.TypeParameterCaches.ContainsKey(doc),"Close attempt invalidated type caches.");
        Check(Analyse.ElementHistoryOpeningArchive.Protected.Contains(doc) && Analyse.ElementHistoryNativeArchive.Protected.Contains(doc),"Close attempt forgot opening/native protection.");
    }
    static void CheckRemoved(string key) {
        Check(!Analyse.ElementHistoryTracker.SnapshotByElementId.ContainsKey(key+"|element|1"),"Closed alias retained instance snapshots.");
        Check(!Analyse.ElementHistoryTracker.FamilyTypeSnapshotByKey.ContainsKey(key+"|type|A"),"Closed alias retained family type snapshots.");
        Check(!Analyse.ElementHistoryTracker.SketchOwnerByCurveUniqueId.ContainsKey(key+"|curve|A"),"Closed alias retained sketch mappings.");
        Check(!Analyse.ElementHistoryTracker.PrimedDocumentKeys.Contains(key) && !Analyse.ElementHistoryTracker.FamilyParameterPrimedDocumentKeys.Contains(key),"Closed alias retained completion flags.");
        Check(!Analyse.ElementHistoryTracker.DeferredPrimeByDocumentKey.ContainsKey(key) && !Analyse.ElementHistoryTracker.PreloadAuditByDocumentKey.ContainsKey(key),"Closed alias retained pending baseline/audit.");
        Check(!Analyse.ElementHistoryTracker.SelectionAuditByDocumentKey.ContainsKey(key) && !Analyse.ElementHistoryTracker.LastSelectionIdsByDocumentKey.ContainsKey(key) && !Analyse.ElementHistoryTracker.RestorationCaptureByDocumentKey.ContainsKey(key),"Closed alias retained selection/restoration queues.");
    }
    public static void Run() {
        var app=new LifecycleChecks();
        var doc=new Document();
        var first=Seed(doc,"project-old.rvt");
        var second=Seed(doc,"project-save-as.rvt");
        var unrelated=new Document();
        var unrelatedSnapshot=Seed(unrelated,"project-save-as.rvt-other");
        Check(Analyse.ElementHistoryTracker.DocumentCaptureKeys[doc].Count==2,"Production scheduler did not retain Save As aliases.");

        var closing=new DocumentClosingEventArgs { DocumentId=10,Document=doc };
        app.OnDocumentClosingSafe(null,closing);
        Check(app._historyClosingDocuments.Count==1,"Closing did not capture the valid document/key.");
        Check(app._pendingProjectBrowserAppearanceRefreshes==0,"Closing changed unrelated pending UI state.");
        CheckPreserved(doc,"project-old.rvt",first); CheckPreserved(doc,"project-save-as.rvt",second);
        app.OnDocumentClosedSafe(null,new DocumentClosedEventArgs { DocumentId=10,Status=RevitAPIEventStatus.Failed });
        CheckPreserved(doc,"project-old.rvt",first); CheckPreserved(doc,"project-save-as.rvt",second);
        Check(app._historyClosingDocuments.Count==0,"Failed close leaked its transient callback state.");

        app.OnDocumentClosingSafe(null,closing);
        app.OnDocumentClosedSafe(null,new DocumentClosedEventArgs { DocumentId=10,Status=RevitAPIEventStatus.Cancelled });
        CheckPreserved(doc,"project-old.rvt",first); CheckPreserved(doc,"project-save-as.rvt",second);
        Check(app._historyClosingDocuments.Count==0,"Cancelled close leaked its transient callback state.");

        app.OnDocumentClosingSafe(null,closing);
        doc.Valid=false; // Any subsequent query of path/application now throws.
        app.OnDocumentClosedSafe(null,new DocumentClosedEventArgs { DocumentId=10,Status=RevitAPIEventStatus.Succeeded });
        Check(LoggedErrors==0,"Successful cleanup queried an invalid Revit document.");
        CheckRemoved("project-old.rvt"); CheckRemoved("project-save-as.rvt");
        Check(!Analyse.ElementHistoryTracker.DocumentCaptureKeys.ContainsKey(doc) && !Analyse.ElementHistoryTracker.TypeParameterCaches.ContainsKey(doc) && !Analyse.ElementHistoryTracker.ParameterDisplayCaches.ContainsKey(doc),"Successful cleanup retained document caches.");
        Check(!Analyse.ElementHistoryOpeningArchive.Protected.Contains(doc) && !Analyse.ElementHistoryNativeArchive.Protected.Contains(doc),"Successful cleanup retained native/opening state.");
        CheckPreserved(unrelated,"project-save-as.rvt-other",unrelatedSnapshot);

        var reopened=new Document { SavedKey="project-save-as.rvt" };
        Analyse.ElementHistoryTracker.ScheduleDeferredPrime(reopened);
        Check(Analyse.ElementHistoryTracker.DeferredPrimeByDocumentKey.ContainsKey(reopened.Key),"Reopening a closed model failed to schedule a fresh baseline.");
        Check(!Analyse.ElementHistoryTracker.PrimedDocumentKeys.Contains(reopened.Key) && !Analyse.ElementHistoryTracker.SnapshotByElementId.ContainsKey(reopened.Key+"|element|1"),"Reopening reused stale completion/snapshots.");

        app.OnDocumentClosedSafe(null,new DocumentClosedEventArgs { DocumentId=999,Status=RevitAPIEventStatus.Succeeded });
        Check(LoggedErrors==0,"Unmatched close should be harmless.");
        var archive=new Document { SavedKey="technical-archive.rvt",Owned=true };
        app.OnDocumentClosingSafe(null,new DocumentClosingEventArgs { DocumentId=11,Document=archive });
        Check(app._historyClosingDocuments.Count==0,"Technical archive entered normal document lifecycle tracking.");
    }
}}
'@
Add-Type -TypeDefinition ($fixtures + $invalidate + $cleanupAndSchedule.Replace('public static','internal static') + $appFixtures + $callbacks + $checks)
[HistoryDocumentLifecycleTests.LifecycleChecks]::Run()
Write-Output 'PASS: production closing/closed callbacks preserve caches on failed/cancelled close, clean both Save As aliases after successful close without querying invalid documents, preserve other models and schedule a fresh baseline on reopen.'
