using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;

namespace BIMaestro.PropertiesAppearance
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PropertiesAppearanceCommand : BaseTrackedCommand
    {
        private static PropertiesAppearanceEditor _editor;
        private static ExternalEvent _event;
        private static UIApplication _executingApplication;
        private static bool _shuttingDown;
        private static readonly Handler _handler = new Handler();
        private static readonly List<Action<UIApplication>> _pending = new List<Action<UIApplication>>();
        protected override string ButtonId => "PropertiesAppearance";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Open(data.Application); return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
        internal static void Open(UIApplication app)
        {
                Couleur.CustomizeRibbonColorsCommand.ShowPreferences(app, "properties");
        }
        internal static PropertiesAppearanceEditor CreateEditor(UIApplication app)
        {
            _shuttingDown = false;
            if (_event == null) _event = ExternalEvent.Create(_handler);
            PropertiesAppearanceService.ReloadSaved();
            return _editor = new PropertiesAppearanceEditor(AppearanceContext.ForEditor(app));
        }
        internal static void ReleaseEditor(PropertiesAppearanceEditor editor)
        {
            editor.Close();
            if (!ReferenceEquals(_editor, editor)) return;
            _editor = null;
            if (_shuttingDown) return;
            try { Request(app => { PropertiesAppearanceService.CancelPreview(); PropertiesAppearanceService.ProcessIdling(app); }); }
            catch { PropertiesAppearanceService.CancelPreview(); }
        }
        internal static void Shutdown()
        {
            _shuttingDown = true;
            _editor?.Close(); _editor = null; _pending.Clear(); _event?.Dispose(); _event = null;
        }
        internal static void Request(Action<UIApplication> action)
        {
            // A save continuation already runs in Revit's API context.
            if (_executingApplication != null) { action(_executingApplication); return; }
            _pending.Add(action);
            var result = _event?.Raise();
            if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
            { _pending.Clear(); throw new InvalidOperationException("Revit est occupé. Réessayez dans un instant."); }
        }
        private sealed class Handler : IExternalEventHandler
        {
            public string GetName() => "Apparence des propriétés";
            public void Execute(UIApplication app)
            {
                var actions = _pending.ToArray(); _pending.Clear();
                _executingApplication = app;
                try
                {
                    foreach (var action in actions)
                        try { action(app); }
                        catch (Exception ex) { _editor?.Notify("Modification non appliquée : " + ex.Message); }
                }
                finally { _executingApplication = null; }
            }
        }
    }
}
