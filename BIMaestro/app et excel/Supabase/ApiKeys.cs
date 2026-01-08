using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Licensing
{
    public static class AiClient
    {
        private const string ProxyUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/ai-proxy";

        public const string QuotaExceededMessage =
            "Quota IA dépassé. Contactez l’administrateur pour augmenter votre limite.";

        /// <summary>Appel générique via le proxy (OpenAI par défaut).</summary>
        public static JObject SendOpenAI(string jwtLicense, object openaiRequest)
        {
            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(60));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new { provider = "openai", parameters = openaiRequest };
            var jsonBody = JsonConvert.SerializeObject(wrapper);

            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = client.PostAsync(ProxyUrl, content).GetAwaiter().GetResult();
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException("La requête IA a expiré (délai de 60 s dépassé). Veuillez réessayer ou reformuler votre demande.");
            }
            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if ((int)resp.StatusCode == 403)
                throw new InvalidOperationException(QuotaExceededMessage);
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("Trop de requêtes, veuillez réessayer.");

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

            return JObject.Parse(raw);
        }

        /// <summary>Compat: modèle + prompt (n=1).</summary>
        public static JObject SendOpenAI(string jwtLicense, string model, string prompt)
            => SendOpenAI(jwtLicense, new { model, prompt });

        /// <summary>Compat: modèle + prompt + n complétions.</summary>
        public static JObject SendOpenAI(string jwtLicense, string model, string prompt, int n)
            => SendOpenAI(jwtLicense, new { model, prompt, n });

        /// <summary>DeepSeek (si tu l’utilises encore).</summary>
        public static JObject SendDeepSeek(string jwtLicense, string query)
        {
            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(60));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new { provider = "deepseek", parameters = new { query } };
            var jsonBody = JsonConvert.SerializeObject(wrapper);

            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = client.PostAsync(ProxyUrl, content).GetAwaiter().GetResult();
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException("La requête IA a expiré (délai de 60 s dépassé). Veuillez réessayer ou reformuler votre demande.");
            }
            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

            return JObject.Parse(raw);
        }
    }
}