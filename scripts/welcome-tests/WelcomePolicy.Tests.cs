using System;
using BIMaestro.Welcome;

internal static class Program
{
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static int Main()
    {
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        const string version = "test-version";
        try
        {
            var fresh = new WelcomeState();
            Check(WelcomePromptPolicy.ProfileDue(fresh, version, now), "Fresh install must start with profile.");
            foreach (var action in new[] { WelcomeResultAction.OptIn, WelcomeResultAction.Dismiss, WelcomeResultAction.Snooze })
            {
                var state = new WelcomeState { LastAttemptUtc = now };
                if (action == WelcomeResultAction.OptIn) { state.Email = "test@example.invalid"; state.FirstName = "Test"; state.LastName = "Profile"; }
                WelcomePromptPolicy.ProfileClosed(state, action, version, now);
                Check(!WelcomePromptPolicy.ProfileDue(state, version, now), "Profile repeated immediately after " + action);
                Check(WelcomePromptPolicy.TutorialDue(state, now) && WelcomePromptPolicy.NextAttempt(state, version, now) == now,
                    "Profile decision lost or postponed the model invitation: " + action);
                WelcomePromptPolicy.TutorialClosed(state, false, now);
                Check(!state.TutorialOfferCompleted && !WelcomePromptPolicy.TutorialDue(state, now.AddHours(23)), "Cross should postpone, not finish the invitation.");
                Check(WelcomePromptPolicy.TutorialDue(state, now.AddDays(1)), "Model invitation did not return next day.");
                Check(WelcomePromptPolicy.NextAttempt(state, version, now) == now.AddDays(1), "No next-day timer scheduled.");
                if (action == WelcomeResultAction.OptIn)
                    Check(state.Email == "test@example.invalid" && state.FirstName == "Test" && state.LastName == "Profile", "Tutorial close erased profile.");
                WelcomePromptPolicy.TutorialClosed(state, true, now.AddDays(1));
                Check(!WelcomePromptPolicy.TutorialDue(state, now.AddDays(10)), "Opened model is offered again.");
            }
            var known = new WelcomeState { Email = "known@example.invalid", FirstName = "Known", LastName = "User" };
            Check(WelcomePromptPolicy.ProfileDue(known, "new-version", now), "Existing users did not receive the company invitation.");
            known.HardDismissed = true;
            Check(WelcomePromptPolicy.ProfileDue(known, "new-version", now), "Old dismissal suppresses the new company invitation.");
            WelcomePromptPolicy.ProfileClosed(known, WelcomeResultAction.Dismiss, "new-version", now);
            Check(!WelcomePromptPolicy.ProfileDue(known, "another-version", now.AddDays(10)), "Optional company is requested repeatedly after dismissal.");
            Check(known.Email == "known@example.invalid" && known.FirstName == "Known" && known.LastName == "User", "Keeping existing information erased the profile.");
            Check(WelcomePromptPolicy.TutorialDue(known, now), "Known profile hides unfinished model invitation.");
            WelcomePromptPolicy.TutorialClosed(known, false, now); // failed model opening also retries next day
            Check(WelcomePromptPolicy.NextAttempt(known, version, now) == now.AddDays(1), "Failed opening lost its retry.");
            WelcomePromptPolicy.TutorialClosed(known, true, now);
            Check(WelcomePromptPolicy.NextAttempt(known, version, now) == null, "Completed onboarding still schedules prompts.");
            Console.WriteLine("PASS: profile first, all decisions continue to model, next-day retry, existing users receive company invitation once, dismissal preserves profile and stops repeat invitations.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
