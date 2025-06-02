using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Net.Http;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Generic;

namespace IA
{
    public partial class CorrectionWindow : Window
    {
        public enum CorrectionDialogResult
        {
            None,
            OK,
            Cancel
        }

        public CorrectionDialogResult CorrectionResult { get; set; } = CorrectionDialogResult.None;
        public string CorrectedText { get; private set; } = "";
        private string _originalText;
        public string SelectedStyle { get; private set; } = "Classique";
        public string CustomInstruction { get; private set; } = string.Empty;

        public CorrectionWindow(string originalText, string baselineCorrectedText)
        {
            InitializeComponent();

            // Applique le thème enregistré
            var prefs = Preferences.LoadPreferences();
            ApplyTheme(prefs.IsDarkTheme);
            darkThemeCheckBox.IsChecked = prefs.IsDarkTheme;

            _originalText = originalText;
            originalTextBox.Text = originalText;

            // Charge la proposition de base
            proposalsListBox.Items.Clear();
            proposalsListBox.Items.Add(CreateListBoxItemFromText(baselineCorrectedText));
            CorrectedText = baselineCorrectedText;

            styleComboBox.SelectionChanged += styleComboBox_SelectionChanged;
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            if (proposalsListBox.SelectedItem is ListBoxItem lbi)
                CorrectedText = GetPlainTextFromListBoxItem(lbi);
            else if (proposalsListBox.Items.Count == 1)
            {
                var soleItem = proposalsListBox.Items[0] as ListBoxItem;
                if (soleItem != null)
                    CorrectedText = GetPlainTextFromListBoxItem(soleItem);
            }

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

        private async void rephraseButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedStyle = (styleComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Classique";
            CustomInstruction = customInstructionTextBox.Text;

            try
            {
                var suggestions = await GetMultipleSuggestionsFromOpenAI(
                    _originalText,
                    SelectedStyle,
                    CustomInstruction
                );

                proposalsListBox.Items.Clear();
                foreach (var s in suggestions)
                    proposalsListBox.Items.Add(CreateListBoxItemFromText(s));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur OpenAI : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void proposalsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (proposalsListBox.SelectedItem != null)
            {
                var sel = proposalsListBox.SelectedItem;
                proposalsListBox.Items.Clear();
                proposalsListBox.Items.Add(sel);
            }
        }

        private void styleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (customInstructionPanel == null) return;

            var item = styleComboBox.SelectedItem as ComboBoxItem;
            customInstructionPanel.Visibility = (item?.Content.ToString() == "Personnalisé")
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void darkThemeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool isDark = darkThemeCheckBox.IsChecked == true;
            ApplyTheme(isDark);

            var prefs = Preferences.LoadPreferences();
            prefs.IsDarkTheme = isDark;
            Preferences.SavePreferences(prefs);
        }

        // ===========================
        //   Méthodes utilitaires
        // ===========================

        private ListBoxItem CreateListBoxItemFromText(string correctedText)
        {
            var lbItem = new ListBoxItem();

            // On ne définit plus explicitement Foreground :
            // le TextBlock va hériter dynamiquement de la couleur du ListBox (changements de thème inclus).
            var tblock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap
            };

            var originalWords = _originalText.Split(' ');
            var correctedWords = correctedText.Split(' ');

            for (int i = 0; i < correctedWords.Length; i++)
            {
                string word = correctedWords[i];
                var run = new Run(word + " ");
                if (i < originalWords.Length && word != originalWords[i])
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

        private void ApplyTheme(bool dark)
        {
            if (dark)
            {
                this.Background = Brushes.DimGray;
                originalTextBox.Background = Brushes.Gray;
                originalTextBox.Foreground = Brushes.White;
                proposalsListBox.Background = Brushes.Gray;
                proposalsListBox.Foreground = Brushes.White;
            }
            else
            {
                this.Background = Brushes.WhiteSmoke;
                originalTextBox.Background = Brushes.White;
                originalTextBox.Foreground = Brushes.Black;
                proposalsListBox.Background = Brushes.White;
                proposalsListBox.Foreground = Brushes.Black;
            }
        }

        private async System.Threading.Tasks.Task<List<string>> GetMultipleSuggestionsFromOpenAI(
            string baselineText,
            string style,
            string customInstruction)
        {
            string prompt = GeneratePrompt(baselineText, style, customInstruction);

            var requestData = new
            {
                model = "gpt-4o-mini",
                messages = new object[] { new { role = "user", content = prompt } },
                max_tokens = 300,
                n = 3,
                temperature = 0.5
            };

            string jsonRequest = JsonConvert.SerializeObject(requestData);

            using (var client = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") })
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TextCorrectionCommand.apiKey);

                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("/v1/chat/completions", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                dynamic result = JsonConvert.DeserializeObject(responseContent);

                if (result?.choices == null || result.choices.Count == 0)
                    throw new Exception("Réponse vide ou incorrecte de l'API.");

                var outputs = new List<string>();
                foreach (var choice in result.choices)
                {
                    string text = choice.message.content?.ToString().Trim();
                    if (!string.IsNullOrEmpty(text))
                        outputs.Add(text.Replace("Correction:", "").Trim());
                }
                return outputs;
            }
        }

        private string GeneratePrompt(string inputText, string style, string customInstruction)
        {
            switch (style)
            {
                case "Professionnelle":
                    return $"Reformulez le texte suivant dans un style formel et professionnel, " +
                           $"en utilisant un langage technique approprié, sans ajouter de nouvelles informations : {inputText}";
                case "Baratin":
                    return $"Réécrivez le texte suivant en l'enrichissant avec des phrases longues, un vocabulaire sophistiqué " +
                           $"et des tournures de phrases complexes, sans ajouter de nouvelles informations : {inputText}";
                case "Cool":
                    return $"Reformulez le texte suivant avec un ton détendu et convivial, sans ajouter de nouvelles informations : {inputText}";
                case "Personnalisé":
                    return $"Reformulez uniquement le texte ci-dessous selon ces instructions personnalisées, " +
                           $"sans explications ni discussion :\n{customInstruction}\n\n" +
                           $"Texte à reformuler : {inputText}";
                default: // Classique
                    return $"Reformulez autrement le texte suivant pour le rendre clair et correct, " +
                           $"sans ajouter de nouvelles informations : {inputText}";
            }
        }
    }
}
