using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        public string license_key { get; set; } // fallback si JWT ne la porte pas
    }

    /// <summary>Buffer + envoi batch async vers collect-usage (proxy OK) + queue disque.</summary>
    public static class Telemetry
    {
        private static readonly object _lock = new object();
        private static readonly List<UsageEvent> _buffer = new List<UsageEvent>(64);
        private static Timer _timer;
        private static volatile bool _flushing;

        private static Uri _endpointBase; // ex: https://...functions.supabase.co/
        private static readonly string[] _paths = new[] { "/functions/v1/collect-usage", "/collect-usage" };
        private static int _activePathIndex;

        private static string _licenseJwt;
        private static string _pluginVersion;
        private static string _machineIdHash;
        private static string _fallbackLicenseKey;

        private static HttpClient _http;

        private static string _queueFile;
        private static string _logFile;

        public static bool FlushImmediatelyForDebug { get; set; } = false;

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

            _licenseJwt = licenseJwt;
            _pluginVersion = string.IsNullOrWhiteSpace(pluginVersion) ? "dev" : pluginVersion;
            _machineIdHash = machineIdHash;
            _fallbackLicenseKey = fallbackLicenseKey;

            var logDir = Paths.RevitLogsDir;
            _logFile = Path.Combine(logDir, "telemetry.log");
            _queueFile = Path.Combine(logDir, "telemetry.queue.json");

            _http = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(10));
            TryRestoreQueueFromDisk();
            WriteLog($"[Init] base={_endpointBase} version={_pluginVersion}");

            _timer = new Timer(async _ => await SafeFlushAsync().ConfigureAwait(false),
                null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        public static void TrackButton(string buttonId, bool success, object context = null)
        {
            try
            {
                if (_endpointBase == null || string.IsNullOrEmpty(_licenseJwt)) { WriteLog("[TrackButton] Telemetry not initialized."); return; }
                if (string.IsNullOrWhiteSpace(buttonId)) { WriteLog("[TrackButton] Empty buttonId."); return; }

                var evt = new UsageEvent
                {
                    button_id = buttonId,
                    success = success,
                    plugin_version = _pluginVersion,
                    machine_id_hash = _machineIdHash,
                    context = context ?? new { },
                    created_at = DateTime.UtcNow,
                    license_key = _fallbackLicenseKey
                };

                lock (_lock) { _buffer.Add(evt); }
                WriteLog($"[TrackButton] queued '{buttonId}', buffer={_buffer.Count}");

                if (FlushImmediatelyForDebug)
                    SafeFlushAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) { WriteLog("[TrackButton] " + ex.Message); }
        }

        public static Task FlushAsync() => SafeFlushAsync();

        public static void Shutdown()
        {
            try { _timer?.Dispose(); } catch { }
            WriteLog("[Shutdown]");
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
                var json = JsonConvert.SerializeObject(payload);

                for (int attempt = 0; attempt < _paths.Length; attempt++)
                {
                    var pathIndex = (_activePathIndex + attempt) % _paths.Length;
                    var endpoint = new Uri(_endpointBase, _paths[pathIndex]);

                    var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    req.Headers.Add("Authorization", $"Bearer {_licenseJwt}");
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
                            WriteLog($"[Flush] HTTP {code} (bad path), try fallback…");
                            continue;
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
                    finally { resp?.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                WriteLog("[Flush/Outer] EX: " + ex.Message);
                PersistQueueToDisk(toSend);
            }
            finally { _flushing = false; }
        }

        private static void PersistQueueToDisk(List<UsageEvent> events)
        {
            try
            {
                List<UsageEvent> disk = new List<UsageEvent>();
                if (File.Exists(_queueFile))
                {
                    var existing = File.ReadAllText(_queueFile);
                    if (!string.IsNullOrWhiteSpace(existing))
                        disk = JsonConvert.DeserializeObject<List<UsageEvent>>(existing) ?? new List<UsageEvent>();
                }
                disk.AddRange(events);
                File.WriteAllText(_queueFile, JsonConvert.SerializeObject(disk));
                WriteLog($"[Persist] queued to disk, total now={disk.Count}");
            }
            catch (Exception ex) { WriteLog("[Persist] EX: " + ex.Message); }
        }

        private static void TryRestoreQueueFromDisk()
        {
            try
            {
                if (!File.Exists(_queueFile)) return;
                var txt = File.ReadAllText(_queueFile);
                if (string.IsNullOrWhiteSpace(txt)) return;
                var disk = JsonConvert.DeserializeObject<List<UsageEvent>>(txt);
                if (disk == null || disk.Count == 0) return;

                lock (_lock) { _buffer.AddRange(disk); }
                File.Delete(_queueFile);
                WriteLog($"[Restore] restored {disk.Count} events from disk");
            }
            catch (Exception ex) { WriteLog("[Restore] EX: " + ex.Message); }
        }

        private static void WriteLog(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(_logFile))
                    _logFile = Path.Combine(Paths.RevitLogsDir, "telemetry.log");
                File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
            }
            catch { /* ignore */ }
        }
    }
}
