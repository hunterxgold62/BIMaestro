using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Input;
using IA;              // ← AiClient
using Licensing;      // ← LicenseManager
using BIMaestro.Localization;

namespace IA
{
    public partial class CorrectionWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/ia?outil=correction-texte-ia";
        public enum CorrectionDialogResult { None, OK, Cancel }
        public CorrectionDialogResult CorrectionResult { get; set; } = CorrectionDialogResult.None;
        public string CorrectedText { get; private set; } = "";

        private readonly string _originalText;
        private readonly string _jwt;

        public CorrectionWindow(string originalText, string baselineCorrectedText, string jwt)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            

            // Contexte
            _originalText = originalText;
            _jwt = jwt;

            // Texte initial et proposition de base
            originalTextBox.Text = originalText;
            proposalsListBox.Items.Clear();
            proposalsListBox.Items.Add(CreateListBoxItemFromText(baselineCorrectedText));
            CorrectedText = baselineCorrectedText;

            // Événements UI
            styleComboBox.SelectionChanged += styleComboBox_SelectionChanged;
            proposalsListBox.MouseDoubleClick += proposalsListBox_MouseDoubleClick;
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(UiLanguage.T("Impossible d’ouvrir la page d’aide : ", "Unable to Open the Help Page: ") + ex.Message, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            if (proposalsListBox.SelectedItem is ListBoxItem lbi)
                CorrectedText = GetPlainTextFromListBoxItem(lbi);

            CorrectionResult = CorrectionDialogResult.OK;
            DialogResult = true;
            Close();
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            CorrectionResult = CorrectionDialogResult.Cancel;
            DialogResult = false;
            Close();
        }

        // === On remplace le HttpClient direct par AiClient.SendOpenAI ===
        private async void rephraseButton_Click(object sender, RoutedEventArgs e)
        {
            string style = (styleComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Classique";
            string custom = customInstructionTextBox.Text.Trim();

            try
            {
                string prompt = GeneratePrompt(_originalText, style, custom);

                // Appel via AiClient
                var jsonDoc = await Task.Run(() =>
    AiClient.SendOpenAI(_jwt, "gpt-4o-mini", prompt, 3)
                );

                // Extraction des résultats
                var suggestions = jsonDoc["choices"]?
                    .Select(ch => ch?["message"]?["content"]?.ToString()?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                // Affichage
                proposalsListBox.Items.Clear();
                foreach (var s in suggestions)
                    proposalsListBox.Items.Add(CreateListBoxItemFromText(s));
            }
            catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
            {
                // MessageBox.Show utilise la constante définie dans AiClient
                MessageBox.Show(
                    UiLanguage.IsEnglish ? "Your AI quota has been exceeded." : AiClient.QuotaExceededMessage,
                    UiLanguage.T("Quota dépassé", "Quota Exceeded"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            catch (InvalidOperationException ex)
            {
                // autres erreurs IA
                MessageBox.Show(ex.Message, UiLanguage.T("Erreur IA", "AI Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        

        private void proposalsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (proposalsListBox.SelectedItem is ListBoxItem sel)
            {
                proposalsListBox.Items.Clear();
                proposalsListBox.Items.Add(sel);
            }
        }

        private void styleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (customInstructionPanel == null) return;
            var item = styleComboBox.SelectedItem as ComboBoxItem;
            customInstructionPanel.Visibility =
                (item?.Content.ToString() == "Personnalisé")
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        

        // === Utilitaires de rendu ===

        private ListBoxItem CreateListBoxItemFromText(string correctedText)
        {
            var lbItem = new ListBoxItem();
            var tblock = new TextBlock { TextWrapping = TextWrapping.Wrap };

            var origWords = _originalText.Split(' ');
            var correctedWords = correctedText.Split(' ');

            for (int i = 0; i < correctedWords.Length; i++)
            {
                string word = correctedWords[i];
                var run = new Run(word + " ");
                if (i < origWords.Length && word != origWords[i])
                    run.FontWeight = FontWeights.Bold;
                tblock.Inlines.Add(run);
            }

            lbItem.Content = tblock;
            return lbItem;
        }

        private string GetPlainTextFromListBoxItem(ListBoxItem lbi)
        {
            if (lbi.Content is TextBlock tb)
            {
                var sb = new StringBuilder();
                foreach (var inline in tb.Inlines)
                    if (inline is Run run)
                        sb.Append(run.Text);
                return sb.ToString().Trim();
            }
            return "";
        }

        

        private string GeneratePrompt(string inputText, string style, string customInstruction)
        {
            if (UiLanguage.IsEnglish)
            {
                return style switch
                {
                    "Professionnelle" => $"Rewrite the following text in a formal, professional style without adding information: {inputText}",
                    "Baratin" => $"Rewrite the following text using sophisticated vocabulary and elaborate phrasing: {inputText}",
                    "Cool" => $"Rewrite the following text in a relaxed, friendly tone: {inputText}",
                    "Personnalisé" => $"Rewrite only according to these instructions, without explanations:\n{customInstruction}\n\n{inputText}",
                    _ => $"Rewrite the following text to make it clearer without adding information: {inputText}",
                };
            }

            return style switch
            {
                "Professionnelle" =>
                    $"Reformulez le texte suivant dans un style formel et professionnel, sans ajouter d’informations : {inputText}",
                "Baratin" =>
                    $"Réécrivez le texte suivant en utilisant un vocabulaire sophistiqué et des tournures élaborées : {inputText}",
                "Cool" =>
                    $"Reformulez le texte suivant avec un ton détendu et convivial : {inputText}",
                "Personnalisé" =>
                    $"Reformulez uniquement selon ces instructions, sans explications :\n{customInstruction}\n\n{inputText}",
                _ => // Classique
                    $"Reformulez le texte suivant pour le rendre plus clair, sans ajouter d’informations : {inputText}",
            };
        }
    }
}
