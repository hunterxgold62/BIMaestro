using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.DB.Events;
using Licensing;

namespace BIMaestro.MepBooster
{
    [Transaction(TransactionMode.Manual)]
    public sealed class MepBoosterCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MepBooster";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            MepBoosterService.Toggle(data.Application);
            return Result.Succeeded;
        }
    }

    internal sealed class MepBoosterService : IExternalEventHandler
    {
        private static MepBoosterService _instance;
        private static PushButton _button;
        private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> _icons =
            new Dictionary<string, System.Windows.Media.Imaging.BitmapImage>();
        internal static System.Windows.Media.Imaging.BitmapImage StateIcon(bool enabled, int size)
        {
            string name = enabled ? "MEP Booster vanne rotation.png" : "MEP Booster vanne rotation OFF.png";
            string key = name + size;
            if (_icons.TryGetValue(key, out var cached)) return cached;
            using (var stream = typeof(MepBoosterService).Assembly.GetManifestResourceStream("BIMaestro.Resources." + name))
            {
                if (stream == null) throw new InvalidOperationException("Icône MEP Booster absente : " + name);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream; bitmap.DecodePixelWidth = size;
                bitmap.EndInit(); bitmap.Freeze();
                _icons[key] = bitmap;
                return bitmap;
            }
        }
        private ExternalEvent _event;
        private DispatcherTimer _timer;
        private BoosterNative.MouseObserver _mouse;
        private BoosterPalette _palette;
        private BoosterPreview _preview;
        private IntPtr _owner;
        private Document _document;
        private string _selection;
        private long _viewId;
        private List<BoosterPart> _parts;
        private BoosterProjection _projection;
        private DateTime _quietSince;
        private uint _lastInputStamp;
        private string _status;
        private string _appliedStatus;
        private DateTime _nextInspectionUtc, _lastLoggedUtc;
        private bool _enabled, _suppressed, _selectionDirty, _previewDirty, _flip, _apply;
        private bool _ready;
        private bool _selectionCheckPending;
        private bool _copyRequested, _copyBusy;
        private int _selectedCount;
        private string _unavailable;
        private double? _angle;
        internal Action<string> DiagnosticSink { get; set; }

        internal static void BindButton(PushButton button)
        {
            _button = button;
            UpdateButton();
        }
        private static void UpdateButton()
        {
            if (_button == null) return;
            bool enabled = _instance?._enabled == true;
            _button.ItemText = "MEP Booster\n" + (enabled ? "ON" : "OFF");
            _button.Image = StateIcon(enabled, 16);
            _button.LargeImage = StateIcon(enabled, 32);
        }
        internal static void Toggle(UIApplication app)
        {
            if (_instance == null)
            {
                _instance = new MepBoosterService();
                _instance.Initialize(app);
            }
            var service = _instance;
            service._enabled = !service._enabled;
            service.Dismiss();
            service._selection = null;
            service._selectionDirty = true;
            service._suppressed = false;
            service._quietSince = DateTime.UtcNow;
            service.SetStatus(service._enabled ? "ON — sélectionnez un accessoire dans la vue." : "OFF");
            if (service._enabled)
            {
                service._timer.Start();
            }
            else
            {
                service._timer.Stop(); service._mouse?.Dispose(); service._mouse = null;
            }
            UpdateButton();
        }
        private void Initialize(UIApplication app)
        {
            _owner = app.MainWindowHandle;
            SetStatus("Initialisation MEP Booster v6 — copie d’orientation — Revit " + app.Application.VersionNumber + " ; " + typeof(MepBoosterService).Assembly.Location);
            _event = ExternalEvent.Create(this);
            _palette = new BoosterPalette(_owner);
            _preview = new BoosterPreview(_owner);
            _palette.Preview += (angle, flip) =>
            {
                if (_apply) return;
                _angle = angle; _flip = flip; _previewDirty = angle.HasValue;
                if (angle == null) _preview.Hide();
                else if (!_event.IsPending) _event.Raise();
            };
            _palette.Apply += (angle, flip) =>
            {
                if (_apply) return;
                _angle = angle; _flip = flip; _apply = true;
                _event.Raise();
            };
            _palette.Dismissed += Rearm;
            _palette.CopyOrientation += () =>
            {
                if (_apply || _copyRequested || _copyBusy) return;
                _copyRequested = true;
                if (!_event.IsPending) _event.Raise();
            };
            _timer = new DispatcherTimer(DispatcherPriority.Normal, _palette.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Tick;
            var application = BIMaestroApp.UIControlledApp;
            application.Idling += Idling;
            application.SelectionChanged += SelectionChanged;
            application.ViewActivated += ViewActivated;
            application.ControlledApplication.DocumentChanged += DocumentChanged;
            application.ControlledApplication.DocumentClosing += DocumentClosing;
        }

        private void SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_copyBusy) return;
            if (!_enabled) return;
            // A notification is not proof of a different selection. Compare actual
            // selected IDs in the next API callback before hiding a usable palette.
            _selectionCheckPending = true;
        }
        private void ViewActivated(object sender, ViewActivatedEventArgs args)
        {
            if (!_enabled) return;
            Dismiss(); _selectionDirty = true; _suppressed = false;
        }
        private void DocumentClosing(object sender, DocumentClosingEventArgs args)
        {
            Dismiss(); _document = null; _parts = null; _projection = null;
        }
        private void DocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            if (_copyBusy) return; // One synchronous, atomic copy; refreshed in its finally block.
            if (!_enabled || !object.Equals(args.GetDocument(), _document)) return;
            if (_apply) return; // The successful operation refreshes snapshots without closing the rosace.
            var affected = new HashSet<long>(args.GetModifiedElementIds().Concat(args.GetDeletedElementIds()).Select(BoosterIds.Value));
            if (_parts == null || !_parts.Any(p => affected.Contains(BoosterIds.Value(p.Id)))) return;
            Rearm();
        }

        private void Idling(object sender, IdlingEventArgs args)
        {
            if (!_enabled || _copyBusy || !(sender is UIApplication app)) return;
            DateTime now = DateTime.UtcNow;
            if (ShouldInspect(_selectionDirty || _selectionCheckPending, _previewDirty || _apply, _palette.IsVisible, now, _nextInspectionUtc))
            {
                _nextInspectionUtc = now.AddMilliseconds(150);
                Poll(app, false);
            }
            if (_button != null && _status != null && _appliedStatus != _status)
            {
                _button.ToolTip = "MEP Booster — " + _status;
                _appliedStatus = _status;
            }
        }
        internal static bool ShouldInspect(bool dirty, bool actionPending, bool visible, DateTime now, DateTime next) =>
            dirty || actionPending || (visible && now >= next);

        private void MouseInput(int message, int x, int y)
        {
            if (_palette.EditingAngle || _copyBusy || _copyRequested) return;
            if (!_enabled || !BoosterNative.IsRevitForeground(_owner)) return;
            if (!_palette.IsVisible) { _quietSince = DateTime.UtcNow; return; }
            if (message == 0x201 && _palette.ContainsPixel(x, y)) return;
            // Defer WPF updates out of the low-level callback; do not run Revit API here.
            _palette.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Rearm));
        }

        private void Tick(object sender, EventArgs args)
        {
            if (_palette.EditingAngle || _copyBusy || _copyRequested) return;
            if (!_enabled) return;
            uint inputStamp = BoosterNative.InputStamp();
            if (inputStamp != _lastInputStamp)
            {
                _lastInputStamp = inputStamp;
                // Track mouse, wheel and keyboard activity without a permanent mouse hook.
                // Moving inside the open rosace must not close it.
                if (!_palette.IsVisible) _quietSince = DateTime.UtcNow;
            }
            if (!BoosterNative.IsRevitForeground(_owner)
                || (BoosterNative.GetForegroundWindow() != _owner && !BoosterNative.IsWindowEnabled(_owner)))
            {
                Suspend();
                return;
            }
            if (BoosterNative.Down(0x1B)) { Rearm(); return; }
            if (BoosterNative.Down(0x11) || BoosterNative.Down(0x10))
            {
                if (_palette.IsVisible)
                {
                    Dismiss(); _suppressed = false; _selectionDirty = true;
                }
                _quietSince = DateTime.UtcNow;
                return;
            }
            if (_palette.IsVisible)
            {
                bool clickOutside = (BoosterNative.Down(1) && !_palette.ContainsCursor())
                    || BoosterNative.Down(2) || BoosterNative.Down(4);
                bool commandKey = Enumerable.Range(0x41, 26).Any(BoosterNative.Down) || BoosterNative.Down(0x2E);
                if (clickOutside || commandKey)
                { Rearm(); return; }
            }
            if (BoosterNative.Down(1) || BoosterNative.Down(2) || BoosterNative.Down(4)) _quietSince = DateTime.UtcNow;
            else PresentIfReady(DateTime.UtcNow);
        }

        // WPF only. All Revit data was captured by Process; presentation must not wait for
        // another Idling/ExternalEvent callback after the mouse stops moving.
        private void PresentIfReady(DateTime now)
        {
            if (!_enabled || _suppressed || !_ready || _selectionCheckPending || _palette.IsVisible
                || now - _quietSince < TimeSpan.FromMilliseconds(200)) return;
            try
            {
                SetStatus("Affichage demandé — " + _selectedCount + " accessoire(s).");
                _palette.ShowPill(_projection, _selectedCount, _unavailable == null && _parts.All(p => p.CanFlip), _unavailable,
                    (degrees, invert) => _parts != null && _parts.All(p => p.CanApply(degrees, invert)));
                if (_owner != IntPtr.Zero && _mouse == null) _mouse = new BoosterNative.MouseObserver(MouseInput);
                SetStatus("Pastille affichée — " + _selectedCount + " accessoire(s), x=" + _palette.Left.ToString("0")
                    + ", y=" + _palette.Top.ToString("0") + (_unavailable == null ? ". Survolez MEP." : ". " + _unavailable));
            }
            catch (Exception ex) { Dismiss(); SetStatus("Erreur d’affichage : " + ex); }
        }

        public void Execute(UIApplication app) => Poll(app, true);

        private void Poll(UIApplication app, bool externalEvent)
        {
            if (!_enabled) return;
            try { Process(app, externalEvent); }
            catch (Exception ex)
            {
                bool wasApplying = _apply;
                Dismiss();
                SetStatus("Erreur : " + ex);
                if (wasApplying) TaskDialog.Show("MEP Booster — opération annulée", ex.Message);
            }
        }
        private void Process(UIApplication app, bool externalEvent)
        {
            if (_palette.EditingAngle || _copyBusy) return;
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            // Window enabled/read-only flags may be temporary while Revit invokes an API callback.
            // They must never permanently suppress presentation, nor gate reads of the selection.
            if (doc == null || doc.IsFamilyDocument || doc.IsModifiable || !BoosterNative.IsRevitForeground(_owner))
            { Suspend(); return; }
            var ids = uidoc.Selection.GetElementIds().OrderBy(BoosterIds.Value).ToList();
            string selection = string.Join(",", ids.Select(BoosterIds.Value));
            _selectionCheckPending = false;
            if (SelectionContextChanged(_document, doc, _viewId, BoosterIds.Value(uidoc.ActiveView.Id), _selection, selection, _selectionDirty))
            {
                Dismiss();
                _document = doc; _viewId = BoosterIds.Value(uidoc.ActiveView.Id); _selection = selection;
                _selectionDirty = false; _suppressed = false; _quietSince = DateTime.UtcNow;
                _parts = null; _projection = null;
                _selectedCount = ids.Count; _unavailable = null;
                SetStatus(ids.Count + " élément(s) détecté(s) — stabilisation.");
            }
            if (_suppressed || ids.Count == 0) return;
            if (_copyRequested)
            {
                if (!externalEvent) return;
                _copyRequested = false; _copyBusy = true;
                _palette.Hide(); _preview.Hide();
                _mouse?.Dispose(); _mouse = null;
                try
                {
                    if (doc.IsReadOnly) throw new InvalidOperationException("Le document est en lecture seule.");
                    BoosterOrientation.Run(uidoc, ids, _preview);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                catch (Exception ex) { TaskDialog.Show("MEP Booster — copie annulée", ex.Message); }
                finally { _copyBusy = false; _preview.Hide(); Rearm(); }
                return;
            }
            if (_ready && (BoosterNative.Down(0x11) || BoosterNative.Down(0x10)
                || BoosterNative.Down(0x1B) || BoosterNative.Down(2) || BoosterNative.Down(4))
            ) { _quietSince = DateTime.UtcNow; return; }
            var projection = BoosterProjection.Read(uidoc);
            if (projection == null) { Dismiss(); SetStatus("Vue non prise en charge : utilisez un plan, une coupe ou une vue 3D non perspective."); return; }
            if (_projection != null && !_projection.Same(projection))
            {
                // Mouse-driven Revit redraws can change the reported projection.
                // Keep the palette anchored; real mouse navigation is handled by
                // MouseInput. Never apply an action using an outdated projection.
                if (_apply) { Rearm(); return; }
                _projection = projection;
                _preview.Hide();
                if (!_palette.IsVisible) _quietSince = DateTime.UtcNow;
            }
            if (_apply)
            {
                if (!externalEvent) return;
                if (doc.IsReadOnly) throw new InvalidOperationException("Le document est actuellement en lecture seule.");
                double angle = _angle.Value;
                bool flip = _flip;
                // Capture local references: DocumentChanged closes the palette during Commit.
                var parts = _parts;
                try { BoosterOperations.Apply(doc, parts, angle, flip); }
                catch (Exception ex)
                {
                    Rearm();
                    TaskDialog.Show("MEP Booster — opération annulée", ex.Message);
                    return;
                }
                _apply = false; _angle = null; _previewDirty = false;
                _preview.Hide();
                uidoc.RefreshActiveView();
                var refreshedIds = uidoc.Selection.GetElementIds().OrderBy(BoosterIds.Value).ToList();
                if (string.Join(",", refreshedIds.Select(BoosterIds.Value)) != _selection) { Rearm(); return; }
                _parts = refreshedIds.Select(id => BoosterPart.Read(doc.GetElement(id) as FamilyInstance, false)).ToList();
                _projection = BoosterProjection.Read(uidoc);
                if (_projection == null) { Rearm(); return; }
                _selectionDirty = false; _selectionCheckPending = false; _ready = true;
                _palette.RefreshAfterRotation(_selectedCount, _parts.All(p => p.CanFlip),
                    (degrees, invert) => _parts.All(p => p.CanApply(degrees, invert)));
                return;
            }
            if (_previewDirty)
            {
                _previewDirty = false;
                if (_angle.HasValue && _parts != null)
                {
                    TryPreview(() =>
                    {
                        if (_parts.Count <= 20)
                            foreach (var part in _parts)
                                part.LoadEdges((FamilyInstance)doc.GetElement(part.Id), Math.Min(900, 5000 / _parts.Count));
                        _preview.Draw(projection, _parts, _angle.Value, _flip);
                    });
                }
                else _preview.Hide();
            }
            if (_ready || _palette.IsVisible) return;
            // A selection made through the ribbon/Properties still gets a pill in the active view.
            // Placement is clamped to the viewport, so cursor position cannot silently block it.
            if (ids.Any(id => !BoosterPart.SupportsCategory(doc.GetElement(id))))
            { Dismiss(); SetStatus("Sélection non compatible : accessoires ou raccords de canalisation uniquement."); return; }
            // Keep the visual cost bounded; beyond 20 parts show axes/arcs only for every part.
            _projection = projection;
            try { _parts = ids.Select(id => BoosterPart.Read(doc.GetElement(id) as FamilyInstance, false)).ToList(); }
            catch (InvalidOperationException ex)
            {
                _unavailable = ex.Message;
            }
            var copyTargets = ids.Select(id => doc.GetElement(id) as FamilyInstance).ToList();
            _palette.SetCopyAvailability(_unavailable == null && copyTargets.All(f => f != null && !f.Mirrored && f.Host == null
                && BoosterIds.Value(f.Category?.Id) == (long)BuiltInCategory.OST_PipeAccessory
                && f.Symbol.Family.Id == copyTargets[0].Symbol.Family.Id)
                && _parts.All(p => p.Motion.Ports.Length == 2 && p.Motion.Ports.All(port =>
                    Math.Abs(System.Windows.Media.Media3D.Vector3D.DotProduct(port.Direction, p.Motion.Axis)) > 0.9999)), ids.Count);
            _ready = true;
            SetStatus("Sélection préparée — " + ids.Count + " accessoire(s), pastille en attente du délai.");
        }
        internal void TryPreview(Action draw)
        {
            try { draw(); }
            catch (Exception ex)
            {
                // Preview is optional: keep the selection and palette usable. Applying
                // still goes through the normal transaction and connectivity checks.
                _preview?.Hide();
                SetStatus("Erreur d’aperçu (rosace conservée) : " + ex);
            }
        }
        private void Suspend()
        {
            // Unlike a deliberate dismissal, a temporary focus/busy state must recover on its own.
            _palette?.Hide(); _preview?.Hide();
            _mouse?.Dispose(); _mouse = null;
            _angle = null; _previewDirty = false; _apply = false;
            _quietSince = DateTime.UtcNow;
        }
        // Revit can return different managed wrappers for the same native Document.
        // Document overrides Equals but does not overload ==/!=. Reference comparison here
        // would reset the debounce on every Idling callback, starving presentation forever.
        internal static bool SelectionContextChanged(object previousDocument, object currentDocument,
            long previousView, long currentView, string previousSelection, string currentSelection, bool dirty)
        {
            return !object.Equals(previousDocument, currentDocument) || previousView != currentView
                || previousSelection != currentSelection || dirty;
        }
        private void SetStatus(string status)
        {
            if (_status == status) return;
            _status = status;
            if (DiagnosticSink != null) { DiagnosticSink(status); return; }
            bool important = status.StartsWith("Erreur") || status.StartsWith("Initialisation") || status == "OFF" || status.StartsWith("ON");
            if (!important && DateTime.UtcNow - _lastLoggedUtc < TimeSpan.FromSeconds(2)) return;
            _lastLoggedUtc = DateTime.UtcNow;
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "Logs");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "mep-booster.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.WriteAllText(file, string.Empty);
                File.AppendAllText(file, DateTime.Now.ToString("s") + " " + status + Environment.NewLine);
            }
            catch { }
        }
        private void Rearm()
        {
            Dismiss();
            _selectionDirty = true;
            _suppressed = false;
            _quietSince = DateTime.UtcNow;
        }
        private void Dismiss()
        {
            _copyRequested = false;
            _palette?.Hide(); _preview?.Hide();
            _mouse?.Dispose(); _mouse = null;
            _ready = false;
            _suppressed = true; _angle = null; _previewDirty = false; _apply = false;
        }
        public string GetName() => "BIMaestro — MEP Booster";

        internal static void Shutdown()
        {
            var service = _instance;
            if (service == null) return;
            service._enabled = false; service._timer.Stop(); service._mouse?.Dispose();
            var application = BIMaestroApp.UIControlledApp;
            application.Idling -= service.Idling;
            application.SelectionChanged -= service.SelectionChanged;
            application.ViewActivated -= service.ViewActivated;
            application.ControlledApplication.DocumentChanged -= service.DocumentChanged;
            application.ControlledApplication.DocumentClosing -= service.DocumentClosing;
            service._palette.Close(); service._preview.Close(); service._event.Dispose();
            _instance = null;
        }
    }
}
