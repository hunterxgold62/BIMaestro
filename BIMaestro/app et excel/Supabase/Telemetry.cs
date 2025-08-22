// BIMaestro/Analytics/Telemetry.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Licensing
{
    internal class UsageEvent
    {
        public string button_id { get; set; }
        public bool success { get; set; }
        public string plugin_version { get; set; }
        public string machine_id_hash { get; set; }
        public object context { get; set; }
        public DateTime created_at { get; set; }
        public string license_key { get; set; } // facultatif
    }

    /// <summary>
    /// Buffer + envoi batch async vers l'Edge Function collect-usage.
    /// Logs -> Mes Documents\RevitLogs\telemetry.log
    /// Fallback d'URL: /functions/v1/collect-usage -> /collect-usage
    /// </summary>
    public static class Telemetry
    {
        private static readonly object _lock = new object();
        private static readonly List<UsageEvent> _buffer = new List<UsageEvent>(64);
        private static Timer _timer;
        private static volatile bool _flushing = false;

        private static Uri _endpointBase; // ex: https://<proj>.functions.supabase.co/
        private static readonly string[] _paths = new[]
        {
            "/functions/v1/collect-usage", // standard Supabase
            "/collect-usage"               // fallback si exposée à la racine
        };
        private static int _activePathIndex = 0;

        private static string _licenseJwt;         // JWT (validate)
        private static string _pluginVersion;      // jamais null après Init
        private static string _machineIdHash;      // jamais null après Init
        private static string _fallbackLicenseKey; // transmis dans le body si le JWT ne la porte pas

        private static readonly HttpClient _http;

        private static string _queueFile;
        private static string _logFile;
        private static bool _initialized;

        /// <summary>DEBUG: flush immédiat à chaque TrackButton (désactive-le en prod).</summary>
        public static bool FlushImmediatelyForDebug { get; set; } = false;

        static Telemetry()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                UseDefaultCredentials = true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <param name="edgeFunctionsBaseUrl">ex: https://xqovxfgghbqxwsadzhzl.functions.supabase.co</param>
        /// <param name="licenseJwt">JWT signé par validate (HS256)</param>
        /// <param name="pluginVersion">version plugin</param>
        /// <param name="machineIdHash">hash machine</param>
        /// <param name="fallbackLicenseKey">clé licence à mettre dans le body si absente du JWT</param>
        public static void Init(
            string edgeFunctionsBaseUrl,
            string licenseJwt,
            string pluginVersion,
            string machineIdHash,
            string fallbackLicenseKey = null)
        {
            if (string.IsNullOrWhiteSpace(edgeFunctionsBaseUrl)) throw new ArgumentNullException(nameof(edgeFunctionsBaseUrl));
            if (string.IsNullOrWhiteSpace(licenseJwt)) throw new ArgumentNullException(nameof(licenseJwt));

            var baseUrl = edgeFunctionsBaseUrl.TrimEnd('/') + "/";
            _endpointBase = new Uri(baseUrl);
            _activePathIndex = 0;

            // valeurs durcies (jamais null)
            _licenseJwt = licenseJwt;
            _pluginVersion = string.IsNullOrWhiteSpace(pluginVersion) ? "dev" : pluginVersion.Trim();
            _machineIdHash = string.IsNullOrWhiteSpace(machineIdHash) ? "unknown" : machineIdHash.Trim();
            _fallbackLicenseKey = string.IsNullOrWhiteSpace(fallbackLicenseKey) ? null : fallbackLicenseKey.Trim();

            // Chemins logs & file-queue
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var logDir = Path.Combine(docs, "RevitLogs");
            Directory.CreateDirectory(logDir);
            _logFile = Path.Combine(logDir, "telemetry.log");
            _queueFile = Path.Combine(logDir, "telemetry_queue.json");

            WriteLog($"[Init] base={_endpointBase} version={_pluginVersion}");
            TryRestoreQueueFromDisk();

            _timer?.Dispose();
            _timer = new Timer(async _ => await SafeFlushAsync().ConfigureAwait(false),
                null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

            _initialized = true;
        }

        public static void TrackButton(string buttonId, bool success, object context = null)
        {
            if (!_initialized || _endpointBase == null || string.IsNullOrEmpty(_licenseJwt))
            {
                WriteLog("[TrackButton] Telemetry not initialized. Event dropped.");
                return;
            }
            if (string.IsNullOrWhiteSpace(buttonId))
            {
                WriteLog("[TrackButton] Empty buttonId.");
                return;
            }

            var evt = new UsageEvent
            {
                button_id = buttonId,
                success = success,
                plugin_version = string.IsNullOrWhiteSpace(_pluginVersion) ? "dev" : _pluginVersion,
                machine_id_hash = string.IsNullOrWhiteSpace(_machineIdHash) ? "unknown" : _machineIdHash,
                context = context ?? new { }, // jamais null
                created_at = DateTime.UtcNow,
                license_key = _fallbackLicenseKey
            };

            lock (_lock)
            {
                _buffer.Add(evt);
                WriteLog($"[TrackButton] queued '{buttonId}', buffer={_buffer.Count}");
            }

            if (FlushImmediatelyForDebug)
            {
                try { SafeFlushAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { WriteLog("[TrackButton/ImmediateFlush] " + ex.Message); }
            }
        }

        public static Task FlushAsync() => SafeFlushAsync();

        /// <summary>Forcer un flush synchrone (utile pour un bouton de test).</summary>
        public static bool ForceFlushSync()
        {
            try { SafeFlushAsync().GetAwaiter().GetResult(); return true; }
            catch (Exception ex) { WriteLog("[ForceFlushSync] " + ex.Message); return false; }
        }

        public static void Shutdown()
        {
            try { _timer?.Dispose(); } catch { }
            WriteLog("[Shutdown]");
            _initialized = false;
        }

        private static async Task SafeFlushAsync()
        {
            if (_flushing) return;
            List<UsageEvent> toSend;

            lock (_lock)
            {
                if (_buffer.Count == 0) return;
                toSend = new List<UsageEvent>(_buffer);
                _buffer.Clear();
            }

            _flushing = true;
            try
            {
                var payload = new { license_key = _fallbackLicenseKey, events = toSend };
                var json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });

                for (int attempt = 0; attempt < _paths.Length; attempt++)
                {
                    var pathIndex = (_activePathIndex + attempt) % _paths.Length;
                    var endpoint = new Uri(_endpointBase, _paths[pathIndex]);

                    var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    req.Headers.Add("Authorization", $"Bearer {_licenseJwt}");
                    // Optionnel : utile si ton Edge réutilise le header apikey
                    // req.Headers.Add("apikey", "<your_anon_key_if_needed>");

                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    WriteLog($"[Flush] POST {endpoint} batch={toSend.Count}");
                    HttpResponseMessage resp = null;
                    try
                    {
                        resp = await _http.SendAsync(req).ConfigureAwait(false);
                        var code = (int)resp.StatusCode;
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (code == 404 || code == 405)
                        {
                            WriteLog($"[Flush] HTTP {code} (bad path), trying fallback…");
                            continue; // essaie l’autre chemin
                        }

                        if (!resp.IsSuccessStatusCode)
                        {
                            WriteLog($"[Flush] HTTP {code} - {body}");
                            PersistQueueToDisk(toSend);
                        }
                        else
                        {
                            WriteLog($"[Flush] HTTP {code} OK (pathIndex={pathIndex})");
                            _activePathIndex = pathIndex;
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        WriteLog("[Flush] EX: " + ex.Message);
                        PersistQueueToDisk(toSend);
                        break;
                    }
                    finally
                    {
                        resp?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("[Flush/Outer] EX: " + ex.Message);
                PersistQueueToDisk(toSend);
            }
            finally
            {
                _flushing = false;
            }
        }

        private static void PersistQueueToDisk(List<UsageEvent> events)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_queueFile))
                {
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var logDir = Path.Combine(docs, "RevitLogs");
                    Directory.CreateDirectory(logDir);
                    _queueFile = Path.Combine(logDir, "telemetry_queue.json");
                }

                List<UsageEvent> disk = new List<UsageEvent>();
                if (File.Exists(_queueFile))
                {
                    var existing = File.ReadAllText(_queueFile);
                    if (!string.IsNullOrWhiteSpace(existing))
                        disk = JsonConvert.DeserializeObject<List<UsageEvent>>(existing) ?? new List<UsageEvent>();
                }
                disk.AddRange(events);
                File.WriteAllText(_queueFile, JsonConvert.SerializeObject(disk, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                }));
                WriteLog($"[Persist] queued to disk, total now={disk.Count}");
            }
            catch (Exception ex)
            {
                WriteLog("[Persist] EX: " + ex.Message);
            }
        }

        private static void TryRestoreQueueFromDisk()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_queueFile))
                {
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var logDir = Path.Combine(docs, "RevitLogs");
                    Directory.CreateDirectory(logDir);
                    _queueFile = Path.Combine(logDir, "telemetry_queue.json");
                }

                if (!File.Exists(_queueFile)) return;
                var txt = File.ReadAllText(_queueFile);
                if (string.IsNullOrWhiteSpace(txt)) return;
                var disk = JsonConvert.DeserializeObject<List<UsageEvent>>(txt);
                if (disk == null || disk.Count == 0) return;

                lock (_lock) { _buffer.AddRange(disk); }
                File.Delete(_queueFile);
                WriteLog($"[Restore] restored {disk.Count} events from disk");
            }
            catch (Exception ex)
            {
                WriteLog("[Restore] EX: " + ex.Message);
            }
        }

        private static void WriteLog(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(_logFile))
                {
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var logDir = Path.Combine(docs, "RevitLogs");
                    Directory.CreateDirectory(logDir);
                    _logFile = Path.Combine(logDir, "telemetry.log");
                }
                File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
            }
            catch { /* ignore */ }
        }

        // ------- Utilitaires conseillés --------

        /// <summary>Version lisible et stable depuis l'assembly.</summary>
        public static string GetAssemblyVersionSafe(Assembly asm = null)
        {
            try
            {
                asm ??= Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;
                var fvi = FileVersionInfo.GetVersionInfo(asm.Location)?.ProductVersion;
                if (!string.IsNullOrWhiteSpace(fvi)) return fvi;
                var v = asm.GetName().Version?.ToString();
                return string.IsNullOrWhiteSpace(v) ? "dev" : v;
            }
            catch
            {
                return "dev";
            }
        }
    }
}
