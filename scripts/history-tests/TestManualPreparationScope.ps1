#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/history-manual-preparation-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.cs'))
$start = $source.IndexOf('        private static bool ManualPreparation;', [StringComparison]::Ordinal)
$end = if ($start -ge 0) { $source.IndexOf('        // Revit API context only:', $start, [StringComparison]::Ordinal) } else { -1 }
if ($start -lt 0 -or $end -le $start) { throw 'Production manual preparation block was not found.' }
$nativeSource = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.NativeClipboard.cs'))
$limitStart = $nativeSource.IndexOf('        internal static int NativeNetworkRootLimit(ulong availablePhysical, uint memoryLoad)', [StringComparison]::Ordinal)
$limitEnd = if ($limitStart -ge 0) { $nativeSource.IndexOf('        private static int NativeNetworkRootLimit()', $limitStart, [StringComparison]::Ordinal) } else { -1 }
if ($limitStart -lt 0 -or $limitEnd -le $limitStart) { throw 'Production native network root limit was not found.' }
$memoryStart = $nativeSource.IndexOf('        [StructLayout(LayoutKind.Sequential)]', [StringComparison]::Ordinal)
$memoryEnd = if ($memoryStart -ge 0) { $nativeSource.IndexOf('        // Keep ordinary networks', $memoryStart, [StringComparison]::Ordinal) } else { -1 }
if ($memoryStart -lt 0 -or $memoryEnd -le $memoryStart) { throw 'Production physical memory status binding was not found.' }
$production = 'using System; using System.Collections.Generic; using System.Diagnostics; using System.Linq; using System.Runtime.InteropServices;' + "`nnamespace Analyse { internal static partial class ElementHistoryDurableBridge {`n" + $source.Substring($start, $end - $start) + $nativeSource.Substring($memoryStart, $memoryEnd - $memoryStart) + $nativeSource.Substring($limitStart, $limitEnd - $limitStart) + "`n} }"
[IO.File]::WriteAllText((Join-Path $output 'Production.cs'), $production)
$fixture = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
namespace Analyse
{
    internal sealed class ElementHistoryEvent
    {
        internal string EventId, ModelKey, Action = "delete";
    }
    internal static class ElementHistoryCompatibility
    {
        internal static string Issue;
        internal static string GetLoadedApiIssue() => Issue;
    }
    internal static partial class ElementHistoryDurableBridge
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, ElementHistoryEvent> Pending = new Dictionary<string, ElementHistoryEvent>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> TransportRetries = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReadinessUpdates = new HashSet<string>(StringComparer.Ordinal);
        private static DateTime NextPassUtc;
        private static int Signals, Checks;
        private enum ReadinessState { Preparing }
        private static string EventId(ElementHistoryEvent ev) => ev.EventId;
        private static void SetReadiness(string id, ReadinessState state, string reason) => ReadinessUpdates.Add(id);
        private static void SignalReadiness() => Signals++;
        private static void Check(bool value, string reason)
        {
            Checks++;
            if (!value) throw new InvalidOperationException(reason);
        }
        private static ElementHistoryEvent Event(string id, string model) => new ElementHistoryEvent { EventId = id, ModelKey = model };
        private static string[] ScopedIds() => Pending.Values.Where(InPreparationScope).Select(EventId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        private static void ExpectScope(params string[] expected) => Check(ScopedIds().SequenceEqual(expected.OrderBy(id => id, StringComparer.Ordinal)), "Preparation scope differs from the requested lot.");
        private static void SeedFailures()
        {
            Errors.Clear(); TransportRetries.Clear(); ReadinessUpdates.Clear();
            foreach (var id in Pending.Keys) { Errors[id] = "existing failure"; TransportRetries[id] = 3; }
        }
        internal static int RunChecks()
        {
            var a1 = Event("a-1", "model-a");
            var a2 = Event("a-2", "model-a");
            var a3 = Event("a-3", "model-a");
            var a4 = Event("a-4", "model-a");
            var b1 = Event("b-1", "model-b");
            foreach (var ev in new[] { a1, a2, a3, a4, b1 }) Pending.Add(ev.EventId, ev);
            var addition = Event("addition", "model-a"); addition.Action = "add";
            var selected = new List<ElementHistoryEvent> { a1, a2, a1, b1, addition, null };
            SeedFailures();
            NextPassUtc = DateTime.UtcNow.AddHours(1);
            var generationBeforeBlock = PreparationGeneration;
            var nextPassBeforeBlock = NextPassUtc;
            ElementHistoryCompatibility.Issue = "Wrong Revit build fixture";
            var blocked = false;
            try { RequestPreparation("MODEL-A", selected); }
            catch (InvalidOperationException ex) { blocked = ex.Message == ElementHistoryCompatibility.Issue; }
            Check(blocked, "A wrong loaded Revit build must reject preparation with the compatibility message.");
            Check(!IsManualPreparing && PreparationGeneration == generationBeforeBlock && PriorityModel == null && PreparationEventIds == null, "A compatibility rejection must not change manual preparation state or scope.");
            Check(!PreparationClock.IsRunning && !PreparationStepClock.IsRunning && NextPassUtc == nextPassBeforeBlock, "A compatibility rejection must not start clocks or reschedule work.");
            Check(Errors.Count == 5 && TransportRetries.Count == 5 && ReadinessUpdates.Count == 0, "A compatibility rejection must not clear failures, retry delays or readiness state.");
            ElementHistoryCompatibility.Issue = null;
            RequestPreparation("MODEL-A", selected);
            Check(IsManualPreparing, "Explicit request must activate manual preparation.");
            ExpectScope("a-1", "a-2");
            Check(!InPreparationScope(null), "A null event must never enter the scope.");
            Check(!InPreparationScope(Event("A-1", "model-a")), "Event identities must remain case sensitive.");
            Check(!InPreparationScope(Event("a-1", "model-b")), "A selected identity must not cross the model boundary.");
            Check(!PreparationEventIds.Contains("addition"), "Nondeletion selection must be ignored.");
            Check(PreparationEventIds.Count == 3, "Selected identities must be deduplicated.");
            Check(ReadinessUpdates.SetEquals(new[] { "a-1", "a-2" }), "Readiness retries must target only the selected lot.");
            Check(!Errors.ContainsKey("a-1") && !Errors.ContainsKey("a-2"), "Selected failures must become retryable.");
            Check(!TransportRetries.ContainsKey("a-1") && !TransportRetries.ContainsKey("a-2"), "Selected retry delays must be cleared.");
            Check(Errors.Keys.OrderBy(id => id).SequenceEqual(new[] { "a-3", "a-4", "b-1" }), "Failures outside the selection must be preserved.");
            Check(TransportRetries.Keys.OrderBy(id => id).SequenceEqual(new[] { "a-3", "a-4", "b-1" }), "Retry delays outside the selection must be preserved.");
            Check(NextPassUtc == DateTime.MinValue, "An explicit request must remove the scheduler delay.");
            selected.Clear(); selected.Add(a3); a1.EventId = "mutated";
            Check(PreparationEventIds.Contains("a-1") && !PreparationEventIds.Contains("mutated") && !PreparationEventIds.Contains("a-3"), "The request must snapshot IDs instead of retaining mutable UI objects or lists.");
            Check(InPreparationScope(Event("a-1", "model-a")), "The original identity must remain selected after UI mutation.");
            a1.EventId = "a-1";

            SeedFailures();
            RequestPreparation("model-a", Array.Empty<ElementHistoryEvent>());
            ExpectScope();
            Check(PreparationEventIds != null && PreparationEventIds.Count == 0, "Empty selection must stay explicitly empty.");
            Check(Errors.Count == 5 && TransportRetries.Count == 5 && ReadinessUpdates.Count == 0, "An empty selection must not retry the model backlog.");
            RequestPreparation("model-a", new[] { addition, null });
            ExpectScope();
            Check(Errors.Count == 5 && ReadinessUpdates.Count == 0, "An invalid selection must not widen to the entire model.");

            SeedFailures();
            RequestPreparation("MODEL-A", null);
            ExpectScope("a-1", "a-2", "a-3", "a-4");
            Check(PreparationEventIds == null, "Null selection is the model-wide request marker.");
            Check(Errors.Keys.SequenceEqual(new[] { "b-1" }) && TransportRetries.Keys.SequenceEqual(new[] { "b-1" }), "A model-wide request must preserve other model failures and delays.");
            Check(ReadinessUpdates.SetEquals(new[] { "a-1", "a-2", "a-3", "a-4" }), "Model-wide retry notifications must remain within that model.");
            RequestPreparation(null, new[] { b1 });
            ExpectScope("b-1");
            RequestPreparation(null, null);
            ExpectScope("a-1", "a-2", "a-3", "a-4", "b-1");

            RequestPreparation("model-a", new[] { a1, a2 });
            var oldGeneration = PreparationGeneration;
            SetPreparationProgress("Export native", 1, 2, oldGeneration);
            Check(PreparationLabel == "Export native" && PreparationCompleted == 1 && PreparationTotal == 2, "The current generation must publish progress.");
            Check(PreparationClock.IsRunning && PreparationStepClock.IsRunning, "Both elapsed clocks must run during preparation.");
            Thread.Sleep(35);
            var stepBefore = PreparationStepClock.ElapsedTicks;
            var totalBefore = PreparationClock.ElapsedTicks;
            SetPreparationProgress("Export native", 2, 2, oldGeneration);
            Check(PreparationStepClock.ElapsedTicks >= stepBefore, "Updating progress in the same phase must not reset its elapsed time.");
            SetPreparationProgress("Publish archive", 0, 2, oldGeneration);
            Check(PreparationClock.ElapsedTicks >= totalBefore && PreparationStepClock.ElapsedTicks < PreparationClock.ElapsedTicks, "Changing phase must reset only the phase clock.");
            CancelPreparation();
            Check(!IsManualPreparing && PreparationGeneration == oldGeneration + 1, "Cancellation must invalidate in-flight progress.");
            Check(!PreparationClock.IsRunning && !PreparationStepClock.IsRunning, "Cancellation must stop both clocks.");
            Check(PreparationLabel == null && PreparationCompleted == 0 && PreparationTotal == 0, "Cancellation must clear live phase counters.");
            var stoppedTotal = PreparationClock.ElapsedTicks;
            var stoppedStep = PreparationStepClock.ElapsedTicks;
            var signalsBefore = Signals;
            SetPreparationProgress("Late export", 99, 100, oldGeneration);
            Thread.Sleep(20);
            Check(PreparationClock.ElapsedTicks == stoppedTotal && PreparationStepClock.ElapsedTicks == stoppedStep, "Stopped elapsed durations must remain stable for the UI summary.");
            Check(PreparationLabel == null && Signals == signalsBefore, "Late progress after cancellation must be ignored without a notification.");

            RequestPreparation("model-a", new[] { a3, a4 });
            var currentGeneration = PreparationGeneration;
            Check(currentGeneration > oldGeneration && PreparationClock.IsRunning && PreparationStepClock.IsRunning, "A subsequent request must start fresh running clocks and a new generation.");
            Check(PreparationClock.ElapsedTicks < stoppedTotal, "A subsequent request must reset the previous total duration.");
            SetPreparationProgress("Current export", 1, 2, currentGeneration);
            signalsBefore = Signals;
            var phaseBefore = PreparationStepClock.ElapsedTicks;
            SetPreparationProgress("Stale export", 87, 99, oldGeneration);
            Check(PreparationLabel == "Current export" && PreparationCompleted == 1 && PreparationTotal == 2, "Old-generation progress must not overwrite a new request.");
            Check(Signals == signalsBefore && PreparationStepClock.ElapsedTicks >= phaseBefore, "Old-generation progress must not notify or restart the new phase clock.");
            Check(PreparationClock.IsRunning && PreparationStepClock.IsRunning, "Old-generation callbacks must not stop current clocks.");
            SetPreparationProgress("Future export", 87, 99, currentGeneration + 1);
            Check(PreparationLabel == "Current export", "Any mismatched progress generation must be ignored.");
            lock (Sync) FinishPreparationLocked();
            Check(!IsManualPreparing && !PreparationClock.IsRunning && !PreparationStepClock.IsRunning, "Successful finish must stop both clocks and exit manual preparation.");
            stoppedTotal = PreparationClock.ElapsedTicks;
            signalsBefore = Signals;
            SetPreparationProgress("Late completion", 2, 2, currentGeneration);
            Thread.Sleep(20);
            Check(PreparationClock.ElapsedTicks == stoppedTotal && PreparationLabel == null && Signals == signalsBefore, "Late completion must preserve the final elapsed summary.");
            Check(PriorityModel == null && PreparationEventIds == null, "Finish must release retained selection data.");
            const ulong gib = 1024UL * 1024 * 1024;
            Check(NativeNetworkRootLimit(0, 0) == 256, "Unknown physical memory must retain the conservative limit.");
            Check(NativeNetworkRootLimit(gib, 30) == 256 && NativeNetworkRootLimit(2 * gib - 1, 30) == 256, "Less than 2 GiB available must limit batches to 256 roots.");
            Check(NativeNetworkRootLimit(2 * gib, 30) == 512 && NativeNetworkRootLimit(4 * gib - 1, 30) == 512, "From 2 to 4 GiB available must permit 512 roots.");
            Check(NativeNetworkRootLimit(4 * gib, 30) == 1024 && NativeNetworkRootLimit(32 * gib, 30) == 1024, "At least 4 GiB available must permit at most 1024 roots.");
            Check(NativeNetworkRootLimit(4 * gib, 84) == 1024, "Memory load below 85 percent must not lower the available-memory limit.");
            Check(NativeNetworkRootLimit(4 * gib, 85) == 256 && NativeNetworkRootLimit(32 * gib, 100) == 256, "Memory load at least 85 percent must reduce batches to 256 roots.");
            Check(NativeNetworkRootLimit(2 * gib, 85) == 256, "High memory load must override the 512-root tier as well.");
            var currentLimit = NativeNetworkRootLimit(4 * gib, 30);
            Check((536 + currentLimit - 1) / currentLimit == 1, "A 536-root network must fit one wave with at least 4 GiB available and moderate memory load.");
            Check((536 + 256 - 1) / 256 == 3, "The previous 256-root policy would require three waves for 536 roots.");
            if (OperatingSystem.IsWindows())
            {
                var physicalMemory = new NativeMemoryStatus();
                var memoryRead = GlobalMemoryStatusEx(physicalMemory);
                Check(memoryRead, "Production GlobalMemoryStatusEx binding failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                Check(physicalMemory.Length >= 64, "The native memory status structure must marshal at least 64 bytes.");
                Check(physicalMemory.AvailablePhysical > 0, "The native binding must return available physical memory.");
            }
            return Checks;
        }
    }
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                var count = ElementHistoryDurableBridge.RunChecks();
                Console.WriteLine("PASS: " + count + " manual preparation scope, generation, timing and memory-limit checks.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
"@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'), $fixture)
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
$projectPath = Join-Path $output 'ManualPreparationScope.Tests.csproj'
[IO.File]::WriteAllText($projectPath, $project)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>')
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.log'); throw 'Manual preparation scope test compilation failed.' }
& dotnet (Join-Path $output 'bin/Debug/net8.0/ManualPreparationScope.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Manual preparation scope checks failed.' }
Write-Output $output