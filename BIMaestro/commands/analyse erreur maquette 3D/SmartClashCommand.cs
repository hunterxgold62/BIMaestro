using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System.Linq;

namespace Analyse
{
    [Transaction(TransactionMode.Manual)]
    public class SmartClashCommand : BaseTrackedCommand
    {
        public static readonly string Smart3DName = "BIMaestro – SmartCheck 3D";
        protected override string ButtonId => "SmartClashCommand";
        private static SmartCheckWindow _window;
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application;
            var compiledApi = typeof(SmartClashCommand).Assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "RevitAPI")?.Version.Major;
            if (int.TryParse(ui.Application.VersionNumber, out int revitYear) && revitYear >= 2025 && compiledApi != revitYear - 2000)
            {
                TaskDialog.Show("Clash 3D", "Cette DLL de BIMaestro a été compilée pour Revit " + (compiledApi + 2000)
                    + ". Installez la version dédiée à Revit " + revitYear + ", puis redémarrez Revit.");
                return Result.Cancelled;
            }
            if (ui.ActiveUIDocument == null || ui.ActiveUIDocument.Document.IsFamilyDocument)
            {
                TaskDialog.Show("Clash 3D", "Ouvrez une maquette de projet pour analyser les conflits.");
                return Result.Cancelled;
            }
            if (_window != null && _window.IsVisible && _window.OwnerDocument.IsValidObject && _window.OwnerDocument.Equals(ui.ActiveUIDocument.Document))
            {
                if (Couleur.AppearanceOnboarding.IsTourPending("clash-3d") && !_window.RestartPreparedTutorial(SmartScanSetup.Capture(ui.ActiveUIDocument)))
                { TaskDialog.Show("Clash 3D", "Attendez la fin de l'action en cours, puis cliquez à nouveau sur Clash 3D pour démarrer le guide."); return Result.Cancelled; }
                _window.Activate(); return Result.Succeeded;
            }
            _window?.Close();
            var handler = new SmartExternalHandler(ui);
            var window = new SmartCheckWindow(ExternalEvent.Create(handler), handler, SmartScanSetup.Capture(ui.ActiveUIDocument));
            _window = window;
            new System.Windows.Interop.WindowInteropHelper(window).Owner = ui.MainWindowHandle;
            window.Closed += (s, e) => { if (ReferenceEquals(_window, window)) _window = null; };
            window.Show();
            return Result.Succeeded;
        }
    }
}
