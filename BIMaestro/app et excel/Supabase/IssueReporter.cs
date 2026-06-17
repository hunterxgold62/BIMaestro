using Autodesk.Revit.UI;
using System;
using System.Diagnostics;
using System.Text;
using System.Windows;

namespace Licensing
{
    internal static class IssueReporter
    {
        private const string LinkedInUrl = "https://www.linkedin.com/in/paul-lemert-b40921207";

        public static void OpenContact()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LinkedInUrl,
                UseShellExecute = true
            });
        }

        public static void ShowCommandError(string commandName, Exception ex, ExternalCommandData data)
        {
            var report = BuildCommandReport(commandName, ex, data);
            ShowIssueDialog(
                "BIMaestro - Erreur",
                $"Le bouton \"{commandName}\" a rencontré un souci.",
                "Tu peux réessayer. Si le problème revient, clique sur \"Signaler un souci\" : LinkedIn s'ouvrira et un résumé sera copié pour m'aider à corriger plus vite.",
                report);
        }

        public static void ShowStartupError(Exception ex)
        {
            var report = BuildStartupReport(ex);
            ShowIssueDialog(
                "BIMaestro - Erreur",
                "Une erreur est survenue au démarrage.",
                "Impossible de lancer BIMaestro pour le moment. Clique sur \"Signaler un souci\" pour me contacter sur LinkedIn avec un résumé copié automatiquement.",
                report);
        }

        private static void ShowIssueDialog(string title, string instruction, string content, string report)
        {
            var td = new TaskDialog(title)
            {
                MainInstruction = instruction,
                MainContent = content,
                ExpandedContent = report,
                CommonButtons = TaskDialogCommonButtons.Close
            };

            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Signaler un souci");

            var result = td.Show();
            if (result != TaskDialogResult.CommandLink1) return;

            TryCopyReport(report);

            try
            {
                OpenContact();
            }
            catch (Exception openEx)
            {
                TaskDialog.Show("BIMaestro - Contact", $"Impossible d'ouvrir LinkedIn : {openEx.Message}");
            }
        }

        private static string BuildCommandReport(string commandName, Exception ex, ExternalCommandData data)
        {
            var doc = data?.Application?.ActiveUIDocument?.Document;
            var view = data?.Application?.ActiveUIDocument?.ActiveView;
            var app = data?.Application?.Application;

            var sb = new StringBuilder();
            sb.AppendLine("Retour BIMaestro");
            sb.AppendLine($"Bouton : {commandName}");
            sb.AppendLine($"Erreur : {ex.GetType().Name}");
            sb.AppendLine($"Message : {ex.Message}");
            sb.AppendLine($"Document : {doc?.Title ?? "Non disponible"}");
            sb.AppendLine($"Vue : {view?.Name ?? "Non disponible"}");
            sb.AppendLine($"Revit : {app?.VersionNumber ?? "Non disponible"}");
            sb.AppendLine($"Utilisateur Revit : {app?.Username ?? Environment.UserName}");
            sb.AppendLine($"Version BIMaestro : {BIMaestroApp.PluginVersion ?? "dev"}");
            return sb.ToString();
        }

        private static string BuildStartupReport(Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Retour BIMaestro");
            sb.AppendLine("Contexte : démarrage du plugin");
            sb.AppendLine($"Erreur : {ex.GetType().Name}");
            sb.AppendLine($"Message : {ex.Message}");
            sb.AppendLine($"Utilisateur Windows : {Environment.UserName}");
            sb.AppendLine($"Machine : {Environment.MachineName}");
            sb.AppendLine($"Version BIMaestro : {BIMaestroApp.PluginVersion ?? "dev"}");
            return sb.ToString();
        }

        private static void TryCopyReport(string report)
        {
            try
            {
                Clipboard.SetText(report);
            }
            catch
            {
                // Le signalement reste possible même si le presse-papiers est verrouillé.
            }
        }
    }
}
