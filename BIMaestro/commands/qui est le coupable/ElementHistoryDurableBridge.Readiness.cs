using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Analyse
{
    internal static partial class ElementHistoryDurableBridge
    {
        internal enum ReadinessState { Preparing, Ready, Unavailable }
        internal sealed class EventReadiness
        {
            public string EventId;
            public ReadinessState State;
            public bool Legacy;
            public string Reason;
        }
        private sealed class ReadinessEntry
        {
            internal EventReadiness Value;
            internal DateTime CheckedUtc;
            internal bool Checking;
        }
        private static readonly Dictionary<string, ReadinessEntry> Readiness = new Dictionary<string, ReadinessEntry>(StringComparer.Ordinal);
        private static readonly System.Threading.SemaphoreSlim ReadinessValidationSlots = new System.Threading.SemaphoreSlim(2);
        internal static event Action ReadyChanged;
        private static bool IsLegacy(ElementHistoryEvent ev) => string.IsNullOrWhiteSpace(ev.EventId) || ev.EventId.StartsWith("legacy-", StringComparison.Ordinal);
        internal static EventReadiness GetReadiness(ElementHistoryEvent ev)
        {
            if (ev == null || ev.Action != "delete") return new EventReadiness { State = ReadinessState.Unavailable, Reason = "Cet événement n’est pas une suppression." };
            var id = EventId(ev);
            lock (Sync)
            {
                if (!Readiness.TryGetValue(id, out var entry))
                { entry = new ReadinessEntry { Value = new EventReadiness { EventId = id, State = ReadinessState.Preparing, Legacy = IsLegacy(ev), Reason = "Préparation des données de restauration…" } }; Readiness[id] = entry; }
                if (!entry.Checking && (entry.CheckedUtc == default || entry.Value.State == ReadinessState.Preparing && DateTime.UtcNow - entry.CheckedUtc > TimeSpan.FromSeconds(2)))
                {
                    entry.Checking = true;
                    Task.Run(async () =>
                    {
                        await ReadinessValidationSlots.WaitAsync().ConfigureAwait(false);
                        try { CheckReadiness(ev, id); }
                        finally { ReadinessValidationSlots.Release(); }
                    });
                }
                return entry.Value;
            }
        }
        private static void SignalReadiness()
        {
            var handlers = ReadyChanged;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
        }
        private static void SetReadiness(string id, ReadinessState state, string reason, bool legacy = false)
        {
            lock (Sync)
            {
                if (!Readiness.TryGetValue(id, out var entry)) Readiness[id] = entry = new ReadinessEntry();
                // Publication can complete while an older cache read is in flight.
                // Its earlier miss must not gray out an already published object.
                if (state == ReadinessState.Preparing && entry.Value?.State == ReadinessState.Ready)
                { entry.Checking = false; return; }
                entry.Value = new EventReadiness { EventId = id, State = state, Reason = reason, Legacy = legacy };
                entry.CheckedUtc = DateTime.UtcNow; entry.Checking = false;
            }
            SignalReadiness();
        }
        private static void CheckReadiness(ElementHistoryEvent ev, string id)
        {
            try
            {
                var request = ElementHistoryDurableStore.ResolveConversion(id);
                if (request != null)
                {
                    if (request.Recipe == null) SetReadiness(id, ReadinessState.Unavailable, request.CaptureFailure ?? "Aucune recette de restauration disponible.", IsLegacy(ev));
                    else SetReadiness(id, ReadinessState.Ready, "Archive de restauration prête et vérifiée.", IsLegacy(ev));
                    return;
                }
                string error;
                lock (Sync) Errors.TryGetValue(id, out error);
                if (error != null) { SetReadiness(id, ReadinessState.Unavailable, error, IsLegacy(ev)); return; }
                if (IsLegacy(ev))
                {
                    request = ToRequest(ev, false);
                    if (request.Recipe == null) { SetReadiness(id, ReadinessState.Unavailable, request.CaptureFailure ?? "Ancienne suppression sans données de restauration.", true); return; }
                    var artifacts = Artifacts(request.Recipe).ToList();
                    // Legacy small native archives remain usable, but an opening
                    // checkpoint must first pass through durable preparation.
                    if (artifacts.Any(a => a.WholeModel)) { SetReadiness(id, ReadinessState.Preparing, "Préparation de l’ancienne copie de secours…", true); return; }
                    foreach (var artifact in artifacts)
                    {
                        if (!string.IsNullOrWhiteSpace(artifact.PayloadId))
                            throw new InvalidDataException("Archive durable sans conversion validée.");
                        var name = Path.GetFileName(artifact.File);
                        if (string.IsNullOrEmpty(name) || !name.StartsWith("snapshot-", StringComparison.OrdinalIgnoreCase)
                            || !artifact.Ready || !File.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history", name)))
                            throw new InvalidDataException("L’ancienne archive native est absente ou incomplète.");
                    }
                    SetReadiness(id, ReadinessState.Ready, "Anciennes données de restauration disponibles.", true); return;
                }
                SetReadiness(id, ReadinessState.Preparing, "Préparation des données de restauration…");
            }
            catch (Exception ex) { SetReadiness(id, ReadinessState.Unavailable, ex.Message, IsLegacy(ev)); }
        }
    }
}
