using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Licensing
{
    /// <summary>
    /// Base pour tracer automatiquement chaque commande.
    /// </summary>
    public abstract class BaseTrackedCommand : IExternalCommand
    {
        protected abstract string ButtonId { get; }

        protected virtual object BuildContext(ExternalCommandData data)
        {
            var uiApp = data?.Application;
            var app = uiApp?.Application;            // Autodesk.Revit.ApplicationServices.Application
            var uidoc = uiApp?.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = uidoc?.ActiveView;

            return new
            {
                // Identités
                revit_username = app?.Username,                   // OK ici (on a UIApplication)
                windows_user = Environment.UserName,
                machine_name = Environment.MachineName,

                // Revit env
                revit_version = app?.VersionNumber,
                revit_build = app?.VersionBuild,               // <- CORRECTION (pas BuildNumber)
                // Contexte doc
                doc_title = doc?.Title ?? "(untitled)",
                doc_path = doc?.PathName ?? string.Empty,
                view = view?.Name,
                view_type = view?.ViewType.ToString()
            };
        }

        protected abstract Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements);

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var res = OnExecute(data, ref message, elements);
                Telemetry.TrackButton(ButtonId, res == Result.Succeeded, BuildContext(data));
                return res;
            }
            catch (Exception ex)
            {
                Telemetry.TrackButton(ButtonId, false, new
                {
                    error = ex.GetType().Name,
                    message = ex.Message
                });
                throw;
            }
        }
    }
}
