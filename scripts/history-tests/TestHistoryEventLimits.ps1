$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tracker = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$window = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryWindow.xaml.cs'))

function Read-Method([string]$Source, [string]$Signature) {
    $start = $Source.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Production method not found: $Signature" }
    $opening = $Source.IndexOf('{', $start)
    $depth = 1
    for ($i = $opening + 1; $i -lt $Source.Length; $i++) {
        if ($Source[$i] -eq '{') { $depth++ }
        if ($Source[$i] -eq '}') { $depth-- }
        if ($depth -eq 0) { return $Source.Substring($start, $i - $start + 1) }
    }
    throw "Unterminated production method: $Signature"
}

$constant = [regex]::Match($tracker, 'internal const int DefaultHistoryEventLimit = \d+;').Value
if (!$constant) { throw 'Shared history limit missing.' }
foreach ($name in 'DefaultElementHistoryTake','DefaultModelHistoryTake','DefaultDeletedHistoryTake') {
    if (!$tracker.Contains("private const int $name = DefaultHistoryEventLimit;")) { throw "Unshared loader limit: $name" }
}
foreach ($name in 'MaxVisibleHistoryEvents','MaxLoadedHistoryEvents') {
    if (!$window.Contains("private const int $name = ElementHistoryTracker.DefaultHistoryEventLimit;")) { throw "Unshared UI limit: $name" }
}
if (!$window.Contains('Bind(events, null, false, true);') -or !$window.Contains('BeginRangeLoad(_loadedHistoryStartDate.Value, _loadedHistoryEndDate.Value);')) { throw 'Unbounded full-period UI route missing.' }
if (!$window.Contains('Historique récent limité')) { throw 'Recent-limit disclosure missing.' }

$methods = @(
    (Read-Method $tracker 'internal static List<ElementHistoryEvent> LoadRecentModelHistory(IEnumerable<string> modelKeys'),
    (Read-Method $tracker 'internal static List<ElementHistoryEvent> LoadElementHistory(IEnumerable<string> modelKeys'),
    (Read-Method $tracker 'internal static List<ElementHistoryEvent> LoadModelHistoryForLocalRange(IEnumerable<string> modelKeys'),
    (Read-Method $tracker 'private static List<ElementHistoryEvent> LoadMatchingHistory(IEnumerable<string> modelKeys'),
    (Read-Method $tracker 'private static bool MatchesHistoryFilter(ElementHistoryEvent ev, HistoryModelScope scope')
) -join "`n"
$windowLoad = Read-Method $window 'private static List<ElementHistoryEvent> LoadWindowHistory(List<string> modelKeys'

