using System;
using System.Threading;

namespace Analyse
{
    // Only retention/maintenance use this clock. Scheduling and event timestamps
    // keep wall time, so an isolated ageing test cannot invent historical events.
    internal static class ElementHistoryClock
    {
        private static long _testUtcTicks;
        internal static DateTime UtcNow
        {
            get
            {
                var ticks = Interlocked.Read(ref _testUtcTicks);
                return ticks == 0 ? DateTime.UtcNow : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        internal static void SetTestUtcNow(DateTime? utcNow)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1")
                throw new InvalidOperationException("L’horloge de validation est réservée au processus de test isolé.");
            if (utcNow.HasValue && utcNow.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Une date UTC explicite est requise.", nameof(utcNow));
            Interlocked.Exchange(ref _testUtcTicks, utcNow?.Ticks ?? 0);
        }
    }
}
