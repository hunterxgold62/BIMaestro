using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal static class ToolRecoveryTests
{
    private const string Create = "revit_create_parametric_family";
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await RetryBeforeStart();
        await RejectIdenticalFailures();
        await ContinueAfterUsefulReads();
        await TerminalFailures();
        await RemoteFailures();
        await CancelWhileWaiting();
        await BoundRecovery();
        await ResourceGuardAndRollback();
        await ResetWhileFinishing();
    }

    private static CodexToolRecovery New() => new CodexToolRecovery((_, token) =>
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    private static Task<object> Success() => Task.FromResult<object>(new { ok = true });
    private static Task<object> Fail(Exception error) => Task.FromException<object>(error);
    private static Task<object> Call(CodexToolRecovery recovery, string tool, JObject args, Func<Task<object>> execute) =>
        recovery.ExecuteAsync(tool, args, execute, null, CancellationToken.None);

    private static async Task RetryBeforeStart()
    {
        foreach (string message in new[] { "Une opération Revit est déjà en attente.",
            "Revit est occupé. Réessayez après fermeture de la boîte de dialogue active.",
            "Revit est occupé. Fermez la boîte de dialogue active puis réessayez." })
        {
            int calls = 0, pauses = 0;
            var recovery = new CodexToolRecovery((_, token) => { pauses++; return Task.CompletedTask; });
            await Call(recovery, Create, new JObject(), () => ++calls <= 2 ? Fail(new InvalidOperationException(message)) : Success());
            Check(calls == 3 && pauses == 2 && !recovery.TryTakeContinuation(out _), "busy before start retries twice then succeeds");
        }
        var bounded = New(); int attempts = 0;
        await Error(() => Call(bounded, Create, new JObject(), () => { attempts++; return Fail(new InvalidOperationException("Une opération Revit est déjà en attente.")); }));
        Check(attempts == 3, "busy retry bounded after two retries");
        var otherBusy = New(); int otherCalls = 0;
        await Error(() => Call(otherBusy, Create, new JObject(), () => { otherCalls++; return Fail(new InvalidOperationException("Revit est occupé après création.")); }));
        Check(otherCalls == 1, "unrecognized busy message never replays mutation");
    }

    private static async Task RejectIdenticalFailures()
    {
        var recovery = New(); int calls = 0;
        Func<Task<object>> invalid = () => { calls++; return Fail(new InvalidOperationException("Pied : profondeur nulle.")); };
        await Error(() => Call(recovery, Create, JObject.Parse("{\"parts\":[{\"width\":0,\"height\":10}],\"name\":\"Table\"}"), invalid));
        var duplicate = await Error(() => Call(recovery, Create, JObject.Parse("{\"name\":\"Table\",\"parts\":[{\"height\":10,\"width\":0}]}"), invalid));
        Check(calls == 1 && duplicate.Detail["recovery"].Value<bool>("repeated_call_blocked"), "nested JSON order does not bypass failed-call protection");
        await Call(recovery, Create, JObject.Parse("{\"parts\":[{\"width\":5,\"height\":10}],\"name\":\"Table\"}"), Success);
        Check(!recovery.TryTakeContinuation(out _), "changed geometry executes and resolves pending recovery");

        recovery.Reset(); calls = 0;
        await Error(() => Call(recovery, "revit_run_family_program", new JObject { ["program_json"] = "{\"b\":2,\"a\":1}" }, invalid));
        await Error(() => Call(recovery, "revit_run_family_program", new JObject { ["program_json"] = "{ \"a\": 1, \"b\": 2 }" }, invalid));
        Check(calls == 1, "program JSON formatting does not bypass failed-call protection");
        await Call(recovery, "revit_configure_family", new JObject(), Success);
        Check(!recovery.TryTakeContinuation(out _), "successful alternative tool resolves recovery");
    }

    private static async Task ContinueAfterUsefulReads()
    {
        var recovery = New();
        await Error(() => Call(recovery, Create, new JObject(), () => Fail(new InvalidOperationException("Pied invalide."))));
        await Call(recovery, "revit_family_contract", new JObject(), Success);
        await Call(recovery, "revit_capabilities", new JObject(), Success);
        await Call(recovery, "revit_inspect_family", new JObject(), Success);
        await Call(recovery, "revit_validate_parametric_family", new JObject(), Success);
        await Call(recovery, "revit_open_created_family", new JObject(), Success);
        Check(recovery.TryTakeContinuation(out string prompt) && prompt.Contains(Create) && prompt.Contains("Pied invalide"), "reads and validation do not conceal failed creation");
        await Error(() => Call(recovery, "revit_family_template_info", new JObject(), () => Fail(new InvalidOperationException("Gabarit inconnu."))));
        await Error(() => Call(recovery, "revit_validate_parametric_family", new JObject { ["other"] = 1 }, () => Fail(new InvalidOperationException("Validation partielle invalide."))));
        await Call(recovery, "revit_validate_parametric_family", new JObject { ["other"] = 2 }, Success);
        Check(recovery.TryTakeContinuation(out prompt) && prompt.Contains("Pied invalide"), "failed helper read does not replace pending mutation failure");

        recovery.Reset();
        await Error(() => Call(recovery, "revit_family_template_info", new JObject { ["hosting"] = "wall" }, () => Fail(new InvalidOperationException("Gabarit inconnu."))));
        await Call(recovery, "revit_family_template_info", new JObject { ["hosting"] = "free" }, Success);
        Check(!recovery.TryTakeContinuation(out _), "corrected failing read resolves its own recovery");

        recovery.Reset();
        await Error(() => Call(recovery, "revit_run_family_program", new JObject { ["validate_only"] = false }, () => Fail(new InvalidOperationException("Contrainte impossible."))));
        await Call(recovery, "revit_run_family_program", new JObject { ["validate_only"] = true }, Success);
        Check(recovery.TryTakeContinuation(out _), "validate-only success cannot conceal failed application of same program tool");
    }

    private static async Task TerminalFailures()
    {
        foreach (Exception failure in new Exception[]
        {
            new InvalidOperationException("Création refusée par l'utilisateur. Ne pas réessayer sans nouvelle demande."),
            new InvalidOperationException("Découpe refusée. Ne pas réessayer sans nouvelle demande."),
            new InvalidOperationException("Mode lecture seule : activez les modifications dans le panneau."),
            new InvalidOperationException("Le document attaché au panneau n'est plus actif. Revenez à ce document ou rouvrez le panneau."),
            new UnauthorizedAccessException("Non autorisé."), new IOException("Réponse perdue."), new TimeoutException("Réponse inconnue."),
            new InvalidOperationException("Erreur liaison.", new IOException("Canal fermé."))
        })
        {
            int calls = 0; var recovery = New();
            await Error(() => Call(recovery, Create, new JObject(), () => { calls++; return Fail(failure); }));
            await Error(() => Call(recovery, "revit_configure_family", new JObject { ["different"] = true }, () => { calls++; return Success(); }));
            await Call(recovery, "revit_inspect_family", new JObject(), Success);
            Check(calls == 1 && !recovery.TryTakeContinuation(out _), "no retry or alternate mutation after " + failure.GetType().Name + ": " + failure.Message);
        }
        var cancelled = New(); int cancelledCalls = 0;
        try { await Call(cancelled, Create, new JObject(), () => { cancelledCalls++; return Fail(new OperationCanceledException("Arrêt utilisateur.")); }); }
        catch (OperationCanceledException) { }
        Check(cancelledCalls == 1 && !cancelled.TryTakeContinuation(out _), "user cancellation never continues");
        try { await Call(cancelled, Create, new JObject { ["other"] = 1 }, () => { cancelledCalls++; return Success(); }); }
        catch (OperationCanceledException) { }
        Check(cancelledCalls == 1, "cancelled request cannot invoke another tool");
    }

    private static async Task CancelWhileWaiting()
    {
        var waiting = new TaskCompletionSource<bool>();
        var recovery = new CodexToolRecovery((_, token) => { waiting.TrySetResult(true); return Task.Delay(10000, token); });
        int calls = 0;
        Task<object> work = Call(recovery, Create, new JObject(), () => { calls++; return Fail(new InvalidOperationException("Une opération Revit est déjà en attente.")); });
        await waiting.Task;
        recovery.Cancel();
        try { await work; } catch (OperationCanceledException) { }
        Check(work.IsCanceled && calls == 1 && !recovery.TryTakeContinuation(out _), "Cancel interrupts retry delay before any second call");

        using var tokenSource = new CancellationTokenSource();
        var tokenWaiting = new TaskCompletionSource<bool>();
        var tokenRecovery = new CodexToolRecovery((_, token) => { tokenWaiting.TrySetResult(true); return Task.Delay(10000, token); });
        work = tokenRecovery.ExecuteAsync(Create, new JObject(), () => Fail(new InvalidOperationException("Une opération Revit est déjà en attente.")), null, tokenSource.Token);
        await tokenWaiting.Task; tokenSource.Cancel();
        try { await work; } catch (OperationCanceledException) { }
        Check(work.IsCanceled && !tokenRecovery.TryTakeContinuation(out _), "external cancellation interrupts retry delay and continuation");
    }

    private static async Task RemoteFailures()
    {
        foreach (var sample in new[]
        {
            new { Failure = (Exception)new OperationCanceledException("Arrêt demandé."), Kind = "cancellation" },
            new { Failure = (Exception)new TaskCanceledException("Arrêt distant."), Kind = "cancellation" },
            new { Failure = (Exception)new IOException("Liaison perdue."), Kind = "unknown" },
            new { Failure = (Exception)new TimeoutException("Réponse perdue."), Kind = "unknown" },
            new { Failure = (Exception)new ObjectDisposedException("Revit"), Kind = "unknown" },
            new { Failure = (Exception)new UnauthorizedAccessException("Accès refusé."), Kind = "permission" },
            new { Failure = (Exception)new System.Security.SecurityException("Permission insuffisante."), Kind = "permission" },
            new { Failure = (Exception)new InvalidOperationException("Contrainte impossible."), Kind = "technical" }
        })
        {
            Check(CodexToolRecovery.RemoteFailureKind(sample.Failure) == sample.Kind &&
                CodexToolRecovery.RemoteFailureKind(new InvalidOperationException("Erreur distante.", sample.Failure)) == sample.Kind,
                "remote failure kind preserves direct and inner " + sample.Failure.GetType().Name);
            var restored = CodexToolRecovery.RestoreRemoteFailure(sample.Kind, sample.Failure.Message);
            Check(CodexToolRecovery.RemoteFailureKind(restored) == sample.Kind && restored.Message == sample.Failure.Message,
                "remote failure roundtrip preserves kind and message for " + sample.Failure.GetType().Name);
        }
        Check(CodexToolRecovery.RemoteFailureKind(null) == "technical" &&
            CodexToolRecovery.RestoreRemoteFailure(null, null) is InvalidOperationException &&
            CodexToolRecovery.RestoreRemoteFailure("unexpected", "Erreur") is InvalidOperationException,
            "legacy or unknown remote failure code uses technical exception without CLR deserialization");

        foreach (var original in new Exception[]
        {
            new InvalidOperationException("Modification refusée. Ne pas réessayer sans nouvelle demande."),
            new OperationCanceledException("Arrêt utilisateur distant."),
            new IOException("Réponse perdue après création."),
            new UnauthorizedAccessException("Permission distante refusée.")
        })
        {
            int calls = 0;
            var recovery = New();
            var remote = CodexToolRecovery.RestoreRemoteFailure(CodexToolRecovery.RemoteFailureKind(original), original.Message);
            try { await Call(recovery, Create, new JObject(), () => { calls++; return Fail(remote); }); }
            catch (CodexToolRecoveryException) { }
            catch (OperationCanceledException) { }
            try { await Call(recovery, "revit_configure_family", new JObject { ["other"] = true }, () => { calls++; return Success(); }); }
            catch (CodexToolRecoveryException) { }
            catch (OperationCanceledException) { }
            Check(calls == 1 && !recovery.TryTakeContinuation(out _), "remote " + original.GetType().Name + " prevents retry and alternate mutation");
        }

        var guarded = New();
        var originalGuard = new OperationCanceledException("Création interrompue pour préserver Revit : mémoire. Simplifier.");
        var remoteGuard = CodexToolRecovery.RestoreRemoteFailure(CodexToolRecovery.RemoteFailureKind(originalGuard), originalGuard.Message);
        var detail = await Error(() => Call(guarded, Create, new JObject { ["count"] = 1000 }, () => Fail(remoteGuard)));
        Check(detail.Detail["recovery"].Value<string>("action") == "simplify" && guarded.TryTakeContinuation(out _),
            "remote resource guard remains simplifiable despite cancellation exception type");
        await Call(guarded, Create, new JObject { ["count"] = 10 }, Success);
        Check(!guarded.TryTakeContinuation(out _), "simplified remote operation can complete after resource guard");
    }

    private static async Task BoundRecovery()
    {
        var recovery = New();
        await Error(() => Call(recovery, Create, new JObject(), () => Fail(new InvalidOperationException("Erreur initiale."))));
        for (int i = 0; i < CodexToolRecovery.MaximumContinuations; i++)
            Check(recovery.TryTakeContinuation(out _), "automatic continuation available " + (i + 1));
        Check(!recovery.TryTakeContinuation(out _), "automatic model continuation count bounded");

        recovery.Reset(); int executions = 0;
        for (int i = 0; i < CodexToolRecovery.MaximumFailures; i++)
        {
            await Call(recovery, "revit_family_contract", new JObject(), Success);
            await Error(() => Call(recovery, Create, new JObject { ["variant"] = i }, () => { executions++; return Fail(new InvalidOperationException("Dimensions incompatibles.")); }));
        }
        await Error(() => Call(recovery, Create, new JObject { ["variant"] = 1000 }, () => { executions++; return Success(); }));
        Check(executions == CodexToolRecovery.MaximumFailures && !recovery.TryTakeContinuation(out _), "different approaches allowed beyond two errors but request budget remains bounded despite reads");
        recovery.Reset(); executions = 0;
        for (int i = 0; i < CodexToolRecovery.MaximumFailures; i++)
            await Error(() => Call(recovery, Create, new JObject(), () => { executions++; return Fail(new InvalidOperationException("Identique.")); }));
        Check(executions == 1 && !recovery.TryTakeContinuation(out _), "duplicates consume budget without executing again");
        recovery.Reset();
        await Call(recovery, Create, new JObject(), Success);
        Check(!recovery.TryTakeContinuation(out _), "new user request resets exhausted recovery");
    }

    private static async Task ResourceGuardAndRollback()
    {
        var recovery = New(); int calls = 0;
        Func<Task<object>> guard = () => { calls++; return Fail(new OperationCanceledException("Création interrompue pour préserver Revit : mémoire. Simplifier.")); };
        var error = await Error(() => Call(recovery, Create, new JObject { ["count"] = 1000 }, guard));
        await Error(() => Call(recovery, Create, new JObject { ["count"] = 1000 }, guard));
        Check(calls == 1 && error.Detail["recovery"].Value<string>("action") == "simplify" && recovery.TryTakeContinuation(out _), "resource protection permits simplification without replaying heavy description");
        await Call(recovery, Create, new JObject { ["count"] = 10 }, Success);
        Check(!recovery.TryTakeContinuation(out _), "simplified creation succeeds after resource protection");

        recovery.Reset();
        await Error(() => Call(recovery, "revit_configure_family", new JObject(), () => Fail(new InvalidOperationException("Revit a annulé la modification : géométrie ou contraintes incompatibles."))));
        Check(recovery.TryTakeContinuation(out _), "geometry rollback is recoverable and distinct from user cancellation");
    }

    private static async Task ResetWhileFinishing()
    {
        var recovery = New(); var finishing = new TaskCompletionSource<object>();
        Task<object> oldRequest = Call(recovery, Create, new JObject(), () => finishing.Task);
        recovery.Reset();
        await Error(() => Call(recovery, Create, new JObject { ["new"] = true }, () => Fail(new InvalidOperationException("Nouveau problème."))));
        finishing.SetResult(new { saved = true }); await oldRequest;
        Check(recovery.TryTakeContinuation(out string prompt) && prompt.Contains("Nouveau problème"), "late previous result cannot clear current request recovery");
    }

    private static async Task<CodexToolRecoveryException> Error(Func<Task<object>> operation)
    {
        try { await operation(); }
        catch (CodexToolRecoveryException error) { return error; }
        throw new Exception("Expected structured tool recovery error.");
    }
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception("FAILED: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
