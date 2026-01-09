using System;

namespace BIMaestro.Welcome
{
    public class WelcomeState
    {
        public int SchemaVersion { get; set; } = 1;

        public bool WelcomeShown { get; set; } = false;
        public bool HardDismissed { get; set; } = false;

        public DateTime? FirstCommandUtc { get; set; } = null;
        public DateTime? SnoozeUntilUtc { get; set; } = null;
        public DateTime? LastAttemptUtc { get; set; } = null;

        public string InstallId { get; set; } = null;

        public bool EmailOptIn { get; set; } = false;
        public DateTime? OptInUtc { get; set; } = null;
        public string Email { get; set; } = null;
        public string FirstName { get; set; } = null;
        public string LastName { get; set; } = null;
        public bool ProfilePending { get; set; } = false;
    }
}
