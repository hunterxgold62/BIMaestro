using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;

namespace BIMaestro.VideoGames
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class RevitGameCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RevitGameCommand";

        protected override Result OnExecute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication application = commandData?.Application;
            UIDocument uiDocument = application?.ActiveUIDocument;
            Document document = uiDocument?.Document;

            if (document == null)
            {
                TaskDialog.Show(UiLanguage.T("Maquette BIM", "BIM Model"), UiLanguage.T("Ouvrez d'abord une maquette Revit.", "Open a Revit model first."));
                return Result.Cancelled;
            }

            if (!(document.ActiveView is View3D view3D) || view3D.IsTemplate)
            {
                TaskDialog.Show(
                    UiLanguage.T("Maquette BIM", "BIM Model"),
                    UiLanguage.T(
                        "Affichez la vue 3D que vous voulez explorer, puis relancez le bouton.\n\nBIMaestro reprend exactement la visibilité, la boîte de coupe et les couleurs de cette vue.",
                        "Display the 3D view you want to explore, then run the command again.\n\nBIMaestro uses that view's visibility, section box, and colours exactly as displayed."));
                return Result.Cancelled;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                // Une seule DLL, compilée avec l'API Revit 2023, est utilisée.
                // Cette détection initialise les adaptations et le diagnostic
                // propres à Maquette BIM sans modifier les autres commandes.
                RevitGameCompatibility.Detect(application);
                GameSceneData scene = RevitGameSceneExporter.Export(document, view3D);
                if (scene.TriangleCount == 0)
                {
                    TaskDialog.Show(
                        UiLanguage.T("Maquette BIM", "BIM Model"),
                        UiLanguage.T("Aucune géométrie 3D visible n'a été trouvée dans la vue active.", "No visible 3D geometry was found in the active view."));
                    return Result.Cancelled;
                }

                RevitGameWindowHost.Show(application, scene);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                TaskDialog.Show(
                    UiLanguage.T("Maquette BIM", "BIM Model"),
                    UiLanguage.T("La conversion de la vue 3D n'a pas pu être terminée.\n\n" + exception.Message, "The 3D view conversion could not be completed.\n\n" + exception.Message));
                return Result.Failed;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    internal static class RevitGameWindowHost
    {
        private static RevitGameWindow _window;

        public static void Show(UIApplication application, GameSceneData scene)
        {
            if (_window != null)
            {
                try { _window.Close(); } catch { }
                _window = null;
            }

            _window = new RevitGameWindow(scene);
            _window.Closed += (sender, args) => _window = null;

            try
            {
                new WindowInteropHelper(_window) { Owner = application.MainWindowHandle };
            }
            catch { }

            _window.Show();
            _window.Activate();
        }
    }
}
