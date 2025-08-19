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

    private UIApplication _uiApp;
    private bool _hasResetWhenOff = false;

    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs");

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // --- LICENCE (inchangé, sauf qu'on récupère maintenant le JWT) ---
            string licenseKey = Environment.UserName;
            string machineId = LicenseManager.ComputeMachineId();          // SHA-256(MachineName + MAC) selon ton implémentation
            string licenseJwt = LicenseManager.Validate(licenseKey, machineId);

            UIControlledApp = application;

            // --- WPF App (inchangé) ---
            if (System.Windows.Application.Current == null)
                WpfApp = new System.Windows.Application() { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            else
                WpfApp = System.Windows.Application.Current;

            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            // --- Couleurs : restaurer l’état (inchangé) ---
            Couleur.ColoringStateManager.LoadState();

            // --- Init tracker + Excel (inchangé) ---
            ExcelLogger.Initialize();
            ExcelLogger.ConfigureActivity(
                idleThreshold: TimeSpan.FromMinutes(15),
                unfocusedThreshold: TimeSpan.FromMinutes(3),
                busyGapThreshold: TimeSpan.FromSeconds(15),
                cpuBusyThreshold: 0.30,
                countBusyWhenUnfocused: false
            );

            // >>> TÉLÉMÉTRIE : Init (ne bloque jamais Revit si ça échoue)
            try
            {
                string functionsBaseUrl = "https://xqovxfgghbqxwsadzhzl.functions.supabase.co";
                string pluginVersion = GetPluginVersion();
                Licensing.Telemetry.Init(
                    functionsBaseUrl,
                    licenseJwt,
                    pluginVersion,
                    machineId,
                    licenseKey);
            }
            catch (Exception telEx)
            {
                // log silencieux si besoin, pas de TaskDialog pour ne pas polluer l’UX
                try
                {
                    string logFilePath = Path.Combine(LogDirectory, "error_log.txt");
                    File.AppendAllText(logFilePath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Telemetry.Init : {telEx.Message}\n{telEx.StackTrace}\n---\n");
                }
                catch { }
            }

            // --- Events Revit (inchangé) ---
            application.ControlledApplication.DocumentOpened += OnDocumentOpenedSafe;
            application.ControlledApplication.DocumentClosing += OnDocumentClosingSafe;
            application.ViewActivated += OnViewActivatedSafe;
            application.Idling += OnIdlingSafe;

            // --- Ruban (inchangé) ---
            AppUI.CreateRibbonUI(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            try
            {
                string logFilePath = Path.Combine(LogDirectory, "error_log.txt");
                File.AppendAllText(logFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnStartup : {ex.Message}\n{ex.StackTrace}\n---\n");
            }
            catch { }
            TaskDialog.Show("Erreur OnStartup", ex.ToString());
            TaskDialog.Show("Erreur de licence", ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            // >>> FLUSH FINAL TÉLÉMÉTRIE (synchrone, entouré pour ne jamais planter)
            try { Telemetry.FlushAsync().GetAwaiter().GetResult(); }
            catch { }
            finally { Telemetry.Shutdown(); }

            ExcelLogger.Shutdown();
            WpfApp?.Shutdown();
        }
        catch (Exception ex)
        {
            try
            {
                string logFilePath = Path.Combine(LogDirectory, "error_log.txt");
                File.AppendAllText(logFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnShutdown : {ex.Message}\n{ex.StackTrace}\n---\n");
            }
            catch { }
        }
        return Result.Succeeded;
    }

    private void OnIdlingSafe(object sender, IdlingEventArgs e)
    {
        try
        {
            _uiApp ??= sender as UIApplication;
            if (_uiApp == null) return;

            // — Coloration existante —
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

            // — Tracking existant —
            ExcelLogger.OnIdling(_uiApp);
        }
        catch (Exception ex)
        {
            try
            {
                string logFilePath = Path.Combine(LogDirectory, "error_log.txt");
                File.AppendAllText(logFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnIdling : {ex.Message}\n{ex.StackTrace}\n---\n");
            }
            catch { }
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

    // >>> utilitaire version plugin (prend InformationalVersion sinon AssemblyVersion)
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
}
