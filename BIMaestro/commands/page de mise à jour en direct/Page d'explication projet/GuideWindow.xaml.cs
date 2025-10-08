// GuideWindow.xaml.cs
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Page
{
    public partial class GuideWindow : Window
    {
        // 👉 Remplace par l'URL réelle de ta page "comment ça marche"
        private const string GuideUrl = "https://sites.google.com/view/guide-bimaestro";

        // Autoriser Google Sites et assets nécessaires (on bloque 'accounts.google.com' pour éviter le login intégré)
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

        public GuideWindow()
        {
            InitializeComponent();
            SetCurrentVersionChip();     // v locale depuis AssemblyFileVersion
            _ = InitAsync();             // tente WebView2 + fallback auto
        }

        // ======== VERSION LOCALE (AssemblyInfo.cs) ========
        private void SetCurrentVersionChip()
        {
            string verStr = GetCurrentVersionString();
            Version v = ParseVersion(verStr) ?? new Version(0, 0, 0, 0);
            CurrentText.Text = "Plugin v" + v; // affiche X.Y.Z.W si présent
        }

        private static string GetCurrentVersionString()
        {
            var asm = typeof(GuideWindow).Assembly;
            var fileVer = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            if (!string.IsNullOrEmpty(fileVer)) return fileVer;
            var asmVer = asm.GetName().Version != null ? asm.GetName().Version.ToString() : null;
            return string.IsNullOrEmpty(asmVer) ? "0.0.0.0" : asmVer;
        }

        private static Version ParseVersion(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;

            // 4 parties
            var m4 = System.Text.RegularExpressions.Regex.Match(input, @"\b(\d+)\.(\d+)\.(\d+)\.(\d+)\b");
            if (m4.Success)
                return new Version(
                    int.Parse(m4.Groups[1].Value),
                    int.Parse(m4.Groups[2].Value),
                    int.Parse(m4.Groups[3].Value),
                    int.Parse(m4.Groups[4].Value));

            // 3 parties
            var m3 = System.Text.RegularExpressions.Regex.Match(input, @"\b(\d+)\.(\d+)\.(\d+)\b");
            if (m3.Success)
                return new Version(
                    int.Parse(m3.Groups[1].Value),
                    int.Parse(m3.Groups[2].Value),
                    int.Parse(m3.Groups[3].Value));

            return null;
        }

        // ======== INIT WEBVIEW2 ========
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

                Web.Source = new Uri(GuideUrl);

                Web.NavigationCompleted += (s, e) =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    Web.Visibility = Visibility.Visible;
                };
            }
            catch (Exception ex)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;

                OpenExternal(GuideUrl);       // fallback auto
                FallbackPanel.Visibility = Visibility.Visible;

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

        // ======== REDIR LIENS & BLOQUE LOGIN ========
        private void Web_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri)) return;

            // Si Google redirige vers le login -> ouvrir dehors (où l'utilisateur est peut-être déjà connecté)
            if (uri.Host.IndexOf("accounts.google.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                e.Cancel = true;
                OpenExternal(GuideUrl);
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
            else OpenExternal(GuideUrl);
        }

        private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => OpenExternal(GuideUrl);

        private void CopyLink_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(GuideUrl);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.F5) Refresh_Click(sender, e);
        }
    }
}
