using Newtonsoft.Json.Linq;
using System;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace BIMaestro.Codex
{
    // These boundaries require native Revit/PDF libraries or a remote service.
    // Fail closed if a test accidentally reaches one; never launch or publish.
    internal sealed class CodexPdfAttachment
    {
        internal string Name { get; set; }
        internal string Text { get; set; }
        internal int PageCount { get; set; }
        internal long FileSizeBytes { get; set; }
        internal int[] VisualPages { get; set; } = Array.Empty<int>();
        internal CodexImageAttachment[] PageImages { get; set; } = Array.Empty<CodexImageAttachment>();
        internal static CodexPdfAttachment FromFile(string path) => throw new NotSupportedException("Native PDF parsing is outside this UI harness.");
        internal (int Page, byte[] Png)[] RenderPages(int[] pages) => throw new NotSupportedException("Native PDF rendering is outside this UI harness.");
    }
    internal sealed class CodexDedicatedRevitClient
    {
        internal CodexDedicatedRevitClient(string pipeName, string secret) { throw new NotSupportedException("A dedicated Revit must not start in the UI harness."); }
        internal string DocumentTitle => "Dedicated Revit test boundary";
        internal Task WaitReadyAsync(System.Diagnostics.Process process) => throw new NotSupportedException();
        internal Task<object> CallAsync(string tool, JObject args, bool shareContext, bool allowChanges, bool applyDirectly, bool useClaude) => throw new NotSupportedException();
        internal Task CancelAsync() => throw new NotSupportedException();
    }
    internal static class CodexFamilyBuilder
    {
        internal static string OutputRoot => System.IO.Path.GetFullPath("tmp/codex-tests/families");
        internal static string ClaudeOutputRoot => System.IO.Path.GetFullPath("tmp/codex-tests/claude-families");
    }
    internal sealed class CodexCommunityWindow : System.Windows.Window
    {
        internal CodexCommunityWindow(CodexRevitBridge bridge, string initialQuery = null, Action<JObject> useAsBase = null) { throw new NotSupportedException("Community windows are outside this UI harness."); }
    }
    internal static class CodexCommunityPublishWindow
    {
        internal static Task<bool> ShowAsync(System.Windows.Window owner, CodexRevitBridge bridge, string filePath = null, string origin = "ai") => throw new NotSupportedException("Publishing is disabled in the UI harness.");
    }
    internal sealed class CodexCommunityLibrary : IDisposable
    {
        internal Task<JObject> Search(string query, string version, string cursor = null) => throw new NotSupportedException("Community network calls are disabled in the UI harness.");
        public void Dispose() { }
    }
    internal static class CommunityStyle { internal static string Category(string category) => category; }
    internal sealed class CodexFamilyArtifact { internal string FilePath { get; set; } internal string PreviewPath { get; set; } internal string[] PreviewPaths { get; set; } internal object Report { get; set; } }
    // UI regression tests use the real window without loading Revit or starting Codex.
    internal sealed class CodexRevitBridge : IDisposable
    {
        internal event Action<string> CreationProgress;
        internal bool ShareContext { get; set; }
        internal bool AllowChanges { get; set; }
        internal bool ApplyDirectly { get; set; }
        internal bool DedicatedSession { get; set; }
        internal bool IsAttachedFamilyDocument { get; set; }
        internal string FamilyOutputRoot { get; set; }
        internal string RevitVersion => "Test";
        internal string DocumentTitle => "Test";
        internal static JArray ToolDefinitions() => new JArray(new JObject {
            ["name"] = "revit_create_family", ["description"] = "Local test boundary for family creation.",
            ["inputSchema"] = new JObject { ["type"] = "object" }
        });
        internal Func<string, JObject, Task<object>> Handler = (tool, args) => Task.FromResult<object>(new { });
        internal Task<object> CallAsync(string tool, JObject args) => Handler(tool, args);
        internal int CancellationCount;
        internal void CancelPending(string reason = null) { CancellationCount++; }
        public void Dispose() { }
    }
    internal static class WindowTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        [STAThread]
        private static int Main(string[] commandLine)
        {
            if (commandLine.Length > 0 && commandLine[0] == "app-server") return FakeServer();
            if (commandLine.Contains("-p")) return FakeClaude(commandLine);
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
                var image = CodexImageAttachment.FromBitmap(bitmap, "test");
                if (!image.DataUrl.StartsWith("data:image/png;base64,")) throw new Exception("Image input was not encoded as PNG");
                Console.WriteLine("PASS: image attachment encoded for vision input");
                var bridge = new CodexRevitBridge();
                System.Windows.ResourceDictionary theme;
                using (var source = System.IO.File.OpenRead("BIMaestro/Themes/BIMaestroTheme.xaml"))
                    theme = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(source);
                var window = new CodexWindow(bridge, theme);
                if (!bridge.ShareContext || !bridge.AllowChanges || !bridge.ApplyDirectly || ((CheckBox)Get(window, "context")).IsChecked != true || ((CheckBox)Get(window, "changes")).IsChecked != true || ((CheckBox)Get(window, "direct")).IsChecked != true) throw new Exception("Requested initial permissions are not checked.");
                var modelType = typeof(CodexWindow).GetNestedType("ModelChoice", BindingFlags.NonPublic);
                Func<string, bool, object> model = (id, isDefault) => Activator.CreateInstance(modelType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { JObject.Parse("{\"model\":\"" + id + "\",\"isDefault\":" + (isDefault ? "true" : "false") + ",\"defaultReasoningEffort\":\"high\",\"supportedReasoningEfforts\":[{\"reasoningEffort\":\"low\"},{\"reasoningEffort\":\"high\"}]} ") }, null);
                var astra = model("gpt-6-astra", false); var fallback = model("other", true);
                var picker = (ComboBox)Get(window, "models"); var effortPicker = (ComboBox)Get(window, "effort");
                picker.ItemsSource = new[] { fallback, astra };
                typeof(CodexWindow).GetMethod("SelectPreferredModel", PrivateInstance).Invoke(window, new object[0]);
                if (!ReferenceEquals(picker.SelectedItem, astra) || (string)effortPicker.SelectedItem != "low") throw new Exception("Astra low was not selected");
                effortPicker.SelectedItem = "high";
                ((Button)Get(window, "reset")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                if ((string)effortPicker.SelectedItem != "low") throw new Exception("New discussion did not reset to low");
                picker.ItemsSource = new[] { fallback };
                typeof(CodexWindow).GetMethod("SelectPreferredModel", PrivateInstance).Invoke(window, new object[0]);
                if (!ReferenceEquals(picker.SelectedItem, fallback)) throw new Exception("Missing-model fallback failed");
                Console.WriteLine("PASS: Astra low preference, new-discussion reset and unavailable-model fallback");
                if (bridge.ApplyDirectly) throw new Exception("New discussion should still reset direct mode.");
                ((CheckBox)Get(window, "context")).IsChecked = true;
                ((CheckBox)Get(window, "changes")).IsChecked = true;
                ((CheckBox)Get(window, "direct")).IsChecked = true;
                if (!bridge.ApplyDirectly) throw new Exception("Direct mode not applied");
                ((CheckBox)Get(window, "context")).IsChecked = false;
                if (bridge.ApplyDirectly || bridge.AllowChanges) throw new Exception("Revoked permissions remained active");
                Console.WriteLine("PASS: permissions checked at opening, direct mode reset for new discussion, revocation respected");
                Set(window, "busy", true);
                typeof(CodexWindow).GetMethod("UpdateControls", PrivateInstance).Invoke(window, new object[0]);
                if (((CheckBox)Get(window, "internet")).IsEnabled) throw new Exception("Internet settings remained editable during a response.");
                Set(window, "busy", false);
                Console.WriteLine("PASS: Internet settings locked while a response is active");
                foreach (string state in new[] { "completed", "interrupted", "failed" })
                {
                    Set(window, "threadId", "test-thread"); Set(window, "turnId", "test-turn"); Set(window, "busy", true);
                    int previousCancellations = bridge.CancellationCount;
                    Notify(window, "turn/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":{\"status\":\"" + state + "\",\"error\":null}}"));
                    if (bridge.CancellationCount != previousCancellations + (state == "completed" ? 0 : 1))
                        throw new Exception("Incorrect cancellation policy for " + state);
                    if ((bool)Get(window, "busy") || ((Button)Get(window, "stop")).IsEnabled)
                        throw new Exception("The turn remained busy after " + state);
                    Console.WriteLine("PASS: UI " + state + " with error:null");
                }
                Notify(window, "turn/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":{\"status\":\"failed\",\"error\":{\"message\":\"Test failure\"}}}"));
                if (!((TextBox)Get(window, "transcript")).Text.Contains("Test failure")) throw new Exception("Missing error message");
                Notify(window, "error", JObject.Parse("{\"threadId\":\"test-thread\",\"error\":null}"));
                Notify(window, "item/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"item\":null}"));
                Notify(window, "turn/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":null}"));
                Console.WriteLine("PASS: UI error messages and null objects");
                Notify(window, "item/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"item\":{\"type\":\"dynamicToolCall\",\"id\":\"undelivered\",\"tool\":\"revit_create_family\",\"status\":\"failed\",\"success\":false,\"arguments\":{},\"contentItems\":[{\"type\":\"inputText\",\"text\":\"Rejected before dispatch\"}]}}"));
                if (!((TextBox)Get(window, "transcript")).Text.Contains("Rejected before dispatch")) throw new Exception("Undelivered tool failure was hidden");
                Console.WriteLine("PASS: server-side failure displayed even without a bridge request");
                using (var client = new CodexClient())
                {
                    ResetRecovery(window);
                    Set(window, "client", client); Set(window, "threadId", "test-thread"); Set(window, "turnId", "test-turn"); Set(window, "busy", true);
                    var arguments = new JObject { ["parts"] = new JArray() };
                    var call = new JObject { ["threadId"] = "test-thread", ["turnId"] = "test-turn", ["tool"] = "revit_validate_family", ["arguments"] = arguments };
                    var pendingResult = new TaskCompletionSource<object>();
                    bridge.Handler = (tool, args) => pendingResult.Task;
                    int cancellations = bridge.CancellationCount;
                    var pendingCall = (Task<object>)typeof(CodexWindow).GetMethod("HandleRequestAsync", PrivateInstance).Invoke(window, new object[] { client, "item/tool/call", call });
                    Notify(window, "turn/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":{\"id\":\"older-turn\",\"status\":\"completed\"}}"));
                    if ((string)Get(window, "turnId") != "test-turn") throw new Exception("Stale completion cleared the active turn");
                    Notify(window, "turn/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":{\"id\":\"test-turn\",\"status\":\"completed\"}}"));
                    if (bridge.CancellationCount != cancellations || !(bool)Get(window, "busy") || !((Button)Get(window, "stop")).IsEnabled || ((Button)Get(window, "reset")).IsEnabled)
                        throw new Exception("Normal completion cancelled Revit or unlocked the panel too early");
                    Notify(window, "turn/started", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":{\"id\":\"test-turn\"}}"));
                    if (Get(window, "turnId") != null) throw new Exception("A late start notification resurrected the completed turn.");
                    pendingResult.SetResult(new CodexFamilyArtifact { FilePath = "finished-after-turn.rfa", Report = new { saved = true } });
                    Pump(pendingCall);
                    if (!JObject.FromObject(pendingCall.GetAwaiter().GetResult()).Value<bool>("success") || (bool)Get(window, "busy") || ((CodexFamilyArtifact)Get(window, "lastArtifact")).FilePath != "finished-after-turn.rfa")
                        throw new Exception("Late Revit result was lost or panel remained busy");
                    Console.WriteLine("PASS: normal turn completion preserves pending Revit operation and its saved result; stale completion ignored");
                    Set(window, "turnId", "test-turn"); Set(window, "busy", true);
                    ResetRecovery(window);
                    int failureCalls = 0;
                    bridge.Handler = (tool, callArgs) => { failureCalls++; return Task.FromException<object>(new InvalidOperationException("Pièce « barbe » : le profil se croise.")); };
                    var failed = Request(window, client, call);
                    string errorText = (string)failed["contentItems"][0]["text"];
                    if (failed.Value<bool>("success") || !errorText.Contains("barbe") || !errorText.Contains("profil se croise")) throw new Exception("Tool error was lost");
                    if (!((TextBox)Get(window, "transcript")).Text.Contains("barbe")) throw new Exception("Tool error not displayed");
                    Console.WriteLine("PASS: exact piece failure reaches both chat and model");
                    if (Request(window, client, call).Value<bool>("success") || failureCalls != 1)
                        throw new Exception("Identical invalid geometry reached Revit twice.");
                    call["arguments"] = new JObject { ["parts"] = new JArray(), ["revision"] = 2 };
                    Request(window, client, call);
                    if (failureCalls != 2) throw new Exception("Corrected geometry was blocked with the failed attempt.");
                    Console.WriteLine("PASS: identical failed arguments blocked before Revit; changed arguments can be tried");
                    call["arguments"] = arguments;
                    ResetRecovery(window);
                    var original = new CodexFamilyArtifact { FilePath = "original.rfa" }; Set(window, "lastArtifact", original);
                    bridge.Handler = (tool, args) => Task.FromResult<object>(new CodexFamilyArtifact { Report = new { validated = true, saved = false } });
                    var validated = Request(window, client, call);
                    if (!validated.Value<bool>("success") || !ReferenceEquals(Get(window, "lastArtifact"), original)) throw new Exception("Validation replaced last saved artifact");
                    Console.WriteLine("PASS: successful dry run does not claim or replace saved RFA");
                    ResetRecovery(window);
                    string[] previews = new[] { "tmp/codex-tests/Dessus.png", "tmp/codex-tests/Dessous.png", "tmp/codex-tests/Isometrie.png" };
                    foreach (string path in previews) System.IO.File.WriteAllBytes(path, Convert.FromBase64String(image.DataUrl.Substring("data:image/png;base64,".Length)));
                    bridge.Handler = (tool, args) => Task.FromResult<object>(new CodexFamilyArtifact { FilePath = "created.rfa", PreviewPaths = previews, Report = new { saved = true } });
                    var withPreviews = Request(window, client, call);
                    if (!withPreviews.Value<bool>("success") || withPreviews["contentItems"].Count(c => (string)c["type"] == "inputImage") != 3)
                        throw new Exception("Multiple Revit previews were not delivered to the model");
                    Console.WriteLine("PASS: three labeled geometry previews returned to model");
                    bool invoked = false;
                    bridge.Handler = (tool, args) => { invoked = true; return Task.FromResult<object>(new { }); };
                    call["turnId"] = "stale-turn";
                    if (Request(window, client, call).Value<bool>("success") || invoked) throw new Exception("Stale tool call reached Revit");
                    Console.WriteLine("PASS: stale turn rejected before Revit execution");
                    Set(window, "client", null); Set(window, "busy", false);
                }
                TestClaudeStop(window);
                TestAutomaticContinuation(window, bridge, astra);
                TestClaudeAutomaticContinuation();
                // Render the actual production layout without opening a native window.
                Set(window, "ready", true); Set(window, "busy", false); Set(window, "lastArtifact", null);
                typeof(CodexWindow).GetMethod("EndActivity", PrivateInstance).Invoke(window, new object[0]);
                picker.ItemsSource = new[] { astra }; picker.SelectedIndex = 0;
                ((CheckBox)Get(window, "context")).IsChecked = true;
                ((CheckBox)Get(window, "changes")).IsChecked = true;
                typeof(CodexWindow).GetMethod("UpdateControls", PrivateInstance).Invoke(window, new object[0]);
                ((TextBlock)Get(window, "status")).Text = "Aperçu de l’interface";
                ((TextBox)Get(window, "transcript")).Text = "Décrivez votre objet ou joignez une image, avec les dimensions connues.\n\nLe résultat sera enregistré dans un nouveau fichier RFA, avec ses matériaux et un aperçu.";
                ((TextBox)Get(window, "input")).Text = "Crée une famille de transformateur électrique d'après cette image, puis charge-la dans le projet.";
                var root = (System.Windows.FrameworkElement)window.Content;
                root.Resources = window.Resources;
                window.Content = null;
                var surface = new System.Windows.Controls.Border { Background = window.Background, Child = root };
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                Render(surface, "codex-window.png", 704, 860);
                var permissions = (Expander)Get(window, "permissions");
                permissions.IsExpanded = true;
                Render(surface, "codex-window-permissions-normal.png", 704, 860);
                Render(surface, "codex-window-permissions.png", 624, 680);
                window.Close();
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, PrivateInstance).SetValue(target, value);
        private static void ResetRecovery(CodexWindow window)
        {
            var field = typeof(CodexWindow).GetField("toolRecovery", PrivateInstance);
            field.SetValue(window, new CodexToolRecovery());
        }
        private static void TestClaudeStop(CodexWindow window)
        {
            string fakeExecutable = System.IO.Path.GetFullPath("tmp/codex-tests/claude.exe");
            System.IO.File.Copy(Assembly.GetExecutingAssembly().Location, fakeExecutable, true);
            // Constructor validation needs an existing claude.exe. This copy is
            // never started: the real Stop implementation has no running process.
            using (var claude = new ClaudeClient(fakeExecutable))
            using (var cancellation = new CancellationTokenSource())
            {
                ResetRecovery(window);
                Set(window, "claudeClient", claude); Set(window, "claudeCancellation", cancellation);
                Set(window, "ready", true); Set(window, "busy", true);
                typeof(CodexWindow).GetMethod("UpdateControls", PrivateInstance).Invoke(window, new object[0]);
                Pump((Task)typeof(CodexWindow).GetMethod("StopAsync", PrivateInstance).Invoke(window, new object[0]));
                if (!cancellation.IsCancellationRequested || !(bool)Get(window, "busy") || ((Button)Get(window, "send")).IsEnabled)
                    throw new Exception("Stopping Claude unlocked the composer before the running task could finish.");
                Console.WriteLine("PASS: stopping Claude cancels recovery and keeps the composer locked until task completion");
                Set(window, "claudeClient", null); Set(window, "claudeCancellation", null); Set(window, "busy", false);
            }
        }
        private static void TestAutomaticContinuation(CodexWindow window, CodexRevitBridge bridge, object model)
        {
            string storage = System.IO.Path.GetFullPath("tmp/codex-tests/ui-server-" + Guid.NewGuid().ToString("N"));
            using (var client = new CodexClient(storage))
            using (var cancellation = new CancellationTokenSource())
            {
                ResetRecovery(window);
                ((System.Collections.Generic.HashSet<string>)Get(window, "completedCodexTurns")).Clear();
                Set(window, "client", client); Set(window, "codexCancellation", cancellation);
                Set(window, "threadId", "auto-thread"); Set(window, "turnId", null);
                Set(window, "ready", true); Set(window, "busy", true);
                var picker = (ComboBox)Get(window, "models"); picker.ItemsSource = new[] { model }; picker.SelectedIndex = 0;
                int nativeCalls = 0;
                bridge.Handler = (tool, callArgs) =>
                {
                    nativeCalls++;
                    if (callArgs.Value<int>("width_mm") == 0)
                        return Task.FromException<object>(new InvalidOperationException("Pièce « pied » : largeur invalide."));
                    return Task.FromResult<object>(new CodexFamilyArtifact { FilePath = "recovered.rfa", Report = new { saved = true } });
                };
                client.Notification += (method, data) => window.Dispatcher.BeginInvoke(new Action(() => Notify(window, method, data)));
                client.ServerRequest = (method, data) => window.Dispatcher.InvokeAsync(() =>
                    (Task<object>)typeof(CodexWindow).GetMethod("HandleRequestAsync", PrivateInstance).Invoke(window, new object[] { client, method, data })).Task.Unwrap();
                Pump(client.StartAsync(Assembly.GetExecutingAssembly().Location));
                var start = (Task)typeof(CodexWindow).GetMethod("StartCodexTurnAsync", PrivateInstance).Invoke(window,
                    new object[] { client, "auto-thread", new object[] { new { type = "text", text = "Créer une famille de table.", text_elements = new object[0] } }, cancellation });
                Pump(start);
                Pump(client.RequestAsync("test/fail-tool", new { }));
                PumpUntil(() => (string)Get(window, "turnId") == "auto-turn-2", "The failed tool did not trigger an automatic follow-up turn.");
                if (!(bool)Get(window, "busy") || ((Button)Get(window, "reset")).IsEnabled)
                    throw new Exception("The panel unlocked during automatic recovery.");
                var state = client.RequestAsync("test/state", new { }); Pump(state);
                if (state.Result.Value<int>("turnStarts") != 2 || string.IsNullOrWhiteSpace((string)state.Result["lastPrompt"]))
                    throw new Exception("The automatic turn did not receive correction context.");
                Pump(client.RequestAsync("test/succeed-tool", new { }));
                PumpUntil(() => !(bool)Get(window, "busy"), "The successful recovery did not finish.");
                if (nativeCalls != 2 || ((CodexFamilyArtifact)Get(window, "lastArtifact"))?.FilePath != "recovered.rfa")
                    throw new Exception("Automatic recovery lost the corrected Revit result.");
                Console.WriteLine("PASS: failed tool resumes automatically in a second Codex turn and preserves the corrected result");
                ResetRecovery(window); Set(window, "busy", true);
                var shortTurn = (Task)typeof(CodexWindow).GetMethod("StartCodexTurnAsync", PrivateInstance).Invoke(window,
                    new object[] { client, "auto-thread", new object[] { new { type = "text", text = "Vérifie le résultat." } }, cancellation });
                Pump(shortTurn);
                PumpUntil(() => !(bool)Get(window, "busy"), "A turn completed before its start acknowledgement left the panel busy.");
                if (Get(window, "turnId") != null) throw new Exception("A late turn/start acknowledgement resurrected a completed turn.");
                Console.WriteLine("PASS: completion before turn/start acknowledgement leaves no stale active turn");
                Set(window, "client", null); Set(window, "codexCancellation", null); Set(window, "turnId", null);
            }
        }
        private static void TestClaudeAutomaticContinuation()
        {
            string cliDirectory = System.IO.Path.GetFullPath("tmp/codex-tests/claude-cli");
            System.IO.Directory.CreateDirectory(cliDirectory);
            string executable = System.IO.Path.Combine(cliDirectory, "claude.exe");
            System.IO.File.Copy(Assembly.GetExecutingAssembly().Location, executable, true);
            System.IO.File.Copy(typeof(JObject).Assembly.Location, System.IO.Path.Combine(cliDirectory, "Newtonsoft.Json.dll"), true);
            foreach (string scenario in new[] { "recover", "refuse" })
            {
                string storage = System.IO.Path.GetFullPath("tmp/codex-tests/claude-" + scenario + "-" + Guid.NewGuid().ToString("N"));
                string workspace = System.IO.Path.Combine(storage, "workspace");
                System.IO.Directory.CreateDirectory(workspace);
                string statePath = System.IO.Path.Combine(workspace, "fake-claude-state.json");
                System.IO.File.WriteAllText(statePath, new JObject { ["scenario"] = scenario, ["calls"] = new JArray() }.ToString());
                var recovery = new CodexToolRecovery();
                var messages = new System.Collections.Generic.List<string>();
                var nativeWidths = new System.Collections.Generic.List<int>();
                using (var claude = new ClaudeClient(executable, storage))
                {
                    var task = claude.AskAsync("Créer la famille de table.", Array.Empty<CodexImageAttachment>(), "test-model", "low",
                        async (tool, arguments) =>
                        {
                            try
                            {
                                var result = await recovery.ExecuteAsync(tool, arguments, () =>
                                {
                                    int width = arguments.Value<int>("width_mm");
                                    nativeWidths.Add(width);
                                    if (scenario == "refuse")
                                        return Task.FromException<object>(new InvalidOperationException("Création refusée par l'utilisateur. Ne pas réessayer sans nouvelle demande."));
                                    if (width == 0)
                                        return Task.FromException<object>(new InvalidOperationException("Pièce « pied » : largeur invalide."));
                                    return Task.FromResult<object>(new { saved = true, filePath = "claude-recovered.rfa" });
                                }, _ => { }, CancellationToken.None);
                                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
                            }
                            catch (CodexToolRecoveryException error) { return error.Detail.ToString(Newtonsoft.Json.Formatting.None); }
                        }, messages.Add, CancellationToken.None, recovery: recovery);
                    Pump(task);
                }
                var calls = (JArray)JObject.Parse(System.IO.File.ReadAllText(statePath))["calls"];
                int expectedCalls = scenario == "recover" ? 5 : 2;
                if (calls.Count != expectedCalls || calls[0].Value<string>("resume") != null ||
                    calls.Skip(1).Any(call => call.Value<string>("resume") != "fake-claude-" + scenario))
                    throw new Exception("Claude lost its session or dispatched an unexpected follow-up for " + scenario + ".");
                if (scenario == "recover")
                {
                    if (!nativeWidths.SequenceEqual(new[] { 0, 100 }) || !messages.Contains("Famille corrigée et enregistrée.") ||
                        messages.Contains("Je m'arrête après cet échec.") || !((string)calls[2]["prompt"]).Contains("pied"))
                        throw new Exception("Claude did not recover autonomously while suppressing identical failed operations.");
                    Console.WriteLine("PASS: real Claude client continues after premature done, preserves --resume and blocks identical tool replay before corrected success");
                }
                else
                {
                    if (nativeWidths.Count != 1 || !messages.Contains("Création refusée, arrêt de la demande."))
                        throw new Exception("Claude continued after a user refusal.");
                    Console.WriteLine("PASS: real Claude client respects user refusal without an automatic follow-up");
                }
            }
        }
        private static int FakeClaude(string[] commandLine)
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            // Only the test-created working directory supplies this state. No
            // credential lookup, network request or installed Claude is used.
            string statePath = System.IO.Path.Combine(Environment.CurrentDirectory, "fake-claude-state.json");
            var state = JObject.Parse(System.IO.File.ReadAllText(statePath));
            string scenario = state.Value<string>("scenario");
            var calls = (JArray)state["calls"];
            int step = calls.Count;
            int resumeIndex = Array.IndexOf(commandLine, "--resume");
            calls.Add(new JObject { ["resume"] = resumeIndex < 0 ? null : commandLine[resumeIndex + 1], ["prompt"] = Console.In.ReadToEnd() });
            System.IO.File.WriteAllText(statePath, state.ToString());
            bool toolCall = step == 0 || scenario == "recover" && (step == 2 || step == 3);
            var answer = new JObject {
                ["reply"] = toolCall ? "" : scenario == "refuse" ? "Création refusée, arrêt de la demande." : step == 1 ? "Je m'arrête après cet échec." : "Famille corrigée et enregistrée.",
                ["tool"] = toolCall ? "revit_create_family" : "",
                ["arguments"] = toolCall ? new JObject { ["width_mm"] = step == 3 ? 100 : 0 } : new JObject(),
                ["done"] = !toolCall
            };
            Console.WriteLine(new JObject { ["session_id"] = "fake-claude-" + scenario, ["structured_output"] = answer }.ToString(Newtonsoft.Json.Formatting.None));
            return 0;
        }
        private static void Pump(Task task)
        {
            PumpUntil(() => task.IsCompleted, "Asynchronous UI test exceeded ten seconds.");
            task.GetAwaiter().GetResult();
        }
        private static void PumpUntil(Func<bool> completed, string failure)
        {
            if (completed()) return;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            timer.Tick += (_, __) => { if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (!completed()) throw new Exception(failure);
        }
        private static int FakeServer()
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            int turnStarts = 0;
            string lastPrompt = null;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                var request = JObject.Parse(line);
                string method = (string)request["method"];
                var id = request["id"];
                if (method == null)
                {
                    if ((string)id == "failed-tool" || (string)id == "successful-tool")
                        Console.WriteLine(new JObject { ["method"] = "turn/completed", ["params"] = new JObject {
                            ["threadId"] = "auto-thread", ["turn"] = new JObject { ["id"] = "auto-turn-" + turnStarts, ["status"] = "completed" } } }.ToString(Newtonsoft.Json.Formatting.None));
                    continue;
                }
                if (method == "initialized") continue;
                if (method == "turn/start")
                {
                    turnStarts++;
                    lastPrompt = (string)request["params"]?["input"]?.First?["text"];
                    if (turnStarts == 3)
                    {
                        Console.WriteLine(new JObject { ["method"] = "turn/started", ["params"] = new JObject {
                            ["threadId"] = "auto-thread", ["turn"] = new JObject { ["id"] = "auto-turn-3" } } }.ToString(Newtonsoft.Json.Formatting.None));
                        Console.WriteLine(new JObject { ["method"] = "turn/completed", ["params"] = new JObject {
                            ["threadId"] = "auto-thread", ["turn"] = new JObject { ["id"] = "auto-turn-3", ["status"] = "completed" } } }.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    Reply(id, new JObject { ["turn"] = new JObject { ["id"] = "auto-turn-" + turnStarts } });
                    continue;
                }
                if (method == "test/fail-tool" || method == "test/succeed-tool")
                {
                    bool success = method == "test/succeed-tool";
                    Reply(id, new JObject());
                    Console.WriteLine(new JObject { ["id"] = success ? "successful-tool" : "failed-tool", ["method"] = "item/tool/call", ["params"] = new JObject {
                        ["threadId"] = "auto-thread", ["turnId"] = "auto-turn-" + turnStarts, ["tool"] = "revit_create_family",
                        ["callId"] = success ? "successful-tool" : "failed-tool", ["arguments"] = new JObject { ["width_mm"] = success ? 100 : 0 } } }.ToString(Newtonsoft.Json.Formatting.None));
                    continue;
                }
                if (method == "test/state") { Reply(id, new JObject { ["turnStarts"] = turnStarts, ["lastPrompt"] = lastPrompt }); continue; }
                Reply(id, new JObject());
            }
            return 0;
        }
        private static void Reply(JToken id, JToken result) => Console.WriteLine(new JObject { ["id"] = id, ["result"] = result }.ToString(Newtonsoft.Json.Formatting.None));
        private static void Render(System.Windows.FrameworkElement surface, string fileName, int width, int height)
        {
            surface.Measure(new System.Windows.Size(width, height));
            surface.Arrange(new System.Windows.Rect(0, 0, width, height)); surface.UpdateLayout();
            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            preview.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using (var file = System.IO.File.Create("tmp/codex-tests/" + fileName)) encoder.Save(file);
        }
        private static object Get(object target, string field) => target.GetType().GetField(field, PrivateInstance).GetValue(target);
        private static void Notify(object target, string method, JObject value) => target.GetType().GetMethod("OnNotification", PrivateInstance).Invoke(target, new object[] { method, value });
        private static JObject Request(object target, CodexClient client, JObject data)
        {
            var task = (Task<object>)target.GetType().GetMethod("HandleRequestAsync", PrivateInstance).Invoke(target, new object[] { client, "item/tool/call", data });
            Pump(task);
            return JObject.FromObject(task.GetAwaiter().GetResult());
        }
    }
}
