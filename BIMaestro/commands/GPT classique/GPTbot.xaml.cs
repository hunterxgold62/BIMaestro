using System;
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
using Newtonsoft.Json.Linq;
using System.IO;
using System.Windows.Documents;
using Markdown.Xaml;
using Licensing;
using LicenseManager = Licensing.LicenseManager;

namespace IA
{
    public partial class GPTBotWindow : Window, INotifyPropertyChanged
    {
        private ObservableCollection<MessageModel> conversationHistory = new ObservableCollection<MessageModel>();
        private readonly string _jwt;
        private bool isAwaitingResponse = false; // Indicateur de réponse en attente

        public event PropertyChangedEventHandler PropertyChanged;

        private UIDocument uidoc;

        // Variable pour stocker les informations des éléments
        private string storedElementInfo = null;
        private string categoryName;

        public GPTBotWindow(string systemMessage, UIDocument uidoc)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            this.uidoc = uidoc; // Stocker le UIDocument

            // Initialiser ElementUtilities avec UIApplication
            ElementUtilities.Initialize(uidoc.Application);

            // Initialiser la collection
            conversationHistory = new ObservableCollection<MessageModel>();

            // Définir l'ItemsSource de la ListBox
            MessagesListBox.ItemsSource = conversationHistory;

            // Récupère le JWT obtenu au démarrage
            _jwt = BIMaestroApp.LicenseJwt;
            if (string.IsNullOrEmpty(_jwt))
            {
                MessageBox.Show("Licence non initialisée. Relancez le plugin.");
                this.Close();
                return;
            }

            // Ajouter le message système...
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

            ScrollToLatestMessage(userMessage);

            // Désactive l'envoi de messages et affiche l'indicateur de chargement
            isAwaitingResponse = true;
            AskButton.IsEnabled = false; // Désactiver le bouton pendant l'attente
            ElementButton.IsEnabled = false;
            LoadingIndicator.Visibility = System.Windows.Visibility.Visible;

            string response = await GetResponseFromDeepSeek();

            var botMessage = new MessageModel { Role = "assistant", Content = response };
            conversationHistory.Add(botMessage);

            ScrollToLatestMessage(botMessage);

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
                // 1) Récupérer la sélection Revit
                ICollection<ElementId> selectedElementIds = uidoc.Selection.GetElementIds();
                if (selectedElementIds == null || !selectedElementIds.Any())
                {
                    MessageBox.Show("Aucun élément sélectionné. Veuillez sélectionner des éléments dans Revit avant de cliquer sur 'Élément'.");
                    return;
                }

                // 2) Pour chaque élément, on construit un ElementInfo
                var elementInfos = new List<ElementInfo>();
                foreach (ElementId elementId in selectedElementIds)
                {
                    Element element = uidoc.Document.GetElement(elementId);

                    // ——————— Remplacement LevelId ———————
                    ElementId lvlId = ElementId.InvalidElementId;
                    Parameter lvlParam = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    if (lvlParam != null && lvlParam.StorageType == StorageType.ElementId)
                    {
                        lvlId = lvlParam.AsElementId();
                    }
                    Level level = lvlId != ElementId.InvalidElementId
                                  ? uidoc.Document.GetElement(lvlId) as Level
                                  : null;
                    string levelName = level?.Name ?? "Niveau inconnu";
                    // ————————————————————————————————

                    string categoryName = element.Category?.Name ?? "Catégorie inconnue";

                    var info = new ElementInfo
                    {
                        Id = element.Id.ToString(),
                        Name = element.Name,
                        Category = categoryName,
                        Material = ElementUtilities.GetElementMaterials(element),
                        CustomParameters = ElementUtilities.GetCustomParameters(element),
                        Level = levelName
                    };

                    elementInfos.Add(info);
                }

                // 3) Conserver le texte pour la prochaine question
                storedElementInfo = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    elementInfos.Select(ei => ei.ToString())
                );

                // 4) Feedback à l'utilisateur dans le chat
                var infoMessage = new MessageModel
                {
                    Role = "assistant",
                    Content = "Les informations des éléments sélectionnés ont été enregistrées. Elles seront incluses dans votre prochaine question."
                };
                conversationHistory.Add(infoMessage);
                ScrollToLatestMessage(infoMessage);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de la récupération des éléments sélectionnés : " + ex.Message);
            }
        }


        private async Task<string> GetResponseFromDeepSeek()
        {
            var sb = new StringBuilder();
            foreach (var msg in conversationHistory)
            {
                sb.AppendLine($"{msg.Role}: {msg.Content}");
            }

            return await Task.Run(() =>
            {
                try
                {
                    var json = AiClient.SendDeepSeek(_jwt, sb.ToString());
                    return json["choices"]?[0]?["message"]?["content"]?.ToString();
                }
                catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
                {
                    // Quota dépassé → on remonte le même message centralisé
                    return AiClient.QuotaExceededMessage;
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("AI proxy error (403)"))
                {
                    // Au cas où ton proxy renverrait un autre texte pour 403
                    return AiClient.QuotaExceededMessage;
                }
                catch (Exception ex)
                {
                    return $"Erreur API : {ex.Message}";
                }
            });
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

        private void ScrollToLatestMessage(MessageModel messageToShow)
        {
            if (messageToShow == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessagesListBox.UpdateLayout();
                MessagesListBox.ScrollIntoView(messageToShow);
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

}