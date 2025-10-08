using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Licensing;
using System;
using System.IO;
using System.Reflection;

public class App : IExternalApplication
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

    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs");

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            UIControlledApp = application;

            // --- WPF App ---
            if (System.Windows.Application.Current == null)
                WpfApp = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            else
                WpfApp = System.Windows.Application.Current;

            Directory.CreateDirectory(LogDirectory);

            // --- Infos Revit ---
            var revitVersion = application.ControlledApplication.VersionNumber;

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

            // log non bloquant si hors-ligne
            if (fromCache)
                AppendLog("Licence utilisée depuis le cache (offline/proxy).");

            // --- États existants ---
            Couleur.ColoringStateManager.LoadState();

            // --- Excel logger ---
            ExcelLogger.Initialize();
            ExcelLogger.ConfigureActivity(
                idleThreshold: TimeSpan.FromMinutes(15),
                unfocusedThreshold: TimeSpan.FromMinutes(3),
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
            application.ControlledApplication.DocumentOpened += OnDocumentOpenedSafe;
            application.ControlledApplication.DocumentClosing += OnDocumentClosingSafe;
            application.ViewActivated += OnViewActivatedSafe;
            application.Idling += OnIdlingSafe;

            // --- Ruban ---
            AppUI.CreateRibbonUI(application);

            return Result.Succeeded;
        }
        catch (InvalidOperationException) // licence invalide / expirée et pas de cache
        {
            string addinsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2024");

            var td = new TaskDialog("BIMaestro – licence requise")
            {
                MainInstruction = "Licence invalide ou expirée",
                MainContent =
                    "Ta licence BIMaestro n'est pas active pour cette machine.\n\n" +
                    "Si tu veux tester, écris-moi : bimaestro.plugin@gmail.com\n" +
                    "Pour désinstaller, supprime BIMaestro.addin du dossier Addins."
            };
            td.CommonButtons = TaskDialogCommonButtons.Close;
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ouvrir le dossier Addins");
            var result = td.Show();
            if (result == TaskDialogResult.CommandLink1)
                System.Diagnostics.Process.Start("explorer.exe", addinsFolder);

            return Result.Failed;
        }
        catch (Exception ex)
        {
            AppendLog($"OnStartup : {ex.Message}\n{ex.StackTrace}");
            TaskDialog.Show("Erreur OnStartup", ex.ToString());
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            try { Telemetry.FlushAsync().GetAwaiter().GetResult(); }
            catch { }
            finally { Telemetry.Shutdown(); }

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

            if (!Couleur.ColoringStateManager.IsColoringActive)
            {
                if (!_hasResetWhenOff)
                {
                    Couleur.CombinedColoringApplication.ResetColorings(_uiApp.MainWindowHandle);
                    Couleur.PartialColoringHelper.ResetPartialColoring(_uiApp.MainWindowHandle);
                    _hasResetWhenOff = true;
                }
            }
            else
            {
                _hasResetWhenOff = false;
                Couleur.CombinedColoringApplication.ApplyTabItemColoring(_uiApp.MainWindowHandle);
                if (Couleur.ColoringStateManager.IsFullMode)
                    Couleur.CombinedColoringApplication.ApplyPapanoelColoring(_uiApp.MainWindowHandle);
                else
                    Couleur.PartialColoringHelper.ApplyPartialColoring(_uiApp.MainWindowHandle);
            }

            ExcelLogger.OnIdling(_uiApp);
        }
        catch (Exception ex)
        {
            AppendLog($"OnIdling : {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void OnViewActivatedSafe(object sender, ViewActivatedEventArgs args)
    {
        try
        {
            _uiApp ??= new UIApplication(args.Document.Application);
            ExcelLogger.OnViewActivated(args.Document, _uiApp);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur ViewActivated", ex.ToString());
        }
    }

    private void OnDocumentOpenedSafe(object sender, DocumentOpenedEventArgs e)
    {
        try
        {
            _uiApp ??= new UIApplication(e.Document.Application);
            ExcelLogger.OnDocumentOpened(e.Document, _uiApp);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur DocumentOpened", ex.ToString());
        }
    }

    private void OnDocumentClosingSafe(object sender, DocumentClosingEventArgs e)
    {
        try
        {
            _uiApp ??= new UIApplication(e.Document.Application);
            ExcelLogger.OnDocumentClosing(e.Document, _uiApp);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur DocumentClosing", ex.ToString());
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
