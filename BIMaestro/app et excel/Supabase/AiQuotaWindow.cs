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
            Title = UiLanguage.Get("AiQuotaWindow.BIMaestroAILimitReached");
            Width = 610;
            Height = 620;
            MinWidth = 480;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "App.Background");

            var content = new StackPanel { Margin = new Thickness(26) };
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var heading = Paragraph(UiLanguage.Get("AiQuotaWindow.YouHaveReachedYourAILimit"), 25);
            heading.FontWeight = FontWeights.Bold;
            content.Children.Add(heading);
            content.Children.Add(Paragraph(UiLanguage.Get("AiQuotaWindow.NeedToContinueGetInTouch"), 18));
            content.Children.Add(Paragraph(UiLanguage.Get("AiQuotaWindow.IDonTSystematicallyReviewEveryoneSQuotas")));
            content.Children.Add(Paragraph(UiLanguage.Get("AiQuotaWindow.IPersonallyPayForAIRequestsIfYou")));
            var reassurance = Paragraph(UiLanguage.Get("AiQuotaWindow.CanTDonateNoProblemKnowingYouAppreciate"));
            reassurance.FontWeight = FontWeights.SemiBold;
            content.Children.Add(reassurance);
            content.Children.Add(Paragraph(UiLanguage.Get("AiQuotaWindow.PaulCreatorOfBIMaestro")));
            var contacts = new WrapPanel();
            contacts.Children.Add(LinkButton("LinkedIn", "https://www.linkedin.com/in/paul-lemert-b40921207", true));
            string subject = UiLanguage.Get("AiQuotaWindow.BIMaestroAIQuotaIncreaseRequest");
            string body = UiLanguage.Get("AiQuotaWindow.HiPaulIHaveReachedMyAILimit");
            contacts.Children.Add(LinkButton(UiLanguage.Get("AiQuotaWindow.SendAnEmail"), "mailto:bimaestro.plugin@gmail.com?subject=" + Uri.EscapeDataString(subject) + "&body=" + Uri.EscapeDataString(body), true));
            content.Children.Add(contacts);
            content.Children.Add(Paragraph("bimaestro.plugin@gmail.com", 12));
            var footer = new WrapPanel();
            footer.Children.Add(LinkButton(UiLanguage.Get("AiQuotaWindow.SupportOptional"), "https://ko-fi.com/bimaestro", false));
            var close = new Button { Content = UiLanguage.Get("AiQuotaWindow.Close"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 8) };
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
                    MessageBox.Show(this, UiLanguage.Get("AiQuotaWindow.UnableToOpenThisLinkYouCanContact"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            return button;
        }
    }
}
