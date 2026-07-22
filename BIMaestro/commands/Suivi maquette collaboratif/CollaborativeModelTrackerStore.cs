using Autodesk.Revit.DB;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

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
                var entry = BuildAutoLogRecord(doc, uiapp, Environment.UserName);
                Task.Run(() =>
                {
                    try
                    {
                        AddPreparedEntryAndSave(entry);
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }

        private static void AddPreparedEntryAndSave(CollaborativeModelRecord entry)
        {
            if (entry == null) return;

            lock (SyncObj)
            {
                EnsureActiveDirectory();

                var records = LoadWithoutLock();
                entry.CreatorName = ResolveCreator(records, entry.ModelPath, entry.ModelName, entry.UserName);
                records.Add(entry);
                SaveAndExportWithoutLock(records);
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

        private static CollaborativeModelRecord BuildAutoLogRecord(
            Document doc,
            Autodesk.Revit.UI.UIApplication uiapp,
            string userName)
        {
            string normalizedUser = string.IsNullOrWhiteSpace(userName) ? Environment.UserName : userName.Trim();
            string modelPath = GetModelPath(doc);
            string modelName = doc?.Title ?? "Maquette inconnue";

            return new CollaborativeModelRecord
            {
                ProjectName = doc?.ProjectInformation?.Name ?? "Projet sans nom",
                ModelName = modelName,
                ModelPath = modelPath,
                UserName = normalizedUser,
                CreatorName = null,
                LastChangedBy = GetLastChangedBy(doc, normalizedUser),
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

            var json = JsonConvert.SerializeObject(ordered, Formatting.Indented);
            File.WriteAllText(JsonPath, json);
            ExportExcel(ordered);
        }

        private static List<CollaborativeModelRecord> LoadWithoutLock()
        {
            if (!File.Exists(JsonPath))
                return new List<CollaborativeModelRecord>();

            try
            {
                var json = File.ReadAllText(JsonPath);
                var data = JsonConvert.DeserializeObject<List<CollaborativeModelRecord>>(json);
                return data ?? new List<CollaborativeModelRecord>();
            }
            catch
            {
                return new List<CollaborativeModelRecord>();
            }
        }

        private static void ExportExcel(List<CollaborativeModelRecord> records)
        {
            using (var workbook = OpenOrCreateWorkbook(ExcelPath))
            {
                var ws = workbook.GetSheet("Suivi_Maquettes") ?? workbook.CreateSheet("Suivi_Maquettes");
                ClearSheet(ws);

                var headerFont = workbook.CreateFont();
                headerFont.IsBold = true;
                var headerStyle = workbook.CreateCellStyle();
                headerStyle.SetFont(headerFont);

                var dateStyle = workbook.CreateCellStyle();
                dateStyle.DataFormat = workbook.CreateDataFormat().GetFormat("yyyy-mm-dd");

                string[] headers =
                {
                    "Projet",
                    "Maquette",
                    "Chemin",
                    "Utilisateur",
                    "Créateur (1er ouvert)",
                    "Dernière modification",
                    "Version Revit",
                    "Date"
                };

                var headerRow = ws.CreateRow(0);
                for (int column = 0; column < headers.Length; column++)
                {
                    var cell = headerRow.CreateCell(column);
                    cell.SetCellValue(headers[column]);
                    cell.CellStyle = headerStyle;
                }

                int rowIndex = 1;
                foreach (var item in records)
                {
                    var row = ws.CreateRow(rowIndex++);
                    row.CreateCell(0).SetCellValue(item.ProjectName ?? string.Empty);
                    row.CreateCell(1).SetCellValue(item.ModelName ?? string.Empty);
                    row.CreateCell(2).SetCellValue(item.ModelPath ?? string.Empty);
                    row.CreateCell(3).SetCellValue(item.UserName ?? string.Empty);
                    row.CreateCell(4).SetCellValue(item.CreatorName ?? string.Empty);
                    row.CreateCell(5).SetCellValue(item.LastChangedBy ?? string.Empty);
                    row.CreateCell(6).SetCellValue(item.RevitVersion ?? string.Empty);

                    var dateCell = row.CreateCell(7);
                    dateCell.SetCellValue(item.TimestampDate);
                    dateCell.CellStyle = dateStyle;
                }

                ws.SetAutoFilter(new CellRangeAddress(0, Math.Max(0, rowIndex - 1), 0, headers.Length - 1));
                ws.CreateFreezePane(0, 1);
                for (int column = 0; column < headers.Length; column++)
                {
                    try { ws.AutoSizeColumn(column); } catch { }
                }

                SaveWorkbook(workbook, ExcelPath);
            }
        }

        private static XSSFWorkbook OpenOrCreateWorkbook(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return new XSSFWorkbook();

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return new XSSFWorkbook(stream);
        }

        private static void ClearSheet(ISheet sheet)
        {
            for (int rowIndex = sheet.LastRowNum; rowIndex >= sheet.FirstRowNum; rowIndex--)
            {
                var row = sheet.GetRow(rowIndex);
                if (row != null)
                    sheet.RemoveRow(row);
            }
        }

        private static void SaveWorkbook(XSSFWorkbook workbook, string path)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    workbook.Write(stream);

                File.Copy(temporaryPath, path, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch { }
            }
        }

        private static string GetModelPath(Document doc)
        {
            if (doc == null)
                return "Inconnu";

            try
            {
                string localPath = doc.PathName;
                string centralPath = null;

                if (doc.IsWorkshared)
                {
                    try
                    {
                        var centralModelPath = doc.GetWorksharingCentralModelPath();
                        if (centralModelPath != null)
                            centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralModelPath);
                    }
                    catch
                    {
                    }
                }

                if (!string.IsNullOrWhiteSpace(localPath) && !string.IsNullOrWhiteSpace(centralPath) &&
                    !string.Equals(localPath, centralPath, StringComparison.OrdinalIgnoreCase))
                {
                    return localPath + "|" + centralPath;
                }

                if (!string.IsNullOrWhiteSpace(centralPath))
                    return centralPath;

                if (!string.IsNullOrWhiteSpace(localPath))
                    return localPath;
            }
            catch
            {
            }

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
