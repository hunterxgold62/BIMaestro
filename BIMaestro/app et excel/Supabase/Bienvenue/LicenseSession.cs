using System;

namespace Licensing
{
    /// <summary>
    /// Stockage runtime des infos de licence (JWT) pour le plugin.
    /// A setter une fois que la licence est validée (au startup / premier besoin).
    /// </summary>
    public static class LicenseSession
    {
        private static readonly object _lock = new object();

        public static string CurrentLicenseKey { get; private set; }
        public static string CurrentJwt { get; private set; }
        public static DateTime? LastSetUtc { get; private set; }

        public static void Set(string licenseKey, string jwt)
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(licenseKey))
                    CurrentLicenseKey = licenseKey;

                if (!string.IsNullOrWhiteSpace(jwt))
                    CurrentJwt = jwt;

                LastSetUtc = DateTime.UtcNow;
            }
        }
    }
}
