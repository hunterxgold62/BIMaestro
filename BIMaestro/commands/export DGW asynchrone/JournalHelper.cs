using System;
using System.IO;

namespace MonPluginRevit
{
    public static class JournalHelper
    {
        // 1) Le nom exact de votre manifeste
        public const string AddinFileName = "addinPaul.addin";
        // 2) Le nom exact de votre DLL
        public const string DllFileName = "BIMaestro.dll";

        // 3) Où se trouvent vos sources "maîtres"
        //    (là où vous avez déjà déposé addinPaul.addin + BIMaestro.dll)
        private static readonly string SourceAddinsFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                "2023"
            );

        public static string AddinManifestSource =>
            Path.Combine(SourceAddinsFolder, AddinFileName);

        public static string AddinDllSource =>
            Path.Combine(SourceAddinsFolder, DllFileName);

        // 4) Le dossier utilisateur Add-ins
        private static string UserAddinsFolder =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                "2023"
            );

        // 5) Déploie .addin + DLL sous %APPDATA%\Autodesk\Revit\Addins\2023
        private static void DeployToUserAddins()
        {
            Directory.CreateDirectory(UserAddinsFolder);

            string destAddin = Path.Combine(UserAddinsFolder, AddinFileName);
            string destDll = Path.Combine(UserAddinsFolder, DllFileName);

            File.Copy(AddinManifestSource, destAddin, overwrite: true);
            File.Copy(AddinDllSource, destDll, overwrite: true);
        }

        /// <summary>
        /// Crée le journal .jrn **après** avoir déployé votre add-in utilisateur.
        /// </summary>
        public static string CreateJournalForTask(string copyRvtPath, string taskJsonPath)
        {
            // 1) Assure-toi que l’add-in est déployé
            try
            {
                DeployToUserAddins();
            }
            catch
            {
                // on ignore les erreurs de copie ici,
                // mais tu peux logger si tu veux
            }

            // 2) Génère le journal dans le même dossier que la copie .rvt
            string folder = Path.GetDirectoryName(copyRvtPath)
                                 ?? throw new InvalidOperationException("Chemin .rvt invalide");
            string journalPath = Path.Combine(
                folder,
                Path.GetFileNameWithoutExtension(copyRvtPath) + "_Export.jrn"
            );

            var lines = new[]
            {
                $"OpenDocument \"{copyRvtPath}\"",
                // appelle maintenant ExportTaskCommand qui est chargé car Revit a trouvé ton .addin
                "ExternalCommandName MonPluginRevit.ExportTaskCommand",
                $"ExternalCommandArgument \"{taskJsonPath}\"",
                "SaveAndCloseDocument"
            };
            File.WriteAllLines(journalPath, lines);

            return journalPath;
        }

        // Le chemin vers Revit.exe reste inchangé
        public static string RevitExePath =>
            @"C:\Program Files\Autodesk\Revit 2023\Revit.exe";
    }
}
