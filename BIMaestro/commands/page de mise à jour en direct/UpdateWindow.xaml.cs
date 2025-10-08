// UpdateWindow.xaml.cs
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Page
{
    public partial class UpdateWindow : Window
    {
        private const string SiteUrl = "https://sites.google.com/view/bimaestro";

        // Hosts autorisés (tout le reste s'ouvre dans le navigateur)
        private static readonly HashSet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sites.google.com",
            "www.google.com",
            "googleusercontent.com",
            "lh3.googleusercontent.com",
            "fonts.googleapis.com",
            "fonts.gstatic.com",
            "ssl.gstatic.com",
            "gstatic.com",
            "drive.google.com",
            "docs.google.com"
        };

        private Version _current = new Version(0, 0, 0);
        private Version _latest; // nullable par usage

        private bool _externalOpenedOnce;

        public UpdateWindow()
        {
            InitializeComponent();
            SetCurrentVersionChip();     // v locale depuis AssemblyFileVersion
            _ = InitAsync();             // tente WebView2 + fallback auto
        }

        // ======== VERSION LOCALE (AssemblyInfo.cs) ========
        private void SetCurrentVersionChip()
        {
            string verStr = GetCurrentVersionString();
            var parsed = ParseVersion(verStr);
            _current = parsed ?? new Version(0, 0, 0);
            CurrentText.Text = "Plugin v" + _current;   // affichera 1.0.5.4 si 4 parties, sinon 1.0.5
        }

        private static string GetCurrentVersionString()
        {
            var asm = typeof(UpdateWindow).Assembly;
            var fileVer = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            if (!string.IsNullOrEmpty(fileVer)) return fileVer;

            var asmVer = asm.GetName().Version != null ? asm.GetName().Version.ToString() : null;
            return string.IsNullOrEmpty(asmVer) ? "0.0.0.0" : asmVer;
        }

        // ✅ NEW: détecte 4 parties d’abord (X.Y.Z.W), sinon 3 (X.Y.Z)
        private static Version ParseVersion(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;

            // 4-parties
            var m4 = Regex.Match(input, @"\b(\d+)\.(\d+)\.(\d+)\.(\d+)\b");
            if (m4.Success)
                return new Version(
                    int.Parse(m4.Groups[1].Value),
                    int.Parse(m4.Groups[2].Value),
                    int.Parse(m4.Groups[3].Value),
                    int.Parse(m4.Groups[4].Value));

            // 3-parties
            var m3 = Regex.Match(input, @"\b(\d+)\.(\d+)\.(\d+)\b");
            if (m3.Success)
                return new Version(
                    int.Parse(m3.Groups[1].Value),
                    int.Parse(m3.Groups[2].Value),
                    int.Parse(m3.Groups[3].Value));

            return null;
        }

        private void UpdateLatestChipUI()
        {
            if (_latest == null)
            {
                LatestChip.Visibility = Visibility.Collapsed;
                return;
            }

            LatestChip.Visibility = Visibility.Visible;
            var bc = new System.Windows.Media.BrushConverter();

            if (_current.CompareTo(_latest) >= 0)
            {
                // À jour
                LatestChip.Background = (System.Windows.Media.Brush)bc.ConvertFrom("#213428");
                LatestChip.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFrom("#2B5C3A");
                LatestText.Foreground = (System.Windows.Media.Brush)bc.ConvertFrom("#B8F5C8");
                LatestText.Text = "À jour (dernière v" + _latest + ")";
            }
            else
            {
                // MàJ dispo
                LatestChip.Background = (System.Windows.Media.Brush)bc.ConvertFrom("#3A2A1E");
                LatestChip.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFrom("#6B4A2D");
                LatestText.Foreground = (System.Windows.Media.Brush)bc.ConvertFrom("#FFD9B3");
                LatestText.Text = "MàJ dispo v" + _latest;
            }
        }

        // ======== INIT WEBVIEW2 (compat .NET Framework) ========
        private async Task InitAsync()
        {
            try
            {
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            "BIMaestro", "WebView2Cache");
                Directory.CreateDirectory(cache);

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
                await Web.EnsureCoreWebView2Async(env);

                TrySetSetting(Web.CoreWebView2.Settings, "IsStatusBarEnabled", false);
                TrySetSetting(Web.CoreWebView2.Settings, "AreDefaultContextMenusEnabled", true);
                TrySetSetting(Web.CoreWebView2.Settings, "AreDevToolsEnabled", false);
                TrySetSetting(Web.CoreWebView2.Settings, "AreBrowserAcceleratorKeysEnabled", true);

                Web.NavigationStarting += Web_NavigationStarting;
                Web.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

                TryLogLoadedWebView2Version();

                Web.Source = new Uri(SiteUrl);

                Web.NavigationCompleted += async (s, e) =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    Web.Visibility = Visibility.Visible;

                    await TryDetectLatestFromDomAsync(); // lit X.Y.Z[.W] depuis le texte de la page
                    UpdateLatestChipUI();
                };
            }
            catch (Exception ex)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;

                if (!_externalOpenedOnce)
                {
                    _externalOpenedOnce = true;
                    OpenExternal(SiteUrl);
                }
                FallbackPanel.Visibility = Visibility.Visible;

                _ = TryFetchLatestViaHttp(); // extraction X.Y.Z[.W] côté HTTP

                Debug.WriteLine("[WebView2] Init failed: " + ex.Message);
            }
        }

        private static void TrySetSetting(object settings, string propertyName, object value)
        {
            try
            {
                var p = settings.GetType().GetProperty(propertyName);
                if (p != null && p.CanWrite) p.SetValue(settings, value, null);
            }
            catch { }
        }

        private static void TryLogLoadedWebView2Version()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = a.GetName();
                    if (n.Name == "Microsoft.Web.WebView2.Core" || n.Name == "Microsoft.Web.WebView2.Wpf")
                        Debug.WriteLine("[WebView2] Loaded " + n.Name + " v" + n.Version + " from " + a.Location);
                }
            }
            catch { }
        }

        // ======== VERSION DISTANTE (sans System.Text.Json) ========
        private static string UnwrapJsonString(string json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;

            // ExecuteScriptAsync renvoie souvent "\"texte\""
            if (json.Length >= 2 && json[0] == '"' && json[json.Length - 1] == '"')
                json = json.Substring(1, json.Length - 2);

            return json.Replace("\\n", "\n")
                       .Replace("\\r", "\r")
                       .Replace("\\t", "\t")
                       .Replace("\\\"", "\"")
                       .Replace("\\\\", "\\");
        }

        private async Task TryDetectLatestFromDomAsync()
        {
            try
            {
                var js = @"(function(){
                    try{
                        var t = '';
                        var h = document.querySelector('h1,h2,h3');
                        if(h) t += h.textContent + '\n';
                        t += document.body.innerText;
                        return t;
                    }catch(e){ return ''; }
                })();";
                string json = await Web.CoreWebView2.ExecuteScriptAsync(js);
                string text = UnwrapJsonString(json);
                var v = ParseVersion(text);      // ➜ lit 1.0.5.4 si présent
                if (v != null) _latest = v;
            }
            catch { }
        }

        private async Task TryFetchLatestViaHttp()
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(10);
                    string html = await http.GetStringAsync(SiteUrl);
                    var v = ParseVersion(html);   // ➜ lit 1.0.5.4 si présent
                    if (v != null)
                    {
                        _latest = v;
                        Dispatcher.Invoke(new Action(UpdateLatestChipUI));
                    }
                }
            }
            catch { }
        }

        // ======== REDIR LIENS & BLOQUE LOGIN ========
        private void Web_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri)) return;

            // Si Google redirige vers le login -> ouvrir dehors
            if (uri.Host.IndexOf("accounts.google.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                e.Cancel = true;
                OpenExternal(SiteUrl);
                return;
            }

            if (AllowedHosts.Contains(uri.Host)) return;

            e.Cancel = true;
            OpenExternal(uri.AbsoluteUri);
        }

        private void CoreWebView2_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            Uri uri;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out uri))
            {
                if (uri.Host.IndexOf("accounts.google.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || !AllowedHosts.Contains(uri.Host))
                {
                    e.Handled = true;
                    OpenExternal(uri.AbsoluteUri);
                }
            }
        }

        private static void OpenExternal(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossible d'ouvrir le lien : " + ex.Message, "Ouverture du lien",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ======== UI ========
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (Web.CoreWebView2 != null) Web.Reload();
            else OpenExternal(SiteUrl);
        }

        private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => OpenExternal(SiteUrl);

        private void CopyLink_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(SiteUrl);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.F5) Refresh_Click(sender, e);
        }
    }
}
