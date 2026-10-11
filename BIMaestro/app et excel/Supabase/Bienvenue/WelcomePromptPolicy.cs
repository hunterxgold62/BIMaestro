using System;

namespace BIMaestro.Welcome
{
    internal static class WelcomePromptPolicy
    {
        internal const string ProfilePromptRevision = "company-2026-10";
        private static bool ProfilePending(WelcomeState state, string version) => state != null &&
            (!string.Equals(state.LastProfilePromptRevision, ProfilePromptRevision, StringComparison.Ordinal) ||
             string.IsNullOrWhiteSpace(state.Email) && !state.HardDismissed &&
             !string.Equals(state.LastWelcomePromptVersion?.Trim(), version?.Trim(), StringComparison.OrdinalIgnoreCase));

        internal static bool ProfileDue(WelcomeState state, string version, DateTime now) => ProfilePending(state, version) &&
            (!state.SnoozeUntilUtc.HasValue || state.SnoozeUntilUtc <= now) &&
            (!state.LastAttemptUtc.HasValue || state.LastAttemptUtc.Value.AddHours(6) <= now);

        internal static bool TutorialDue(WelcomeState state, DateTime now) => state != null && !state.TutorialOfferCompleted &&
            (!state.TutorialSnoozeUntilUtc.HasValue || state.TutorialSnoozeUntilUtc <= now);

        internal static DateTime? NextAttempt(WelcomeState state, string version, DateTime now)
        {
            DateTime? next = null;
            if (ProfilePending(state, version))
            {
                next = state.SnoozeUntilUtc > now ? state.SnoozeUntilUtc : now;
                if (state.LastAttemptUtc.HasValue && state.LastAttemptUtc.Value.AddHours(6) > next)
                    next = state.LastAttemptUtc.Value.AddHours(6);
            }
            if (state != null && !state.TutorialOfferCompleted)
            {
                DateTime tutorial = state.TutorialSnoozeUntilUtc > now ? state.TutorialSnoozeUntilUtc.Value : now;
                if (!next.HasValue || tutorial < next) next = tutorial;
            }
            return next;
        }

        internal static void ProfileClosed(WelcomeState state, WelcomeResultAction action, string version, DateTime now)
        {
            if (action == WelcomeResultAction.OptIn)
            {
                state.EmailOptIn = true; state.OptInUtc = now; state.ProfilePending = true;
                state.WelcomeShown = true; state.LastWelcomePromptVersion = version;
                state.LastProfilePromptRevision = ProfilePromptRevision;
                state.HardDismissed = false;
            }
            else if (action == WelcomeResultAction.Dismiss)
            { state.HardDismissed = true; state.LastWelcomePromptVersion = version; state.LastProfilePromptRevision = ProfilePromptRevision; }
            else state.SnoozeUntilUtc = now.AddDays(1);
            // A profile decision does not postpone or complete the separate model invitation.
        }

        internal static void TutorialClosed(WelcomeState state, bool opened, DateTime now)
        {
            state.TutorialOfferCompleted = opened;
            state.TutorialSnoozeUntilUtc = opened ? (DateTime?)null : now.AddDays(1);
        }
    }
}
