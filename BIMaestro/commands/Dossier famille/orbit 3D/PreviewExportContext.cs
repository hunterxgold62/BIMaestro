using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DB = Autodesk.Revit.DB;

namespace Famille.Orbit3D
{
    /// <summary>
    /// Contexte d’export pipeline Revit (compat 2023).
    /// Agrège les triangles par couleur et gère la pile des transforms d’instances.
    /// </summary>
    internal class PreviewExportContext : DB.IExportContext
    {
        private readonly List<MeshData> _result = new List<MeshData>();
        private readonly Dictionary<(byte A, byte R, byte G, byte B), MeshData> _byColor
            = new Dictionary<(byte, byte, byte, byte), MeshData>();

        private readonly Stack<DB.Transform> _tx = new Stack<DB.Transform>();
        private Color _currentColor = Color.FromRgb(200, 200, 200);
        private double _currentOpacity = 1.0;

        public IList<MeshData> Result => _result;

        public bool Start() { _tx.Clear(); _tx.Push(DB.Transform.Identity); return true; }
        public void Finish() { }
        public bool IsCanceled() => false;

        public DB.RenderNodeAction OnViewBegin(DB.ViewNode node) => DB.RenderNodeAction.Proceed;
        // Revit 2023 : fin de vue par ElementId
        public void OnViewEnd(DB.ElementId elementId) { }

        public DB.RenderNodeAction OnElementBegin(DB.ElementId elementId) => DB.RenderNodeAction.Proceed;
        public void OnElementEnd(DB.ElementId elementId) { }

        public DB.RenderNodeAction OnInstanceBegin(DB.InstanceNode node)
        {
            _tx.Push(_tx.Peek().Multiply(node.GetTransform()));
            return DB.RenderNodeAction.Proceed;
        }
        public void OnInstanceEnd(DB.InstanceNode node) => _tx.Pop();

        public DB.RenderNodeAction OnFaceBegin(DB.FaceNode node) => DB.RenderNodeAction.Proceed;
        public void OnFaceEnd(DB.FaceNode node) { }

        public void OnMaterial(DB.MaterialNode node)
        {
            // Transparence 0..100 (100 = très transparent) → on “clamp” pour l’aperçu
            double opacity = 1.0 - (node.Transparency / 100.0);
            if (opacity < 0.15) opacity = 0.15;

            var c = node.Color;
            _currentColor = Color.FromRgb(c.Red, c.Green, c.Blue); // pas d’alpha ici
            _currentOpacity = opacity;
        }

        public void OnPolymesh(DB.PolymeshTopology pm)
        {
            var pts = pm.GetPoints();
            var facets = pm.GetFacets();
            var T = _tx.Peek();

            var key = (_currentColor.A, _currentColor.R, _currentColor.G, _currentColor.B);
            if (!_byColor.TryGetValue(key, out var md))
            {
                md = new MeshData { DiffuseColor = _currentColor, Opacity = _currentOpacity };
                _byColor[key] = md;
                _result.Add(md);
            }

            int baseIndex = md.Positions.Count;
            foreach (var f in facets)
            {
                var p0 = T.OfPoint(pts[f.V1]); var P0 = new Point3D(p0.X, p0.Y, p0.Z);
                var p1 = T.OfPoint(pts[f.V2]); var P1 = new Point3D(p1.X, p1.Y, p1.Z);
                var p2 = T.OfPoint(pts[f.V3]); var P2 = new Point3D(p2.X, p2.Y, p2.Z);

                md.Positions.Add(P0); md.Positions.Add(P1); md.Positions.Add(P2);

                var n = Vector3D.CrossProduct(P1 - P0, P2 - P0);
                if (n.LengthSquared > 1e-18) n.Normalize(); else n = new Vector3D(0, 0, 1);
                md.Normals.Add(n); md.Normals.Add(n); md.Normals.Add(n);

                md.Indices.Add(baseIndex++); md.Indices.Add(baseIndex++); md.Indices.Add(baseIndex++);
            }
        }

        // Non utilisés mais requis par l’interface 2023
        public void OnCurve(DB.CurveNode node) { }
        public void OnPolyline(DB.PolylineNode node) { }
        public DB.RenderNodeAction OnLinkBegin(DB.LinkNode node) => DB.RenderNodeAction.Skip;
        public void OnLinkEnd(DB.LinkNode node) { }
        public bool AllowOutOfScope() => false;
        public void SetReference(DB.Reference r) { }

        public void OnRPC(DB.RPCNode node)
        {
            throw new System.NotImplementedException();
        }

        public void OnLight(DB.LightNode node)
        {
            throw new System.NotImplementedException();
        }
    }
}
