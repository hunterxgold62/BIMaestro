using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Licensing
{
    /// <summary>
    /// Validation licence + cache hors-ligne (fichier) + machine_id.
    /// Cache : Mes Documents\RevitLogs\License\token_{licence}.json
    /// </summary>
    public static class LicenseManager
    {
        // Edge Function “validate”
        private const string ValidateUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/validate";

        // Edge Function “upsert-profile”
        private const string UpsertProfileUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/upsert-profile";

        // Supabase ANON key (publique)
        private const string ApiKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
          + "eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30."
          + "ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo";

        private static string CacheFile(string licenseKey)
            => Path.Combine(Paths.LicenseDir, $"token_{Sanitize(licenseKey)}.json");

        private static string InstallIdFile
            => Path.Combine(Paths.LicenseDir, "install_id.txt");

        private static string Sanitize(string s)
            => new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());

        /// <summary>SHA-256(MachineName + MAC)</summary>
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

        /// <summary>
        /// ID d’installation persistant (GUID) : RevitLogs\License\install_id.txt
        /// </summary>
        public static string GetOrCreateInstallId()
        {
            Directory.CreateDirectory(Paths.LicenseDir);
            if (File.Exists(InstallIdFile))
            {
                try
                {
                    var t = File.ReadAllText(InstallIdFile).Trim();
                    if (!string.IsNullOrEmpty(t)) return t;
                }
                catch { /* ignore */ }
            }
            var id = Guid.NewGuid().ToString("N");
            try { File.WriteAllText(InstallIdFile, id); } catch { /* ignore */ }
            return id;
        }

        /// <summary>
        /// Réseau-seulement. Surcharge compatible avec ton ancien code.
        /// </summary>
        public static string Validate(string licenseKey, string machineId, string userAgent = null)
        {
            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(15));
            if (!string.IsNullOrWhiteSpace(userAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            client.DefaultRequestHeaders.Add("apikey", ApiKey);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

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
                throw new InvalidOperationException($"Erreur licence : {err}");
            }

            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var token = JObject.Parse(raw)["token"]?.ToString();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Réponse invalide du serveur de licence.");

            return token!;
        }

        /// <summary>
        /// Essaie online. Si réseau/proxy KO, utilise un jeton en cache
        /// encore valide pour cette machine. Renvoie le JWT. Indique si cache.
        /// </summary>
        public static string ValidateOrUseCache(string licenseKey, string machineId, out bool fromCache, string userAgent = null)
        {
            Directory.CreateDirectory(Paths.LicenseDir);

            Exception netErr = null;
            try
            {
                string jwt = Validate(licenseKey, machineId, userAgent);
                SaveToken(licenseKey, jwt);
                fromCache = false;
                return jwt;
            }
            catch (HttpRequestException ex) { netErr = ex; }
            catch (WebException ex) { netErr = ex; }

            // réseau KO -> tente le cache
            string cached = LoadTokenIfValid(licenseKey, machineId);
            if (cached != null) { fromCache = true; return cached; }

            fromCache = false;
            var msg = "Impossible de valider la licence et aucun jeton valide en cache."
                    + (netErr != null ? $" Détail réseau : {netErr.Message}" : "");
            throw new InvalidOperationException(msg, netErr);
        }

        /// <summary>
        /// Envoie (opt-in) email/prénom/nom pour cette licence.
        /// Authentification: Authorization: Bearer &lt;jwt_licence&gt;
        /// </summary>
        public static void UpsertUserProfile(
            string jwtLicenseToken,
            string installId,
            string email,
            string firstName,
            string lastName,
            string machineIdHash = null)
        {
            if (string.IsNullOrWhiteSpace(jwtLicenseToken))
                throw new InvalidOperationException("JWT licence manquant (impossible de sync le profil).");

            if (string.IsNullOrWhiteSpace(installId))
                installId = GetOrCreateInstallId();

            if (string.IsNullOrWhiteSpace(email))
                throw new InvalidOperationException("Email manquant.");

            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(15));

            // IMPORTANT : ici, PAS ApiKey. L’Edge Function attend le JWT licence.
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwtLicenseToken}");

            var body = new
            {
                install_id = installId,
                email = email,
                first_name = firstName,
                last_name = lastName,
                machine_id_hash = machineIdHash
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = client.PostAsync(UpsertProfileUrl, content).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException($"Erreur sync profil : {err}");
            }
        }

        /// <summary>
        /// Variante safe: n’explose jamais l’UX si réseau KO.
        /// </summary>
        public static void TryUpsertUserProfileNoThrow(
            string jwtLicenseToken,
            string installId,
            string email,
            string firstName,
            string lastName,
            string machineIdHash = null)
        {
            try
            {
                UpsertUserProfile(jwtLicenseToken, installId, email, firstName, lastName, machineIdHash);
            }
            catch
            {
                // ignore volontairement
            }
        }

        private static void SaveToken(string licenseKey, string token)
        {
            try
            {
                var path = CacheFile(licenseKey);
                File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(new { token }));
            }
            catch { /* ignore */ }
        }

        private static string LoadTokenIfValid(string licenseKey, string machineId)
        {
            var path = CacheFile(licenseKey);
            if (!File.Exists(path)) return null;

            try
            {
                var raw = File.ReadAllText(path);
                var tok = JObject.Parse(raw)["token"]?.ToString();
                if (string.IsNullOrEmpty(tok)) return null;

                var parts = tok.Split('.');
                if (parts.Length != 3) return null;
                var payload = JObject.Parse(DecodeBase64Url(parts[1]));

                long exp = payload.Value<long?>("exp") ?? 0;
                string mid = payload.Value<string>("machine_id") ?? "";
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (now >= exp) return null; // expiré
                if (!string.Equals(mid, machineId, StringComparison.OrdinalIgnoreCase)) return null;

                return tok;
            }
            catch { return null; }

            static string DecodeBase64Url(string s)
            {
                s = s.Replace('-', '+').Replace('_', '/');
                s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
        }
    }
}
