using Autodesk.Revit.DB;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Analyse
{
    internal sealed class CollaborativeModelRecord
    {
        public string ProjectName { get; set; }
        public string ModelName { get; set; }
        public string ModelPath { get; set; }
        public string UserName { get; set; }
        public string CreatorName { get; set; }
        public string LastChangedBy { get; set; }
        public string RevitVersion { get; set; }
        public DateTime TimestampDate { get; set; }
    }

    internal static class CollaborativeModelTrackerStore
    {
        private static readonly object SyncObj = new object();

        public const string PreferredSharedDirectory = @"P:\0-Boîte à outils Revit\5-Logiciels";

        private static string _activeDirectory;
        private static string _lastDirectoryResolutionMessage;
        private static string _configuredSharedDirectory;

        private static readonly string FallbackDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "SuiviMaquettesCollaboratif");

        private static readonly string CustomPathConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "SuiviMaquettesCollaboratif",
            "shared_path_override.txt");

        public static string ActiveDirectory
        {
            get
            {
                lock (SyncObj)
                {
                    return ResolveWritableDirectoryNoThrow();
                }
            }
        }

        public static string JsonPath => Path.Combine(ActiveDirectory, "SuiviMaquettesCollaboratif.json");
        public static string ExcelPath => Path.Combine(ActiveDirectory, "SuiviMaquettesCollaboratif.xlsx");

        public static string LastDirectoryResolutionMessage
        {
            get
            {
                lock (SyncObj)
                {
                    return _lastDirectoryResolutionMessage;
                }
            }
        }


        public static bool IsUsingFallbackLocal
        {
            get
            {
                lock (SyncObj)
                {
                    var active = ResolveWritableDirectoryNoThrow();
                    return string.Equals(active, FallbackDirectory, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static bool TrySetSharedDirectory(string directory, out string error)
        {
            lock (SyncObj)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    error = "Chemin vide.";
                    return false;
                }

                directory = directory.Trim();
                if (!TryCreateAndWrite(directory, out error))
                    return false;

                _configuredSharedDirectory = directory;
                PersistConfiguredDirectory(directory);
                _activeDirectory = directory;
                _lastDirectoryResolutionMessage = $"Dossier commun personnalisé utilisé: {directory}";
                return true;
            }
        }

        public static List<CollaborativeModelRecord> Load()
        {
            lock (SyncObj)
            {
                EnsureActiveDirectory();
                return LoadWithoutLock();
            }
        }

        public static List<string> GetKnownUsers()
        {
            var users = Load()
                .Select(r => r.UserName)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u)
                .ToList();

            if (!users.Contains(Environment.UserName, StringComparer.OrdinalIgnoreCase))
                users.Insert(0, Environment.UserName);

            return users;
        }

        public static void AddEntryAndSave(Document doc, Autodesk.Revit.UI.UIApplication uiapp, string userName)
        {
            lock (SyncObj)
            {
                EnsureActiveDirectory();

                var records = LoadWithoutLock();
                var entry = BuildRecord(doc, uiapp, userName, records);
                records.Add(entry);
                SaveAndExportWithoutLock(records);
            }
        }

        public static void TryAutoLog(Document doc, Autodesk.Revit.UI.UIApplication uiapp)
        {
            try
            {
                AddEntryAndSave(doc, uiapp, Environment.UserName);
            }
            catch
            {
            }
        }

        private static CollaborativeModelRecord BuildRecord(
            Document doc,
            Autodesk.Revit.UI.UIApplication uiapp,
            string userName,
            List<CollaborativeModelRecord> existingRecords)
        {
            string normalizedUser = string.IsNullOrWhiteSpace(userName) ? Environment.UserName : userName.Trim();
            string modelPath = GetModelPath(doc);
            string modelName = doc?.Title ?? "Maquette inconnue";

            string creatorName = ResolveCreator(existingRecords, modelPath, modelName, normalizedUser);
            string lastChangedBy = GetLastChangedBy(doc, normalizedUser);

            return new CollaborativeModelRecord
            {
                ProjectName = doc?.ProjectInformation?.Name ?? "Projet sans nom",
                ModelName = modelName,
                ModelPath = modelPath,
                UserName = normalizedUser,
                CreatorName = creatorName,
                LastChangedBy = lastChangedBy,
                RevitVersion = uiapp?.Application?.VersionNumber ?? "Inconnue",
                TimestampDate = DateTime.Today
            };
        }

        private static string ResolveCreator(
            List<CollaborativeModelRecord> records,
            string modelPath,
            string modelName,
            string fallbackUser)
        {
            var existing = records
                .Where(r => IsSameModel(r, modelPath, modelName))
                .OrderBy(r => r.TimestampDate)
                .FirstOrDefault();

            if (existing == null)
                return fallbackUser;

            if (!string.IsNullOrWhiteSpace(existing.CreatorName))
                return existing.CreatorName;

            return !string.IsNullOrWhiteSpace(existing.UserName) ? existing.UserName : fallbackUser;
        }

        private static bool IsSameModel(CollaborativeModelRecord record, string modelPath, string modelName)
        {
            if (record == null)
                return false;

            if (!string.IsNullOrWhiteSpace(record.ModelPath) && !string.IsNullOrWhiteSpace(modelPath) &&
                !record.ModelPath.Equals("Chemin non disponible", StringComparison.OrdinalIgnoreCase) &&
                !modelPath.Equals("Chemin non disponible", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(record.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(record.ModelName ?? string.Empty, modelName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLastChangedBy(Document doc, string fallbackUser)
        {
            if (doc != null && doc.IsWorkshared && doc.ActiveView != null)
            {
                try
                {
                    var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, doc.ActiveView.Id);
                    if (info != null && !string.IsNullOrWhiteSpace(info.LastChangedBy))
                        return info.LastChangedBy;
                }
                catch
                {
                }
            }

            return fallbackUser;
        }

        private static void SaveAndExportWithoutLock(List<CollaborativeModelRecord> records)
        {
            var ordered = records
                .OrderByDescending(r => r.TimestampDate)
                .ToList();

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(ordered, options));
            ExportExcel(ordered);
        }

        private static List<CollaborativeModelRecord> LoadWithoutLock()
        {
            if (!File.Exists(JsonPath))
                return new List<CollaborativeModelRecord>();

            try
            {
                var json = File.ReadAllText(JsonPath);
                var data = JsonSerializer.Deserialize<List<CollaborativeModelRecord>>(json);
                return data ?? new List<CollaborativeModelRecord>();
            }
            catch
            {
                return new List<CollaborativeModelRecord>();
            }
        }

        private static void ExportExcel(List<CollaborativeModelRecord> records)
        {
            ExcelPackage.License.SetNonCommercialPersonal("BIMaestro");
            using (var package = new ExcelPackage(new FileInfo(ExcelPath)))
            {
                var ws = package.Workbook.Worksheets["Suivi_Maquettes"] ?? package.Workbook.Worksheets.Add("Suivi_Maquettes");
                ws.Cells.Clear();

                ws.Cells[1, 1].Value = "Projet";
                ws.Cells[1, 2].Value = "Maquette";
                ws.Cells[1, 3].Value = "Chemin";
                ws.Cells[1, 4].Value = "Utilisateur";
                ws.Cells[1, 5].Value = "Créateur (1er ouvert)";
                ws.Cells[1, 6].Value = "Dernière modification";
                ws.Cells[1, 7].Value = "Version Revit";
                ws.Cells[1, 8].Value = "Date";

                var row = 2;
                foreach (var item in records)
                {
                    ws.Cells[row, 1].Value = item.ProjectName;
                    ws.Cells[row, 2].Value = item.ModelName;
                    ws.Cells[row, 3].Value = item.ModelPath;
                    ws.Cells[row, 4].Value = item.UserName;
                    ws.Cells[row, 5].Value = item.CreatorName;
                    ws.Cells[row, 6].Value = item.LastChangedBy;
                    ws.Cells[row, 7].Value = item.RevitVersion;
                    ws.Cells[row, 8].Value = item.TimestampDate;
                    ws.Cells[row, 8].Style.Numberformat.Format = "yyyy-mm-dd";
                    row++;
                }

                if (ws.Dimension != null)
                {
                    ws.Cells[ws.Dimension.Address].AutoFilter = true;
                    ws.Cells[ws.Dimension.Address].AutoFitColumns();
                }

                package.Save();
            }
        }

        private static string GetModelPath(Document doc)
        {
            if (doc == null)
                return "Inconnu";

            if (!string.IsNullOrWhiteSpace(doc.PathName))
                return doc.PathName;

            try
            {
                if (doc.IsModelInCloud)
                {
                    var cloudPath = doc.GetCloudModelPath();
                    return ModelPathUtils.ConvertModelPathToUserVisiblePath(cloudPath);
                }
            }
            catch
            {
            }

            return "Chemin non disponible";
        }

        private static void EnsureActiveDirectory()
        {
            var _ = ResolveWritableDirectoryNoThrow();
        }

        private static string ResolveWritableDirectoryNoThrow()
        {
            if (!string.IsNullOrWhiteSpace(_activeDirectory))
                return _activeDirectory;

            if (string.IsNullOrWhiteSpace(_configuredSharedDirectory))
                _configuredSharedDirectory = LoadConfiguredDirectory();

            if (!string.IsNullOrWhiteSpace(_configuredSharedDirectory) && TryCreateAndWrite(_configuredSharedDirectory, out var customErr))
            {
                _activeDirectory = _configuredSharedDirectory;
                _lastDirectoryResolutionMessage = $"Dossier commun personnalisé utilisé: {_configuredSharedDirectory}";
                return _activeDirectory;
            }

            if (TryCreateAndWrite(PreferredSharedDirectory, out var prefError))
            {
                _activeDirectory = PreferredSharedDirectory;
                _lastDirectoryResolutionMessage = $"Dossier partagé utilisé: {PreferredSharedDirectory}";
                return _activeDirectory;
            }

            if (TryCreateAndWrite(FallbackDirectory, out var fallbackError))
            {
                _activeDirectory = FallbackDirectory;
                _lastDirectoryResolutionMessage =
                    $"Lecteur partagé indisponible ({PreferredSharedDirectory}). Fallback local utilisé: {FallbackDirectory}. Erreur initiale: {prefError}";
                return _activeDirectory;
            }

            _activeDirectory = PreferredSharedDirectory;
            _lastDirectoryResolutionMessage =
                $"Impossible d'initialiser les dossiers de sortie. Erreur partagé: {prefError} | Erreur fallback: {fallbackError}";
            return _activeDirectory;
        }

        private static string LoadConfiguredDirectory()
        {
            try
            {
                if (!File.Exists(CustomPathConfigFile))
                    return null;

                var path = File.ReadAllText(CustomPathConfigFile)?.Trim();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        private static void PersistConfiguredDirectory(string directory)
        {
            try
            {
                var configDir = Path.GetDirectoryName(CustomPathConfigFile);
                if (!string.IsNullOrWhiteSpace(configDir))
                    Directory.CreateDirectory(configDir);
                File.WriteAllText(CustomPathConfigFile, directory ?? string.Empty);
            }
            catch
            {
            }
        }

        private static bool TryCreateAndWrite(string directory, out string error)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write_test.tmp");
                File.WriteAllText(probe, DateTime.Now.ToString("O"));
                File.Delete(probe);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
