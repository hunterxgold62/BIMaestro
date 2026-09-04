using Newtonsoft.Json.Linq;
using System;

namespace Licensing
{
    internal static class ProfilePayload
    {
        internal static JObject Create(
            string installId,
            string email,
            string firstName,
            string lastName,
            string machineIdHash,
            string pluginLanguage)
        {
            var body = new JObject
            {
                ["install_id"] = installId,
                ["email"] = email,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["machine_id_hash"] = machineIdHash
            };

            string normalizedLanguage = NormalizeLanguage(pluginLanguage);
            if (normalizedLanguage != null)
                body["plugin_language"] = normalizedLanguage;

            return body;
        }

        internal static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return null;

            string normalized = language.Trim().Replace('_', '-');
            int separatorIndex = normalized.IndexOf('-');
            string primaryLanguage = separatorIndex >= 0
                ? normalized.Substring(0, separatorIndex)
                : normalized;

            if (string.Equals(primaryLanguage, "fr", StringComparison.OrdinalIgnoreCase))
                return "fr";
            if (string.Equals(primaryLanguage, "en", StringComparison.OrdinalIgnoreCase))
                return "en";

            return null;
        }
    }
}
