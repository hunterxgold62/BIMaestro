using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Licensing
{
    public static class AiClient
    {
        // L’URL de ta function ai‑proxy
        private const string ProxyUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/ai-proxy";

        /// <summary>
        /// Envoie un prompt à l’Edge Function, qui appelle OpenAI et logge l’usage.
        /// </summary>
        /// <param name="jwtLicense">Le JWT renvoyé par LicenseManager.Validate()</param>
        /// <param name="model">Le modèle à utiliser (ex : "gpt-4o-mini")</param>
        /// <param name="prompt">Le texte de la requête</param>
        /// <returns>La réponse JSON brute d’OpenAI (usage + choix)</returns>
        public static JsonDocument SendChat(string jwtLicense, string model, string prompt)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwtLicense}");
            var payload = new { model, prompt };
            // PostAsJsonAsync nécessite System.Net.Http.Json
            var resp = client.PostAsJsonAsync(ProxyUrl, payload)
                             .GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException($"AI error: {err}");
            }

            var json = JsonDocument.Parse(
                resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            );
            return json;
        }
    }
}
