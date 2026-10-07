using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Analyse;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMaestro.Tutorials
{
    // Only this isolated training scene can be reset or moved by the tutorial.
    internal static class DemoClashExercise
    {
        internal const string ViewName = "BIMaestro - 10 Clash 3D";
        internal const string Prefix = "BIMaestro_DEMO_CLASH_";
        private static double M(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
        private static XYZ Point(double x, double y, double z) => new XYZ(M(x), M(y), M(z));
        private static string Mark(Element element) => element?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
        internal static bool IsTraining(Document doc) => doc != null && !doc.IsFamilyDocument &&
            Path.GetFileName(doc.PathName).StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase);
        internal static List<ElementId> Sources(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(Pipe))
            .Where(pipe => (Mark(pipe) ?? "").StartsWith(Prefix, StringComparison.Ordinal)).Select(pipe => pipe.Id).ToList();

        internal static View3D Prepare(Document doc)
        {
            if (!IsTraining(doc)) throw new InvalidOperationException("Crée ou ouvre la maquette BIMaestro_Apprentissage pour cet exercice. Le bouton TUTO explique aussi Clash 3D dans un projet ordinaire.");
            using (var tx = new Transaction(doc, "BIMaestro - Préparer le parcours Clash 3D"))
            {
                tx.Start();
                BuildScene(doc);
                tx.Commit();
            }
            return new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Single(view => view.Name == ViewName);
        }

        // Called inside the builder's transaction too, before the new document has a path.
        internal static void BuildScene(Document doc)
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.Elevation)).First();
            var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
            var system = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().First();
            var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
            var old = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => (Mark(e) ?? "").StartsWith(Prefix, StringComparison.Ordinal)).Select(e => e.Id).ToList();
            // Rebuild the four marked objects, so a previous correction or deletion is repeatable.
            if (old.Count > 0) doc.Delete(old);
            var wall = Wall.Create(doc, Line.CreateBound(Point(-4, 40, 0), Point(-2, 40, 0)),
                wallType.Id, level.Id, M(3), -level.Elevation, false, false);
            wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).Set(Prefix + "WALL");
            AddPipe(doc, system, pipeType, level, "THROUGH", Point(-3, 38, 1.5), Point(-3, 42, 1.5));
            AddPipe(doc, system, pipeType, level, "CROSS_A", Point(1, 41, 1.5), Point(5, 41, 1.5));
            AddPipe(doc, system, pipeType, level, "CROSS_B", Point(3, 39, 1.5), Point(3, 43, 1.5));
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => v.Name == ViewName);
            if (view == null)
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(doc, type.Id); view.Name = ViewName;
            }
            view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.FlatColors;
            view.SetSectionBox(new BoundingBoxXYZ { Min = Point(-5, 37, -.5), Max = Point(6, 44, 3.5) });
            view.IsSectionBoxActive = true;
        }

        private static void AddPipe(Document doc, PipingSystemType system, PipeType type, Level level, string suffix, XYZ start, XYZ end)
        {
            var pipe = Pipe.Create(doc, system.Id, type.Id, level.Id, start, end);
            pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(M(.1));
            pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).Set(Prefix + suffix);
        }

        internal static bool IsCrossing(Document doc, ModelIssue issue) => issue?.Kind == IssueKind.LocalClash && Pair(doc, issue, "CROSS_A", "CROSS_B");
        internal static bool IsWall(Document doc, ModelIssue issue) => issue?.Kind == IssueKind.MepThroughWallNoSleeve && Pair(doc, issue, "THROUGH", "WALL");
        private static bool Pair(Document doc, ModelIssue issue, string first, string second)
        {
            if (issue == null || issue.IsApproximate || !string.IsNullOrEmpty(issue.LinkUniqueId)) return false;
            string a = Mark(doc.GetElement(issue.ElementId)), b = Mark(doc.GetElement(issue.RelatedId));
            return a == Prefix + first && b == Prefix + second || a == Prefix + second && b == Prefix + first;
        }

        internal static bool Verify(Document doc, SmartScanSession scan, bool corrected)
        {
            if (scan == null || !scan.Complete || scan.Cancelled || scan.Error != null || scan.SourceCount != 3 ||
                scan.Options.Scope != SmartScanScope.Selection || !scan.Options.LocalClashes || scan.Options.MinimumVolumeMm3 != 10) return false;
            return scan.Issues.Count == (corrected ? 1 : 2) && scan.Issues.Count(i => IsWall(doc, i)) == 1 &&
                scan.Issues.Count(i => IsCrossing(doc, i)) == (corrected ? 0 : 1);
        }

        internal static void Correct(Document doc)
        {
            if (!IsTraining(doc)) throw new InvalidOperationException("La correction d'essai est réservée à la maquette de formation.");
            var pipe = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>().Single(p => Mark(p) == Prefix + "CROSS_B");
            var location = pipe.Location as LocationCurve;
            if (location == null) throw new InvalidOperationException("Le tuyau d'essai n'a pas de courbe modifiable.");
            var line = location.Curve as Line;
            if (line == null || line.GetEndPoint(0).DistanceTo(Point(3, 39, 1.5)) > M(.001) || line.GetEndPoint(1).DistanceTo(Point(3, 43, 1.5)) > M(.001))
                throw new InvalidOperationException("Le tuyau d'essai a déjà été déplacé. Relance le parcours Clash 3D pour réinitialiser sa scène.");
            using (var tx = new Transaction(doc, "BIMaestro - Décaler le tuyau d'essai de 300 mm"))
            {
                tx.Start(); ElementTransformUtils.MoveElement(doc, pipe.Id, Point(0, 0, .3)); tx.Commit();
            }
        }
    }
}
