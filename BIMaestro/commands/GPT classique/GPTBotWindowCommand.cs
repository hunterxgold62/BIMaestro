using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.ApplicationServices;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class GPTBotWindowCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Afficher la fenêtre de sélection de profil
            ProfileSelectionWindow selectionWindow = new ProfileSelectionWindow();
            bool? result = selectionWindow.ShowDialog();

            if (result == true)
            {
                // Récupérer le profil sélectionné
                string selectedProfile = selectionWindow.SelectedProfile;

                // Récupérer les informations utilisateur
                UIApplication uiapp = commandData.Application;
                Application app = uiapp.Application;

                // Récupérer le nom d'utilisateur du système
                string userName = System.Environment.UserName;

                string revitVersion = app.VersionNumber;

                // Créer le message système basé sur le profil et les informations utilisateur
                string systemMessage = GetSystemMessage(selectedProfile, userName, revitVersion);

                // Ouvrir la fenêtre du chatbot avec le message système
                GPTBotWindow chatWindow = new GPTBotWindow(systemMessage, commandData.Application.ActiveUIDocument);
                chatWindow.Topmost = true; // Facultatif
                chatWindow.Show();

                return Result.Succeeded;
            }
            else
            {
                return Result.Cancelled;
            }
        }

        private string GetSystemMessage(string profile, string userName, string revitVersion)
        {
            switch (profile)
            {
                case "Basique":
                    return "Vous êtes un assistant virtuel prêt à aider avec diverses questions. Répondez de manière claire et concise.";
                case "Personnelle Revit":
                    return $"Vous êtes un expert en Revit {revitVersion} assistant {userName} dans ses tâches quotidiennes. Lorsqu'un élément est fourni, analysez ses informations et fournissez des conseils appropriés. Fournissez des réponses détaillées sur l'utilisation de Revit, en adoptant un ton professionnel et amical.";
                case "BIM Manager":
                    return $"En tant que BIM Manager spécialisé en Revit {revitVersion}, vous aidez {userName} à gérer les modèles BIM, à respecter les normes de modélisation et à améliorer la coordination entre les disciplines. Lorsqu'un élément est fourni, analysez-le en profondeur et suggérez des améliorations ou signalez des problèmes potentiels. Fournissez des conseils approfondis et des meilleures pratiques.";
                default:
                    return "Vous êtes un assistant virtuel prêt à aider avec diverses questions.";
            }
        }
    }
}