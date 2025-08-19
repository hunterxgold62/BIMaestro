using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Licensing
{
    /// <summary>
    /// Hérite de cette classe au lieu de IExternalCommand pour tracer automatiquement.
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
                doc = doc?.Title,
                view = view?.Name,
                viewType = view?.ViewType.ToString()
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
                Telemetry.TrackButton(ButtonId, false, new { error = ex.GetType().Name, ex.Message });
                throw;
            }
        }
    }
}
