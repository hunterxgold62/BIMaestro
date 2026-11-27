using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BIMaestro.Compatibility
{
    // The 2023 DLL is also distributed to Revit 2022, whose API has no SelectionChanged event.
    // Keep all newer event/argument types out of its metadata and use idle polling on that host.
    internal sealed class RevitSelectionChangedSubscription : IDisposable
    {
        private readonly UIControlledApplication _application;
        private readonly Action<Document, ICollection<ElementId>> _callback;
        private EventInfo _event;
        private Delegate _handler;
        private MethodInfo _getDocument, _getElements;
        private Document _lastDocument;
        private HashSet<ElementId> _lastIds;
        private DateTime _nextCheckUtc;
        private bool _polling, _disposed;

        internal RevitSelectionChangedSubscription(UIControlledApplication application, Action<Document, ICollection<ElementId>> callback)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            try
            {
                _event = application.GetType().GetEvent("SelectionChanged");
                var argument = _event?.EventHandlerType?.GetMethod("Invoke")?.GetParameters().LastOrDefault()?.ParameterType;
                _getDocument = argument?.GetMethod("GetDocument");
                _getElements = argument?.GetMethod("GetSelectedElements");
                if (_getDocument != null && _getElements != null)
                {
                    Action<object, EventArgs> relay = NativeSelectionChanged;
                    _handler = Delegate.CreateDelegate(_event.EventHandlerType, relay.Target, relay.Method);
                    _event.AddEventHandler(application, _handler);
                    return;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            _handler = null;
            _polling = true;
            application.Idling += Idle;
        }
        private void NativeSelectionChanged(object sender, EventArgs args)
        {
            if (_disposed) return;
            try
            {
                var document = _getDocument.Invoke(args, null) as Document;
                var ids = (_getElements.Invoke(args, null) as IEnumerable<ElementId>)?.ToList() ?? new List<ElementId>();
                _callback(document, ids);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        private void Idle(object sender, IdlingEventArgs args)
        {
            if (_disposed || DateTime.UtcNow < _nextCheckUtc) return;
            _nextCheckUtc = DateTime.UtcNow.AddMilliseconds(100);
            try
            {
                var uiDocument = (sender as UIApplication)?.ActiveUIDocument;
                var document = uiDocument?.Document;
                var ids = new HashSet<ElementId>(uiDocument?.Selection.GetElementIds() ?? new List<ElementId>());
                if (_lastIds != null && Equals(document, _lastDocument) && _lastIds.SetEquals(ids)) return;
                _lastDocument = document; _lastIds = ids;
                _callback(document, ids.ToList());
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_polling) _application.Idling -= Idle;
            if (_handler != null) _event.RemoveEventHandler(_application, _handler);
            _handler = null; _lastDocument = null; _lastIds = null;
        }
    }
}
