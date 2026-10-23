using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMaestro.MotorTests
{
    internal static class MotorRouteNativeCases
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Get(object value, string name) => value?.GetType().GetField(name, Hidden)?.GetValue(value)
            ?? value?.GetType().GetProperty(name, Hidden)?.GetValue(value);
        private static void Set(object value, string name, object field) => value.GetType().GetField(name, Hidden).SetValue(value, field);
        private static double[] V(XYZ p) => p == null ? null : new[] { p.X, p.Y, p.Z };
        private static double Dimension(FamilyInstance door, BuiltInParameter id)
        {
            foreach (Element e in new Element[] { door, door.Symbol })
                if (e?.get_Parameter(id) is Parameter p && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
            return 0;
        }
        private sealed class Pose
        {
            internal XYZ Center;
            internal double Yaw;
        }
        internal static void Run(Assembly production, Document doc, UIApplication app, string folder)
        {
            var output = new Dictionary<string, object>();
            var cases = new List<object>();
            var checks = new List<object>();
            output["assembly"] = production.Location;
            output["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(production.Location)));
            output["cases"] = cases; output["checks"] = checks;
            output["scope"] = "Production FindRoute called on the disposable model: every returned motion is verified by native Revit solids before the production method exposes its route. No document saved.";
            var pump = (FamilyInstance)doc.GetElement(new ElementId(809675L));
            output["pump"] = new { id = pump.Id.Value, family = pump.Symbol.Family.Name, name = pump.Name };
            try
            {
                foreach (long id in new[] { 923915L, 794529L }) RunCase(id);
            }
            finally
            {
                output["passed"] = checks.All(c => (bool)c.GetType().GetProperty("passed").GetValue(c));
                File.WriteAllText(Path.Combine(folder, "motor-route-cases.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            }
            void Check(bool passed, string name, long doorId) => checks.Add(new { passed, name, doorId });
            void RunCase(long doorId)
            {
                var row = new Dictionary<string, object> { ["doorId"] = doorId }; cases.Add(row);
                Window window = null;
                try
                {
                    var door = (FamilyInstance)doc.GetElement(new ElementId(doorId));
                    var location = ((LocationPoint)door.Location).Point;
                    var frame = door.GetTransform(); var normal = frame.BasisY.Normalize();
                    double width = Dimension(door, BuiltInParameter.DOOR_WIDTH), height = Dimension(door, BuiltInParameter.DOOR_HEIGHT);
                    row["door"] = new { family = door.Symbol.Family.Name, name = door.Name, location = V(location), normal = V(normal), widthMm = width * 304.8, heightMm = height * 304.8 };
                    var state = Activator.CreateInstance(production.GetType("BIMaestro.MepBooster.MotorExtractionState", true), true);
                    Set(state, "DoorPoint", location); Set(state, "DoorNormal", normal);
                    Set(state, "DoorId", door.Id); Set(state, "DoorLinkId", ElementId.InvalidElementId); Set(state, "DoorName", door.Name);
                    Set(state, "DoorWidth", width); Set(state, "DoorHeight", height); Set(state, "DoorSill", location.Z);
                    Set(state, "AllowIntermediateDoors", true);
                    var projection = Activator.CreateInstance(production.GetType("BIMaestro.MepBooster.BoosterProjection", true), true);
                    Set(projection, "Left", 500); Set(projection, "Top", 200); Set(projection, "Right", 1400); Set(projection, "Bottom", 1100);
                    Set(projection, "Center", (pump.GetTransform().Origin + location) / 2); Set(projection, "Up", XYZ.BasisY); Set(projection, "RightAxis", XYZ.BasisX);
                    Set(projection, "Width", 80.0); Set(projection, "Height", 80.0);
                    var type = production.GetType("BIMaestro.MepBooster.MotorExtractionWindow", true);
                    window = (Window)Activator.CreateInstance(type, Hidden, null,
                        new object[] { new UIDocument(doc), pump, projection, app.MainWindowHandle, state }, null);
                    window.Loaded += (_, __) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        try
                        {
                            row["fields"] = ((IDictionary)Get(window, "_fields")).Keys.Cast<string>()
                                .ToDictionary(k => k, k => ((System.Windows.Controls.TextBox)((IDictionary)Get(window, "_fields"))[k]).Text);
                            var watch = Stopwatch.StartNew();
                            type.GetMethod("FindRoute", Hidden).Invoke(window, null);
                            row["seconds"] = watch.Elapsed.TotalSeconds;
                            row["status"] = ((TextBlock)Get(window, "_status")).Text;
                            row["details"] = ((TextBlock)Get(window, "_details")).Text;
                            row["errorDetails"] = Get(window, "_lastError");
                            var envelope = Get(window, "_envelope");
                            if (envelope != null)
                            {
                                var ef = (Autodesk.Revit.DB.Transform)Get(envelope, "Frame");
                                row["envelope"] = new { sizeMm = V((XYZ)Get(envelope, "Size") * 304.8), centerWorld = V(ef.OfPoint((XYZ)Get(envelope, "Center"))),
                                    marginMm = (double)Get(envelope, "Margin") * 304.8, x = V(ef.BasisX), y = V(ef.BasisY), z = V(ef.BasisZ) };
                            }
                            var poses = (Get(window, "_route") as IEnumerable)?.Cast<object>().Select(p => new Pose { Center = (XYZ)Get(p, "Center"), Yaw = (double)Get(p, "Yaw") }).ToList();
                            row["route"] = poses?.Select(p => new { center = V(p.Center), yaw = p.Yaw }).ToArray();
                            row["routeCount"] = poses?.Count ?? 0;
                            row["endpoint"] = poses == null || poses.Count == 0 ? null : V(poses.Last().Center);
                            Check(poses != null && poses.Count > 1, "production route returned after native motion validation", doorId);
                            var crossings = new List<object>();
                            if (poses != null)
                            for (int i = 1; i < poses.Count; i++)
                            {
                                var a = poses[i - 1].Center; var b = poses[i].Center;
                                double sa = (a - location).DotProduct(normal), sb = (b - location).DotProduct(normal);
                                if (sa * sb >= -1e-10) continue;
                                var hit = a + (b - a) * (sa / (sa - sb));
                                double lateral = (hit - location).DotProduct(frame.BasisX);
                                if (width > 0 && Math.Abs(lateral) > width / 2 + 1e-6) continue;
                                if (height > 0 && (hit.Z < location.Z - 1e-6 || hit.Z > location.Z + height + 1e-6)) continue;
                                crossings.Add(new { segment = i, fromSide = sa, toSide = sb, intersection = V(hit), lateralMm = lateral * 304.8 });
                            }
                            row["targetCrossings"] = crossings;
                            Check(crossings.Count > 0, "a returned segment crosses the selected target doorway", doorId);
                            if (poses != null && poses.Count > 1)
                            {
                                int turns = Enumerable.Range(1, poses.Count - 1).Count(i => Math.Abs(Math.Atan2(Math.Sin(poses[i].Yaw - poses[i - 1].Yaw), Math.Cos(poses[i].Yaw - poses[i - 1].Yaw))) > 1e-7);
                                row["turns"] = turns;
                                row["lengthM"] = Enumerable.Range(1, poses.Count - 1).Sum(i => poses[i].Center.DistanceTo(poses[i - 1].Center)) * .3048;
                            }
                            ((DispatcherTimer)Get(window, "_timer")).Stop();
                            window.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            string path = Path.Combine(folder, "motor-route-" + doorId + ".png");
                            using (var file = File.Create(path)) png.Save(file);
                            row["capture"] = path;
                        }
                        catch (Exception ex) { row["error"] = ex.ToString(); Check(false, "native route case completed", doorId); }
                        finally { window.Close(); }
                    }));
                    window.ShowDialog();
                }
                catch (Exception ex) { row["error"] = ex.ToString(); Check(false, "native route window opened", doorId); }
                finally { if (window?.IsVisible == true) window.Close(); }
            }
        }
    }
}
