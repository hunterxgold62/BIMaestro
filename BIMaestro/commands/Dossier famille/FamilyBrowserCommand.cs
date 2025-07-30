using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class FamilyBrowserCommand : IExternalCommand
    {
        public static UIApplication uiapp;
        public static FamilyBrowserWindow MainWindowRef;

        public static LoadFamilyHandler LoadFamilyHandlerInstance;
        public static ExternalEvent LoadFamilyEventInstance;

        public static ReloadFamilyHandler ReloadFamilyHandlerInstance;
        public static ExternalEvent ReloadFamilyEventInstance;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            uiapp = data.Application;

            // Si la fenêtre est déjà ouverte, on la remet simplement au premier plan
            if (MainWindowRef != null)
            {
                if (MainWindowRef.WindowState == System.Windows.WindowState.Minimized)
                    MainWindowRef.WindowState = System.Windows.WindowState.Normal;
                if (!MainWindowRef.IsVisible)
                    MainWindowRef.Show();
                MainWindowRef.Activate();
                return Result.Succeeded;
            }

            // (Re)créer les handlers et événements
            LoadFamilyHandlerInstance = new LoadFamilyHandler();
            LoadFamilyEventInstance = ExternalEvent.Create(LoadFamilyHandlerInstance);

            ReloadFamilyHandlerInstance = new ReloadFamilyHandler();
            ReloadFamilyEventInstance = ExternalEvent.Create(ReloadFamilyHandlerInstance);

            // Créer et afficher la fenêtre
            try
            {
                var window = new FamilyBrowserWindow();
                MainWindowRef = window;
                window.Closed += (s, e) => MainWindowRef = null;
                window.Show();
                window.Activate();
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
