using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Events;
using System;
using System.Collections.Generic;
using System.IO;
using MyRevitPlugin;
using IA;
using Autodesk.Revit.DB.Events;

public class App : IExternalApplication
{
    // Instance de UIControlledApplication pour l'interface Revit
    public static UIControlledApplication UIControlledApp { get; private set; }

    // Instance WPF que nous allons créer ici
    public static System.Windows.Application WpfApp { get; private set; }

    // Gestion des sessions de document (clés = doc.Title)
    private Dictionary<string, WorkSession> documentSessions;
    private Document previousDocument;
    private UIApplication uiApp;

    // Drapeau pour s'assurer que le reset de coloration n'est exécuté qu'une seule fois
    private bool _hasResetWhenOff = false;

    // Dossier de log pour error_log.txt
    private static readonly string logDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs");

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Stocker l'instance de UIControlledApplication
            UIControlledApp = application;

            // Création ou récupération de l'instance WPF
            if (System.Windows.Application.Current == null)
            {
                WpfApp = new System.Windows.Application() { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            }
            else
            {
                WpfApp = System.Windows.Application.Current;
            }

            documentSessions = new Dictionary<string, WorkSession>();
            ColoringStateManager.LoadState();

            // Vérification du dossier de log
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Abonnement aux événements
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;
            application.ViewActivated += OnViewActivatedSafe;

            // Création de l'interface utilisateur (Ribbon, boutons, etc.)
            AppUI.CreateRibbonUI(application);

            // Application du traitement lors de l'état "Idling" de Revit
            application.Idling += OnIdlingSafe;

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            LogError("OnStartup", ex);
            TaskDialog.Show("Erreur OnStartup", ex.ToString());
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            // Exemple de nettoyage supplémentaire
            if (WpfApp != null)
            {
                WpfApp.Shutdown();
            }
        }
        catch (Exception ex)
        {
            LogError("OnShutdown", ex);
            // On ne fait pas planter Revit pour autant
        }
        return Result.Succeeded;
    }

    /// <summary>
    /// Méthode appelée lors de l'état "Idling" de Revit, protégée par un try/catch.
    /// </summary>
    private void OnIdlingSafe(object sender, IdlingEventArgs e)
    {
        try
        {
            OnIdling(sender, e);
        }
        catch (Exception ex)
        {
            LogError("OnIdling", ex);
            TaskDialog.Show("Erreur OnIdling", ex.ToString());
        }
    }

    /// <summary>
    /// Méthode interne pour appliquer ou réinitialiser les colorations (sans try/catch).
    /// </summary>
    private void OnIdling(object sender, IdlingEventArgs e)
    {
        uiApp ??= sender as UIApplication;
        if (uiApp == null)
            return;

        if (!ColoringStateManager.IsColoringActive)
        {
            if (!_hasResetWhenOff)
            {
                CombinedColoringApplication.ResetColorings(uiApp.MainWindowHandle);
                PartialColoringHelper.ResetPartialColoring(uiApp.MainWindowHandle);
                _hasResetWhenOff = true;
            }
            return;
        }

        _hasResetWhenOff = false;
        CombinedColoringApplication.ApplyTabItemColoring(uiApp.MainWindowHandle);

        if (ColoringStateManager.IsFullMode)
        {
            CombinedColoringApplication.ApplyPapanoelColoring(uiApp.MainWindowHandle);
        }
        else
        {
            PartialColoringHelper.ApplyPartialColoring(uiApp.MainWindowHandle);
        }
    }

    /// <summary>
    /// Protège l'appel au ViewActivated.
    /// </summary>
    private void OnViewActivatedSafe(object sender, ViewActivatedEventArgs args)
    {
        try
        {
            OnViewActivated(args);
        }
        catch (Exception ex)
        {
            LogError("OnViewActivated", ex);
            TaskDialog.Show("Erreur ViewActivated", ex.ToString());
        }
    }

    /// <summary>
    /// Lors du changement de vue, vérifie si le document actif a changé afin de gérer la session.
    /// </summary>
    private void OnViewActivated(ViewActivatedEventArgs args)
    {
        uiApp ??= new UIApplication(args.Document.Application);
        Document activeDoc = args.Document;

        // Vérifier si le document est valide
        if (activeDoc == null || !activeDoc.IsValidObject) return;
        if (previousDocument != null && !previousDocument.IsValidObject) previousDocument = null;

        // Si on a changé de doc
        if (previousDocument == null
            || !previousDocument.IsValidObject
            || previousDocument.Title != activeDoc.Title)
        {
            SwitchSession(activeDoc);
        }
    }

    /// <summary>
    /// DocumentOpened - abonné avec un try/catch.
    /// </summary>
    private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
    {
        try
        {
            StartSession(e.Document);
        }
        catch (Exception ex)
        {
            LogError("DocumentOpened", ex);
            TaskDialog.Show("Erreur DocumentOpened", ex.ToString());
        }
    }

    /// <summary>
    /// DocumentClosing - abonné avec un try/catch.
    /// </summary>
    private void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
    {
        try
        {
            EndSession(e.Document);
        }
        catch (Exception ex)
        {
            LogError("DocumentClosing", ex);
            TaskDialog.Show("Erreur DocumentClosing", ex.ToString());
        }
    }

    /// <summary>
    /// On ouvre une session pour ce document, 
    /// et on log "Ouvert" via ExcelLogger.
    /// </summary>
    private void StartSession(Document document)
    {
        if (!documentSessions.ContainsKey(document.Title))
        {
            documentSessions[document.Title] = new WorkSession();
        }
        documentSessions[document.Title].StartSession();

        // --> Ici, on appelle la méthode "haute-niveau" d'ExcelLogger
        if (uiApp != null)
        {
            ExcelLogger.StartDocumentSessionLog(document, uiApp);
        }

        previousDocument = document;
    }

    /// <summary>
    /// On clôture la session pour ce document, 
    /// et on log "Fermé" via ExcelLogger avec la durée totale.
    /// </summary>
    private void EndSession(Document document)
    {
        if (documentSessions.ContainsKey(document.Title))
        {
            documentSessions[document.Title].EndSession();
            TimeSpan totalDuration = documentSessions[document.Title].GetTotalDuration();

            // --> Ici, on appelle la méthode "haute-niveau" d'ExcelLogger
            if (uiApp != null)
            {
                ExcelLogger.EndDocumentSessionLog(document, uiApp, totalDuration);
            }

            documentSessions.Remove(document.Title);
        }
    }

    /// <summary>
    /// Lors d'un changement de document (switch), 
    /// on arrête la session précédente (sans quitter Revit) et on démarre la nouvelle.
    /// </summary>
    private void SwitchSession(Document newDocument)
    {
        // On clôture la session sur le précédent, SANS logger "Fermé" dans l'Excel 
        // si on ne souhaite que 1 seule ligne "Fermé" à la fin.
        // --> Mais si tu veux logguer quelque chose ici, tu peux appeler 
        //     ExcelLogger.EndDocumentSessionLog(...) ou un event "Switch".
        if (previousDocument != null && documentSessions.ContainsKey(previousDocument.Title))
        {
            documentSessions[previousDocument.Title].EndSession();
        }

        // Puis on démarre la session pour le nouveau doc
        if (!documentSessions.ContainsKey(newDocument.Title))
        {
            documentSessions[newDocument.Title] = new WorkSession();
        }
        documentSessions[newDocument.Title].StartSession();

        previousDocument = newDocument;
    }

    /// <summary>
    /// Méthode statique pour enregistrer les exceptions dans un fichier error_log.txt
    /// </summary>
    private static void LogError(string context, Exception ex)
    {
        try
        {
            string logFilePath = Path.Combine(logDirectory, "error_log.txt");
            using (var writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context} : {ex.Message}");
                writer.WriteLine(ex.StackTrace);
                writer.WriteLine("------------------------------------------------------------");
            }
        }
        catch
        {
            // On évite de relancer une exception dans le catch du catch
        }
    }
}

/// <summary>
/// Classe permettant de suivre la durée d'une session de travail sur un document.
/// </summary>
public class WorkSession
{
    private List<Tuple<DateTime, DateTime>> sessions;
    private DateTime? currentStart;

    public WorkSession()
    {
        sessions = new List<Tuple<DateTime, DateTime>>();
    }

    public void StartSession()
    {
        if (currentStart == null)
        {
            currentStart = DateTime.Now;
        }
    }

    public void EndSession()
    {
        if (currentStart != null)
        {
            DateTime endTime = DateTime.Now;
            sessions.Add(new Tuple<DateTime, DateTime>(currentStart.Value, endTime));
            currentStart = null;
        }
    }

    public TimeSpan GetTotalDuration()
    {
        TimeSpan totalDuration = TimeSpan.Zero;
        foreach (var session in sessions)
        {
            totalDuration += session.Item2 - session.Item1;
        }
        return totalDuration;
    }
}
