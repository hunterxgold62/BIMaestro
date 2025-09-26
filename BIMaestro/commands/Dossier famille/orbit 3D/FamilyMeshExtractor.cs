using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;              // WPF Color (MeshData)
using System.Windows.Media.Media3D;      // Point3D, Vector3D (fallback legacy)
using DB = Autodesk.Revit.DB;

namespace Famille.Orbit3D
{
    internal static class FamilyMeshExtractor
    {
        /// <summary>
        /// Extrait un aperçu maillé d’un document de famille.
        /// - Pipeline Revit via CustomExporter (fidèle).
        /// - Un seul niveau de détail choisi automatiquement (fluide).
        /// - Fallback legacy si l’export ne retourne rien.
        /// </summary>
        public static IList<MeshData> ExtractFromFamilyDoc(DB.Document famDoc)
        {
            var outMeshes = new List<MeshData>();
            if (famDoc == null) return outMeshes;

            // --- 1) Vue 3D temporaire ---
            DB.View3D view3D = null;
            using (var t = new DB.Transaction(famDoc, "Orbit3D - Vue"))
            {
                t.Start();
                try
                {
                    var vft = new DB.FilteredElementCollector(famDoc)
                                    .OfClass(typeof(DB.ViewFamilyType))
                                    .Cast<DB.ViewFamilyType>()
                                    .FirstOrDefault(v => v.ViewFamily == DB.ViewFamily.ThreeDimensional);
                    if (vft != null)
                    {
                        view3D = DB.View3D.CreateIsometric(famDoc, vft.Id);
                        view3D.DetailLevel = DB.ViewDetailLevel.Fine;
                        view3D.DisplayStyle = PreferredDisplayStyle();  // 2023: Shading, 2024+: Shaded
                        view3D.Name = "_Orbit3D_Temp";
                    }
                    t.Commit();
                }
                catch { try { t.RollBack(); } catch { } }
            }

            // --- 2) Auto-sélection d’un niveau (FINE→MEDIUM→COARSE) ---
            const int TRI_LIMIT = 250_000; // limite de triangles pour rester fluide

            IList<MeshData> ExportAt(DB.Document doc, DB.View3D v, DB.ViewDetailLevel lvl)
            {
                if (v != null)
                {
                    using var tx = new DB.Transaction(doc, "Orbit3D - Set DL");
                    try { tx.Start(); v.DetailLevel = lvl; tx.Commit(); } catch { try { tx.RollBack(); } catch { } }
                }

                var ctx = new PreviewExportContext();
                var exporter = new DB.CustomExporter(doc, ctx);
                try { exporter.Export(v); } catch { }
                return ctx.Result ?? new List<MeshData>();
            }

            outMeshes = ExportAt(famDoc, view3D, DB.ViewDetailLevel.Fine).ToList();
            int tris = outMeshes.Sum(m => m.Indices.Count / 3);

            if (tris == 0 || tris > TRI_LIMIT)
            {
                var m = ExportAt(famDoc, view3D, DB.ViewDetailLevel.Medium).ToList();
                int t2 = m.Sum(x => x.Indices.Count / 3);
                if (t2 > 0 && (tris == 0 || t2 < tris)) { outMeshes = m; tris = t2; }
            }
            if (tris == 0 || tris > TRI_LIMIT)
            {
                var c = ExportAt(famDoc, view3D, DB.ViewDetailLevel.Coarse).ToList();
                int t3 = c.Sum(x => x.Indices.Count / 3);
                if (t3 > 0) { outMeshes = c; tris = t3; }
            }

            // --- 3) Cleanup ---
            if (view3D != null)
            {
                using var t = new DB.Transaction(famDoc, "Orbit3D - Cleanup");
                try { t.Start(); famDoc.Delete(view3D.Id); t.Commit(); } catch { try { t.RollBack(); } catch { } }
            }

            // --- 4) Fallback legacy si rien exporté ---
            if (tris == 0)
                outMeshes = LegacyExtract(famDoc).ToList();

            return outMeshes;
        }

        // Compat 2023/2024/2025
        private static DB.DisplayStyle PreferredDisplayStyle()
        {
            try { return (DB.DisplayStyle)System.Enum.Parse(typeof(DB.DisplayStyle), "Shaded"); }
            catch { return DB.DisplayStyle.Shading; } // Revit 2023
        }

