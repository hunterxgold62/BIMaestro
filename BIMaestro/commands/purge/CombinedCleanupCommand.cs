// Commands/CombinedCleanupCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.IO;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class CombinedCleanupCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "CombinedCleanupCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;

            // 1) Fenêtre d’options
            var window = new CleanupWindow();
            var helper = new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = uiapp.MainWindowHandle
            };
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            // 2) Pré-vol : copie « - Purger » + avertissement workshared
            if (!PreflightCopyAndWarnings(uiapp, doc, out bool aborted))
                return aborted ? Result.Cancelled : Result.Failed;

            // 3) Exécution selon choix
            if (window.DeleteViews)
            {
                var cmd = new DeleteUnplacedViewsCommand();
                cmd.Execute(data, ref message, elements);
            }

            if (window.DeleteFamilies)
            {
                var cmd = new DeleteUnusedFamiliesCommand();
                cmd.Execute(data, ref message, elements);
            }

            if (window.DeleteSchedules)
            {
                var cmd = new DeleteUnusedSchedulesCommand();
                cmd.Execute(data, ref message, elements);
            }

            if (window.DeleteHardFamilies)
            {
                var cmd = new DeleteUnusedFamiliesHardCommand();
                cmd.Execute(data, ref message, elements);
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// Gère :
        /// - l’avertissement si modèle partagé (workshared) avec possibilité d’annuler,
        /// - la proposition de créer une copie « Nom - Purger.rvt » (non-workshared uniquement),
        /// - le nom comporte déjà “Purger” ⇒ ne rien demander.
        /// </summary>
        private bool PreflightCopyAndWarnings(UIApplication uiapp, Document doc, out bool abortedByUser)
        {
            abortedByUser = false;

            bool isWorkshared = doc.IsWorkshared;
            string title = doc.Title ?? "Projet";
            bool alreadyPurger = title.IndexOf("Purger", StringComparison.InvariantCultureIgnoreCase) >= 0;

            // Avertissement systématique si workshared
            if (isWorkshared)
            {
                var warn = new TaskDialog("Modèle partagé détecté")
                {
                    MainInstruction = "Le modèle est en travail partagé.",
                    MainContent =
                        "La copie automatique de sécurité est déconseillée sur un central/local.\n" +
                        "Si vous devez travailler sur une copie, utilisez de préférence « Ouvrir » > « Détacher du central » " +
                        "puis enregistrez sous un nouveau nom.\n\n" +
                        "Voulez-vous continuer la purge sur le modèle ouvert ?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                    MainIcon = TaskDialogIcon.TaskDialogIconWarning
                };
                if (warn.Show() != TaskDialogResult.Yes)
                {
                    abortedByUser = true;
                    return true; // retour vrai = pas d'erreur; abortedByUser = true ⇒ caller renvoie Cancelled
                }
            }

            // Pas de proposition si le nom contient déjà "Purger"
            if (alreadyPurger)
                return true;

            // Proposer une copie auto seulement si non-workshared
            if (!isWorkshared)
            {
                var ask = new TaskDialog("Créer une copie pour purge ?")
                {
                    MainInstruction = $"Créer automatiquement une copie « {title} - Purger.rvt » et travailler dessus ?",
                    MainContent =
                        "La copie sera enregistrée dans le même dossier que le fichier actuel. " +
                        "Le document ouvert pointera ensuite vers cette copie (l’original reste intact).",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.Yes,
                    MainIcon = TaskDialogIcon.TaskDialogIconInformation
                };
                if (ask.Show() == TaskDialogResult.Yes)
                {
                    if (string.IsNullOrWhiteSpace(doc.PathName))
                    {
                        TaskDialog.Show("Enregistrement requis",
                            "Le projet n’a pas encore été enregistré. Impossible de déterminer un dossier pour la copie.\n" +
                            "Veuillez enregistrer le fichier, puis relancer la commande.");
                        abortedByUser = true;
                        return true;
                    }

                    string newPath = BuildUniquePurgePath(doc);
                    try
                    {
                        var opts = new SaveAsOptions { OverwriteExistingFile = false };
                        doc.SaveAs(newPath, opts);

                        TaskDialog.Show("Copie créée",
                            $"La copie de purge a été créée :\n{newPath}\n\n" +
                            "Vous travaillez désormais sur cette copie.");
                    }
                    catch (Exception ex)
                    {
                        var err = new TaskDialog("Erreur lors de la copie")
                        {
                            MainInstruction = "La création de la copie a échoué.",
                            MainContent = $"Détail : {ex.Message}\n\nSouhaitez-vous continuer sans créer de copie ?",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No,
                            MainIcon = TaskDialogIcon.TaskDialogIconWarning
                        };
                        if (err.Show() != TaskDialogResult.Yes)
                        {
                            abortedByUser = true;
                            return true;
                        }
                    }
                }
            }

            return true;
        }

        private static string BuildUniquePurgePath(Document doc)
        {
            string originalPath = doc.PathName; // ex: C:\Dossier\Modele.rvt
            string dir = Path.GetDirectoryName(originalPath);
            string baseName = Path.GetFileNameWithoutExtension(originalPath);

            string candidate = Path.Combine(dir!, $"{baseName} - Purger.rvt");
            if (!File.Exists(candidate))
                return candidate;

            // Si existe déjà, ajouter un suffixe (2), (3), ...
            for (int i = 2; i < 1000; i++)
            {
                string c = Path.Combine(dir!, $"{baseName} - Purger ({i}).rvt");
                if (!File.Exists(c))
                    return c;
            }
            // Fallback improbable
            string ticks = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(dir!, $"{baseName} - Purger_{ticks}.rvt");
        }
    }
}
