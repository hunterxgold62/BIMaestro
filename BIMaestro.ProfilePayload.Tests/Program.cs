using Newtonsoft.Json.Linq;
using System;

namespace Licensing.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                AssertLanguage("fr", "fr");
                AssertLanguage("fr-FR", "fr");
                AssertLanguage("fr_FR", "fr");
                AssertLanguage("en", "en");
                AssertLanguage("en-US", "en");
                AssertLanguage("en_GB", "en");
                AssertLanguageOmitted(null);
                AssertLanguageOmitted("");
                AssertLanguageOmitted("de-DE");
                Console.WriteLine("Profile payload tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        private static void AssertLanguage(string input, string expected)
        {
            JObject payload = CreatePayload(input);
            Assert((string)payload["plugin_language"] == expected,
                $"Expected '{input}' to produce '{expected}'.");
            Assert((string)payload["install_id"] == "install-123",
                "Existing profile fields must remain present.");
        }

        private static void AssertLanguageOmitted(string input)
        {
            JObject payload = CreatePayload(input);
            Assert(payload.Property("plugin_language") == null,
                $"Expected '{input ?? "null"}' to omit plugin_language.");
        }

        private static JObject CreatePayload(string language)
        {
            return ProfilePayload.Create(
                "install-123", "user@example.com", "Ada", "Lovelace", "hash", language);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
