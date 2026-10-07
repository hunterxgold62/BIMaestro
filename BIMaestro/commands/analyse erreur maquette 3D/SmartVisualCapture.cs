using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    // Invoked only during an API slice, and only for objects which actually produce a result.
    internal sealed class SmartVisualCapture
    {
        internal const int ObjectLimit = 6000, TotalLimit = 100000, SceneLimit = 3000, DrawingLimit = 60000;
        private int _triangles, _drawingTriangles;
        internal SmartVisualMesh Read(IList<Solid> solids, IList<(Mesh Mesh, Transform Transform)> meshes, bool partial)
        {
            if (_triangles >= TotalLimit) return Unavailable("Budget des aperçus atteint. Utilisez « Voir en 3D » pour cet objet.");
            var triangles = new List<SmartVisualTriangle>();
            try
            {
                foreach (var solid in solids)
                {
                    if (solid.Faces.Size > 2000) return Unavailable("Objet trop détaillé pour l'aperçu léger.");
                    foreach (Face face in solid.Faces)
                        if (!Append(face.Triangulate(.35), Transform.Identity, triangles)) return Unavailable("Objet trop détaillé pour l'aperçu léger.");
                }
                foreach (var mesh in meshes)
                    if (!Append(mesh.Mesh, mesh.Transform, triangles)) return Unavailable("Objet trop détaillé pour l'aperçu léger.");
                _triangles += triangles.Count;
                return new SmartVisualMesh(triangles, partial ? "Géométrie maillée ou partielle : représentation indicative." : null);
            }
            catch { return Unavailable("La géométrie de cet objet n'a pas pu être préparée pour l'aperçu."); }
        }
        private bool Append(Mesh mesh, Transform transform, List<SmartVisualTriangle> triangles)
        {
            if (mesh.NumTriangles + triangles.Count > ObjectLimit || _triangles + mesh.NumTriangles + triangles.Count > TotalLimit) return false;
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                triangles.Add(new SmartVisualTriangle(Point(transform.OfPoint(triangle.get_Vertex(0))),
                    Point(transform.OfPoint(triangle.get_Vertex(1))), Point(transform.OfPoint(triangle.get_Vertex(2)))));
            }
            return true;
        }
        private static SmartVisualPoint Point(XYZ p) => new SmartVisualPoint(p.X, p.Y, p.Z);
        private static SmartVisualMesh Unavailable(string notice) => new SmartVisualMesh(new SmartVisualTriangle[0], notice);
        internal SmartVisualScene Scene(SmartVisualMesh source, SmartVisualMesh obstacle, BoundingBoxXYZ box, bool approximate)
        {
            if (box == null) return null;
            var center = Point((box.Min + box.Max) / 2);
            var span = box.Max - box.Min;
            var radius = Math.Max(span.X, Math.Max(span.Y, span.Z)) / 2 + 450 / 304.8;
            var notices = new List<string>();
            source = CheckPart(source, center, radius, "Objet orange", notices);
            obstacle = CheckPart(obstacle, center, radius, "Obstacle bleu", notices);
            return new SmartVisualScene(source, obstacle, center, radius, approximate, string.Join(" ", notices.Distinct()));
        }
        private SmartVisualMesh CheckPart(SmartVisualMesh mesh, SmartVisualPoint center, double radius, string name, List<string> notices)
        {
            if (mesh == null) return null;
            if (!string.IsNullOrEmpty(mesh.Notice)) notices.Add(name + " : " + mesh.Notice);
            if (_drawingTriangles >= DrawingLimit)
            { notices.Add(name + " : budget des aperçus atteint. Utilisez « Voir en 3D »."); return null; }
            int count = SmartVisualScene.Clip(mesh, center, radius).Take(SceneLimit + 1).Count();
            if (count == 0) { if (mesh.TriangleCount > 0) notices.Add(name + " : surfaces hors du cadrage local."); return null; }
            if (count > SceneLimit || _drawingTriangles + count > DrawingLimit)
            { notices.Add(name + " : limite de complexité des aperçus atteinte. Utilisez « Voir en 3D »."); return null; }
            _drawingTriangles += count; return mesh;
        }
    }
}
