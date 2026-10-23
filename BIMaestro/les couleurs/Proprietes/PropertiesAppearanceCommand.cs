using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;

namespace BIMaestro.PropertiesAppearance
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PropertiesAppearanceCommand : BaseTrackedCommand
    {
        private static PropertiesAppearanceWindow _window;
        private static ExternalEvent _event;
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
                // The laboratory may opt in only for its disposable Revit 2025 fixture.
                var lab=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="BIMaestro.PropertiesCustomGroupProbe");
                var openLab=lab?.GetType("BIMaestro.PropertiesCustomGroupTests.Probe")?.GetMethod("OpenEditor");
                if(openLab!=null&&(bool)openLab.Invoke(null,new object[]{app}))return;
                if (_window != null && _window.IsVisible) { _window.Activate(); return; }
                if (_event == null) _event = ExternalEvent.Create(_handler);
                PropertiesAppearanceService.ReloadSaved();
                ThemeManager.EnsureThemeLoaded();
                _window = new PropertiesAppearanceWindow(AppearanceContext.ForEditor(app));
                new WindowInteropHelper(_window) { Owner = app.MainWindowHandle };
                _window.Closed += (_, __) => { _window = null; if (_shuttingDown) return; try { Request(app => { PropertiesAppearanceService.CancelPreview(); PropertiesAppearanceService.ProcessIdling(app); }); } catch { PropertiesAppearanceService.CancelPreview(); } };
                _window.Show();
        }
        internal static void Shutdown()
        {
            _shuttingDown = true;
            _window?.Close(); _pending.Clear(); _event?.Dispose(); _event = null;
        }
        internal static void Request(Action<UIApplication> action)
        {
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
                foreach (var action in actions)
                    try { action(app); }
                    catch (Exception ex) { _window?.Notify("Modification non appliquée : " + ex.Message); }
            }
        }
    }
}
