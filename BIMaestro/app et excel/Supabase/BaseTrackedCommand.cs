using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.UI;
using System;

namespace Licensing
{
    /// <summary>
    /// À hériter pour tracer automatiquement (succès/échec + contexte)
    /// + hook "Welcome" après premier usage.
    /// </summary>
    public abstract class BaseTrackedCommand : IExternalCommand
    {
        protected abstract string ButtonId { get; }

        protected virtual object BuildContext(ExternalCommandData data)
        {
            var doc = data.Application?.ActiveUIDocument?.Document;
            var view = data.Application?.ActiveUIDocument?.ActiveView;
            return new
            {
                doc_title = doc?.Title ?? "",
                view = view?.Name ?? "",
                view_type = view?.ViewType.ToString() ?? "",
                revit_username = data.Application?.Application?.Username ?? Environment.UserName,
                windows_user = Environment.UserName,
                revit_version = data.Application?.Application?.VersionNumber
            };
        }

        protected abstract Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements);

        private string ResolveButtonDisplayName()
        {
            var fullName = GetType().FullName;
            var info = AppUI.GetRibbonButtonByCommandClass(fullName);
            if (!string.IsNullOrWhiteSpace(info?.DisplayName))
            {
                return NormalizeLabel(info.DisplayName);
            }

            return NormalizeLabel(ButtonId);
        }

        private static string NormalizeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return string.Join(" ", value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            // ✅ Hook global : au tout premier bouton BIMaestro, start timer (2 min), puis popup.
            // Le JWT (si dispo) permet ensuite de sync l'email (opt-in) vers Supabase.
            try
            {
                BIMaestro.Welcome.WelcomeManager.NotifyFirstCommandUsed(
                    data.Application,
                    Licensing.LicenseSession.CurrentJwt
                );
            }
            catch
            {
                // Jamais bloquer une commande pour ça
            }

            try
            {
                AppUI.SetUiApplication(data.Application);
            }
            catch
            {
                // Non bloquant
            }

            try
            {
                var trackedButtonLabel = ResolveButtonDisplayName();
                var res = OnExecute(data, ref message, elements);
                Telemetry.TrackButton(trackedButtonLabel, res == Result.Succeeded, BuildContext(data));
                if (res == Result.Succeeded)
                {
                    ButtonRecentManager.RegisterUse(trackedButtonLabel, GetType().FullName);
                }
                return res;
            }
            catch (Exception ex)
            {
                var trackedButtonLabel = ResolveButtonDisplayName();
                Telemetry.TrackButton(trackedButtonLabel, false, new { error = ex.GetType().Name, ex.Message });
                throw;
            }
        }
    }
}