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
        /// Appelle OpenAI via ai-proxy (provider="openai").
        /// </summary>
        /// <param name="jwtLicense">JWT de licence obtenu via LicenseManager</param>
        /// <param name="model">Le modèle OpenAI (ex. "gpt-4o-mini")</param>
        /// <param name="prompt">Le prompt à envoyer</param>
        public static JsonDocument SendOpenAI(string jwtLicense, string model, string prompt)
        {
            var body = new
            {
                provider = "openai",
                parameters = new { model, prompt }
            };
            return SendRequest(jwtLicense, body);
        }

        /// <summary>
        /// Appelle DeepSeek via ai-proxy (provider="deepseek").
        /// </summary>
        /// <param name="jwtLicense">JWT de licence obtenu via LicenseManager</param>
        /// <param name="query">La requête DeepSeek</param>
        public static JsonDocument SendDeepSeek(string jwtLicense, string query)
        {
            var body = new
            {
                provider = "deepseek",
                parameters = new { query }
            };
            return SendRequest(jwtLicense, body);
        }

        /// <summary>
        /// Méthode interne qui envoie la requête JSON à l’edge-function et retourne le JSON brut.
        /// </summary>
        private static JsonDocument SendRequest(string jwtLicense, object payload)
        {
            using var client = new HttpClient();
            // Passe le JWT de licence en header
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtLicense);

            // Envoie synchrone pour s’intégrer dans le plugin Revit
            var resp = client.PostAsJsonAsync(ProxyUrl, payload)
                             .GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException($"AI proxy error ({resp.StatusCode}): {err}");
            }

            // Lit la réponse JSON et la parse
            var jsonString = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonDocument.Parse(jsonString);
        }
    }
}
