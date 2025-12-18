using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Licensing
{
    /// <summary>
    /// Récupère les messages ciblés depuis Supabase et affiche un popup Revit.
    /// Poll léger depuis OnIdling pour ne pas bloquer l'UI.
    /// </summary>
    public static class PushMessageClient
    {
        private class PushMessage
        {
            public string id { get; set; }
            public string title { get; set; }
            public string content { get; set; }
            public string severity { get; set; }
        }

        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

        private static readonly object _lock = new object();

        private static Uri _endpoint;
        private static string _licenseJwt;
        private static string _machineId;
        private static string _installId;
        private static string _pluginVersion;

        private static string _stateFile;
        private static string _lastMessageId;

        private static HttpClient _http;
        private static UIApplication _uiApp;

        private static Task<PushMessage> _currentFetch;
        private static PushMessage _pendingMessage;
        private static DateTime _nextCheckUtc = DateTime.MaxValue;

        public static void Init(string edgeFunctionsBaseUrl, string licenseJwt, string machineId, string installId, string pluginVersion)
        {
            if (string.IsNullOrWhiteSpace(edgeFunctionsBaseUrl) || string.IsNullOrWhiteSpace(licenseJwt))
                return;

            _endpoint = new Uri(edgeFunctionsBaseUrl.TrimEnd('/') + "/functions/v1/push-message");
            _licenseJwt = licenseJwt;
            _machineId = machineId;
            _installId = installId;
            _pluginVersion = string.IsNullOrWhiteSpace(pluginVersion) ? "dev" : pluginVersion;

            _stateFile = Path.Combine(Paths.RevitLogsDir, "push_message_state.json");
            _http = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(8));

            TryRestoreState();
            _nextCheckUtc = DateTime.UtcNow + InitialDelay;
        }

        public static void OnIdling(UIApplication uiApp)
        {
            if (_endpoint == null || string.IsNullOrWhiteSpace(_licenseJwt))
                return;

            _uiApp ??= uiApp;
            if (_uiApp == null)
                return;

            if (_pendingMessage != null)
            {
                ShowDialog(_pendingMessage);
                _pendingMessage = null;
                return;
            }

            if (DateTime.UtcNow < _nextCheckUtc)
                return;

            lock (_lock)
            {
                if (_currentFetch == null)
                {
                    _currentFetch = FetchNextAsync();
                    _currentFetch.ContinueWith(t =>
                    {
                        try
                        {
                            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                                _pendingMessage = t.Result;
                        }
                        catch (Exception ex) { WriteLog("Continuation EX: " + ex.Message); }
                        finally { _currentFetch = null; }
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }
        }

        private static async Task<PushMessage> FetchNextAsync()
        {
            try
            {
                var payload = new
                {
                    machine_id = _machineId,
                    install_id = _installId,
                    plugin_version = _pluginVersion,
                    last_message_id = _lastMessageId
                };

                var json = JsonConvert.SerializeObject(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                req.Headers.Add("Authorization", $"Bearer {_licenseJwt}");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                WriteLog($"[Fetch] POST {_endpoint}");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NoContent)
                {
                    _nextCheckUtc = DateTime.UtcNow + CheckInterval;
                    return null;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    WriteLog($"[Fetch] HTTP {(int)resp.StatusCode} - {body}");
                    _nextCheckUtc = DateTime.UtcNow + RetryInterval;
                    return null;
                }

                var msg = JsonConvert.DeserializeObject<PushMessage>(body);
                if (msg == null || string.IsNullOrWhiteSpace(msg.id) || string.IsNullOrWhiteSpace(msg.content))
                {
                    _nextCheckUtc = DateTime.UtcNow + CheckInterval;
                    return null;
                }

                _lastMessageId = msg.id;
                PersistState();
                _nextCheckUtc = DateTime.UtcNow + CheckInterval;
                return msg;
            }
            catch (Exception ex)
            {
                WriteLog("[Fetch] EX: " + ex.Message);
                _nextCheckUtc = DateTime.UtcNow + RetryInterval;
                return null;
            }
        }

        private static void ShowDialog(PushMessage msg)
        {
            try
            {
                var td = new TaskDialog(string.IsNullOrWhiteSpace(msg.title) ? "Message BIMaestro" : msg.title)
                {
                    MainInstruction = string.IsNullOrWhiteSpace(msg.title) ? "Message BIMaestro" : msg.title,
                    MainContent = msg.content,
                    CommonButtons = TaskDialogCommonButtons.Ok
                };

                if (!string.IsNullOrWhiteSpace(msg.severity) && msg.severity.Equals("warning", StringComparison.OrdinalIgnoreCase))
                    td.MainIcon = TaskDialogIcon.TaskDialogIconWarning;

                td.Show();
            }
            catch (Exception ex)
            {
                WriteLog("[Dialog] EX: " + ex.Message);
            }
        }

        private static void TryRestoreState()
        {
            try
            {
                if (!File.Exists(_stateFile)) return;
                var raw = File.ReadAllText(_stateFile);
                if (string.IsNullOrWhiteSpace(raw)) return;

                var state = JsonConvert.DeserializeObject<dynamic>(raw);
                _lastMessageId = state?.last_message_id;
            }
            catch (Exception ex) { WriteLog("[RestoreState] EX: " + ex.Message); }
        }

        private static void PersistState()
        {
            try
            {
                var raw = JsonConvert.SerializeObject(new { last_message_id = _lastMessageId });
                File.WriteAllText(_stateFile, raw);
            }
            catch (Exception ex) { WriteLog("[PersistState] EX: " + ex.Message); }
        }

        private static void WriteLog(string line)
        {
            try
            {
                var path = Path.Combine(Paths.RevitLogsDir, "push_messages.log");
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
                Debug.WriteLine($"[PushMessage] {line}");
            }
            catch { /* ignore */ }
        }
    }
}