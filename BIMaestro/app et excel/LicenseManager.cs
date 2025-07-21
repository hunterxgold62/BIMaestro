using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Licensing
{
    /// <summary>
    /// Centralise tout ce qui concerne la licence :
    /// calcul du machine_id et validation “serveur‑first”.
    /// </summary>
    public static class LicenseManager
    {
        // URL de votre Edge Function “validate”
        private const string ValidateUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/validate";

        // Votre ANON key publique
        private const string ApiKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
          + "eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30."
          + "ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo";

        /// <summary>
        /// Appelle la function validate et jette si la licence n’est pas active.
        /// Retourne le JWT de licence.
        /// </summary>
        public static string Validate(string licenseKey, string machineId)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("apikey", ApiKey);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

            var payload = new
            {
                license_key = licenseKey,
                machine_id = machineId
            };

            // Appel synchrone (GetAwaiter...) pour simplifier l'appel depuis Revit
            var response = client
                .PostAsJsonAsync(ValidateUrl, payload)
                .GetAwaiter().GetResult();

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // 403 = expirée, révoquée ou machine non autorisée
                throw new InvalidOperationException("Licence expirée ou non active.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 404, 500 ou autre
                var err = response.Content
                                  .ReadAsStringAsync()
                                  .GetAwaiter().GetResult();
                throw new InvalidOperationException($"Erreur licence : {err}");
            }

            // 200 = OK, on lit le JSON { "token": "..." }
            var json = response.Content
                               .ReadFromJsonAsync<JsonElement>()
                               .GetAwaiter().GetResult();

            if (json.TryGetProperty("token", out var tokElem)
                && tokElem.ValueKind == JsonValueKind.String)
            {
                return tokElem.GetString()!;
            }
            throw new InvalidOperationException("Réponse invalide du serveur de licence.");
        }

        /// <summary>
        /// Calcule le machine_id comme SHA‑256(MachineName + MAC).
        /// </summary>
        public static string ComputeMachineId()
        {
            string name = Environment.MachineName;
            string mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .FirstOrDefault() ?? "";

            string raw = name + mac;
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }
    }
}
