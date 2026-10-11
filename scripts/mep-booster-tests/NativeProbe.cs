using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BIMaestro.MepTests
{
    // Exercises the installed production DLL, through its real Idling and ExternalEvent callbacks.
    public sealed class NativeProbe : IExternalApplication
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private string _folder;
        private int _stage;
        private DateTime _deadline;
        private Type _service;
        private object _instance;
        private UIDocument _document;
        private List<FamilyInstance> _valves;
        private XYZ _basis;
        private string _connections;
        private readonly List<string> _checks = new List<string>();
        private readonly Dictionary<string, object> _report = new Dictionary<string, object>();
        public Result OnStartup(UIControlledApplication app)
        {
            _folder = Path.GetDirectoryName(GetType().Assembly.Location);
            _deadline = DateTime.UtcNow.AddMinutes(4);
            app.Idling += Step;
            app.DialogBoxShowing += Dialog;
            File.WriteAllText(Path.Combine(_folder, "loaded.txt"), "loaded");
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app)
        {
            app.Idling -= Step;
            app.DialogBoxShowing -= Dialog;
            return Result.Succeeded;
        }
        private void Dialog(object sender, DialogBoxShowingEventArgs args)
        {
            if (args is TaskDialogShowingEventArgs dialog && dialog.DialogId == "TaskDialog_External_Tools_External_Tool_Failure")
            {
                File.AppendAllText(Path.Combine(_folder, "startup-dialogs.txt"), dialog.DialogId + Environment.NewLine);
                // The known Results Explorer missing-icon warning must not block this isolated test.
                dialog.OverrideResult((int)TaskDialogResult.Close);
            }
        }
        private object Field(string name) => _service.GetField(name, Hidden).GetValue(_instance);
        private Window Palette => (Window)Field("_palette");
        private string Connections(FamilyInstance instance) => string.Join(";", instance.MEPModel.ConnectorManager.Connectors
            .Cast<Connector>().OrderBy(c => c.Id).Select(c => c.Id + ":" + c.Origin.ToString() + ":" +
                string.Join(",", c.AllRefs.Cast<Connector>().Where(r => r.Owner.Id != instance.Id)
                    .Select(r => r.Owner.UniqueId + ":" + r.Id).OrderBy(x => x))));
        private void Check(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException(text);
            _checks.Add(text);
        }
        private void Save(string error = null)
        {
            _report["checks"] = _checks;
            _report["stage"] = _stage;
            _report["status"] = _instance == null ? "initialising" : Field("_status");
            _report["error"] = error;
            File.WriteAllText(Path.Combine(_folder, "report.json"), JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
        }
        private void Step(object sender, IdlingEventArgs args)
        {
            if (_stage == 99) return;
            try
            {
                if (DateTime.UtcNow > _deadline) throw new TimeoutException("Timed out waiting for the production MEP palette: " + (_instance == null ? "initialisation" : Field("_status")));
                var app = (UIApplication)sender;
                if (_stage == 0)
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "BIMaestro");
                    _service = assembly.GetType("BIMaestro.MepBooster.MepBoosterService", true);
                    Check(_service.GetMethod("ProcessIdling", Hidden) != null, "The installed common DLL contains the corrected Idling integration");
                    _report["revit"] = app.Application.VersionNumber;
                    _report["assembly"] = assembly.Location;
                    _report["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
                    Check(app.ActiveUIDocument == null, "Test starts in a separate empty Revit instance");
                    _document = app.OpenAndActivateDocument(Path.Combine(_folder, "BoosterFixture.rvt"));
                    var view = new FilteredElementCollector(_document.Document).OfClass(typeof(View3D)).Cast<View3D>()
                        .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective && v.Name.Contains("09"))
                        ?? new FilteredElementCollector(_document.Document).OfClass(typeof(View3D)).Cast<View3D>()
                            .First(v => !v.IsTemplate && !v.IsPerspective);
                    _document.RequestViewChange(view);
                    _valves = new FilteredElementCollector(_document.Document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .Where(v => v.Symbol.Family.Name == "Vanne papillon - 50-300 mm"
                            && (v.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "").Contains("BOOSTER_"))
                        .OrderBy(v => v.UniqueId).ToList();
                    Check(_valves.Count >= 2, "Fixture contains connected training valves");
                    if (!(bool)_service.GetProperty("IsEnabled", Hidden).GetValue(null)) _service.GetMethod("Toggle", Hidden).Invoke(null, new object[] { app });
                    _instance = _service.GetField("_instance", Hidden).GetValue(null);
                    _stage = 10;
                }
                else if (_stage == 10)
                {
                    _document.Selection.SetElementIds(new[] { _valves[0].Id });
                    _document.GetOpenUIViews().First(v => v.ViewId == _document.ActiveView.Id).ZoomToFit();
                    _stage = 1;
                }
                else if (_stage == 1 && Palette.IsVisible)
                {
                    Check((bool)Field("_idlingObserved"), "Production MEP Booster receives Idling in Revit 2025");
                    Check((int)Field("_selectedCount") == 1 && Field("_unavailable") == null, "A usable MEP pill appears for one valve");
                    _basis = _valves[0].GetTransform().BasisY;
                    _connections = Connections(_valves[0]);
                    _stage = 2;
                    Palette.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var type = Palette.GetType();
                        type.GetMethod("Expand", Hidden).Invoke(Palette, new[] { Field("_projection"), (object)1 });
                        var buttons = (IEnumerable<Button>)type.GetField("_actions", Hidden).GetValue(Palette);
                        var button = buttons.First(b => !(bool)b.Tag && Convert.ToDouble(b.CommandParameter) == 45);
                        Check(button.IsEnabled, "The +45 degree rotation button is enabled");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }), DispatcherPriority.Background);
                }
                else if (_stage == 2 && !_basis.IsAlmostEqualTo(_valves[0].GetTransform().BasisY))
                {
                    Check(!(bool)Field("_apply"), "The real production ExternalEvent applies the requested rotation");
                    Check(Connections(_valves[0]) == _connections, "Rotation preserves connected ports and neighbouring pipe connections");
                    _document.Selection.SetElementIds(new[] { _valves[0].Id, _valves[1].Id });
                    _stage = 3;
                }
                else if (_stage == 3 && Palette.IsVisible && (int)Field("_selectedCount") == 2)
                {
                    Check(Field("_unavailable") == null, "A usable MEP pill appears for two selected valves");
                    _stage = 99;
                    Save();
                    File.WriteAllText(Path.Combine(_folder, "finished.txt"), "PASS");
                    return;
                }
                Save();
            }
            catch (Exception ex)
            {
                _stage = 99;
                Save(ex.ToString());
                File.WriteAllText(Path.Combine(_folder, "finished.txt"), "FAIL");
            }
        }
    }
}
