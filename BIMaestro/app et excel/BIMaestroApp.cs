using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Licensing;
using System;
using System.IO;
using System.Reflection;
using BIMaestro.Localization;


public class BIMaestroApp : IExternalApplication
{
    public static UIControlledApplication UIControlledApp { get; private set; }
    public static System.Windows.Application WpfApp { get; private set; }

    // >>> Ajout : expose ces infos au reste du plugin
    public static string LicenseJwt { get; private set; }
    public static string MachineId { get; private set; }
    public static string InstallId { get; private set; }
    public static string PluginVersion { get; private set; }



    private UIApplication _uiApp;
    private bool _hasResetWhenOff = false;
    private bool _hasShownTimeTrackingError = false;
    private string _lastRibbonTabTitle;
    private DateTime _nextRibbonInspectionUtc = DateTime.MinValue;
    private DateTime _lastRibbonApplyUtc = DateTime.MinValue;
    private int _pendingProjectBrowserViewRefreshes;
    private DateTime _nextProjectBrowserViewRefreshUtc =
        DateTime.MinValue;

    private static readonly TimeSpan RibbonInspectionInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RibbonSafetyRefreshInterval =
        TimeSpan.FromSeconds(30);

    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs");

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            UIControlledApp = application;
            UiLanguage.Initialize(application.ControlledApplication.Language.ToString());

            // --- WPF BIMaestroApp ---
            if (System.Windows.Application.Current == null)
                WpfApp = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            else
                WpfApp = System.Windows.Application.Current;

            Directory.CreateDirectory(LogDirectory);

            // --- Infos Revit ---
            var revitVersion = application.ControlledApplication.VersionNumber;
            Couleur.ProjectBrowserColoring.ConfigureRevitVersion(
                revitVersion);

            // Username Revit (fallback Windows)
            string revitUser = TryReadRevitUsernameFromIni(revitVersion)
                               ?? Environment.UserName;

            // --- LICENCE (réseau sinon cache) ---
            string licenseKey = string.IsNullOrWhiteSpace(revitUser) ? Environment.UserName : revitUser;
            MachineId = LicenseManager.ComputeMachineId();
            InstallId = LicenseManager.GetOrCreateInstallId();
            PluginVersion = GetPluginVersion();
            string userAgent = $"BIMaestro/{PluginVersion} Revit/{revitVersion} Install/{InstallId}";

            bool fromCache;
            LicenseJwt = LicenseManager.ValidateOrUseCache(licenseKey, MachineId, out fromCache, userAgent);
            // Rend le JWT dispo partout (BaseTrackedCommand, Welcome, etc.)
            Licensing.LicenseSession.Set(licenseKey, LicenseJwt);


            // log non bloquant si hors-ligne
            if (fromCache)
                AppendLog("Licence utilisée depuis le cache (offline/proxy).");

            // --- États existants ---
            Couleur.ColoringStateManager.LoadState();

            // --- Excel logger ---
            ExcelLogger.Initialize();
            ExcelLogger.ConfigureActivity(
                idleThreshold: TimeSpan.FromMinutes(15),
                unfocusedThreshold: TimeSpan.FromMinutes(10),
                busyGapThreshold: TimeSpan.FromSeconds(15),
                cpuBusyThreshold: 0.30,
                countBusyWhenUnfocused: false
            );

            // --- TÉLÉMÉTRIE ---
            try
            {
                string functionsBaseUrl = "https://xqovxfgghbqxwsadzhzl.functions.supabase.co";
                Telemetry.Init(
                    edgeFunctionsBaseUrl: functionsBaseUrl,
                    licenseJwt: LicenseJwt,
                    pluginVersion: PluginVersion,
                    machineIdHash: MachineId,
                    fallbackLicenseKey: licenseKey
                );
                Telemetry.TrackButton("Plugin.Startup", true, new
                {
                    revit_username = revitUser,
                    windows_user = Environment.UserName,
                    machine_name = Environment.MachineName,
                    install_id = InstallId,
                    revit_version = revitVersion
                });
               
            }
            catch (Exception telEx)
            {
                AppendLog($"Telemetry.Init : {telEx.Message}\n{telEx.StackTrace}");
            }

