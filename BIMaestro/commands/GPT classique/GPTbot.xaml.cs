using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Linq;
using System.IO;
using System.Windows.Documents;
using Markdown.Xaml;

namespace IA
{
    public partial class GPTBotWindow : Window, INotifyPropertyChanged
    {
        private readonly HttpClient httpClient = new HttpClient();
        private readonly string apiKey = ApiKeys.DeepSeekKey;
        private ObservableCollection<MessageModel> conversationHistory = new ObservableCollection<MessageModel>();
        private bool isAwaitingResponse = false; // Indicateur de réponse en attente

        public event PropertyChangedEventHandler PropertyChanged;

        private UIDocument uidoc;

        // Variable pour stocker les informations des éléments
        private string storedElementInfo = null;

        public GPTBotWindow(string systemMessage, UIDocument uidoc)
        {
            InitializeComponent();

            this.uidoc = uidoc; // Stocker le UIDocument

            // Initialiser ElementUtilities avec UIApplication
            ElementUtilities.Initialize(uidoc.Application);

            // Initialiser la collection
            conversationHistory = new ObservableCollection<MessageModel>();

            // Définir l'ItemsSource de la ListBox
            MessagesListBox.ItemsSource = conversationHistory;

            // Configuration du client HTTP
            httpClient.BaseAddress = new Uri("https://api.deepseek.com/"); // URL de l'API DeepSeek
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            // Ajouter le message système à l'historique des conversations
            if (!string.IsNullOrEmpty(systemMessage))
            {
                var systemMessageModel = new MessageModel { Role = "system", Content = systemMessage };
                conversationHistory.Add(systemMessageModel);
            }
        }

        // Méthode pour capturer l'événement de la molette de la souris et permettre le défilement
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3);
                e.Handled = true;  // Marque l'événement comme géré pour éviter un défilement multiple
            }
        }

        // Gestion du clic du bouton "Envoyer"
        private async void AskButton_Click(object sender, RoutedEventArgs e)
        {
            if (isAwaitingResponse) return; // Empêche d'envoyer un nouveau message avant la réponse

            string userInput = InputBox.Text;
            if (string.IsNullOrEmpty(userInput)) return;

            // Si des informations d'éléments sont stockées, les ajouter à la question
            if (!string.IsNullOrEmpty(storedElementInfo))
            {
                userInput += "\n\nLes informations des éléments sélectionnés sont les suivantes :\n" + storedElementInfo;
                storedElementInfo = null; // Réinitialiser les informations stockées
            }

            var userMessage = new MessageModel { Role = "user", Content = userInput };
            conversationHistory.Add(userMessage);
            InputBox.Clear();

            MessagesListBox.ScrollIntoView(userMessage);

            // Désactive l'envoi de messages et affiche l'indicateur de chargement
            isAwaitingResponse = true;
            AskButton.IsEnabled = false; // Désactiver le bouton pendant l'attente
            ElementButton.IsEnabled = false;
            LoadingIndicator.Visibility = System.Windows.Visibility.Visible;

            string response = await GetResponseFromDeepSeek();

            var botMessage = new MessageModel { Role = "assistant", Content = response };
            conversationHistory.Add(botMessage);

            MessagesListBox.ScrollIntoView(botMessage);

            // Réactive l'envoi de messages et cache l'indicateur de chargement
            isAwaitingResponse = false;
            AskButton.IsEnabled = true;
            ElementButton.IsEnabled = true;
            LoadingIndicator.Visibility = System.Windows.Visibility.Collapsed;
        }

        // Gestion du clic du bouton "Élément"
        private void ElementButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Récupérer les éléments actuellement sélectionnés
                ICollection<ElementId> selectedElementIds = uidoc.Selection.GetElementIds();

                if (selectedElementIds.Count == 0)
                {
                    MessageBox.Show("Aucun élément sélectionné. Veuillez sélectionner des éléments dans Revit avant de cliquer sur le bouton 'Élément'.");
                    return;
                }

                List<ElementInfo> elementInfos = new List<ElementInfo>();

                foreach (ElementId elementId in selectedElementIds)
                {
                    Element element = uidoc.Document.GetElement(elementId);
                    Level level = element.Document.GetElement(element.LevelId) as Level;
                    string levelName = level != null ? level.Name : "Niveau inconnu";

                    string categoryName = element.Category?.Name ?? "Catégorie inconnue";

                    ElementInfo elementInfo = new ElementInfo
                    {
                        Id = element.Id.ToString(),
                        Name = element.Name,
                        Category = categoryName,
                        Material = ElementUtilities.GetElementMaterials(element),
                        CustomParameters = ElementUtilities.GetCustomParameters(element), // Pas besoin de passer UIApplication
                        Level = levelName,
                        SurfaceAndVolume = ElementUtilities.GetElementFloorAreaAndVolume(element, categoryName) // Pass category
                    };
                    elementInfos.Add(elementInfo);
                }

                storedElementInfo = string.Join("\n\n", elementInfos.Select(ei => ei.ToString()));

                // Optionnel : Afficher un message dans le chat pour informer que les éléments ont été enregistrés
                var infoMessage = new MessageModel { Role = "assistant", Content = "Les informations des éléments sélectionnés ont été enregistrées. Elles seront envoyées avec votre prochaine question." };
                conversationHistory.Add(infoMessage);
                MessagesListBox.ScrollIntoView(infoMessage);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de la récupération des éléments sélectionnés : " + ex.Message);
            }
        }

        private async Task<string> GetResponseFromDeepSeek()
        {
            var messages = new List<dynamic>();
            foreach (var message in conversationHistory)
            {
                messages.Add(new { role = message.Role, content = message.Content });
            }

            var requestData = new
            {
                model = "deepseek-chat", // Remplacez par le modèle DeepSeek approprié
                messages = messages,
                temperature = 0.2,
                max_tokens = 2500
            };

            string jsonRequest = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("v1/chat/completions", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<dynamic>(responseContent);
                return result.choices[0].message.content.ToString(); // Ajustez selon la structure de réponse de DeepSeek
            }
            else
            {
                return $"Erreur lors de la requête : {response.StatusCode}, {await response.Content.ReadAsStringAsync()}";
            }
        }

        // Gestionnaire d'événement pour copier le contenu du message
        private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MenuItem menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                string textToCopy = menuItem.CommandParameter as string;
                if (!string.IsNullOrEmpty(textToCopy))
                {
                    Clipboard.SetText(textToCopy);
                }
            }
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}