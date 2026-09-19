using Autodesk.Revit.DB.Events;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;

namespace BIMaestro.Codex
{
    // Cooperative cancellation only: never abort a thread inside the Revit API.
    internal sealed class CodexCreationGuard : IDisposable
    {
        [ThreadStatic] private static CodexCreationGuard current;
        private readonly Autodesk.Revit.ApplicationServices.Application app;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly Stopwatch stepElapsed = Stopwatch.StartNew();
        private readonly long initialMemory;
        private readonly string path, name;
        private string stage = "préparation", stopped;
        private long lastLog;
        private bool completed;
        private bool listening;
        internal static bool IsActive => current != null;

        // Revit expects feet. Keep a 1 mm floor even on versions whose native
        // short-curve tolerance is smaller; never stretch a requested profile.
        internal static Autodesk.Revit.DB.Line CreateLine(Autodesk.Revit.DB.XYZ start, Autodesk.Revit.DB.XYZ end)
        {
            double minimum = Math.Max(1 / 304.8, (current?.app.ShortCurveTolerance ?? 0) * 1.01);
            double length = start.DistanceTo(end);
            if (double.IsNaN(length) || double.IsInfinity(length) || length + 1e-12 < minimum)
                throw new InvalidOperationException("Trait trop court : " + (length * 304.8).ToString("0.######") +
                    " mm ; minimum " + (minimum * 304.8).ToString("0.######") +
                    " mm. Corriger les coordonnées ou le scénario de variation avant de relancer.");
            return Autodesk.Revit.DB.Line.CreateBound(start, end);
        }

        internal CodexCreationGuard(Autodesk.Revit.ApplicationServices.Application app, string name)
        {
            if (current != null) throw new InvalidOperationException("Une création de famille est déjà en cours.");
            this.app = app; this.name = name;
            initialMemory = Memory();
            string directory = Path.Combine(CodexClient.DataDirectory, "Operations");
            try { Directory.CreateDirectory(directory); path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".json"); } catch { }
            app.ProgressChanged += OnProgress;
            listening = true;
            current = this; Write("running");
        }
        private static long Memory() { using (var process = Process.GetCurrentProcess()) return process.PrivateMemorySize64; }
        private void Evaluate()
        {
            if (stopped != null) return;
            if (stepElapsed.Elapsed.TotalMinutes > 5) stopped = "une étape de création dépasse 5 minutes";
            else if (Memory() - initialMemory > 2L * 1024 * 1024 * 1024) stopped = "hausse de mémoire supérieure à 2 Go pendant la création";
        }
        private void OnProgress(object sender, ProgressChangedEventArgs e)
        {
            Evaluate();
            if (elapsed.ElapsedMilliseconds - lastLog > 1000) Write(stopped == null ? "running" : "cancellation_requested");
            if (stopped == null) return;
            if (e.Stage == ProgressStage.Unchanged || e.Stage == ProgressStage.PositionChanged && e.Cancellable)
            {
                try { e.Cancel(); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
        }
        internal static void Check(string phase = null)
        {
            var guard = current;
            if (guard == null) return;
            if (phase != null) guard.stage = phase;
            guard.Evaluate();
            if (phase != null) guard.Write(guard.stopped == null ? "running" : "stopped");
            if (guard.stopped != null)
                throw new OperationCanceledException("Création interrompue pour préserver Revit : " + guard.stopped +
                    ". Étape : " + guard.stage + ". Simplifier la géométrie ou les contraintes avant une nouvelle tentative ; ne pas relancer la même création.");
        }
        internal void Complete() { completed = true; }
        internal void Pause()
        {
            Check();
            if (listening) app.ProgressChanged -= OnProgress;
            listening = false;
            current = null;
            Write("between_steps");
        }
        internal void Resume()
        {
            if (current != null && current != this) throw new InvalidOperationException("Une autre étape de création est en cours.");
            current = this;
            stepElapsed.Restart();
            if (!listening) app.ProgressChanged += OnProgress;
            listening = true;
            Check();
        }
        private void Write(string state)
        {
            lastLog = elapsed.ElapsedMilliseconds;
            if (path == null) return;
            try
            {
                using var process = Process.GetCurrentProcess();
                long memory = process.PrivateMemorySize64;
                File.WriteAllText(path, JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, process_id = process.Id,
                    family = name, stage, state, seconds = elapsed.Elapsed.TotalSeconds, private_memory_mb = memory / 1048576,
                    memory_growth_mb = (memory - initialMemory) / 1048576, reason = stopped }, Formatting.Indented));
            }
            catch { }
        }
        public void Dispose()
        {
            if (listening) app.ProgressChanged -= OnProgress;
            if (current == this) current = null;
            Write(completed ? "completed" : "failed_or_cancelled");
        }
    }
}
