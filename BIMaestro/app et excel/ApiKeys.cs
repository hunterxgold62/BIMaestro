using System;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Licensing
{
    public static class AiClient
    {
        private const string ProxyUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/ai-proxy";

        // Méthode générique pour OpenAI
        public static JObject SendOpenAI(string jwtLicense, object openaiRequest)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new
            {
                provider = "openai",
                parameters = openaiRequest
            };

            string jsonBody = JsonConvert.SerializeObject(wrapper);
            var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            var resp = client
                .PostAsync(ProxyUrl, content)
                .GetAwaiter().GetResult();

            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("Trop de requêtes, veuillez réessayer dans quelques instants.");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

            return JObject.Parse(raw);
        }

        // Surcharge claire pour OpenAI : prend model + prompt
        public static JObject SendOpenAI(string jwtLicense, string model, string prompt)
        {
            var openaiRequest = new
            {
                model,
                prompt
            };
            return SendOpenAI(jwtLicense, openaiRequest);
        }

        // >>> MISE À JOUR <<< : méthode DeepSeek corrigée
        public static JObject SendDeepSeek(string jwtLicense, string query)
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

            string jsonBody = JsonConvert.SerializeObject(wrapper);
            var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            var resp = client
                .PostAsync(ProxyUrl, content)
                .GetAwaiter().GetResult();

            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");
            return JObject.Parse(raw);
        }
    }
}