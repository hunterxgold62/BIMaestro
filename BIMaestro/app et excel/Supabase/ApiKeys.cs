// AiClient.cs
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Licensing
{
    public static class AiClient
    {
        private const string ProxyUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/ai-proxy";
        public const string QuotaExceededMessage =
       "Eh ben bravo, petit gourmand ! T'as tout englouti : les 100 000 tokens y sont passés ! 🍽️\n" +
"Va falloir appeler l’administrateur pour en reprendre une part. 😅";

        /// <summary>
        /// Méthode générique pour appeler le proxy Supabase.
        /// </summary>
        public static JObject SendOpenAI(string jwtLicense, object openaiRequest)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtLicense);

            // On wrappe le provider + paramètres
            var wrapper = new
            {
                provider = "openai",
                parameters = openaiRequest
            };

            string jsonBody = JsonConvert.SerializeObject(wrapper);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var resp = client.PostAsync(ProxyUrl, content).GetAwaiter().GetResult();
            string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if ((int)resp.StatusCode == 403)
                // on force le message standardisé
                throw new InvalidOperationException(QuotaExceededMessage);

            // Gestion des erreurs
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("Trop de requêtes, veuillez réessayer plus tard.");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

            return JObject.Parse(raw);
        }

        /// <summary>
        /// Appel OpenAI simple, par défaut 1 complétion.
        /// </summary>
        public static JObject SendOpenAI(string jwtLicense, string model, string prompt)
        {
            // Garde la surcharge existante pour rétro‑compatibilité
            return SendOpenAI(jwtLicense, new
            {
                model,
                prompt
            });
        }

        /// <summary>
        /// Appel OpenAI en spécifiant le nombre de complétions (n).
        /// </summary>
        public static JObject SendOpenAI(string jwtLicense, string model, string prompt, int n)
        {
            var openaiRequest = new
            {
                model,
                prompt,
                n
            };
            return SendOpenAI(jwtLicense, openaiRequest);
        }

        /// <summary>
        /// Appel DeepSeek (inchangé).
        /// </summary>
        public static JObject SendDeepSeek(string jwtLicense, string query)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new
            {
                provider = "deepseek",
                parameters = new { query }
            };

            string jsonBody = JsonConvert.SerializeObject(wrapper);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var resp = client.PostAsync(ProxyUrl, content).GetAwaiter().GetResult();
            string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

            return JObject.Parse(raw);
        }
    }
}
