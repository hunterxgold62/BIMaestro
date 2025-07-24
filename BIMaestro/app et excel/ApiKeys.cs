using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Licensing
{
    public static class AiClient
    {
        private const string ProxyUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/ai-proxy";

        // Méthode générique pour OpenAI
        public static JsonDocument SendOpenAI(string jwtLicense, object openaiRequest)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new
            {
                provider = "openai",
                parameters = openaiRequest
            };

            var resp = client
                .PostAsJsonAsync(ProxyUrl, wrapper)
                .GetAwaiter().GetResult();

            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("Trop de requêtes, veuillez réessayer dans quelques instants.");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

            return JsonDocument.Parse(raw);
        }

        // Surcharge claire pour OpenAI : prend model + prompt
        public static JsonDocument SendOpenAI(string jwtLicense, string model, string prompt)
        {
            var openaiRequest = new
            {
                model,
                prompt
            };
            return SendOpenAI(jwtLicense, openaiRequest);
        }

        // >>> MISE À JOUR <<< : méthode DeepSeek corrigée
        public static JsonDocument SendDeepSeek(string jwtLicense, string query)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtLicense);

            // On n’envoie QUE `query` : c’est ce que ton edge-function attend
            var wrapper = new
            {
                provider = "deepseek",
                parameters = new
                {
                    query // correctement nommé
                }
            };

            var resp = client
                .PostAsJsonAsync(ProxyUrl, wrapper)
                .GetAwaiter().GetResult();

            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");
            return JsonDocument.Parse(raw);
        }
    }
}
