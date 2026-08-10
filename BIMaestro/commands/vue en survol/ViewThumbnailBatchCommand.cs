using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Windows;
using System.Windows.Interop;

namespace BIMaestro.ViewHover
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ViewThumbnailBatchCommand : BaseTrackedCommand
    {
        private static ViewThumbnailBatchWindow _window;

        protected override string ButtonId => "ViewThumbnailBatch";

        protected override Result OnExecute(
            ExternalCommandData data,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiApplication = data.Application;
            if (uiApplication?.ActiveUIDocument?.Document == null)
            {
                TaskDialog.Show(
                    "BIMaestro - Miniatures",
                    "Aucun projet Revit actif.");
                return Result.Cancelled;
            }

            if (_window != null)
            {
                if (!_window.IsVisible) _window.Show();
                if (_window.WindowState == WindowState.Minimized)
                    _window.WindowState = WindowState.Normal;
                _window.Activate();
                return Result.Succeeded;
            }

            var handler = new ViewThumbnailBatchStartHandler();
            ExternalEvent externalEvent = ExternalEvent.Create(handler);
            _window = new ViewThumbnailBatchWindow(
                handler,
                externalEvent,
                uiApplication.ActiveUIDocument.Document.Title);
            new WindowInteropHelper(_window)
            {
                Owner = uiApplication.MainWindowHandle
            };
            _window.Closed += (_, __) => _window = null;
            _window.Show();
            return Result.Succeeded;
        }
    }

    internal sealed class ViewThumbnailBatchStartHandler :
        IExternalEventHandler
    {
        private readonly object _sync = new object();
        private ViewPreviewBatchMode _mode;
        private bool _hasRequest;

        internal event Action<string> StartFailed;

        internal void Request(ViewPreviewBatchMode mode)
        {
            lock (_sync)
            {
                _mode = mode;
                _hasRequest = true;
            }
        }

        public void Execute(UIApplication app)
        {
            ViewPreviewBatchMode mode;
            lock (_sync)
            {
                if (!_hasRequest) return;
                mode = _mode;
                _hasRequest = false;
            }

            if (!ViewHoverPreviewService.StartBatch(
                    app,
                    mode,
                    out string error))
            {
                StartFailed?.Invoke(error);
            }
        }

        public string GetName()
        {
            return "BIMaestro - Génération des miniatures de vues";
        }
    }
}
