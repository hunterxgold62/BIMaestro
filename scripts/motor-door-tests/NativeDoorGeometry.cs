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
    // Explicit empty-process native fixture. It tests production Clear and
    // ClearForSearch against actual Revit solids plus the portal registry. It
    // does not claim to test FamilyInstance parameter/capture interpretation.
    public sealed class NativeDoorGeometry : IExternalApplication
    {
        private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private bool _done;
        private readonly List<string> _checks = new List<string>();
        private Assembly _assembly;
        public Result OnStartup(UIControlledApplication app)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_MOTOR_DOOR_CHECK") == "1") app.Idling += Run;
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
        private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Hidden).Invoke(target, args);
        private static object Field(object target, string name) => target.GetType().GetField(name, Hidden).GetValue(target);
        private void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks.Add(message); }
        private object Create(string type, params object[] args) => Activator.CreateInstance(_assembly.GetType("BIMaestro.MepBooster." + type, true), Hidden, null, args, null);
        private object Vector(XYZ p) => Create("MotorVector", p.X, p.Y, p.Z);
        private object Pose(XYZ p, double yaw = 0) => Create("MotorRoutePose", p, yaw);
        private object Geometry(XYZ size)
        {
            var envelope = Create("MotorEnvelope");
            Set(envelope, "Frame", Transform.Identity); Set(envelope, "Center", XYZ.Zero); Set(envelope, "Size", size);
            Set(envelope, "Sign", 1); Set(envelope, "Margin", 0.0);
            var type = _assembly.GetType("BIMaestro.MepBooster.MotorRouteGeometry", true);
            var geometry = RuntimeHelpers.GetUninitializedObject(type);
            Set(geometry, "_reference", envelope); Set(geometry, "Unknown", new List<string>()); Set(geometry, "Assumptions", new List<string>());
            foreach (var name in new[] { "_obstacles", "_doors", "_index" })
            { var field = type.GetField(name, Hidden); field.SetValue(geometry, Activator.CreateInstance(field.FieldType)); }
            return geometry;
        }
        private void Portal(object geometry, XYZ origin, XYZ x, XYZ normal)
            => ((IList)Field(geometry, "_doors")).Add(Create("MotorDoorPortal", "test door", Vector(origin), Vector(x), Vector(normal), Vector(XYZ.BasisZ), 1.0, 2.1, .2));
        private static Solid Box(XYZ min, XYZ max)
        {
            var points = new[] { min, new XYZ(max.X, min.Y, min.Z), new XYZ(max.X, max.Y, min.Z), new XYZ(min.X, max.Y, min.Z) };
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, max.Z - min.Z);
        }
        private void Obstacle(object geometry, XYZ min, XYZ max, string name)
        {
            var type = geometry.GetType().GetNestedType("Obstacle", Hidden);
            var obstacle = Activator.CreateInstance(type, true);
            Set(obstacle, "Solid", Box(min, max)); Set(obstacle, "Min", min); Set(obstacle, "Max", max); Set(obstacle, "Name", name);
            Set(obstacle, "MeshComplete", false);
            var triangles = type.GetField("Triangles", Hidden); triangles.SetValue(obstacle, Activator.CreateInstance(triangles.FieldType));
            var obstacles = (IList)Field(geometry, "_obstacles"); obstacles.Add(obstacle);
            var index = (IDictionary)Field(geometry, "_index");
            for (int x = -2; x <= 2; x++) for (int y = -2; y <= 2; y++) index[((long)x << 32) ^ (uint)y] = obstacles;
        }
        private void Run(object sender, IdlingEventArgs args)
        {
            if (_done) return; _done = true;
            string error = null;
            try
            {
                _assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "BIMaestro");
                var from = Pose(new XYZ(0, -2, 1)); var to = Pose(new XYZ(0, 2, 1));
                var open = Geometry(new XYZ(.8, 1.2, 1));
                using ((IDisposable)open)
                {
                    Portal(open, XYZ.Zero, XYZ.BasisX, XYZ.BasisY);
                    Obstacle(open, new XYZ(-3, -.1, 0), new XYZ(-.5, .1, 3), "left wall jamb");
                    Obstacle(open, new XYZ(.5, -.1, 0), new XYZ(3, .1, 3), "right wall jamb");
                    Check((bool)Call(open, "Clear", from, to), "Opened intermediate portal fits and actual wall jambs are respected");
                    Check((bool)Call(open, "ClearForSearch", from, to), "Search accepts the same opened portal");
                    Obstacle(open, new XYZ(-.5, -.02, 0), new XYZ(.5, .02, 2.1), "closed door leaf");
                    ((IList)Field(open, "_doors")).Clear();
                    Check(!(bool)Call(open, "Clear", from, to), "Closed intermediate leaf remains an obstacle without the open assumption");
                    Check(!(bool)Call(open, "ClearForSearch", from, to), "Search also refuses the closed leaf");
                }
                var wide = Geometry(new XYZ(1.1, 1.2, 1));
                using ((IDisposable)wide)
                {
                    Portal(wide, XYZ.Zero, XYZ.BasisX, XYZ.BasisY);
                    Check(!(bool)Call(wide, "Clear", from, to), "Nominal opening rejects a motor wider than the opening even without jamb solids");
                }
                var turn = Geometry(new XYZ(.8, .8, 1));
                using ((IDisposable)turn)
                {
                    Portal(turn, XYZ.Zero, XYZ.BasisX, XYZ.BasisY);
                    var first = Pose(new XYZ(0, 0, 1)); var last = Pose(new XYZ(0, 0, 1), Math.PI / 2);
                    Check((bool)Call(turn, "Clear", first, first) && (bool)Call(turn, "Clear", last, last), "Both endpoint orientations individually fit in a portal");
                    Check(!(bool)Call(turn, "Clear", first, last), "Turn is refused when its intermediate corners exceed the nominal opening");
                    Check(!(bool)Call(turn, "ClearForSearch", first, last), "Search conservatively refuses the same turn");
                }
                var linked = Geometry(new XYZ(.8, 1.2, 1));
                using ((IDisposable)linked)
                {
                    Portal(linked, new XYZ(10, 20, 3), XYZ.BasisY, -XYZ.BasisX);
                    Check((bool)Call(linked, "Clear", Pose(new XYZ(12, 20, 4), Math.PI / 2), Pose(new XYZ(8, 20, 4), Math.PI / 2)),
                        "Transformed linked-door frame accepts equivalent passage");
                }
            }
            catch (Exception ex) { error = ex.ToString(); }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "native-door-report.json"),
                JsonSerializer.Serialize(new { passed = error == null, checks = _checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            var ui = (UIApplication)sender;
            if (ui.ActiveUIDocument == null && Environment.GetEnvironmentVariable("BIMAESTRO_ANTIBES_ROUTE_CHECK") != "1") ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
    }
}
