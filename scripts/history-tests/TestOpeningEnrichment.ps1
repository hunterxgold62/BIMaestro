$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$start = $source.IndexOf('                    if (current.Recipe?.Native?.WholeModel == true')
$end = $source.IndexOf('                    var elementTimer = Stopwatch.StartNew();', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Production opening-checkpoint enrichment guard was not found.' }
$guard = $source.Substring($start,$end-$start)
$fixtures = @'
using System;
using System.Collections.Generic;
using System.Linq;
namespace HistoryOpeningEnrichmentTests {
class Document { }
class Receipt { internal bool WholeModel,Ready; internal string Failure; }
class Recipe { internal Receipt Native; }
class Snapshot { internal Recipe Recipe; internal bool DetailCaptureAttempted; }
class CaptureState { internal readonly Dictionary<int,Snapshot> Pending=new Dictionary<int,Snapshot>(); }
static class ElementHistoryOpeningArchive {
    internal static readonly HashSet<int> InvalidIds=new HashSet<int>();
    internal static int ValidationCalls;
    internal static Recipe FreezeForDeletion(Document doc,Recipe recipe,int id) {
        ValidationCalls++;
        return InvalidIds.Contains(id) ? null : recipe;
    }
}
public static class EnrichmentChecks {
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static void Run() {
        var doc=new Document();
        var ready=new Snapshot { Recipe=new Recipe { Native=new Receipt { WholeModel=true,Ready=true } } };
        var pending=new Snapshot { Recipe=new Recipe { Native=new Receipt { WholeModel=true } } };
        var failed=new Snapshot { Recipe=new Recipe { Native=new Receipt { WholeModel=true,Failure="Copy failed" } } };
        var dirty=new Snapshot { Recipe=new Recipe { Native=new Receipt { WholeModel=true,Ready=true } } };
        var ordinary=new Snapshot { Recipe=new Recipe() };
        var perObject=new Snapshot { Recipe=new Recipe { Native=new Receipt { Ready=true } } };
        var missing=new Snapshot();
        var snapshots=new Dictionary<int,Snapshot> {
            {1,ready},{2,pending},{3,failed},{4,dirty},{5,ordinary},{6,perObject},{7,missing}
        };
        var state=new CaptureState();
        foreach(var pair in snapshots) state.Pending[pair.Key]=pair.Value;
        ElementHistoryOpeningArchive.InvalidIds.Add(4);
        var heavyCalls=new HashSet<int>();
        foreach(var idValue in state.Pending.Keys.ToList()) {
            var current=state.Pending[idValue];
            state.Pending.Remove(idValue);
            var id=idValue;
'@
$checks = @'
            // Represents the existing Enrich/native-copy path after the guard.
            heavyCalls.Add(idValue);
        }
        Check(!heavyCalls.Contains(1) && ready.DetailCaptureAttempted,"A ready valid checkpoint must satisfy detail preparation without a heavy API call.");
        Check(!state.Pending.ContainsKey(1),"A ready checkpoint must leave the enrichment queue.");
        Check(!heavyCalls.Contains(2) && !pending.DetailCaptureAttempted,"An in-progress copy must neither call heavy APIs nor announce completed details.");
        Check(state.Pending.ContainsKey(2) && Object.ReferenceEquals(state.Pending[2],pending),"A pending copy must preserve the exact snapshot for a later pass.");
        Check(heavyCalls.Contains(3) && !state.Pending.ContainsKey(3),"A failed copy must reach normal enrichment/fallback.");
        Check(heavyCalls.Contains(4) && !dirty.DetailCaptureAttempted,"A dirty checkpoint must be rejected even if its file receipt is ready.");
        Check(heavyCalls.Contains(5) && heavyCalls.Contains(6) && heavyCalls.Contains(7),"Direct recipes, per-object archives and missing recipes must retain their normal capture path.");
        Check(heavyCalls.Count==5 && ElementHistoryOpeningArchive.ValidationCalls==4,"Opening guard touched unexpected recipes or started unexpected heavy work.");
    }
}}
'@
Add-Type -TypeDefinition ($fixtures+$guard+$checks)
[HistoryOpeningEnrichmentTests.EnrichmentChecks]::Run()
Write-Output 'PASS: production opening-checkpoint guard skips heavy API work when ready, retains pending snapshots without announcing details, falls back on failed/dirty copies and preserves ordinary recipe processing.'
