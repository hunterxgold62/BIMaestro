using System;
using System.Net.Http;
using System.Windows;

namespace MyPluginNamespace
{
    public partial class UpdateWindow : Window
    {
        private const string GoogleDocUrl = "https://docs.google.com/document/d/1Oqa9Yt3NfoAROr0qq-vQacgevbZP_TF9RFXs0ttAptg/export?format=html";

        public UpdateWindow()
        {
            InitializeComponent();
            LoadHtmlContent();
        }

        private async void LoadHtmlContent()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    // Récupérer le HTML du document
                    string htmlContent = await client.GetStringAsync(GoogleDocUrl);

                    // Afficher le contenu HTML dans le WebBrowser
                    WebBrowserControl.NavigateToString(htmlContent);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur de chargement : " + ex.Message);
            }
        }
    }
}