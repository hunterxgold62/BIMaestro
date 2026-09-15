using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMaestro.Codex;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BIMaestro.CodexTests
{
    // Test-only application. Loaded in a separately launched empty Revit process.
    // It neither opens user projects nor closes Revit itself.
    public sealed class NativeValidationApp : IExternalApplication
    {
        private UIControlledApplication application;
        private string DirectoryPath => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public Result OnStartup(UIControlledApplication value)
        {
            application = value; application.Idling += OnIdle;
            File.WriteAllText(Path.Combine(DirectoryPath, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            return Result.Succeeded;
        }
        private void OnIdle(object sender, IdlingEventArgs args)
        {
            application.Idling -= OnIdle;
            try
            {
                var ui = sender as UIApplication ?? throw new InvalidOperationException("Contexte UIApplication absent.");
                if (ui.Application.Documents.Size != 0) throw new InvalidOperationException("Le banc exige une instance Revit vide ; aucun document utilisateur ne sera modifié.");
                var report = CodexNativeValidation.Run(ui, message => File.WriteAllText(Path.Combine(DirectoryPath, "progress.txt"), DateTime.Now.ToString("O") + " " + message));
                File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), JsonConvert.SerializeObject(new { error = ex.ToString() }, Formatting.Indented)); }
        }
        public Result OnShutdown(UIControlledApplication value) { if (application != null) application.Idling -= OnIdle; return Result.Succeeded; }
    }
}
