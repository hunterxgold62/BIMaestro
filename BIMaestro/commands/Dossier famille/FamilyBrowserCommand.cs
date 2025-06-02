using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Windows.Interop;

namespace FamilyBrowserPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class FamilyBrowserCommand : IExternalCommand
    {
        public static UIApplication uiapp;
        public static FamilyBrowserWindow MainWindowRef; // Référence à la fenêtre principale
        public static LoadFamilyHandler LoadFamilyHandlerInstance;
        public static ExternalEvent LoadFamilyEventInstance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                uiapp = commandData.Application;

                // Initialiser le handler et l'event
                LoadFamilyHandlerInstance = new LoadFamilyHandler();
                LoadFamilyEventInstance = ExternalEvent.Create(LoadFamilyHandlerInstance);

                FamilyBrowserWindow window = new FamilyBrowserWindow();
                MainWindowRef = window;

                WindowInteropHelper helper = new WindowInteropHelper(window);
                helper.Owner = uiapp.MainWindowHandle;

                window.Topmost = true;
                window.Show(); // Non modal

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur", ex.Message);
                return Result.Failed;
            }
        }
    }
}
