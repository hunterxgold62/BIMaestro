using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using System;
using System.IO;

public static class ExcelLogger
{
    private static string excelFilePath;

    // Objet statique pour le verrouillage
    private static readonly object _lockObj = new object();

    static ExcelLogger()
    {
        try
        {
            // Définition du contexte de licence pour EPPlus
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            // Dossier RevitLogs dans Mes Documents
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs"
            );
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            excelFilePath = Path.Combine(logDirectory, "Historique_Temps_Revit.xlsx");

            // Création du fichier Excel s'il n'existe pas
            if (!File.Exists(excelFilePath))
            {
                CreateExcelFile();
            }
            else
            {
                // Vérifie que la feuille "Historique_Temps_Revit" existe
                lock (_lockObj)
                {
                    using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
                    {
                        if (package.Workbook.Worksheets["Historique_Temps_Revit"] == null)
                        {
                            CreateExcelFile();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur", $"Initialisation d'ExcelLogger échouée : {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Crée le fichier Excel et la feuille "Historique_Temps_Revit" avec des en-têtes.
    /// </summary>
    private static void CreateExcelFile()
    {
        try
        {
            lock (_lockObj)
            {
                using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
                {
                    var ws = package.Workbook.Worksheets["Historique_Temps_Revit"]
                             ?? package.Workbook.Worksheets.Add("Historique_Temps_Revit");

                    // En-têtes
                    ws.Cells["A1"].Value = "Event";
                    ws.Cells["B1"].Value = "Document ID";
                    ws.Cells["C1"].Value = "Document Name";
                    ws.Cells["D1"].Value = "Revit Version";
                    ws.Cells["E1"].Value = "Date";
                    ws.Cells["F1"].Value = "Time";
                    ws.Cells["G1"].Value = "Duration";

                    ws.Column(7).Style.Numberformat.Format = "[hh]:mm:ss";

                    package.Save();
                }
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur", $"Création du fichier Excel échouée : {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Méthode appelée pour signaler un document "Ouvert" dans l'Excel.
    /// </summary>
    public static void StartDocumentSessionLog(Document document, UIApplication uiApp)
    {
        LogEvent("Ouvert", document, uiApp, TimeSpan.Zero);
    }

    /// <summary>
    /// Méthode appelée pour signaler un document "Fermé" dans l'Excel,
    /// en passant la durée totale calculée en amont.
    /// </summary>
    public static void EndDocumentSessionLog(Document document, UIApplication uiApp, TimeSpan duration)
    {
        LogEvent("Fermé", document, uiApp, duration);
    }

    /// <summary>
    /// Méthode interne unique pour logguer dans la feuille "Activity Log".
    /// </summary>
    private static void LogEvent(string eventType, Document document, UIApplication uiApp, TimeSpan duration)
    {
        try
        {
            if (document == null || uiApp == null) return;

            // Choix du docId (pathName ou titre)
            string docId = !string.IsNullOrEmpty(document.PathName) ? document.PathName : document.Title;
            string docName = document.Title;
            string revitVersion = GetRevitVersion(uiApp);
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            string time = DateTime.Now.ToString("HH:mm:ss");
            string durationStr = duration != default
                ? duration.ToString(@"hh\:mm\:ss")
                : "00:00:00";

            lock (_lockObj)
            {
                using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
                {
                    var ws = package.Workbook.Worksheets["Historique_Temps_Revit"]
                             ?? package.Workbook.Worksheets.Add("Historique_Temps_Revit");

                    int lastRow = (ws.Dimension?.End.Row ?? 1) + 1;

                    ws.Cells[lastRow, 1].Value = eventType;   // A
                    ws.Cells[lastRow, 2].Value = docId;       // B
                    ws.Cells[lastRow, 3].Value = docName;     // C
                    ws.Cells[lastRow, 4].Value = revitVersion;// D
                    ws.Cells[lastRow, 5].Value = date;        // E
                    ws.Cells[lastRow, 6].Value = time;        // F

                    // Durée
                    if (TimeSpan.TryParse(durationStr, out TimeSpan parsedDuration))
                    {
                        ws.Cells[lastRow, 7].Value = parsedDuration;
                        ws.Cells[lastRow, 7].Style.Numberformat.Format = "[hh]:mm:ss";
                    }
                    else
                    {
                        ws.Cells[lastRow, 7].Value = "00:00:00";
                    }

                    package.Save();
                }
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur", $"Échec de l'enregistrement dans Excel : {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Récupère la version de Revit.
    /// </summary>
    private static string GetRevitVersion(UIApplication uiApp)
    {
        try
        {
            return uiApp.Application.VersionNumber;
        }
        catch
        {
            return "Inconnue";
        }
    }
}
