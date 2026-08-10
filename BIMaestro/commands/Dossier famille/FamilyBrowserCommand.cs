using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using System;
using static Famille.FamilyBrowserWindow;

namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class FamilyBrowserCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "FamilyBrowserCommand";

        public static readonly Preview3DHandler Preview3DHandlerInstance = new Preview3DHandler();
        public static readonly ExternalEvent Preview3DEventInstance = ExternalEvent.Create(Preview3DHandlerInstance);

        public static UIApplication uiapp;
        public static FamilyBrowserWindow MainWindowRef;

        public static LoadFamilyHandler LoadFamilyHandlerInstance;
        public static ExternalEvent LoadFamilyEventInstance;

        public static ReloadFamilyHandler ReloadFamilyHandlerInstance;
        public static ExternalEvent ReloadFamilyEventInstance;

        public static LoadCollectionHandler LoadCollectionHandlerInstance;
        public static ExternalEvent LoadCollectionEventInstance;

        public static GeneratePreviewImagesHandler GeneratePreviewHandlerInstance;
        public static ExternalEvent GeneratePreviewEventInstance;

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            uiapp = data.Application;

            if (MainWindowRef != null)
            {
                if (MainWindowRef.WindowState == System.Windows.WindowState.Minimized)
                    MainWindowRef.WindowState = System.Windows.WindowState.Normal;
                if (!MainWindowRef.IsVisible)
                    MainWindowRef.Show();
                MainWindowRef.Activate();
                return Result.Succeeded;
            }

            LoadFamilyHandlerInstance = new LoadFamilyHandler();
            LoadFamilyEventInstance = ExternalEvent.Create(LoadFamilyHandlerInstance);

            ReloadFamilyHandlerInstance = new ReloadFamilyHandler();
            ReloadFamilyEventInstance = ExternalEvent.Create(ReloadFamilyHandlerInstance);

            LoadCollectionHandlerInstance = new LoadCollectionHandler();
            LoadCollectionEventInstance = ExternalEvent.Create(LoadCollectionHandlerInstance);

            GeneratePreviewHandlerInstance = new GeneratePreviewImagesHandler();
            GeneratePreviewEventInstance = ExternalEvent.Create(GeneratePreviewHandlerInstance);

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
                TaskDialog.Show(UiLanguage.T("Erreur", "Error"), ex.Message);
                return Result.Failed;
            }
        }
    }
}
