using System;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Licensing
{
    /// <summary>HttpClient “proxy-friendly” à utiliser partout.</summary>
    public static class NetSupport
    {
        private const string ValidateUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/validate";

        private const string ApiKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
          + "eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30."
          + "ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo";

        public static string ComputeMachineId()
        {
            string name = Environment.MachineName;
            string mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault() ?? "";

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(name + mac));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }

        public static string ValidateLicense(string licenseKey, string machineId, string userAgent = null)
        {
            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(15));
            if (!string.IsNullOrWhiteSpace(userAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            // Cette fonction amorce la licence avant qu'un JWT de licence existe.
            // L'API key reste dans "apikey" ; "Authorization" est réservé au JWT
            // de licence utilisé par les autres Edge Functions.
            client.DefaultRequestHeaders.Add("apikey", ApiKey);

            var body = new { license_key = licenseKey, machine_id = machineId };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = client.PostAsync(ValidateUrl, content).GetAwaiter().GetResult();

            if (resp.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                throw new HttpRequestException("Proxy requiert une authentification (407).");

            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Votre licence BIMaestro est expirée ou inactive.");

            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                // Une erreur de passerelle ou de serveur ne doit pas bloquer Revit
                // si un jeton local non expiré existe. Les refus métier (400/403)
                // restent bloquants et ne contournent jamais l'état de la licence.
                int statusCode = (int)resp.StatusCode;
                if (resp.StatusCode == HttpStatusCode.Unauthorized ||
                    resp.StatusCode == HttpStatusCode.RequestTimeout ||
                    statusCode == 429 ||
                    statusCode >= 500)
                {
                    throw new HttpRequestException(
                        $"Serveur de licence temporairement indisponible (HTTP {statusCode}) : {err}");
                }

                throw new InvalidOperationException($"Erreur licence : {err}");
            }

            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var token = JObject.Parse(raw)["token"]?.ToString();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Réponse invalide du serveur de licence.");

            return token!;
        }

        public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                UseDefaultCredentials = true,
                PreAuthenticate = true
            };

            // certains proxys ignorent UseDefaultCredentials
            if (handler.Proxy != null && handler.Proxy.Credentials == null)
                handler.Proxy.Credentials = CredentialCache.DefaultCredentials;

            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(15)
            };

            client.DefaultRequestHeaders.ExpectContinue = false;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BIMaestro/1.0 (+Revit)");
            return client;
        }
    }
}
