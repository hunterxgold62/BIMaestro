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
        internal string SourcePath { get; set; }
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
        internal static JArray ToolDefinitions(bool mepMode = false) => mepMode
            ? new JArray(new JObject { ["name"] = "revit_mep_inspect", ["description"] = "Local MEP inspection test boundary.", ["inputSchema"] = new JObject { ["type"] = "object" } })
            : new JArray(new JObject {
            ["name"] = "revit_create_family", ["description"] = "Local test boundary for family creation.",
            ["inputSchema"] = new JObject { ["type"] = "object" }
        });
        internal Func<string, JObject, Task<object>> Handler = (tool, args) => Task.FromResult<object>(new { });
        internal Task<object> CallAsync(string tool, JObject args) => Handler(tool, args);
        internal int CancellationCount;
        internal void ClearCreatedFamily() { }
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
            if (commandLine.Length == 2 && commandLine[0] == "auth" && commandLine[1] == "status")
            { Console.WriteLine("{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}"); return 0; }
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
                var window = new CodexWindow(bridge, theme, historyDirectory: System.IO.Path.GetFullPath("tmp/codex-tests/history-" + Guid.NewGuid().ToString("N")));
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
                var mepBridge = new CodexRevitBridge();
                System.Windows.ResourceDictionary mepTheme;
                using (var source = System.IO.File.OpenRead("BIMaestro/Themes/BIMaestroTheme.xaml"))
                    mepTheme = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(source);
                var mepWindow = new CodexWindow(mepBridge, mepTheme, mepMode: true, historyDirectory: System.IO.Path.GetFullPath("tmp/codex-tests/history-mep-" + Guid.NewGuid().ToString("N")));
                if (!mepBridge.ShareContext || mepBridge.AllowChanges || mepBridge.ApplyDirectly ||
                    ((CheckBox)Get(mepWindow, "changes")).IsChecked != false ||
                    ((CheckBox)Get(mepWindow, "direct")).IsChecked != false ||
                    ((CheckBox)Get(mepWindow, "separateMode")).Parent != null ||
                    ((Button)Get(mepWindow, "community")).Parent != null ||
                    ((Button)Get(mepWindow, "openDiagnostics")).Parent == null)
                    throw new Exception("MEP mode did not start read-only or still exposes family controls.");
                if (!mepWindow.Title.Contains("Assistant MEP") || !CodexWindow.MepDeveloperInstructions.Contains("revit_mep_inspect"))
                    throw new Exception("MEP mode identity or instructions are missing.");
                typeof(CodexWindow).GetMethod("Error", PrivateInstance).Invoke(mepWindow,
                    new object[] { new InvalidOperationException("Diagnostic UI test"), "test.ui", null });
                if (!((TextBox)Get(mepWindow, "transcript")).Text.Contains("Diagnostic "))
                    throw new Exception("MEP errors do not expose a diagnostic reference.");
                bool rejectedFamilyTool = false;
                try
                {
                    var call = (Task<object>)typeof(CodexWindow).GetMethod("CallRevitAsync", PrivateInstance)
                        .Invoke(mepWindow, new object[] { "revit_create_family", new JObject() });
                    Pump(call);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("n'est pas disponible"))
                {
                    rejectedFamilyTool = true;
                }
                if (!rejectedFamilyTool) throw new Exception("MEP mode accepted a family tool.");
                mepWindow.Close();
                Console.WriteLine("PASS: MEP panel starts read-only, hides family controls and rejects family tools");
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
                TestDiscussionHistory(theme, model("gpt-6-astra", true));
                TestLargeDiscussionResume(theme, model("gpt-6-astra", true));
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
        private static void TestDiscussionHistory(System.Windows.ResourceDictionary theme, object model)
        {
            string directory = System.IO.Path.GetFullPath("tmp/codex-tests/history-persistence-" + Guid.NewGuid().ToString("N"));
            var store = new CodexDiscussionHistory(directory);
            var first = new CodexWindow(new CodexRevitBridge { IsAttachedFamilyDocument = true }, theme, historyDirectory: directory);
            typeof(CodexWindow).GetMethod("BeginHistory", PrivateInstance).Invoke(first, new object[] { "Grille de ventilation été" });
            ((TextBox)Get(first, "transcript")).Text = "Vous\nCréer une grille\n\nCodex\nFamille créée : grille.rfa\n";
            ((TextBox)Get(first, "input")).Text = "Augmente la largeur";
            Set(first, "threadId", "stored-thread");
            Set(first, "lastArtifact", new CodexFamilyArtifact { FilePath = "grille.rfa" });
            typeof(CodexWindow).GetMethod("SaveHistory", PrivateInstance).Invoke(first, new object[0]);
            var saved = store.List(false, out int invalid).Single();
            if (invalid != 0 || saved.SessionId != "stored-thread" || saved.Families.Single() != "grille.rfa")
                throw new Exception("Session or RFA reference not saved: " + ((TextBlock)Get(first, "status")).Text);
            bool locked = false;
            try { using (store.Acquire(saved.Id)) { } } catch (System.IO.IOException) { locked = true; }
            if (!locked) throw new Exception("Concurrent edit was allowed.");
            first.Close();
            var reopened = new CodexWindow(new CodexRevitBridge { IsAttachedFamilyDocument = true }, theme, historyDirectory: directory);
            typeof(CodexWindow).GetMethod("RestoreHistory", PrivateInstance).Invoke(reopened, new object[] { saved });
            if (((TextBox)Get(reopened, "transcript")).Text != saved.Transcript ||
                ((TextBox)Get(reopened, "input")).Text != "Augmente la largeur" ||
                (string)Get(reopened, "pendingHistorySession") != "stored-thread" ||
                ((CheckBox)Get(reopened, "direct")).IsChecked == true || Get(reopened, "client") != null)
                throw new Exception("Offline restore lost content, draft, session or permissions.");
            using (var client = new CodexClient(System.IO.Path.Combine(directory, "fake-codex")))
            {
                Pump(client.StartAsync(Assembly.GetExecutingAssembly().Location));
                Set(reopened, "client", client); Set(reopened, "ready", true);
                var picker = (ComboBox)Get(reopened, "models"); picker.ItemsSource = new[] { model }; picker.SelectedIndex = 0;
                Pump((Task)typeof(CodexWindow).GetMethod("SendAsync", PrivateInstance).Invoke(reopened, new object[0]));
                var stateTask = client.RequestAsync("test/state", new { }); Pump(stateTask);
                if ((string)stateTask.Result["resumedThread"] != "stored-thread" ||
                    !((string)stateTask.Result["lastPrompt"]).Contains("Inspecte l'état actuel"))
                    throw new Exception("Codex history did not resume its saved session with current Revit context.");
                Set(reopened, "busy", false);
                ((Button)Get(reopened, "reset")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                if (Get(reopened, "discussion") != null || Get(reopened, "pendingHistorySession") != null ||
                    Get(reopened, "lastArtifact") != null || store.List(false, out _).Count != 1)
                    throw new Exception("New discussion erased the archive or retained previous session state.");
            }
            reopened.Close();
            var claudeSaved = new CodexDiscussion { Provider = 1, Title = "Claude grille", SessionId = "stored-claude", Transcript = "Claude\nGrille enregistrée", Model = "opus", Effort = "high" };
            store.Save(claudeSaved);
            var claudeWindow = new CodexWindow(new CodexRevitBridge { IsAttachedFamilyDocument = true }, theme, historyDirectory: directory);
            typeof(CodexWindow).GetMethod("RestoreHistory", PrivateInstance).Invoke(claudeWindow, new object[] { claudeSaved });
            string claudeStorage = System.IO.Path.Combine(directory, "claude-client");
            string claudeWorkspace = System.IO.Path.Combine(claudeStorage, "workspace");
            System.IO.Directory.CreateDirectory(claudeWorkspace);
            string claudeState = System.IO.Path.Combine(claudeWorkspace, "fake-claude-state.json");
            System.IO.File.WriteAllText(claudeState, new JObject { ["scenario"] = "history-resume", ["calls"] = new JArray() }.ToString());
            using (var claude = new ClaudeClient(System.IO.Path.GetFullPath("tmp/codex-tests/claude-cli/claude.exe"), claudeStorage))
            {
                Set(claudeWindow, "claudeClient", claude);
                Pump((Task)typeof(CodexWindow).GetMethod("ConnectClaudeAsync", PrivateInstance).Invoke(claudeWindow, new object[0]));
                ((TextBox)Get(claudeWindow, "input")).Text = "Reprends la grille";
                Pump((Task)typeof(CodexWindow).GetMethod("SendClaudeAsync", PrivateInstance).Invoke(claudeWindow, new object[0]));
                var sent = JObject.Parse(System.IO.File.ReadAllText(claudeState))["calls"].Single();
                if ((string)sent["resume"] != "stored-claude" || !((string)sent["prompt"]).Contains("Reprise d'une discussion") ||
                    !store.Read(claudeSaved.Id).Transcript.Contains("Reprise Claude réussie"))
                    throw new Exception("Claude history did not restore and persist its native session.");
            }
            claudeWindow.Close();
            var historyDialog = (System.Windows.Window)typeof(CodexWindow).GetMethod("BuildHistoryDialog", PrivateInstance)
                .Invoke(first, new object[] { store.List(false, out _), 0 });
            var historyRoot = (System.Windows.FrameworkElement)historyDialog.Content;
            historyDialog.Content = null;
            Render(new Border { Background = System.Windows.Media.Brushes.White, Child = historyRoot }, "discussion-history.png", 880, 610);
            var mep = new CodexDiscussion { Title = "MEP", Mep = true };
            store.Save(mep);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "bad json");
            if (store.List(false, out invalid).Count != 2 || invalid != 1 || store.List(true, out _).Single().Id != mep.Id)
                throw new Exception("Corrupt history or assistant-mode separation failed.");
            bool traversalRejected = false;
            try { store.Read("../escape"); } catch (System.IO.InvalidDataException) { traversalRejected = true; }
            if (!traversalRejected) throw new Exception("History accepted an unsafe id.");
            Console.WriteLine("PASS: discussion survives window closure, restores offline, resumes Codex, keeps RFA references and isolates concurrent windows/modes/corrupt entries");
            Console.WriteLine("PASS: restored Claude discussion reconnects, passes --resume and saves its continued transcript");
        }

        private static void TestLargeDiscussionResume(System.Windows.ResourceDictionary theme, object model)
        {
            const string initial = "Vous\nCréer le filtre DN 400, conserver la cote imposée 1200 mm.\n";
            const string recent = "\nCodex\nFamille créée et ouverte, DN et Variante en occurrence.\n";
            const string request = "Corrige le piquage et détaille l'intérieur.";
            // Escaping multiplies the size of control characters. Keep real Unicode
            // at the excerpt boundaries too; the archive must stay byte-for-byte intact.
            string journal = initial + string.Concat(Enumerable.Repeat("\"\\\n\tété 😀", 180000)) + recent;
            foreach (string session in new[] { null, "missing-rollout", "native-large-history", "resume-denied" })
            {
                string directory = System.IO.Path.GetFullPath("tmp/codex-tests/large-history-" + Guid.NewGuid().ToString("N"));
                var store = new CodexDiscussionHistory(directory);
                var saved = new CodexDiscussion { Title = "Gros filtre", Transcript = journal, SessionId = session,
                    Draft = request, Families = new System.Collections.Generic.List<string> { "filtre.rfa" } };
                store.Save(saved);
                var window = new CodexWindow(new CodexRevitBridge { IsAttachedFamilyDocument = true }, theme, historyDirectory: directory);
                typeof(CodexWindow).GetMethod("RestoreHistory", PrivateInstance).Invoke(window, new object[] { saved });
                using (var client = new CodexClient(System.IO.Path.Combine(directory, "fake-codex")))
                {
                    Pump(client.StartAsync(Assembly.GetExecutingAssembly().Location));
                    Set(window, "client", client); Set(window, "ready", true);
                    var picker = (ComboBox)Get(window, "models"); picker.ItemsSource = new[] { model }; picker.SelectedIndex = 0;
                    if (session == "native-large-history")
                    {
                        Set(window, "threadId", session); Set(window, "pendingHistorySession", null);
                        ((CheckBox)Get(window, "internet")).IsChecked = true;
                        if ((string)Get(window, "pendingHistorySession") != session)
                            throw new Exception("Internet configuration lost the native session.");
                    }
                    Pump((Task)typeof(CodexWindow).GetMethod("SendAsync", PrivateInstance).Invoke(window, new object[0]));
                    if (session == "resume-denied")
                    {
                        if (((TextBox)Get(window, "input")).Text != request || store.Read(saved.Id).SessionId != session ||
                            !((TextBox)Get(window, "transcript")).Text.Contains("Session access denied"))
                            throw new Exception("An unrelated resume failure was hidden or discarded the draft.");
                    }
                    else
                    {
                        var state = client.RequestAsync("test/state", new { }); Pump(state);
                        string prompt = (string)state.Result["lastPrompt"];
                        if (state.Result.Value<int>("turnStarts") != 1 || prompt == null || prompt.Length > 350000 ||
                            !prompt.StartsWith(request) || !prompt.Contains("filtre.rfa") ||
                            ((TextBox)Get(window, "input")).Text.Length != 0)
                            throw new Exception("Large discussion failed to send one bounded continuation with RFA context.");
                        bool native = session == "native-large-history";
                        if (native ? prompt.Contains("Historique partiel") :
                            !prompt.Contains("Historique partiel") || !prompt.Contains("1200 mm") || !prompt.Contains("DN et Variante"))
                            throw new Exception("Fallback omitted initial/recent context, or replayed history during native resume.");
                        if (native ? (string)state.Result["resumedThread"] != session :
                            state.Result.Value<int>("threadStarts") != 1 || store.Read(saved.Id).SessionId != "new-thread")
                            throw new Exception("Missing-session recovery or native-session preservation failed.");
                    }
                    if (!store.Read(saved.Id).Transcript.StartsWith(journal) || !store.Read(saved.Id).Families.Contains("filtre.rfa"))
                        throw new Exception("Large archive or family reference was truncated.");
                    Set(window, "turnId", null); Set(window, "busy", false); Set(window, "client", null);
                }
                window.Close();
            }
            Console.WriteLine("PASS: multi-megabyte history remains intact, fallback is bounded, missing rollout recovers once, Internet preserves native context and unrelated errors remain visible");
        }

        private static void TestClaudeAutomaticContinuation()
        {
            string cliDirectory = System.IO.Path.GetFullPath("tmp/codex-tests/claude-cli");
            System.IO.Directory.CreateDirectory(cliDirectory);
            string executable = System.IO.Path.Combine(cliDirectory, "claude.exe");
            System.IO.File.Copy(Assembly.GetExecutingAssembly().Location, executable, true);
            System.IO.File.Copy(typeof(JObject).Assembly.Location, System.IO.Path.Combine(cliDirectory, "Newtonsoft.Json.dll"), true);
            foreach (string scenario in new[] { "recover", "refuse", "images" })
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
                    var images = scenario == "images" ? new[] { new CodexImageAttachment {
                        Name = "fiche.pdf - page 1", DataUrl = "data:image/png;base64,cGFnZQ==" } } : Array.Empty<CodexImageAttachment>();
                    var task = claude.AskAsync("Créer la famille de table.", images, "test-model", "low",
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
                                    if (width == 0 && scenario != "images")
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
                if (scenario == "images")
                {
                    var sent = JObject.Parse((string)calls[0]["prompt"]);
                    if ((string)sent["message"]["content"][1]["source"]["data"] != "cGFnZQ==" ||
                        nativeWidths.Count != 1 || !messages.Contains("Famille corrigée et enregistrée."))
                        throw new Exception("PDF image payload or streamed Revit roundtrip failed.");
                    Console.WriteLine("PASS: PDF image uses streaming input/output, dispatches Revit tool and resumes with text result");
                }
                else if (scenario == "recover")
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
            bool missingResultRejected = false;
            try { ClaudeClient.ReadResult("{\"type\":\"system\"}\n", true); }
            catch (InvalidOperationException) { missingResultRejected = true; }
            if (!missingResultRejected) throw new Exception("Incomplete image stream was accepted.");

            string errorStorage = System.IO.Path.Combine(cliDirectory, "early-exit");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(errorStorage, "workspace"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(errorStorage, "workspace", "fake-claude-state.json"),
                new JObject { ["scenario"] = "early-exit", ["calls"] = new JArray() }.ToString());
            using (var claude = new ClaudeClient(executable, errorStorage))
            {
                bool diagnosticPreserved = false;
                try
                {
                    Pump(claude.AskAsync("PDF", new[] { new CodexImageAttachment {
                        DataUrl = "data:image/png;base64," + new string('A', 2000000) } }, "test-model", "low",
                        (_, __) => throw new Exception("No tool should run after a CLI failure."), _ => { }, CancellationToken.None));
                }
                catch (InvalidOperationException ex) { diagnosticPreserved = ex.Message.Contains("test CLI startup error"); }
                if (!diagnosticPreserved) throw new Exception("Broken input pipe hid the Claude startup diagnostic.");
                var diagnostic = JObject.Parse(System.IO.File.ReadAllText(claude.LastDiagnosticPath));
                if ((string)diagnostic["transport"] != ClaudeClient.TransportRevision || diagnostic.Value<int>("inputUtf8Bytes") < 2000000 ||
                    diagnostic.ToString().Contains("base64")) throw new Exception("Transport diagnostic missing or contains image payload.");
                Console.WriteLine("PASS: early CLI exit during large PDF input preserves stderr diagnostic");
            }
            foreach (string scenario in new[] { "api-error", "stall-input" })
            {
                string storage = System.IO.Path.Combine(cliDirectory, scenario);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(storage, "workspace"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(storage, "workspace", "fake-claude-state.json"),
                    new JObject { ["scenario"] = scenario, ["calls"] = new JArray() }.ToString());
                using (var claude = new ClaudeClient(executable, storage))
                using (var cancellation = new CancellationTokenSource(scenario == "stall-input" ? 500 : 5000))
                {
                    bool expected = false;
                    try
                    {
                        Pump(claude.AskAsync("PDF privé", new[] { new CodexImageAttachment { DataUrl = "data:image/png;base64," + new string('A', 6000000) } },
                            "test-model", "low", (_, __) => throw new Exception("No tool expected"), _ => { }, cancellation.Token));
                    }
                    catch (OperationCanceledException) { expected = scenario == "stall-input"; }
                    catch (InvalidOperationException ex) { expected = scenario == "api-error" && ex.Message.Contains("Image rejected by provider") && !ex.Message.Contains("private-init-marker"); }
                    if (!expected || typeof(ClaudeClient).GetField("running", PrivateInstance).GetValue(claude) != null)
                        throw new Exception("Claude transport failed cancellation/cleanup/error extraction: " + scenario);
                }
            }
            Console.WriteLine("PASS: blocked PDF upload cancels and releases process; streaming API failure shows result without init dump");
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
            if (scenario == "early-exit") { Console.Error.WriteLine("test CLI startup error"); return 1; }
            if (scenario == "stall-input") { Thread.Sleep(30000); return 1; }
            if (scenario == "api-error")
            {
                Console.In.ReadToEnd();
                Console.WriteLine("{\"type\":\"system\",\"marker\":\"private-init-marker\"}");
                Console.WriteLine("{\"type\":\"result\",\"is_error\":true,\"result\":\"Image rejected by provider\"}");
                return 1;
            }
            var calls = (JArray)state["calls"];
            int step = calls.Count;
            bool streamed = scenario == "images" && step == 0;
            int outputIndex = Array.IndexOf(commandLine, "--output-format");
            if (streamed && (outputIndex < 0 || commandLine[outputIndex + 1] != "stream-json" ||
                !commandLine.Contains("--verbose") || !commandLine.Contains("--input-format")))
            { Console.Error.WriteLine("Image input requires streaming output and verbose mode."); return 1; }
            int resumeIndex = Array.IndexOf(commandLine, "--resume");
            calls.Add(new JObject { ["resume"] = resumeIndex < 0 ? null : commandLine[resumeIndex + 1], ["prompt"] = Console.In.ReadToEnd() });
            System.IO.File.WriteAllText(statePath, state.ToString());
            if (scenario == "history-resume")
            {
                Console.WriteLine(new JObject { ["session_id"] = "stored-claude", ["structured_output"] = new JObject {
                    ["reply"] = "Reprise Claude réussie", ["tool"] = "", ["arguments"] = new JObject(), ["done"] = true } }.ToString(Newtonsoft.Json.Formatting.None));
                return 0;
            }
            bool toolCall = step == 0 || scenario == "recover" && (step == 2 || step == 3);
            var answer = new JObject {
                ["reply"] = toolCall ? "" : scenario == "refuse" ? "Création refusée, arrêt de la demande." : step == 1 && scenario != "images" ? "Je m'arrête après cet échec." : "Famille corrigée et enregistrée.",
                ["tool"] = toolCall ? "revit_create_family" : "",
                ["arguments"] = toolCall ? new JObject { ["width_mm"] = step == 3 ? 100 : 0 } : new JObject(),
                ["done"] = !toolCall
            };
            if (streamed) Console.WriteLine("{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"assistant\",\"message\":{}}\n");
            Console.WriteLine(new JObject { ["type"] = "result", ["session_id"] = "fake-claude-" + scenario, ["structured_output"] = answer }.ToString(Newtonsoft.Json.Formatting.None));
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
            int threadStarts = 0;
            string resumedThread = null;
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
                if (method == "account/read") { Reply(id, new JObject { ["account"] = new JObject { ["type"] = "chatgpt" } }); continue; }
                if (method == "thread/resume")
                {
                    resumedThread = (string)request["params"]["threadId"];
                    if (resumedThread == "missing-rollout" || resumedThread == "resume-denied")
                    {
                        Console.WriteLine(new JObject { ["id"] = id, ["error"] = new JObject { ["code"] = -32600,
                            ["message"] = resumedThread == "missing-rollout" ? "no rollout found for thread id missing-rollout" : "Session access denied" } }.ToString(Newtonsoft.Json.Formatting.None));
                        continue;
                    }
                    Reply(id, new JObject { ["thread"] = new JObject { ["id"] = resumedThread } }); continue;
                }
                if (method == "thread/start")
                {
                    threadStarts++;
                    Reply(id, new JObject { ["thread"] = new JObject { ["id"] = "new-thread" } }); continue;
                }
                if (method == "turn/start")
                {
                    lastPrompt = (string)request["params"]?["input"]?.First?["text"];
                    if (lastPrompt?.Length > 1048576)
                    {
                        Console.WriteLine(new JObject { ["id"] = id, ["error"] = new JObject { ["code"] = -32600,
                            ["message"] = "Input exceeds the maximum length of 1048576 characters." } }.ToString(Newtonsoft.Json.Formatting.None));
                        continue;
                    }
                    turnStarts++;
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
                if (method == "test/state") { Reply(id, new JObject { ["turnStarts"] = turnStarts, ["threadStarts"] = threadStarts, ["lastPrompt"] = lastPrompt, ["resumedThread"] = resumedThread }); continue; }
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
