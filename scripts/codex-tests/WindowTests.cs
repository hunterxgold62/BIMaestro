using Newtonsoft.Json.Linq;
using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace BIMaestro.Codex
{
    internal static class CodexDiagnostics
    {
        internal static string RecordFailure(string tool, JObject args, Exception error) => "test-diagnostic.json";
    }
    internal sealed class CodexFamilyArtifact { internal string FilePath { get; set; } internal string PreviewPath { get; set; } internal object Report { get; set; } }
    // UI regression tests use the real window without loading Revit or starting Codex.
    internal sealed class CodexRevitBridge : IDisposable
    {
        internal bool ShareContext { get; set; }
        internal bool AllowChanges { get; set; }
        internal bool ApplyDirectly { get; set; }
        internal string DocumentTitle => "Test";
        internal static JArray ToolDefinitions() => new JArray();
        internal Func<string, JObject, Task<object>> Handler = (tool, args) => Task.FromResult<object>(new { });
        internal Task<object> CallAsync(string tool, JObject args) => Handler(tool, args);
        internal void CancelPending() { }
        public void Dispose() { }
    }
    internal static class WindowTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        [STAThread]
        private static int Main()
        {
            try
            {
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
                var image = CodexImageAttachment.FromBitmap(bitmap, "test");
                if (!image.DataUrl.StartsWith("data:image/png;base64,")) throw new Exception("Image input was not encoded as PNG");
                Console.WriteLine("PASS: image attachment encoded for vision input");
                var bridge = new CodexRevitBridge();
                var window = new CodexWindow(bridge);
                if (bridge.ApplyDirectly || ((CheckBox)Get(window, "direct")).IsEnabled) throw new Exception("Direct mode enabled initially");
                ((CheckBox)Get(window, "context")).IsChecked = true;
                ((CheckBox)Get(window, "changes")).IsChecked = true;
                ((CheckBox)Get(window, "direct")).IsChecked = true;
                if (!bridge.ApplyDirectly) throw new Exception("Direct mode not applied");
                ((CheckBox)Get(window, "context")).IsChecked = false;
                if (bridge.ApplyDirectly || bridge.AllowChanges) throw new Exception("Revoked permissions remained active");
                Console.WriteLine("PASS: direct mode requires opt-in and is revoked with context");
                foreach (string state in new[] { "completed", "interrupted", "failed" })
                {
                    Set(window, "threadId", "test-thread"); Set(window, "turnId", "test-turn"); Set(window, "busy", true);
                    Notify(window, "turn/completed", JObject.Parse("{\"threadId\":\"test-thread\",\"turn\":{\"status\":\"" + state + "\",\"error\":null}}"));
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
                    Set(window, "client", client); Set(window, "threadId", "test-thread"); Set(window, "turnId", "test-turn"); Set(window, "busy", true);
                    var arguments = new JObject { ["parts"] = new JArray() };
                    var call = new JObject { ["threadId"] = "test-thread", ["turnId"] = "test-turn", ["tool"] = "revit_validate_family", ["arguments"] = arguments };
                    bridge.Handler = (tool, args) => Task.FromException<object>(new InvalidOperationException("Pièce « barbe » : le profil se croise."));
                    var failed = Request(window, client, call);
                    string errorText = (string)failed["contentItems"][0]["text"];
                    if (failed.Value<bool>("success") || !errorText.Contains("barbe") || !errorText.Contains("test-diagnostic.json")) throw new Exception("Tool error was lost");
                    if (!((TextBox)Get(window, "transcript")).Text.Contains("barbe")) throw new Exception("Tool error not displayed");
                    Console.WriteLine("PASS: exact piece failure reaches both chat and model");
                    var original = new CodexFamilyArtifact { FilePath = "original.rfa" }; Set(window, "lastArtifact", original);
                    bridge.Handler = (tool, args) => Task.FromResult<object>(new CodexFamilyArtifact { Report = new { validated = true, saved = false } });
                    var validated = Request(window, client, call);
                    if (!validated.Value<bool>("success") || !ReferenceEquals(Get(window, "lastArtifact"), original)) throw new Exception("Validation replaced last saved artifact");
                    Console.WriteLine("PASS: successful dry run does not claim or replace saved RFA");
                    bool invoked = false;
                    bridge.Handler = (tool, args) => { invoked = true; return Task.FromResult<object>(new { }); };
                    call["turnId"] = "stale-turn";
                    if (Request(window, client, call).Value<bool>("success") || invoked) throw new Exception("Stale tool call reached Revit");
                    Console.WriteLine("PASS: stale turn rejected before Revit execution");
                    Set(window, "client", null); Set(window, "busy", false);
                }
                // Render the actual production layout without opening a native window.
                ((TextBox)Get(window, "transcript")).Text = "Décrivez votre objet ou joignez une image, avec les dimensions connues.\n\nLe résultat sera enregistré dans un nouveau fichier RFA, avec ses matériaux et un aperçu.";
                ((TextBox)Get(window, "input")).Text = "Crée une famille de transformateur électrique d'après cette image, puis charge-la dans le projet.";
                var root = (System.Windows.FrameworkElement)window.Content;
                window.Content = null;
                ((DockPanel)root).Background = window.Background;
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                root.Measure(new System.Windows.Size(624, 830));
                root.Arrange(new System.Windows.Rect(0, 0, 624, 830)); root.UpdateLayout();
                var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(624, 830, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                preview.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                using (var file = System.IO.File.Create("tmp/codex-tests/codex-window.png")) encoder.Save(file);
                window.Close();
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, PrivateInstance).SetValue(target, value);
        private static object Get(object target, string field) => target.GetType().GetField(field, PrivateInstance).GetValue(target);
        private static void Notify(object target, string method, JObject value) => target.GetType().GetMethod("OnNotification", PrivateInstance).Invoke(target, new object[] { method, value });
        private static JObject Request(object target, CodexClient client, JObject data)
        {
            var task = (Task<object>)target.GetType().GetMethod("HandleRequestAsync", PrivateInstance).Invoke(target, new object[] { client, "item/tool/call", data });
            return JObject.FromObject(task.GetAwaiter().GetResult());
        }
    }
}
