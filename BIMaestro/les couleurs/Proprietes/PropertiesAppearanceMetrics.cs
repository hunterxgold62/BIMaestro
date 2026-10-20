using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BIMaestro.PropertiesAppearance
{
    // Opt-in, in-process diagnostics for the disposable Revit test harness.
    // No samples, timers, logging or file writes are enabled in normal use.
    internal static class PropertiesAppearanceMetrics
    {
        internal static bool Enabled;
        internal static long CellReads;
        private static readonly Dictionary<string, List<double>> Samples = new Dictionary<string, List<double>>();
        internal static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;
        internal static void End(string name, long start)
        {
            if (start == 0 || !Enabled) return;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (!Samples.TryGetValue(name, out var values)) Samples[name] = values = new List<double>();
            if (values.Count < 20000) values.Add(ms);
        }
        internal static void Add(string name, double ms)
        {
            if (!Enabled) return;
            if (!Samples.TryGetValue(name, out var values)) Samples[name] = values = new List<double>();
            if (values.Count < 20000) values.Add(ms);
        }
        internal static void Reset() { Samples.Clear(); CellReads = 0; }
        internal static object Snapshot() => new { CellReads, Timings = Samples.ToDictionary(p => p.Key, p =>
        {
            var a = p.Value.OrderBy(x => x).ToArray();
            return new { Count = a.Length, MeanMs = Math.Round(a.Average(), 3), P95Ms = Math.Round(a[Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * .95) - 1)], 3), MaxMs = Math.Round(a.Last(), 3) };
        }) };
    }
}
