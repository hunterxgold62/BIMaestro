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
            public bool Excluded;
            public string Reason;
        }
        private sealed class ReadinessEntry
        {
            internal EventReadiness Value;
            internal DateTime CheckedUtc;
            internal bool Checking;
            internal bool Retryable;
            internal long Generation;
        }
        private static readonly Dictionary<string, ReadinessEntry> Readiness = new Dictionary<string, ReadinessEntry>(StringComparer.Ordinal);
        private static readonly System.Threading.SemaphoreSlim ReadinessValidationSlots = new System.Threading.SemaphoreSlim(2);
        internal static event Action ReadyChanged;
        private static bool IsLegacy(ElementHistoryEvent ev) => string.IsNullOrWhiteSpace(ev.EventId) || ev.EventId.StartsWith("legacy-", StringComparison.Ordinal);
        // A direct recipe reuses the exact recorded project type/level references.
        // Existence is validated in the Revit API callback before each creation.
        // No backup import, mesh proxy or replacement type is authorized by this flag.
        internal static bool IsReadyDirectRequest(HistoryRestoreRequest request)
        {
            if (request?.Recipe == null) return false;
            foreach (var recipe in Recipes(request.Recipe))
                if (recipe == null || !recipe.UsesExistingModelDefinitions || recipe.Native != null
                    || recipe.OpeningFallback != null || recipe.Kind == "native_archive"
                    || ElementHistoryReconstruction.ReadRecipe(recipe) == null) return false;
            return true;
        }

        internal static HistoryRestoreRequest NormalizeLegacyDirectRequest(HistoryRestoreRequest request)
        {
            if (request?.Recipe == null) return request;
            foreach (var recipe in Recipes(request.Recipe))
                if (recipe == null || recipe.Native != null || recipe.OpeningFallback != null
                    || recipe.Kind == "native_archive" || ElementHistoryReconstruction.ReadRecipe(recipe) == null) return request;
            var copy = JObject.FromObject(request).ToObject<HistoryRestoreRequest>();
            foreach (var recipe in Recipes(copy.Recipe)) recipe.UsesExistingModelDefinitions = true;
            return copy;
        }

        internal static bool IsReadyNativeRequest(HistoryRestoreRequest request)
            => request?.Recipe?.Native != null && !request.Recipe.Native.DefinitionOnly
                && !request.Recipe.Native.WholeModel && string.IsNullOrWhiteSpace(request.Recipe.Native.File)
                && !string.IsNullOrWhiteSpace(request.Recipe.Native.PayloadId);

        internal static EventReadiness GetReadiness(ElementHistoryEvent ev)
        {
            if (ev == null || ev.Action != "delete") return new EventReadiness { State = ReadinessState.Unavailable, Reason = "Cet événement n’est pas une suppression." };
            var id = EventId(ev);
            var classification = ElementHistoryDurableStore.GetCachedOpeningClassification(id,CurrentStore);
            if (classification?.IsExcluded() == true)
                return new EventReadiness { EventId=id, State=ReadinessState.Unavailable,Legacy=IsLegacy(ev),Excluded=true,Reason="Dépendance non physique exclue." };
            lock (Sync)
            {
                if (!Readiness.TryGetValue(id, out var entry))
                { entry = new ReadinessEntry { Value = new EventReadiness { EventId = id, State = ReadinessState.Preparing, Legacy = IsLegacy(ev), Reason = "Préparation des données de restauration…" } }; Readiness[id] = entry; }
                if (!entry.Checking && (entry.CheckedUtc == default || (entry.Value.State == ReadinessState.Preparing || entry.Retryable) && DateTime.UtcNow - entry.CheckedUtc > TimeSpan.FromSeconds(2)))
                {
                    entry.Checking = true;
                    var generation = entry.Generation;
                    Task.Run(async () =>
                    {
                        await ReadinessValidationSlots.WaitAsync().ConfigureAwait(false);
                        try { CheckReadiness(ev, id, generation); }
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
        private static void SetReadiness(string id, ReadinessState state, string reason, bool legacy = false, bool retryable = false, long? expectedGeneration = null, bool excluded = false)
        {
            lock (Sync)
            {
                if (!Readiness.TryGetValue(id, out var entry)) Readiness[id] = entry = new ReadinessEntry();
                // A publication or a newer failure supersedes every older IO
                // result, including an older Unavailable/corruption result.
                if (expectedGeneration.HasValue && entry.Generation != expectedGeneration.Value) return;
                // Publication can complete while an older cache read is in flight.
                // Its earlier miss must not gray out an already published object.
                if (state == ReadinessState.Preparing && entry.Value?.State == ReadinessState.Ready)
                { entry.Checking = false; return; }
                entry.Value = new EventReadiness { EventId = id, State = state, Reason = reason, Legacy = legacy, Excluded = excluded };
                entry.CheckedUtc = DateTime.UtcNow; entry.Checking = false;
                entry.Retryable = retryable;
                entry.Generation++;
            }
            SignalReadiness();
        }
        private static void CheckReadiness(ElementHistoryEvent ev, string id, long generation)
        {
            void Finish(ReadinessState state, string reason, bool legacy = false, bool retryable = false, bool excluded = false)
                => SetReadiness(id, state, reason, legacy, retryable, generation, excluded);
            try
            {
                var classification=ElementHistoryDurableStore.ResolveOpeningClassification(id);
                if(classification?.IsExcluded()==true)
                { Finish(ReadinessState.Unavailable,"Dépendance non physique exclue.",IsLegacy(ev),excluded:true);return; }
                var request = ResolvePreparedRequest(id);
                if (request != null)
                {
                    if (request.Recipe == null) Finish(ReadinessState.Unavailable, request.CaptureFailure ?? "Aucune recette de restauration disponible.", IsLegacy(ev));
                    else if (IsReadyDirectRequest(request)) Finish(ReadinessState.Ready, ReadyReason(request.Recipe), IsLegacy(ev));
                    else if (!IsReadyNativeRequest(request))
                    {
                        string preparationFailure; lock(Sync) Errors.TryGetValue(id,out preparationFailure);
                        if(preparationFailure!=null) Finish(ReadinessState.Unavailable,preparationFailure,IsLegacy(ev));
                        else if(TryGetOpeningNativeRequest(ev)!=null) Finish(ReadinessState.Preparing,"Archive native à préparer. Utilisez Charger les éléments en attente.",IsLegacy(ev));
                        else Finish(ReadinessState.Unavailable,"Aucune copie native disponible et recette enregistrée incomplète ou incompatible.",IsLegacy(ev));
                    }
                    else Finish(ReadinessState.Ready, ReadyReason(request.Recipe), IsLegacy(ev));
                    return;
                }
                string error;
                lock (Sync)
                {
                    Errors.TryGetValue(id, out error);
                    if (error == null && Errors.TryGetValue("load", out var loadError))
                    { Finish(ReadinessState.Unavailable, "Chargement de l’historique impossible : " + loadError, IsLegacy(ev), true); return; }
                }
                if (error != null) { Finish(ReadinessState.Unavailable, error, IsLegacy(ev)); return; }
                if (IsLegacy(ev))
                {
                    request = NormalizeLegacyDirectRequest(ToRequest(ev, false));
                    if (request.Recipe == null) { Finish(ReadinessState.Unavailable, request.CaptureFailure ?? "Ancienne suppression sans données de restauration.", true); return; }
                    if (IsReadyDirectRequest(request))
                    { Finish(ReadinessState.Ready, ReadyReason(request.Recipe), true); return; }
                    if(request.Recipe.UsesExistingModelDefinitions || request.Recipe.Native==null || request.Recipe.Native.DefinitionOnly)
                    {
                        if(TryGetOpeningNativeRequest(ev)!=null) Finish(ReadinessState.Preparing,"Ancienne copie à préparer en archive native.",true);
                        else Finish(ReadinessState.Unavailable,"Ancienne suppression sans copie native ni recette enregistrée compatible.",true);
                        return;
                    }
                    var artifacts = Artifacts(request.Recipe).ToList();
                    // Legacy small native archives remain usable, but an opening
                    // checkpoint must first pass through durable preparation.
                    if (artifacts.Any(a => a.WholeModel)) { Finish(ReadinessState.Preparing, "Préparation de l’ancienne copie de secours…", true); return; }
                    foreach (var artifact in artifacts)
                    {
                        if (!string.IsNullOrWhiteSpace(artifact.PayloadId))
                            throw new InvalidDataException("Archive durable sans conversion validée.");
                        var name = Path.GetFileName(artifact.File);
                        if (string.IsNullOrEmpty(name) || !name.StartsWith("snapshot-", StringComparison.OrdinalIgnoreCase)
                            || !artifact.Ready || !File.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history", name)))
                            throw new InvalidDataException("L’ancienne archive native est absente ou incomplète.");
                    }
                    Finish(ReadinessState.Ready, "Anciennes données de restauration disponibles.", true); return;
                }
                Finish(ReadinessState.Preparing, "Préparation des données de restauration…");
            }
            catch (Exception ex) { Finish(ReadinessState.Unavailable, ex.Message, IsLegacy(ev), ElementHistoryRetryPolicy.IsTransient(ex)); }
        }
    }
}
