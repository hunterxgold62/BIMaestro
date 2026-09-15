using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BIMaestro.Codex
{
    // JSON-RPC over redirected stdio: no listening socket or API key in BIMaestro.
    internal sealed class CodexClient : IDisposable
    {
        internal static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "Codex");
        internal static readonly string WorkDirectory = Path.Combine(DataDirectory, "workspace");
        private readonly string storageDirectory;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken>> pending = new ConcurrentDictionary<string, TaskCompletionSource<JToken>>();
        private readonly SemaphoreSlim writer = new SemaphoreSlim(1, 1);
        private Process process;
        private int sequence;
        private bool disposed;
        internal event Action<string, JObject> Notification;
        internal event Action Disconnected;
        internal Func<string, JObject, Task<object>> ServerRequest;

        internal CodexClient(string storageDirectory = null)
        {
            this.storageDirectory = storageDirectory ?? DataDirectory;
        }

        internal static string FindExecutable()
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                    if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
            {
                var candidate = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (candidate != null) return candidate;
            }
            return null;
        }

        internal async Task StartAsync(string executable)
        {
            if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sélectionnez l'exécutable officiel codex.exe (installation de Codex).");
            string workspace = Path.Combine(storageDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            string home = Path.Combine(storageDirectory, "home");
            Directory.CreateDirectory(home);
            // Dedicated home: do not inherit the desktop's MCP servers, hooks, history or API credentials.
            File.WriteAllText(Path.Combine(home, "config.toml"),
                "forced_login_method = \"chatgpt\"\ncli_auth_credentials_store = \"keyring\"\n" +
                "sandbox_mode = \"read-only\"\napproval_policy = \"on-request\"\nweb_search = \"disabled\"\n" +
                "[features]\nshell_tool = false\nunified_exec = false\n", new UTF8Encoding(false));
            var start = new ProcessStartInfo(executable, "app-server --listen stdio://")
            {
                WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["CODEX_HOME"] = home;
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) && key != "CODEX_HOME")
                    start.EnvironmentVariables.Remove(key);
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, __) => FailPending();
            process.Start();
            // Never write stderr to telemetry or disk: authentication diagnostics can be sensitive.
            process.ErrorDataReceived += (_, __) => { };
            process.BeginErrorReadLine();
            _ = ReadAsync();
            await RequestAsync("initialize", new
            {
                clientInfo = new { name = "bimaestro_revit", title = "BIMaestro Revit", version = "0.1.0" },
                capabilities = new { experimentalApi = true }
            });
            await WriteAsync(new { method = "initialized", @params = new { } });
        }

        internal async Task<JToken> RequestAsync(string method, object parameters)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CodexClient));
            string id = "bimaestro-" + Interlocked.Increment(ref sequence);
            var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                await WriteAsync(new { id, method, @params = parameters });
                if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(45))) != completion.Task)
                    throw new TimeoutException("Codex ne répond pas. Déconnectez puis reconnectez le panneau.");
                return await completion.Task;
            }
            finally { pending.TryRemove(id, out _); }
        }

        private async Task WriteAsync(object message)
        {
            await writer.WaitAsync();
            try
            {
                if (disposed || process == null || process.HasExited) throw new IOException("Codex est déconnecté.");
                // Explicit UTF-8, including accents, regardless of the parent process code page.
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message) + "\n");
                await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
                await process.StandardInput.BaseStream.FlushAsync();
            }
            finally { writer.Release(); }
        }

        private async Task ReadAsync()
        {
            try
            {
                string line;
                while (!disposed && (line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    var message = JObject.Parse(line);
                    string method = (string)message["method"];
                    if (method != null)
                    {
                        if (message["id"] != null) _ = ReplyAsync(message);
                        else Notification?.Invoke(method, message["params"] as JObject ?? new JObject());
                    }
                    else if (message["id"] != null && pending.TryRemove((string)message["id"], out var completion))
                    {
                        if (message["error"] != null)
                            completion.TrySetException(new InvalidOperationException((string)message["error"]["message"] ?? "Erreur Codex."));
                        else completion.TrySetResult(message["result"] ?? new JObject());
                    }
                }
            }
            catch (Exception) { /* Disconnect is surfaced to the UI; no raw protocol logging. */ }
            finally { FailPending(); }
        }

        private async Task ReplyAsync(JObject message)
        {
            try
            {
                if (ServerRequest == null) throw new InvalidOperationException("Requête non prise en charge.");
                object result = await ServerRequest((string)message["method"], message["params"] as JObject ?? new JObject());
                await WriteAsync(new { id = message["id"], result });
            }
            catch (Exception)
            {
                try { await WriteAsync(new { id = message["id"], error = new { code = -32601, message = "Opération indisponible ou refusée par BIMaestro." } }); }
                catch (Exception) { }
            }
        }

        private void FailPending()
        {
            foreach (var pair in pending)
                if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(new IOException("Connexion Codex fermée."));
            if (!disposed) Disconnected?.Invoke();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            FailPending();
            try { if (process != null && !process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            process?.Dispose();
        }
    }
}
