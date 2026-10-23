using System;
using System.IO;

namespace Analyse
{
    // Corrupt/incompatible historical data must remain an explicit failure.
    // Only transport failures can become eligible again without new evidence.
    internal static class ElementHistoryRetryPolicy
    {
        internal static bool IsTransient(Exception error)
        {
            bool transport = false;
            for (int depth = 0; error != null && depth < 16; depth++, error = error.InnerException)
            {
                if (error is InvalidDataException) return false;
                if (error is IOException || error is TimeoutException) transport = true;
            }
            return transport;
        }

        internal static TimeSpan Delay(int failures)
            => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Max(0, Math.Min(6, failures - 1)))));
    }
}