        // -------------------- Fallback legacy (get_Geometry) --------------------
        private static IList<MeshData> LegacyExtract(DB.Document famDoc)
        {
            var acc = new List<MeshData>();

            DB.View3D v = null;
            using (var t = new DB.Transaction(famDoc, "Orbit3D-Legacy-View"))
            {
                t.Start();
                try
                {
                    var vft = new DB.FilteredElementCollector(famDoc)
                                    .OfClass(typeof(DB.ViewFamilyType))
                                    .Cast<DB.ViewFamilyType>()
                                    .FirstOrDefault(x => x.ViewFamily == DB.ViewFamily.ThreeDimensional);
                    if (vft != null)
                    {
                        v = DB.View3D.CreateIsometric(famDoc, vft.Id);
                        v.DetailLevel = DB.ViewDetailLevel.Fine;
                        v.DisplayStyle = PreferredDisplayStyle();
                        v.Name = "_Orbit3D_Legacy";
                    }
                    t.Commit();
                }
                catch { try { t.RollBack(); } catch { } }
            }

            var opt = new DB.Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = true,
                View = v
            };

            var elems = new DB.FilteredElementCollector(famDoc)
                        .WhereElementIsNotElementType()
                        .Where(e => e.Category != null && e.Category.CategoryType == DB.CategoryType.Model);

            foreach (var el in elems)
            {
                DB.Transform tEl = DB.Transform.Identity;
                if (el is DB.FamilyInstance fi) { try { tEl = fi.GetTransform(); } catch { } }

                DB.GeometryElement ge = null;
                try { ge = el.get_Geometry(opt); } catch { ge = null; }
                if (ge == null) continue;

                foreach (DB.GeometryObject g in ge)
                {
                    try
                    {
                        if (g is DB.GeometryInstance gi)
                        {
                            var tx = tEl.Multiply(gi.Transform);
                            foreach (DB.GeometryObject gg in gi.GetInstanceGeometry())
                                LegacyAdd(acc, gg, tx);
                        }
                        else
                        {
                            LegacyAdd(acc, g, tEl);
                        }
                    }
                    catch { }
                }
            }

            if (v != null)
            {
                using var t = new DB.Transaction(famDoc, "Orbit3D-Legacy-Cleanup");
                try { t.Start(); famDoc.Delete(v.Id); t.Commit(); } catch { try { t.RollBack(); } catch { } }
            }

            return acc;
        }

        private static void LegacyAdd(List<MeshData> acc, DB.GeometryObject g, DB.Transform t)
        {
            if (g is DB.Solid s && s.Faces.Size > 0)
            {
                var md = new MeshData();
                int baseIndex = 0;
                foreach (DB.Face f in s.Faces)
                {
                    DB.Mesh m = null;
                    try { m = f.Triangulate(); } catch { }
                    if (m == null) continue;

                    for (int i = 0; i < m.NumTriangles; i++)
                    {
                        var tri = m.get_Triangle(i);
                        var p0 = t.OfPoint(tri.get_Vertex(0));
                        var p1 = t.OfPoint(tri.get_Vertex(1));
                        var p2 = t.OfPoint(tri.get_Vertex(2));

                        var P0 = new Point3D(p0.X, p0.Y, p0.Z);
                        var P1 = new Point3D(p1.X, p1.Y, p1.Z);
                        var P2 = new Point3D(p2.X, p2.Y, p2.Z);

                        md.Positions.Add(P0); md.Positions.Add(P1); md.Positions.Add(P2);

                        var n = Vector3D.CrossProduct(P1 - P0, P2 - P0);
                        if (n.LengthSquared > 1e-18) n.Normalize(); else n = new Vector3D(0, 0, 1);
                        md.Normals.Add(n); md.Normals.Add(n); md.Normals.Add(n);

                        md.Indices.Add(baseIndex++); md.Indices.Add(baseIndex++); md.Indices.Add(baseIndex++);
                    }
                }
                if (md.Indices.Count > 0) acc.Add(md);
            }
            else if (g is DB.Mesh rm)
            {
                var md = new MeshData();
                int baseIndex = 0;
                for (int i = 0; i < rm.NumTriangles; i++)
                {
                    var tri = rm.get_Triangle(i);
                    var p0 = t.OfPoint(tri.get_Vertex(0));
                    var p1 = t.OfPoint(tri.get_Vertex(1));
                    var p2 = t.OfPoint(tri.get_Vertex(2));

                    var P0 = new Point3D(p0.X, p0.Y, p0.Z);
                    var P1 = new Point3D(p1.X, p1.Y, p1.Z);
                    var P2 = new Point3D(p2.X, p2.Y, p2.Z);

                    md.Positions.Add(P0); md.Positions.Add(P1); md.Positions.Add(P2);

                    var n = Vector3D.CrossProduct(P1 - P0, P2 - P0);
                    if (n.LengthSquared > 1e-18) n.Normalize(); else n = new Vector3D(0, 0, 1);
                    md.Normals.Add(n); md.Normals.Add(n); md.Normals.Add(n);

                    md.Indices.Add(baseIndex++); md.Indices.Add(baseIndex++); md.Indices.Add(baseIndex++);
                }
                if (md.Indices.Count > 0) acc.Add(md);
            }
        }
    }
}