# Exercise production selection, ordering, caps and full-date loading without Revit.
# The only adapters are an in-memory line store, identity scope and a plain-value codec.
$support = @'
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
public sealed class ElementHistoryEvent {
    public int ElementId; public string ModelKey; public string UniqueId; public string Action; public DateTime Ts;
}
public sealed class HistoryModelScope {
    public HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
public static class CollaborativeModelTrackerStore { public static string ActiveDirectory = "."; }
public static class JsonConvert {
    public static T DeserializeObject<T>(string line) {
        var p = line.Split('|');
        return (T)(object)new ElementHistoryEvent { ElementId=int.Parse(p[0]), ModelKey=p[1], UniqueId=p[2], Action="delete", Ts=DateTime.Parse(p[3], null, System.Globalization.DateTimeStyles.RoundtripKind) };
    }
}
public static class ElementHistoryTracker {
    private const int DefaultModelHistoryTake = DefaultHistoryEventLimit;
    internal static readonly List<string> Lines = new List<string>();
    static HistoryModelScope BuildHistoryModelScope(IEnumerable<string> keys) { var scope=new HistoryModelScope(); foreach(var key in keys) scope.Keys.Add(key); return scope; }
    static bool MatchesHistoryModelScope(string key, HistoryModelScope scope) { return scope.Keys.Contains(key); }
    static string[] GetHistoryFiles(string directory) { return new[] { "synthetic-history" }; }
    static IEnumerable<string> ReadHistoryLines(string path) { return Lines; }
    static bool MayContainUtcRange(string path, DateTime start, DateTime end) { return true; }
    static string BuildHistoryEventIdentity(ElementHistoryEvent e) { return e.ModelKey+":"+e.ElementId+":"+e.Ts.ToString("O"); }
'@
$checks = @'
}
public static class WindowLimitFixture {
    static string BuildHistoryEventKey(ElementHistoryEvent e) { return e.ModelKey+":"+e.ElementId+":"+e.Ts.ToString("O"); }
    public static List<ElementHistoryEvent> Load(List<string> keys) { return LoadWindowHistory(keys, null, ElementHistoryTracker.DefaultHistoryEventLimit); }
    public static List<ElementHistoryEvent> LoadSelected(List<string> keys, string uid) { return LoadWindowHistory(keys, new List<string>{uid}, ElementHistoryTracker.DefaultHistoryEventLimit); }
'@
$runner = @'
}
public static class HistoryEventLimitChecks {
    static void Check(bool ok, string text) { if(!ok) throw new Exception(text); }
    static void Fill(int count, DateTime date) {
        ElementHistoryTracker.Lines.Clear();
        for(int i=1;i<=count;i++) ElementHistoryTracker.Lines.Add(i+"|model-A|uid-"+i+"|"+date.AddSeconds(i).ToString("O"));
        for(int i=1;i<=300;i++) ElementHistoryTracker.Lines.Add((count+i)+"|model-B|foreign-"+i+"|"+date.AddHours(20).ToString("O"));
    }
    public static string Run() {
        var keys=new List<string>{"model-A"}; var day=DateTime.SpecifyKind(new DateTime(2026,10,20,8,0,0), DateTimeKind.Local).ToUniversalTime();
        Check(ElementHistoryTracker.DefaultHistoryEventLimit==10000,"Expected shared default 10,000");
        Fill(2501,day);
        Check(ElementHistoryTracker.LoadRecentModelHistory(keys).Count==2501,"2,501 records must survive the default loader");
        Check(WindowLimitFixture.Load(keys).Count==2501,"2,501 records must survive the actual UI loader");
        Fill(12075,day);
        var recent=ElementHistoryTracker.LoadRecentModelHistory(keys);
        Check(recent.Count==10000 && recent.First().ElementId==12075 && recent.Last().ElementId==2076,"Recent window must return exactly the newest 10,000");
        Check(WindowLimitFixture.Load(keys).Count==10000,"UI recent window must share the 10,000 default");
        Check(WindowLimitFixture.LoadSelected(keys,"uid-1").Count==10000,"Selected-context path must share the same recent bound");
        ElementHistoryTracker.Lines.Add("1|model-A|uid-1|"+day.AddSeconds(1).ToString("O"));
        ElementHistoryTracker.Lines.Add("99999|model-A|old-event|"+day.AddDays(-1).ToString("O"));
        var full=ElementHistoryTracker.LoadModelHistoryForLocalRange(keys,day.ToLocalTime().Date,day.ToLocalTime().Date);
        Check(full.Count==12075,"Full-period loading must include all 12,075 batch records and deduplicate persisted lines");
        Check(full.Select(e=>e.ElementId).Distinct().Count()==12075 && full.All(e=>e.ModelKey=="model-A"),"Full-period batch must preserve every identity and model scope");
        Check(full.Last().ElementId==1 && full.First().ElementId==12075,"Full-period loading must recover the truncated batch boundary in descending order");
        return "PASS: production loader fixture 2,501 / recent 10,000 / complete 12,075; model/date scope, deduplication and boundary identities verified. UI cap/disclosure/full-period route checked statically.";
    }
}
'@
Add-Type -TypeDefinition ($support + "`n" + $constant + "`n" + $methods + "`n" + $checks + "`n" + $windowLoad + "`n" + $runner)
[HistoryEventLimitChecks]::Run()

# Compile and invoke the actual panel-switch method on an STA thread; no window is shown.
# This validates API use and reversible mode switching, not real 10k-card rendering performance.
Add-Type -AssemblyName PresentationFramework
$panelMethod = Read-Method $window 'private void ConfigureCardVirtualization(int rowCount)'
$panelThreshold = [regex]::Match($window, 'private const int VirtualizedCardsThreshold = \d+;').Value
$panelSupport = @'
using System;
using System.Windows;
using System.Windows.Controls;
public sealed class CardPanelChecks {
    private ListBox VisualCardsList = new ListBox();
    private ItemsPanelTemplate _mosaicItemsPanel;
    private bool _cardsAreVirtualized;
    public static void Run() { new CardPanelChecks().Check(); }
    void Check() {
        _mosaicItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        VisualCardsList.ItemsPanel=_mosaicItemsPanel;
        ConfigureCardVirtualization(2501);
        VisualCardsList.ItemsPanel.Seal();
        if(!_cardsAreVirtualized || !(VisualCardsList.ItemsPanel.LoadContent() is VirtualizingStackPanel)
            || !ScrollViewer.GetCanContentScroll(VisualCardsList) || !VirtualizingPanel.GetIsVirtualizing(VisualCardsList)
            || VirtualizingPanel.GetVirtualizationMode(VisualCardsList)!=VirtualizationMode.Recycling) throw new Exception("Large-history virtualization missing");
        ConfigureCardVirtualization(12075);
        ConfigureCardVirtualization(250);
        VisualCardsList.ItemsPanel.Seal();
        if(_cardsAreVirtualized || !(VisualCardsList.ItemsPanel.LoadContent() is WrapPanel)
            || ScrollViewer.GetCanContentScroll(VisualCardsList)) throw new Exception("Small-history mosaic was not restored");
    }
'@
$refs = @([System.Windows.FrameworkElement].Assembly.Location, [System.Windows.DependencyObject].Assembly.Location, [System.Windows.Media.Visual].Assembly.Location)
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this WPF fixture with PowerShell -STA.' }
Add-Type -TypeDefinition ($panelSupport + "`n" + $panelThreshold + "`n" + $panelMethod + "`n}") -ReferencedAssemblies $refs
[CardPanelChecks]::Run()
Write-Output 'PASS: actual WPF panel method compiles and switches to recycling virtualization above threshold, then restores the small-history mosaic. No Revit or real-card performance benchmark.'