            // --- Events Revit ---
            application.ControlledApplication.DocumentChanged += OnDocumentChangedSafe;
            Analyse.ElementHistoryTracker.Start();
            application.ControlledApplication.DocumentCreated += OnDocumentCreatedSafe;
            application.ControlledApplication.DocumentOpened += OnDocumentOpenedSafe;
            application.ControlledApplication.DocumentClosing += OnDocumentClosingSafe;
            application.ViewActivated += OnViewActivatedSafe;
            application.SelectionChanged += OnSelectionChangedSafe;
            application.Idling += OnIdlingSafe;

            // --- Ruban ---
            AppUI.CreateRibbonUI(application);
            Page.SecretGifShortcutManager.Initialize();

            // "//" à rajouté pour retiré le message de bienvenue 
            BIMaestro.Welcome.WelcomeManager.Initialize(application);
            BIMaestro.Welcome.WelcomeManager.TrySyncPendingProfile(LicenseJwt);
            Page.UpdateNotificationManager.Initialize(application);

       
         return Result.Succeeded;
        }
        catch (InvalidOperationException ex)
        {
            IssueReporter.ShowStartupError(ex);
            return Result.Failed;
        }
        catch (Exception ex)
        {
            AppendLog($"OnStartup : {ex.Message}\n{ex.StackTrace}");
            IssueReporter.ShowStartupError(ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            BIMaestro.ViewHover.ViewDeckService.Shutdown();
            try { Telemetry.FlushAsync().GetAwaiter().GetResult(); }
            catch { }
            finally { Telemetry.Shutdown(); }

            Page.SecretGifShortcutManager.Shutdown();
            Analyse.ElementHistoryTracker.Stop();
            ExcelLogger.Shutdown();
            WpfApp?.Shutdown();
        }
        catch (Exception ex)
        {
            AppendLog($"OnShutdown : {ex.Message}\n{ex.StackTrace}");
        }
        return Result.Succeeded;
    }

    private void OnIdlingSafe(object sender, IdlingEventArgs e)
    {
        try
        {
            _uiApp ??= sender as UIApplication;
            if (_uiApp == null) return;

            Page.SecretGifShortcutManager.PollKeyboardState();
            Analyse.ElementHistoryTracker.ProcessDeferredPrime(_uiApp.ActiveUIDocument?.Document);
            RefreshProjectBrowserActiveViewWhenNeeded();
            BIMaestro.ViewHover.ViewHoverPreviewService.ProcessPending(_uiApp);
            BIMaestro.ViewHover.ViewDeckService.ProcessIdling(_uiApp);

            if (!Couleur.ColoringStateManager.IsColoringActive)
            {
                if (!_hasResetWhenOff)
                {
                    Couleur.CombinedColoringApplication.ResetColorings(_uiApp.MainWindowHandle);
                    Couleur.PartialColoringHelper.ResetPartialColoring(_uiApp.MainWindowHandle);
                    _hasResetWhenOff = true;
                    _lastRibbonTabTitle = null;
                    _lastRibbonApplyUtc = DateTime.MinValue;
                }
            }
            else
            {
                _hasResetWhenOff = false;
                RefreshRibbonColorsWhenNeeded();
            }

            ExcelLogger.OnIdling(_uiApp);

        }
        catch (Exception ex)
        {
            AppendLog($"OnIdling : {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void RefreshRibbonColorsWhenNeeded()
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextRibbonInspectionUtc)
            return;

        _nextRibbonInspectionUtc = now + RibbonInspectionInterval;
        string activeTabTitle =
            Couleur.RevitRibbonCatalog.GetActiveTabTitle() ?? string.Empty;
        bool tabChanged = !string.Equals(
            activeTabTitle,
            _lastRibbonTabTitle,
            StringComparison.OrdinalIgnoreCase);
        bool safetyRefreshDue =
            now - _lastRibbonApplyUtc >= RibbonSafetyRefreshInterval;

        if (!tabChanged && !safetyRefreshDue)
            return;

        Couleur.CombinedColoringApplication.ApplyTabItemColoring(
            _uiApp.MainWindowHandle);
        if (Couleur.ColoringStateManager.IsFullMode)
        {
            Couleur.CombinedColoringApplication.ApplyPapanoelColoring(
                _uiApp.MainWindowHandle);
        }
        else
        {
            Couleur.PartialColoringHelper.ApplyPartialColoring(
                _uiApp.MainWindowHandle);
        }

        Couleur.RevitRibbonGlobalColoring.Apply(_uiApp.MainWindowHandle);
        _lastRibbonTabTitle = activeTabTitle;
        _lastRibbonApplyUtc = now;
    }

    private void OnViewActivatedSafe(object sender, ViewActivatedEventArgs args)
    {
        try
        {
            _uiApp ??= new UIApplication(args.Document.Application);
            Analyse.ElementHistoryTracker.ScheduleDeferredPrime(args.Document);
            ExcelLogger.OnViewActivated(args.Document, _uiApp);
            Couleur.ProjectBrowserColoring
                .CompleteAutomaticFocusNavigation();
            Couleur.ProjectBrowserColoring.TrackActiveView(
                args.Document,
                args.CurrentActiveView);
            BIMaestro.ViewHover.ViewHoverPreviewService.TrackActivatedView(
                args.Document,
                args.CurrentActiveView);
            BIMaestro.ViewHover.ViewDeckChangeService.Activate(args.Document, args.CurrentActiveView);
            _pendingProjectBrowserViewRefreshes = 3;
            _nextProjectBrowserViewRefreshUtc =
                DateTime.UtcNow.AddMilliseconds(80);
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException ex)
        {
            AppendLog($"OnViewActivated (suivi du temps) : {ex.Message}\n{ex.StackTrace}");

            if (_hasShownTimeTrackingError) return;
            _hasShownTimeTrackingError = true;

            var td = new TaskDialog(UiLanguage.T("BIMaestro - Suivi du temps", "BIMaestro - Time Tracking"))
            {
                MainInstruction = UiLanguage.T("Un problème est survenu avec l'enregistrement du temps.", "A problem occurred while recording time."),
                MainContent = UiLanguage.T(
                    "BIMaestro n'arrive plus à suivre correctement le temps sur ce projet.\n\nPour rétablir le suivi, enregistre ton travail puis relance Revit.",
                    "BIMaestro can no longer track time correctly for this project.\n\nTo restore tracking, save your work and restart Revit.")
            };
            td.CommonButtons = TaskDialogCommonButtons.Close;
            td.Show();
        }
        catch (Exception ex)
        {
            AppendLog($"OnViewActivated : {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void RefreshProjectBrowserActiveViewWhenNeeded()
    {
        if (_pendingProjectBrowserViewRefreshes <= 0 ||
            DateTime.UtcNow < _nextProjectBrowserViewRefreshUtc)
        {
            return;
        }

        UIDocument uiDocument = _uiApp?.ActiveUIDocument;
        Document document = uiDocument?.Document;
        View activeView = uiDocument?.ActiveView;
        if (document != null && activeView != null)
        {
            Couleur.ProjectBrowserColoring.TrackActiveView(
                document,
                activeView);
        }

        _pendingProjectBrowserViewRefreshes--;
        _nextProjectBrowserViewRefreshUtc =
            DateTime.UtcNow.AddMilliseconds(
                _pendingProjectBrowserViewRefreshes == 2
                    ? 140
                    : 260);
    }

    private void OnSelectionChangedSafe(object sender, SelectionChangedEventArgs args)
    {
        try
        {
            var doc = args.GetDocument();
            var selectedIds = args.GetSelectedElements();
            BIMaestro.ViewHover.ViewDeckChangeService.CaptureSelection(doc, selectedIds);
            Analyse.ElementHistoryTracker.CaptureSelectedElementDetails(doc, selectedIds);
            Analyse.ElementHistoryHoverInfoService.OnSelectionChanged(
                doc,
                selectedIds);
            Couleur.ProjectBrowserColoring.FocusSelectedSheetContent(
                doc,
                selectedIds);
        }
        catch (Exception ex)
        {
            AppendLog($"OnSelectionChanged : {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void OnDocumentOpenedSafe(object sender, DocumentOpenedEventArgs e)
    {
        try
        {
            _uiApp ??= new UIApplication(e.Document.Application);
            Analyse.ElementHistoryTracker.ScheduleDeferredPrime(e.Document);
            Analyse.ElementHistoryHoverInfoService.OnDocumentOpened(e.Document);
            ExcelLogger.OnDocumentOpened(e.Document, _uiApp);
            Analyse.CollaborativeModelTrackerStore.TryAutoLog(e.Document, _uiApp);
            BIMaestro.ViewHover.ViewHoverPreviewService
                .LoadCachedPreviews(e.Document);
            BIMaestro.ViewHover.ViewHoverPreviewService
                .ScheduleCacheMaintenance(e.Document);
        }
        catch (Exception ex)
        {
            TaskDialog.Show(UiLanguage.T("Erreur DocumentOpened", "DocumentOpened Error"), ex.ToString());
        }
    }

    private void OnDocumentCreatedSafe(object sender, DocumentCreatedEventArgs e)
    {
        try
        {
            _uiApp ??= new UIApplication(e.Document.Application);
            Analyse.ElementHistoryTracker.ScheduleDeferredPrime(e.Document);
            BIMaestro.ViewHover.ViewHoverPreviewService
                .ScheduleCacheMaintenance(e.Document);
        }
        catch (Exception ex)
        {
            AppendLog($"OnDocumentCreated : {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void OnDocumentClosingSafe(object sender, DocumentClosingEventArgs e)
    {
        try
        {
            _uiApp ??= new UIApplication(e.Document.Application);
            Analyse.ElementHistoryHoverInfoService.Hide();
            BIMaestro.ViewHover.ViewHoverPreviewService.ForgetDocument(
                e.Document);
            ExcelLogger.OnDocumentClosing(e.Document, _uiApp);
            Analyse.CollaborativeModelTrackerStore.TryAutoLog(e.Document, _uiApp);
        }
        catch (Exception ex)
        {
            TaskDialog.Show(UiLanguage.T("Erreur DocumentClosing", "DocumentClosing Error"), ex.ToString());
        }
    }

    private void OnDocumentChangedSafe(object sender, DocumentChangedEventArgs e)
    {
        Document document = null;
        try
        {
            document = e.GetDocument();
            BIMaestro.ViewHover.ViewDeckChangeService.Track(document, e);
        }
        catch (Exception ex) { AppendLog("ViewDeck change tracking: " + ex); }
        try
        {
            document = e.GetDocument();
            Analyse.ElementHistoryTracker.CaptureDocumentChanges(document, e);
        }
        catch
        {
        }

        try
        {
            document ??= e.GetDocument();
            BIMaestro.ViewHover.ViewHoverPreviewService.TrackDocumentChanges(
                document,
                e);
        }
        catch
        {
        }
    }
    private static string GetPluginVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info;
            var v = asm.GetName().Version;
            return v != null ? v.ToString() : "dev";
        }
        catch { return "dev"; }
    }

    private static string TryReadRevitUsernameFromIni(string versionNumber)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var iniPath = Path.Combine(appData, "Autodesk", "Revit", $"Autodesk Revit {versionNumber}", "Revit.ini");
            if (!File.Exists(iniPath)) return null;

            string currentSection = "";
            foreach (var lineRaw in File.ReadAllLines(iniPath))
            {
                var line = lineRaw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                    currentSection = line.Substring(1, line.Length - 2);
                else if (currentSection.Equals("UserInterface", StringComparison.OrdinalIgnoreCase)
                         && line.StartsWith("Username=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line.Substring("Username=".Length).Trim();
                    return string.IsNullOrWhiteSpace(v) ? null : v;
                }
            }
            return null;
        }
        catch { return null; }
    }

    private static void AppendLog(string txt)
    {
        try
        {
            var path = Path.Combine(LogDirectory, "error_log.txt");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {txt}\n---\n");
        }
        catch { }
    }
}
