// This probe must run inside a valid Revit API callback. The compiler does not
// execute it and no geometric doubles or replacement algorithms are used.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Modification;

namespace BIMaestro.ReservationTests
{
    [Transaction(TransactionMode.Manual)]
    public sealed class NativeReservationProbe : IExternalCommand
    {
        private static readonly Type CommandType = typeof(ReservationAutoV3Command);
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var results = new List<string>();
            Document fixture = data.Application.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                DirectShape host, source, outside, separated, asymmetric, inclined;
                DirectShapeType sourceType;
                Level level;
                using (var tx = new Transaction(fixture, "Autoréservation geometric fixtures"))
                {
                    tx.Start();
                    host = Box(fixture, new XYZ(-.5, -5, 0), new XYZ(.5, 5, 5), BuiltInCategory.OST_GenericModel);
                    source = Box(fixture, new XYZ(-3, -.5, 1), new XYZ(3, .5, 2), BuiltInCategory.OST_MechanicalEquipment);
                    outside = Box(fixture, new XYZ(-3, 6, 1), new XYZ(3, 7, 2), BuiltInCategory.OST_GenericModel);
                    separated = DirectShape.CreateElement(fixture, ElementIdExtensions.CreateElementId((int)BuiltInCategory.OST_GenericModel));
                    separated.SetShape(new GeometryObject[]
                    {
                        BoxSolid(new XYZ(-3, -.5, 1), new XYZ(-1, .5, 2)),
                        BoxSolid(new XYZ(1, -.5, 1), new XYZ(3, .5, 2))
                    });
                    sourceType = DirectShapeType.Create(fixture, "Generic source type fixture", ElementIdExtensions.CreateElementId((int)BuiltInCategory.OST_GenericModel));
                    level = Level.Create(fixture, 0);
                    asymmetric = DirectShape.CreateElement(fixture, ElementIdExtensions.CreateElementId((int)BuiltInCategory.OST_GenericModel));
                    asymmetric.SetShape(new GeometryObject[] { TrianglePrism() });
                    inclined = DirectShape.CreateElement(fixture, ElementIdExtensions.CreateElementId((int)BuiltInCategory.OST_GenericModel));
                    inclined.SetShape(new GeometryObject[] { SolidUtils.CreateTransformed(
                        BoxSolid(new XYZ(-3, -1, -.5), new XYZ(3, 1, .5)),
                        Transform.CreateTranslation(new XYZ(0, 0, 2.5)).Multiply(Transform.CreateRotation(XYZ.BasisX, Math.PI / 6))) });
                    tx.Commit();
                }
                CheckIntersection(source, Transform.Identity, host, Transform.Identity, true, new XYZ(0, 0, 1.5));
                results.Add("PASS generic DirectShape physical intersection and centroid");
                CheckFootprint(host, Transform.Identity, source, Transform.Identity,
                    XYZ.BasisY, new XYZ(0, 0, 1.5), 1, 1, Math.Sqrt(2));
                results.Add("PASS circular opening encloses every corner of a rectangular object");
                CheckFootprint(host, Transform.Identity, asymmetric, Transform.Identity,
                    XYZ.BasisY, new XYZ(0, 0, 2), 4, 4, Math.Sqrt(32));
                results.Add("PASS asymmetric cross-section uses projected extents, rather than volume centroid");
                double cosine = Math.Cos(Math.PI / 6), sine = Math.Sin(Math.PI / 6);
                CheckFootprint(host, Transform.Identity, inclined, Transform.Identity,
                    XYZ.BasisY, new XYZ(0, 0, 2.5), 2 * cosine + sine, 2 * sine + cosine, Math.Sqrt(5));
                results.Add("PASS inclined rectangular section preserves corner-covering circular diameter");
                CheckIntersection(outside, Transform.Identity, host, Transform.Identity, false, null);
                results.Add("PASS separated solids refuse an intersection");
                CheckIntersection(separated, Transform.Identity, host, Transform.Identity, false, null);
                results.Add("PASS overlapping bounding boxes with disjoint solids refuse an intersection");
                Transform linked = Transform.CreateRotation(XYZ.BasisZ, Math.PI / 3);
                linked.Origin = new XYZ(12, -4, 2);
                CheckIntersection(source, linked, host, linked, true, linked.OfPoint(new XYZ(0, 0, 1.5)));
                results.Add("PASS translated and rotated linked geometry");
                CheckFootprint(host, linked, source, linked,
                    linked.OfVector(XYZ.BasisY), linked.OfPoint(new XYZ(0, 0, 1.5)), 1, 1, Math.Sqrt(2));
                results.Add("PASS generic IFC-style host and source footprint after link transforms");
                CheckGenericSelection(source, true);
                CheckGenericSelection(sourceType, false);
                CheckGenericSelection(level, false);
                results.Add("PASS generic physical instance accepted; element type and level refused");
                NativeHeightMappingProbe.Run(data.Application, fixture, results);
                message = string.Join(Environment.NewLine, results);
                TaskDialog.Show("Autoréservation native probe", message);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = string.Join(Environment.NewLine, results) + Environment.NewLine + (ex.InnerException ?? ex);
                return Result.Failed;
            }
            finally { fixture.Close(false); }
        }

        private static DirectShape Box(Document document, XYZ min, XYZ max, BuiltInCategory category)
        {
            DirectShape shape = DirectShape.CreateElement(document, ElementIdExtensions.CreateElementId((int)category));
            shape.SetShape(new GeometryObject[] { BoxSolid(min, max) });
            return shape;
        }

        private static Solid BoxSolid(XYZ min, XYZ max)
        {
            XYZ[] corners = { min, new XYZ(max.X, min.Y, min.Z), new XYZ(max.X, max.Y, min.Z), new XYZ(min.X, max.Y, min.Z) };
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, max.Z - min.Z);
        }

        private static Solid TrianglePrism()
        {
            XYZ[] points = { new XYZ(-3, -2, 0), new XYZ(-3, 2, 0), new XYZ(-3, 2, 4) };
            var loop = new CurveLoop();
            for (int i = 0; i < 3; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 3]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisX, 6);
        }

        private static void CheckFootprint(Element host, Transform hostTransform, Element source, Transform sourceTransform,
            XYZ wallAxis, XYZ center, double length, double height, double diameter)
        {
            MethodInfo method = CommandType.GetMethod("GetObjectOpeningFootprint", PrivateStatic);
            // A fallback is deliberately absent: these fixtures have usable native solids.
            object footprint = method.Invoke(null, new object[] { host, hostTransform, source, sourceTransform, true, wallAxis, null });
            Type footprintType = footprint.GetType();
            var actualCenter = (XYZ)footprintType.GetProperty("Center").GetValue(footprint);
            double actualLength = (double)footprintType.GetProperty("Length").GetValue(footprint);
            double actualHeight = (double)footprintType.GetProperty("Height").GetValue(footprint);
            double actualDiameter = (double)footprintType.GetProperty("Diameter").GetValue(footprint);
            if (actualCenter.DistanceTo(center) > 1e-7 || Math.Abs(actualLength - length) > 1e-7
                || Math.Abs(actualHeight - height) > 1e-7 || Math.Abs(actualDiameter - diameter) > 1e-7)
                throw new InvalidOperationException("Unexpected physical opening footprint: projected center or dimensions.");
        }

        private static void CheckIntersection(Element source, Transform sourceTransform, Element host, Transform hostTransform,
            bool expected, XYZ expectedCenter)
        {
            MethodInfo method = CommandType.GetMethod("TryGetElementHostIntersectionCenter", PrivateStatic);
            object[] arguments = { source, sourceTransform, host, hostTransform, null };
            bool actual = (bool)method.Invoke(null, arguments);
            if (actual != expected || actual && ((XYZ)arguments[4]).DistanceTo(expectedCenter) > 1e-7)
                throw new InvalidOperationException("Unexpected physical intersection or centroid.");
        }

        private static void CheckGenericSelection(Element element, bool expected)
        {
            MethodInfo method = CommandType.GetMethod("IsExpectedObject", PrivateStatic);
            bool actual = (bool)method.Invoke(null, new object[] { element, ReservationAutoV3Window.ObjectType.Autre });
            if (actual != expected) throw new InvalidOperationException("Unexpected generic-object selection result.");
        }
    }
}
