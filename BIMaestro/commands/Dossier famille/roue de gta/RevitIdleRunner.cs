using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;

namespace BIMaestro.UI
{
    internal static class RevitIdleRunner
    {
        public static void Run(UIApplication uiapp, Action action)
        {
            EventHandler<IdlingEventArgs> h = null;
            h = (s, e) =>
            {
                try { uiapp.Idling -= h; action?.Invoke(); }
                catch { }
            };
            uiapp.Idling += h;
        }
    }
}
