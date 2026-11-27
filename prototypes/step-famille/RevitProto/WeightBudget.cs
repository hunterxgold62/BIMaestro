using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BIMaestro.ProtoStep
{
    // Famille à poids cible : aucune pièce n'est retirée ; on simplifie d'abord celles qui coûtent le plus
    // par rapport à leur taille (petites pièces très détaillées), jusqu'à passer sous le poids visé.
    internal static class WeightBudget
    {
        private const double Mm = 1 / 304.8;

        internal sealed class Item
        {
            public string Name, Group; public Solid Solid; public List<XYZ[]> Tris; public double DiagMm; public int Level;
            public double Cost;                 // coût estimé en octets (calibré sur la famille fidèle)
            public List<XYZ[]> Simple;          // version simplifiée (triangles), calculée à la demande
            public double SimpleCell;
            public double Priority => Cost / Math.Max(DiagMm, 1); // cher et petit = simplifié en premier
        }

        // Ordre de grandeur relatif d'une face de solide et d'un triangle de maillage dans un .rfa.
        private const double FaceUnits = 10, TriUnits = 1;

        internal static double RawUnits(Item i) => i.Solid != null ? i.Solid.Faces.Size * FaceUnits + i.Solid.Edges.Size * 2 : i.Tris.Count * TriUnits;

        internal static List<XYZ[]> Triangles(Solid s)
        {
            var list = new List<XYZ[]>();
            try
            {
                var tri = SolidUtils.TessellateSolidOrShell(s, new SolidOrShellTessellationControls { LevelOfDetail = 0.3, MinAngleInTriangle = 0.1, MinExternalAngleBetweenTriangles = 0.3 });
                for (int c = 0; c < tri.ShellComponentCount; c++)
                {
                    var sc = tri.GetShellComponent(c);
                    for (int t = 0; t < sc.TriangleCount; t++) { var x = sc.GetTriangle(t); list.Add(new[] { sc.GetVertex(x.VertexIndex0), sc.GetVertex(x.VertexIndex1), sc.GetVertex(x.VertexIndex2) }); }
                }
            }
            catch
            {
                foreach (Face f in s.Faces)
                {
                    var m = f.Triangulate(0.3);
                    for (int i = 0; i < m.NumTriangles; i++) { var t = m.get_Triangle(i); list.Add(new[] { t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2) }); }
                }
            }
            return list;
        }

        // Réduction par regroupement de sommets : les sommets d'une même cellule fusionnent, les triangles écrasés disparaissent.
        internal static List<XYZ[]> Cluster(List<XYZ[]> tris, double cell)
        {
            (long, long, long) Key(XYZ p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            var acc = new Dictionary<(long, long, long), (XYZ sum, int n)>();
            foreach (var t in tris) foreach (var p in t)
                { var k = Key(p); acc[k] = acc.TryGetValue(k, out var a) ? (a.sum + p, a.n + 1) : (p, 1); }
            var rep = acc.ToDictionary(kv => kv.Key, kv => kv.Value.sum / kv.Value.n);
            var seen = new HashSet<string>(); var outTris = new List<XYZ[]>();
            foreach (var t in tris)
            {
                var k = t.Select(Key).ToArray();
                if (k[0] == k[1] || k[1] == k[2] || k[0] == k[2]) continue;
                var id = string.Join("|", k.Select(x => $"{x.Item1},{x.Item2},{x.Item3}").OrderBy(x => x, StringComparer.Ordinal));
                if (!seen.Add(id)) continue;
                outTris.Add(new[] { rep[k[0]], rep[k[1]], rep[k[2]] });
            }
            return outTris;
        }

        internal static ElementId ShapeCategory(Document doc) =>
            new[] { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel }.Select(c => new ElementId(c)).FirstOrDefault(c => DirectShape.IsValidCategoryId(c, doc));

        internal static bool AddMesh(Document doc, ElementId cat, List<XYZ[]> tris, ElementId material)
        {
            if (cat == null || tris.Count == 0) return false;
            try
            {
                var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.Mesh, Fallback = TessellatedShapeBuilderFallback.Salvage };
                b.OpenConnectedFaceSet(false);
                foreach (var t in tris) { try { b.AddFace(new TessellatedFace(t.ToList(), material ?? ElementId.InvalidElementId)); } catch { } }
                b.CloseConnectedFaceSet(); b.Build();
                var geo = b.GetBuildResult().GetGeometricalObjects();
                if (geo.Count == 0) return false;
                DirectShape.CreateElement(doc, cat).SetShape(geo);
                return true;
            }
            catch { return false; }
        }

        // Prépare la version simplifiée d'une pièce : cellule = part de sa propre taille (ses détails fins disparaissent, pas sa silhouette).
        internal static void Simplify(Item i, double relCell)
        {
            double cell = Math.Max(3 * Mm, relCell * i.DiagMm * Mm);
            if (i.Simple != null && Math.Abs(i.SimpleCell - cell) < 1e-9) return;
            var src = i.Tris ?? Triangles(i.Solid);
            i.Simple = Cluster(src, cell); i.SimpleCell = cell;
        }

        // Choisit les pièces à simplifier : par priorité décroissante, jusqu'à ce que l'estimation passe sous la cible.
        internal static HashSet<Item> Plan(List<Item> items, double bytesPerUnit, double targetBytes, double relCell, double fixedBytes)
        {
            var set = new HashSet<Item>();
            double total = fixedBytes + items.Sum(i => i.Cost);
            foreach (var i in items.OrderByDescending(i => i.Priority))
            {
                if (total <= targetBytes) break;
                Simplify(i, relCell);
                double after = i.Simple.Count * TriUnits * bytesPerUnit;
                if (after >= i.Cost) continue;
                total += after - i.Cost; set.Add(i);
            }
            return set;
        }

        internal static void Top(List<Item> items, StringBuilder report, int n = 20)
        {
            report.AppendLine($"Pièces les plus lourdes (estimation) :");
            foreach (var i in items.OrderByDescending(i => i.Cost).Take(n))
                report.AppendLine($"  {i.Cost / 1024 / 1024,6:0.00} Mo  {(i.Solid != null ? "solide " : "maillage")}  {i.DiagMm,6:0} mm  {i.Group} / {i.Name}");
        }
    }
}
