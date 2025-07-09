// File: ParameterSettingsCommand.cs
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace YourNamespace
{
    [Transaction(TransactionMode.Manual)]
    public class ParameterSettingsCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // Ouvre la fenêtre WPF en modal sur Revit
            var window = new ParameterSettingsWindow();
            new WindowInteropHelper(window)
            {
                Owner = commandData.Application.MainWindowHandle
            };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
