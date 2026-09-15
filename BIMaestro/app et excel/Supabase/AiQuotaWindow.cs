using BIMaestro.Localization;
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Licensing
{
    internal sealed class AiQuotaWindow : Window
    {
        private static int _pendingOrVisible;
        private static DateTime _lastClosedUtc = DateTime.MinValue;

        // Never block a worker: Revit commands can be waiting for their AI batches.
        internal static void NotifyQuotaExceeded()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted ||
                Interlocked.CompareExchange(ref _pendingOrVisible, 1, 0) != 0) return;
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        // Coalesce late responses from the same parallel batch.
                        if (DateTime.UtcNow - _lastClosedUtc < TimeSpan.FromSeconds(30))
                        {
                            Interlocked.Exchange(ref _pendingOrVisible, 0);
                            return;
                        }
                        var window = new AiQuotaWindow();
                        var handle = BIMaestro.Welcome.RevitWindowHandle.GetRevitMainWindowHandle();
                        if (handle != IntPtr.Zero) new WindowInteropHelper(window).Owner = handle;
                        window.Closed += (_, __) =>
                        {
                            _lastClosedUtc = DateTime.UtcNow;
                            Interlocked.Exchange(ref _pendingOrVisible, 0);
                        };
                        window.Show();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("AI quota window: " + ex.Message);
                        Interlocked.Exchange(ref _pendingOrVisible, 0);
                    }
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AI quota notification: " + ex.Message);
                Interlocked.Exchange(ref _pendingOrVisible, 0);
            }
        }

        internal AiQuotaWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/BIMaestro;component/Themes/BIMaestroTheme.xaml", UriKind.Relative)
            });
            Title = UiLanguage.T("BIMaestro — Limite IA atteinte", "BIMaestro — AI limit reached");
            Width = 610;
            Height = 620;
            MinWidth = 480;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "App.Background");

            var content = new StackPanel { Margin = new Thickness(26) };
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var heading = Paragraph(UiLanguage.T("Votre limite IA est atteinte", "You have reached your AI limit"), 25);
            heading.FontWeight = FontWeights.Bold;
            content.Children.Add(heading);
            content.Children.Add(Paragraph(UiLanguage.T(
                "Besoin de continuer ? Contactez-moi.", "Need to continue? Get in touch."), 18));
            content.Children.Add(Paragraph(UiLanguage.T(
                "Je ne vérifie pas systématiquement les quotas de chacun et leur augmentation n’est pas automatique. Il vous suffit de me contacter : je pourrai modifier votre limite pour que vous puissiez continuer à utiliser l’IA.",
                "I don’t systematically review everyone’s quotas, and increases are not automatic. Just contact me: I can adjust your limit so you can keep using AI.")));
            content.Children.Add(Paragraph(UiLanguage.T(
                "Les appels à l’IA ont un coût que je prends personnellement en charge. Si vous le souhaitez, un don, même très faible, m’aide à couvrir ces frais et à poursuivre le développement de BIMaestro.",
                "I personally pay for AI requests. If you wish, even a very small donation helps cover these costs and supports continued development of BIMaestro.")));
            var reassurance = Paragraph(UiLanguage.T(
                "Vous ne pouvez pas faire de don ? Aucun souci ! Si vous appréciez mon travail, c’est déjà beaucoup. Contactez-moi quand même pour augmenter votre limite : aucun don n’est nécessaire.",
                "Can’t donate? No problem! Knowing you appreciate my work already means a lot. Contact me anyway to increase your limit: no donation is required."));
            reassurance.FontWeight = FontWeights.SemiBold;
            content.Children.Add(reassurance);
            content.Children.Add(Paragraph(UiLanguage.T("Paul — Créateur de BIMaestro", "Paul — Creator of BIMaestro")));
            var contacts = new WrapPanel();
            contacts.Children.Add(LinkButton("LinkedIn", "https://www.linkedin.com/in/paul-lemert-b40921207", true));
            string subject = UiLanguage.T("BIMaestro — Demande d’augmentation du quota IA", "BIMaestro — AI quota increase request");
            string body = UiLanguage.T("Bonjour Paul,\r\n\r\nJ’ai atteint ma limite IA et souhaiterais l’augmenter.\r\nMon nom d’utilisateur Revit : \r\n\r\nMerci pour ton travail !", "Hi Paul,\r\n\r\nI have reached my AI limit and would like to increase it.\r\nMy Revit username: \r\n\r\nThanks for your work!");
            contacts.Children.Add(LinkButton(UiLanguage.T("Envoyer un e-mail", "Send an email"), "mailto:bimaestro.plugin@gmail.com?subject=" + Uri.EscapeDataString(subject) + "&body=" + Uri.EscapeDataString(body), true));
            content.Children.Add(contacts);
            content.Children.Add(Paragraph("bimaestro.plugin@gmail.com", 12));
            var footer = new WrapPanel();
            footer.Children.Add(LinkButton(UiLanguage.T("Soutenir (facultatif)", "Support (optional)"), "https://ko-fi.com/bimaestro", false));
            var close = new Button { Content = UiLanguage.T("Fermer", "Close"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 8) };
            close.SetResourceReference(StyleProperty, "SecondaryButton");
            close.Click += (_, __) => Close();
            footer.Children.Add(close);
            content.Children.Add(footer);
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        }

        private static TextBlock Paragraph(string text, double size = 14)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            return block;
        }

        private Button LinkButton(string label, string uri, bool primary)
        {
            var button = new Button { Content = label, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 8) };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
            button.Click += (_, __) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true }); }
                catch (Exception)
                {
                    MessageBox.Show(this, UiLanguage.T("Impossible d’ouvrir ce lien. Vous pouvez me contacter à l’adresse : bimaestro.plugin@gmail.com", "Unable to open this link. You can contact me at: bimaestro.plugin@gmail.com"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            return button;
        }
    }
}
