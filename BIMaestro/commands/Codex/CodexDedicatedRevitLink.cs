using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BIMaestro.Codex
{
    // The chat remains in the original Revit. Only Revit API calls cross this local pipe.
    internal sealed class CodexDedicatedRevitLink : IDisposable
    {
        private readonly string pipeName, secret;
        private readonly CodexRevitBridge bridge;
        private readonly Dispatcher dispatcher;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();

        internal CodexDedicatedRevitLink(string pipeName, string secret, UIApplication app)
        {
            this.pipeName = pipeName; this.secret = secret;
            dispatcher = Dispatcher.CurrentDispatcher;
            bridge = new CodexRevitBridge(null, app.Application.VersionNumber) { DedicatedSession = true };
            bridge.AttachEvent(ExternalEvent.Create(bridge));
            _ = ListenAsync();
        }

        private async Task ListenAsync()
        {
            while (!shutdown.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    _ = ServeAsync(connected);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { if (!shutdown.IsCancellationRequested) await Task.Delay(500).ConfigureAwait(false); }
                finally { pipe?.Dispose(); }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
            {
                try
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    var request = JObject.Parse(line ?? "{}");
                    if ((string)request["secret"] != secret) throw new InvalidOperationException("Connexion Revit non autorisée.");
                    string command = (string)request["command"];
                    if (command == "cancel")
                    {
                        await dispatcher.InvokeAsync(() => bridge.CancelPending()).Task.ConfigureAwait(false);
                        await writer.WriteLineAsync("{\"ok\":true}").ConfigureAwait(false);
                        return;
                    }
                    if (command == "hello")
                    {
                        await writer.WriteLineAsync("{\"ok\":true}").ConfigureAwait(false);
                        return;
                    }
                    if (command != "call") throw new InvalidOperationException("Commande de liaison inconnue.");
                    var operation = await dispatcher.InvokeAsync(() => {
                        bridge.ShareContext = request.Value<bool>("shareContext");
                        bridge.AllowChanges = request.Value<bool>("allowChanges");
                        bridge.ApplyDirectly = request.Value<bool>("applyDirectly");
                        bridge.FamilyOutputRoot = request.Value<bool>("useClaude") ? CodexFamilyBuilder.ClaudeOutputRoot : CodexFamilyBuilder.OutputRoot;
                        return bridge.CallAsync((string)request["tool"], request["args"] as JObject ?? new JObject());
                    }).Task.ConfigureAwait(false);
                    object result = await operation.ConfigureAwait(false);
                    var response = new JObject { ["ok"] = true, ["documentTitle"] = bridge.DocumentTitle };
                    if (result is CodexFamilyArtifact artifact)
                    {
                        response["artifact"] = new JObject {
                            ["filePath"] = artifact.FilePath, ["previewPath"] = artifact.PreviewPath,
                            ["previewPaths"] = artifact.PreviewPaths == null ? null : JArray.FromObject(artifact.PreviewPaths),
                            ["report"] = artifact.Report == null ? null : JToken.FromObject(artifact.Report)
                        };
                    }
                    else response["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result);
                    await writer.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try { await writer.WriteLineAsync(new JObject {
                        ["ok"] = false, ["error"] = ex.Message, ["failureKind"] = CodexToolRecovery.RemoteFailureKind(ex)
                    }.ToString(Formatting.None)).ConfigureAwait(false); }
                    catch (IOException) { }
                }
            }
        }

        public void Dispose() { shutdown.Cancel(); bridge.CancelPending(); bridge.Dispose(); shutdown.Dispose(); }
    }

    internal sealed class CodexDedicatedRevitClient
    {
        private readonly string pipeName, secret;
        internal string DocumentTitle { get; private set; } = "Nouvelle famille · Revit séparé";
        internal CodexDedicatedRevitClient(string pipeName, string secret) { this.pipeName = pipeName; this.secret = secret; }

        private async Task<JObject> ExchangeAsync(JObject request, int connectTimeout = 5000)
        {
            request["secret"] = secret;
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(connectTimeout).ConfigureAwait(false);
                using (var reader = new StreamReader(pipe, Encoding.UTF8))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    await writer.WriteLineAsync(request.ToString(Formatting.None)).ConfigureAwait(false);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) throw new IOException("La liaison avec le Revit séparé a été interrompue.");
                    var response = JObject.Parse(line);
                    if (response.Value<bool>("ok") != true)
                        throw CodexToolRecovery.RestoreRemoteFailure((string)response["failureKind"],
                            (string)response["error"] ?? "Le Revit séparé a refusé la commande.");
                    DocumentTitle = (string)response["documentTitle"] ?? DocumentTitle;
                    return response;
                }
            }
        }

        internal async Task WaitReadyAsync(Process process)
        {
            for (int attempt = 0; attempt < 90; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException("Le nouveau Revit s'est fermé avant la connexion.");
                try { await ExchangeAsync(new JObject { ["command"] = "hello" }, 1000); return; }
                catch (TimeoutException) { }
                catch (IOException) { }
                await Task.Delay(1000);
            }
            throw new TimeoutException("Le Revit séparé n'a pas établi la liaison avec Famille IA.");
        }

        internal async Task<object> CallAsync(string tool, JObject args, bool shareContext, bool allowChanges, bool applyDirectly, bool useClaude)
        {
            var response = await ExchangeAsync(new JObject {
                ["command"] = "call", ["tool"] = tool, ["args"] = args,
                ["shareContext"] = shareContext, ["allowChanges"] = allowChanges,
                ["applyDirectly"] = applyDirectly, ["useClaude"] = useClaude
            });
            if (response["artifact"] is JObject artifact)
                return new CodexFamilyArtifact {
                    FilePath = (string)artifact["filePath"], PreviewPath = (string)artifact["previewPath"],
                    PreviewPaths = artifact["previewPaths"]?.ToObject<string[]>(), Report = artifact["report"]
                };
            return response["result"];
        }

        internal async Task CancelAsync()
        {
            try { await ExchangeAsync(new JObject { ["command"] = "cancel" }, 1000); }
            catch (Exception) { /* The other Revit may already have closed. */ }
        }
    }
}
