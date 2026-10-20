using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;

namespace BIMaestro.Home
{
    // The browser integration is deliberately opt-in and restricted to Revit Home.
    internal static class HomeAppearanceService
    {
        const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMaestro", "HomeAppearance.test.json");
        static readonly List<WeakReference> browsers = new List<WeakReference>();
        static readonly HashSet<object> pending = new HashSet<object>(ReferenceComparer.Instance);
        static readonly object requestGate = new object();
        static DateTime nextScan, nextTick;
        static bool initialized, restorePending;
        static string openPath;
        static string openId;
        static string acknowledgement;
        static long acknowledgementGeneration;
        static string resourceScript;
        static string resourceRevision;
        static string historyJson, historyRevision;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BrowserState> browserStates = new System.Runtime.CompilerServices.ConditionalWeakTable<object, BrowserState>();
        internal static string CurrentStyle { get; private set; } = "native";
        internal static bool HistoryEnabled { get; private set; }
        internal static string LastStatus { get; private set; } = "Accueil Autodesk conservé.";

        internal static void ConfigureTestSettingsPath(string path) { if (initialized) throw new InvalidOperationException("Configure before Initialize"); settingsPath = path; }
        internal static void Initialize(UIControlledApplication application)
        {
            if (initialized) return;
            HomeHistoryCatalog.ConfigureVersion(application.ControlledApplication.VersionNumber);
            try
            {
                if (File.Exists(settingsPath))
                {
                    var saved = JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(settingsPath));
                    if (saved != null && IsStyle(saved.Style)) { CurrentStyle = saved.Style; HistoryEnabled = saved.History; }
                }
            }
            catch (Exception ex) { Log("Preferences: " + ex.Message); }
            application.Idling += OnIdling;
            initialized = true;
            if (HistoryEnabled) HomeHistoryCatalog.RequestRefresh();
        }
        internal static void Shutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            initialized = false;
            browsers.Clear();
        }
        internal static void SetPreferences(string style, bool history)
        {
            if (!IsStyle(style)) throw new ArgumentException("Affichage inconnu.", nameof(style));
            var json = JsonConvert.SerializeObject(new Preferences { Style = style, History = history }, Formatting.Indented);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            var temporary = settingsPath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(temporary, json);
            try { if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null); else File.Move(temporary, settingsPath); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            CurrentStyle = style; HistoryEnabled = history; restorePending = true;
            if (history) HomeHistoryCatalog.RequestRefresh();
            RequestRefresh();
        }
        static bool IsStyle(string style) => new[] { "native", "2023", "2024", "2025", "2026" }.Contains(style);
        internal static void RequestRefresh() { nextScan = DateTime.MinValue; nextTick = DateTime.MinValue; }
        internal static bool IsDarkTheme()
        {
            try
            {
                var manager = typeof(UIApplication).Assembly.GetType("Autodesk.Revit.UI.UIThemeManager");
                return string.Equals(Convert.ToString(manager?.GetProperty("CurrentTheme", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null)), "Dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; } // 2023 does not expose a dark application theme.
        }
        static void OnIdling(object sender, IdlingEventArgs args)
        {
            var ui = sender as UIApplication;
            if (ui == null) return;
            ProcessOpen(ui);
            if (DateTime.UtcNow < nextTick) return;
            nextTick = DateTime.UtcNow.AddSeconds(1);
            if (CurrentStyle == "native" && !HistoryEnabled && !restorePending) return;
            try
            {
                if (DateTime.UtcNow >= nextScan)
                {
                    Discover(); nextScan = DateTime.UtcNow.AddSeconds(8);
                    if (HistoryEnabled) HomeHistoryCatalog.RequestRefresh();
                }
                bool dark = IsDarkTheme();
                foreach (var weak in browsers.ToArray())
                {
                    var browser = weak.Target;
                    if (browser == null) { browsers.Remove(weak); continue; }
                    lock (pending) { if (!pending.Add(browser)) continue; }
                    if (!Evaluate(browser, BuildScript(dark, browserStates.GetOrCreateValue(browser)))) lock (pending) pending.Remove(browser);
                }
                // Keep restoration armed until a Home page positively acknowledges it.
            }
            catch (Exception ex) { LastStatus = "Accueil : " + ex.Message; Log(ex.ToString()); }
        }
        static string ReadResource(string name)
        {
            using (var stream = typeof(HomeAppearanceService).Assembly.GetManifestResourceStream("BIMaestro.Home." + name))
            {
                if (stream == null) throw new InvalidOperationException("Ressource accueil absente : " + name);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        static string BuildScript(bool dark, BrowserState state)
        {
            if (HistoryEnabled)
            {
                var current = HomeHistoryCatalog.GetPayloadJson();
                if (!ReferenceEquals(current, historyJson)) { historyJson = current; historyRevision = Guid.NewGuid().ToString("N"); }
            }
            if (resourceScript == null)
            {
                resourceScript = ReadResource("home-presets.js") + "\n" + ReadResource("home-legacy.js") + "\n" + ReadResource("home-modern.js") + "\n" + ReadResource("home-history.js");
                using (var sha = System.Security.Cryptography.SHA256.Create()) resourceRevision = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(resourceScript)));
            }
            string token = JsonConvert.SerializeObject(resourceRevision + CurrentStyle + dark);
            string ack; long ackGeneration;
            lock (requestGate) { ack = acknowledgement; ackGeneration = acknowledgementGeneration; }
            string restore = "['BIMaestroHomeHistory','BIMaestroHomeConceptLegacy','BIMaestroHomeConceptModern','BIMaestroHomeProbe'].forEach(function(k){if(window[k]&&window[k].restore)window[k].restore();delete window[k];});";
            return "(function(){var u=String(location.href).replace(/\\\\/g,'/');if(!/\\/ModelBrowser\\/resource\\/(?:index|udabrowser)\\.html(?:[?#]|$)/i.test(u))return JSON.stringify({home:false});"
                + "if(window.__bmHomeFeatureRevision!==" + token + "){"
                + "if(window.__bmLogCatalogProbe){window.__bmLogCatalogProbe.restore();delete window.__bmLogCatalogProbe;}"
                + restore + "window.__bmHomeDark=" + (dark ? "true" : "false") + ";" + resourceScript
                + (CurrentStyle == "native" ? "" : "var api=window.BIMaestroHomeConceptLegacy||window.BIMaestroHomeConceptModern;if(api){var applied=api.apply(" + JsonConvert.SerializeObject(CurrentStyle) + ");if(applied&&applied.applied===false)return JSON.stringify({home:true,waiting:true});}")
                + "window.__bmHomeFeatureRevision=" + token + ";}"
                + (HistoryEnabled ? (state.HistoryRevision != historyRevision ? "window.__bmHomePayload=" + historyJson + ";window.__bmHomePayloadRevision=" + JsonConvert.SerializeObject(historyRevision) + ";" : "") + "if(window.BIMaestroHomeHistory)window.BIMaestroHomeHistory.update(window.__bmHomePayload||{status:'loading'});" : "if(window.BIMaestroHomeHistory)window.BIMaestroHomeHistory.restore();")
                + (ack ?? "")
                + "var request=window.__bmHomeOpenRequest||null;window.__bmHomeOpenRequest=null;var thumbs=(window.__bmHomeThumbnailRequests||[]).splice(0,8);return JSON.stringify({home:true,historyRevision:window.__bmHomePayloadRevision||null,thumbnails:thumbs,ack:" + (ack == null ? 0 : ackGeneration) + ",restored:" + (CurrentStyle == "native" && !HistoryEnabled ? "true" : "false") + ",request:request});})()";
        }
        static void ProcessOpen(UIApplication app)
        {
            string path, id;
            lock (requestGate) { path = openPath; id = openId; openPath = null; openId = null; }
            if (path == null) return;
            string error = null;
            try
            {
                if (!HistoryEnabled || !HomeHistoryCatalog.IsKnownPath(path)) throw new InvalidOperationException("Ce chemin ne fait plus partie du catalogue actif.");
                // Revit performs its normal version, worksharing and missing-file checks.
                app.OpenAndActivateDocument(path);
            }
            catch (Exception ex) { error = "Ouverture impossible : " + ex.Message; }
            lock (requestGate)
            {
                acknowledgementGeneration++;
                acknowledgement = "if(window.BIMaestroHomeHistory)window.BIMaestroHomeHistory.acknowledgeOpen(" + JsonConvert.SerializeObject(id) + "," + JsonConvert.SerializeObject(error) + ");";
            }
        }
        static bool Evaluate(object browser, string script)
        {
            try
            {
                var extensions = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CefSharp.WebBrowserExtensions", false)).FirstOrDefault(t => t != null);
                var method = extensions?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "EvaluateScriptAsync" && m.GetParameters().Length >= 2 && m.GetParameters()[0].ParameterType.IsInstanceOfType(browser) && m.GetParameters()[1].ParameterType == typeof(string) && m.GetParameters().Skip(2).All(p => p.IsOptional));
                object value;
                if (method != null) value = method.Invoke(null, method.GetParameters().Select((p, i) => i == 0 ? browser : i == 1 ? (object)script : Type.Missing).ToArray());
                else
                {
                    method = browser.GetType().GetMethods().FirstOrDefault(m => (m.Name == "ExecuteScriptAsync" || m.Name == "EvaluateScriptAsync") && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters().Skip(1).All(p => p.IsOptional));
                    if (method == null) return false;
                    value = method.Invoke(browser, method.GetParameters().Select((p, i) => i == 0 ? (object)script : Type.Missing).ToArray());
                }
                var task = value as Task;
                if (task == null) return false;
                task.ContinueWith(done =>
                {
                    try
                    {
                        if (done.IsFaulted) { Log("Browser: " + done.Exception?.GetBaseException().Message); return; }
                        var result = done.GetType().GetProperty("Result")?.GetValue(done, null);
                        var success = result?.GetType().GetProperty("Success")?.GetValue(result, null);
                        if (success is bool && !(bool)success) return;
                        var output = result?.GetType().GetProperty("Result")?.GetValue(result, null) ?? result;
                        var text = Convert.ToString(output);
                        if (string.IsNullOrWhiteSpace(text)) return;
                        if (text.StartsWith("\"", StringComparison.Ordinal)) text = JsonConvert.DeserializeObject<string>(text);
                        var response = JsonConvert.DeserializeObject<BrowserResponse>(text);
                        if (response?.Home != true) return;
                        browserStates.GetOrCreateValue(browser).HistoryRevision = response.HistoryRevision;
                        if (HistoryEnabled && response.Thumbnails != null) HomeHistoryCatalog.RequestThumbnails(response.Thumbnails);
                        LastStatus = response.Waiting ? "Accueil en cours de chargement." : "Affichage appliqué à l’accueil.";
                        if (response.Restored) restorePending = false;
                        lock (requestGate) { if (response.Ack != 0 && response.Ack == acknowledgementGeneration) acknowledgement = null; }
                        if (response.Request != null && HomeHistoryCatalog.IsKnownPath(response.Request.Path))
                            lock (requestGate) { if (openPath == null) { openPath = response.Request.Path; openId = response.Request.Id; } }
                    }
                    catch (Exception ex) { Log("Browser response: " + ex.Message); }
                    finally { lock (pending) pending.Remove(browser); }
                });
                return true;
            }
            catch (Exception ex) { Log("Browser invoke: " + ex.GetBaseException().Message); return false; }
        }
        static void Discover()
        {
            var seen = new HashSet<object>(ReferenceComparer.Instance); var found = new List<object>();
            if (Application.Current != null) foreach (Window w in Application.Current.Windows) Walk(w, seen, found, 0);
            foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>()) Walk(source.RootVisual, seen, found, 0);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.IndexOf("ApplicationHome", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Type[] types; try { types = assembly.GetTypes(); } catch { continue; }
                foreach (var type in types.Where(t => (t.FullName ?? "").IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0 || (t.FullName ?? "").IndexOf("ModelBrowser", StringComparison.OrdinalIgnoreCase) >= 0))
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                        try { if (!field.FieldType.IsValueType && field.FieldType != typeof(string)) Walk(field.GetValue(null), seen, found, 0); } catch { }
            }
            browsers.Clear(); foreach (var item in found.Distinct(ReferenceComparer.Instance)) browsers.Add(new WeakReference(item));
        }
        static void Walk(object value, HashSet<object> seen, List<object> found, int depth)
        {
            if (value == null || depth > 18 || seen.Count > 30000 || !seen.Add(value)) return;
            var type = value.GetType(); var name = type.FullName ?? "";
            if (name.IndexOf("ChromiumWebBrowser", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0) found.Add(value);
            var dependency = value as DependencyObject;
            if (dependency != null)
            {
                try { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dependency); i++) Walk(VisualTreeHelper.GetChild(dependency, i), seen, found, depth + 1); } catch { }
                try { foreach (var child in LogicalTreeHelper.GetChildren(dependency)) if (!(child is string)) Walk(child, seen, found, depth + 1); } catch { }
            }
            if (depth < 8 && (name.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("ModelBrowser", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Cef", StringComparison.OrdinalIgnoreCase) >= 0))
                foreach (var field in type.GetFields(InstanceFlags)) try { if (!field.FieldType.IsValueType && field.FieldType != typeof(string)) Walk(field.GetValue(value), seen, found, depth + 1); } catch { }
            foreach (var key in new[] { "Content", "Child", "Browser", "WebBrowser", "WebView", "CoreWebView2" })
                try { var property = type.GetProperty(key, InstanceFlags); if (property != null && property.GetIndexParameters().Length == 0) Walk(property.GetValue(value, null), seen, found, depth + 1); } catch { }
        }
        static void Log(string text) { System.Diagnostics.Trace.WriteLine("[BIMaestro Home] " + text); }
        sealed class Preferences { public string Style { get; set; } = "native"; public bool History { get; set; } }
        sealed class BrowserState { public BrowserState() { } public string HistoryRevision; }
        sealed class BrowserResponse { public bool Home { get; set; } public bool Restored { get; set; } public bool Waiting { get; set; } public long Ack { get; set; } public string HistoryRevision { get; set; } public string[] Thumbnails { get; set; } public OpenRequest Request { get; set; } }
        sealed class OpenRequest { public string Id { get; set; } public string Path { get; set; } }
        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
