using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Licensing
{
    /// <summary>
    /// Validation de licence côté Edge Function Supabase + identifiants locaux robustes.
    /// </summary>
    public static class LicenseManager
    {
        private const string ValidateUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/validate";

        // ANON key publique (OK à embarquer côté client)
        private const string ApiKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
          + "eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30."
          + "ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo";

        private static readonly HttpClient _http;

        static LicenseManager()
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
                Timeout = TimeSpan.FromSeconds(8)
            };
            _http.DefaultRequestHeaders.Add("apikey", ApiKey);
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BIMaestro/LicenseClient");
        }

        /// <summary>
        /// Appelle la function validate. Retourne le JWT si OK.
        /// Retry simple avec backoff.
        /// </summary>
        public static string Validate(string licenseKey, string machineId, string userAgentExtra = null)
        {
            if (!string.IsNullOrWhiteSpace(userAgentExtra))
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgentExtra);

            var payload = new
            {
                license_key = licenseKey,
                machine_id = machineId
            };

            string jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // 2 tentatives
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var resp = _http.PostAsync(ValidateUrl, content).GetAwaiter().GetResult();
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new InvalidOperationException("Votre licence BIMaestro est expirée, révoquée ou non autorisée sur cette machine.");

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Erreur licence ({(int)resp.StatusCode}) : {body}");

                    var json = JObject.Parse(body);
                    var token = json["token"]?.ToString();
                    if (string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("Réponse invalide du serveur de licence (token manquant).");

                    return token;
                }
                catch (Exception ex) when (attempt == 1 && IsTransient(ex))
                {
                    // Backoff rapide
                    System.Threading.Thread.Sleep(400);
                    continue;
                }
            }

            // Si on est ici, dernière tentative a échoué avec exception non-transient
            throw new InvalidOperationException("Impossible de valider la licence (réseau/serveur).");
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is HttpRequestException || ex is TaskCanceledException || ex is TimeoutException;
        }

        /// <summary>
        /// Calcule un hash machine stable à partir de MachineName + MAC (hors loopback/virtual quand possible).
        /// </summary>
        public static string ComputeMachineId()
        {
            string name = Environment.MachineName;
            string mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic =>
                    nic.OperationalStatus == OperationalStatus.Up &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !nic.Description.ToLowerInvariant().Contains("virtual") &&
                    !nic.Description.ToLowerInvariant().Contains("vmware") &&
                    !nic.Description.ToLowerInvariant().Contains("hyper-v"))
                .Select(nic => nic.GetPhysicalAddress()?.ToString())
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;

            string raw = name + mac;
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// ID d’installation persistant (GUID stocké en local). Stable même si le MAC change.
        /// </summary>
        public static string GetOrCreateInstallId()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMaestro", "install_id.txt");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path))
                {
                    var txt = File.ReadAllText(path).Trim();
                    if (Guid.TryParse(txt, out var _)) return txt;
                }
                var id = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, id);
                return id;
            }
            catch
            {
                // Fallback en RAM si on ne peut pas écrire
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
