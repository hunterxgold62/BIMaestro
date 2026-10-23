using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BIMaestro.MotorTests
{
    public sealed class NativeGeometry : IExternalApplication
    {
        private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private bool _done;
        private readonly List<string> _checks = new List<string>();
        public Result OnStartup(UIControlledApplication app)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_MOTOR_GEOMETRY_CHECK") == "1") app.Idling += Run;
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Hidden).SetValue(target, value);
        private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Hidden).Invoke(target, args);
        private void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks.Add(message); }
        private static Solid Box(XYZ center, XYZ size)
        {
            var p = new[] { new XYZ(-1,-1,-1), new XYZ(1,-1,-1), new XYZ(1,1,-1), new XYZ(-1,1,-1) }
                .Select(v => center + new XYZ(v.X * size.X / 2, v.Y * size.Y / 2, v.Z * size.Z / 2)).ToArray();
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(p[i], p[(i + 1) % 4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, size.Z);
        }
        private static bool Intersects(IEnumerable<Solid> parts, Solid obstacle)
        {
            foreach (var part in parts)
                using (var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(part, obstacle, BooleanOperationsType.Intersect))
                    if (intersection.Volume > 1e-10) return true;
            return false;
        }
        private void Run(object sender, IdlingEventArgs args)
        {
            if (_done) return; _done = true;
            string error = null;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "BIMaestro");
                var envelopeType = assembly.GetType("BIMaestro.MepBooster.MotorEnvelope", true);
                var reference = Activator.CreateInstance(envelopeType, true);
                Set(reference, "Frame", Transform.Identity); Set(reference, "Center", XYZ.Zero);
                Set(reference, "Size", new XYZ(2, 1, 1)); Set(reference, "Sign", 1); Set(reference, "Margin", 0.0);
                Set(reference, "TranslationDirection", new XYZ(1, 1, 0).Normalize());
                foreach (bool mirrored in new[] { false, true })
                {
                    var frame = Transform.Identity; if (mirrored) frame.BasisX = -XYZ.BasisX; Set(reference, "Frame", frame);
                    var parts = ((IEnumerable)Call(reference, "SweepParts", 6.0)).Cast<Solid>().ToList();
                    try
                    {
                        using (var wall = Box(new XYZ(3, 3, 0), new XYZ(.02, .02, .1)))
                            Check(Intersects(parts, wall), "Diagonal sweep catches thin intermediate obstacle; mirrored=" + mirrored);
                        using (var outside = Box(new XYZ(-.7, 3.6, 0), new XYZ(.02, .02, .1)))
                            Check(!Intersects(parts, outside), "Diagonal sweep does not fill its bounding box; mirrored=" + mirrored);
                        bool covered = true;
                        for (int i = 0; i <= 20; i++)
                        {
                            var center = new XYZ(1, 1, 0).Normalize() * (6.0 * i / 20);
                            foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 })
                            using (var probe = Box(center + new XYZ(x * .95, y * .45, 0), new XYZ(.01, .01, .01)))
                                covered &= Intersects(parts, probe);
                        }
                        Check(covered, "Swept prisms cover moving-box corners along entire segment; mirrored=" + mirrored);
                    }
                    finally { foreach (var part in parts) part.Dispose(); }
                }
                Set(reference, "Frame", Transform.Identity); Set(reference, "Size", new XYZ(4, .6, .6));
                var geometryType = assembly.GetType("BIMaestro.MepBooster.MotorRouteGeometry", true);
                var poseType = assembly.GetType("BIMaestro.MepBooster.MotorRoutePose", true);
                object Pose(double angle) => Activator.CreateInstance(poseType, Hidden, null, new object[] { XYZ.Zero, angle }, null);
                var geometry = RuntimeHelpers.GetUninitializedObject(geometryType);
                Set(geometry, "_reference", reference); Set(geometry, "Unknown", new List<string>()); Set(geometry, "Assumptions", new List<string>());
                var obstaclesField = geometryType.GetField("_obstacles", Hidden);
                var obstacles = (IList)Activator.CreateInstance(obstaclesField.FieldType); obstaclesField.SetValue(geometry, obstacles);
                Check((bool)Call(geometry, "Clear", Pose(0), Pose(Math.PI / 2)), "Ninety-degree turn is free in an empty environment");
                var obstacleType = geometryType.GetNestedType("Obstacle", Hidden);
                var obstacle = Activator.CreateInstance(obstacleType, true);
                var corner = Box(new XYZ(1.4, 1.4, 0), new XYZ(.04, .04, .2));
                Set(obstacle, "Solid", corner); Set(obstacle, "Min", new XYZ(1.38, 1.38, -.1));
                Set(obstacle, "Max", new XYZ(1.42, 1.42, .1)); Set(obstacle, "Name", "Turn-only obstacle"); obstacles.Add(obstacle);
                Check((bool)Call(geometry, "Clear", Pose(0), Pose(0)), "Turn obstacle is clear at starting pose");
                Check((bool)Call(geometry, "Clear", Pose(Math.PI / 2), Pose(Math.PI / 2)), "Turn obstacle is clear at ending pose");
                Check(!(bool)Call(geometry, "Clear", Pose(0), Pose(Math.PI / 2)), "Rotation detects obstacle between two individually clear poses");
                ((IDisposable)geometry).Dispose();
            }
            catch (Exception ex) { error = ex.ToString(); }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "native-report.json"),
                JsonSerializer.Serialize(new { passed = error == null, checks = _checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            // Only this explicitly launched, empty test process exits. Never close a user document.
            var ui = (UIApplication)sender;
            if (ui.ActiveUIDocument == null && Environment.GetEnvironmentVariable("BIMAESTRO_ANTIBES_ROUTE_CHECK") != "1") ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
    }
}
