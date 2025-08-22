using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Licensing
{
    public static class AiClient
    {
        private const string ProxyUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/ai-proxy";

        public const string QuotaExceededMessage =
            "Eh ben bravo, petit gourmand ! T'as tout englouti : les 100 000 tokens y sont passés ! 🍽️\n" +
            "Va falloir appeler l’administrateur pour en reprendre une part. 😅";

        private static readonly HttpClient _http;

        static AiClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                UseDefaultCredentials = true
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BIMaestro/AiProxyClient");
        }

        private static JObject PostWithRetry(HttpRequestMessage req)
        {
            // 3 tentatives avec backoff simple sur 429/timeout/transient
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                    string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if ((int)resp.StatusCode == 403)
                        throw new InvalidOperationException(QuotaExceededMessage);

                    if ((int)resp.StatusCode == 429)
                        throw new InvalidOperationException("Trop de requêtes, réessayez dans un instant.");

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"AI proxy error ({(int)resp.StatusCode}): {raw}");

                    return JObject.Parse(raw);
                }
                catch (Exception ex) when (attempt < 3 && IsTransient(ex))
                {
                    Thread.Sleep(300 * attempt);
                    continue;
                }
            }
            throw new InvalidOperationException("Échec de l’appel AI (réseau/serveur).");
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is HttpRequestException || ex is TaskCanceledException || ex is TimeoutException;
        }

        /// <summary>Méthode générique pour appeler le proxy Supabase.</summary>
        public static JObject SendOpenAI(string jwtLicense, object openaiRequest)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ProxyUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new
            {
                provider = "openai",
                parameters = openaiRequest
            };

            string jsonBody = JsonConvert.SerializeObject(wrapper, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            return PostWithRetry(req);
        }

        public static JObject SendOpenAI(string jwtLicense, string model, string prompt)
            => SendOpenAI(jwtLicense, new { model, prompt });

        public static JObject SendOpenAI(string jwtLicense, string model, string prompt, int n)
            => SendOpenAI(jwtLicense, new { model, prompt, n });

        public static JObject SendDeepSeek(string jwtLicense, string query)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ProxyUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtLicense);

            var wrapper = new
            {
                provider = "deepseek",
                parameters = new { query }
            };

            string jsonBody = JsonConvert.SerializeObject(wrapper, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            return PostWithRetry(req);
        }
    }
}
