using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    internal sealed class GameMeshData
    {
        public Point3DCollection Positions { get; } = new Point3DCollection();
        public Int32Collection Indices { get; } = new Int32Collection();
        public IList<Color> VertexColors { get; } = new List<Color>();
        public Vector3DCollection VertexNormals { get; } = new Vector3DCollection();
        public bool IsTransparent { get; set; }
        public bool HasCompleteNormals { get; set; } = true;

        public void Freeze()
        {
            try { Positions.Freeze(); } catch { }
            try { Indices.Freeze(); } catch { }
            try { VertexNormals.Freeze(); } catch { }
        }
    }

    internal sealed class GameDoorData
    {
        public GameDoorData(string key)
        {
            Key = key ?? string.Empty;
        }

        public string Key { get; }
        public GameMeshData OpaqueMesh { get; private set; } = new GameMeshData();
        public GameMeshData TransparentMesh { get; private set; } =
            new GameMeshData { IsTransparent = true };
        public Point3D Center { get; private set; }
        public Point3D Hinge { get; private set; }

        public bool HasGeometry =>
            OpaqueMesh.Positions.Count > 0 ||
            TransparentMesh.Positions.Count > 0;

        public GameMeshData GetMesh(bool transparent)
        {
            return transparent ? TransparentMesh : OpaqueMesh;
        }

        public bool FinalizeGeometry()
        {
            int pointCount = OpaqueMesh.Positions.Count + TransparentMesh.Positions.Count;
            if (pointCount == 0)
                return false;

            double meanX = 0.0;
            double meanY = 0.0;
            double minZ = double.MaxValue;
            double maxZ = double.MinValue;
            AccumulateFirstPass(OpaqueMesh, ref meanX, ref meanY, ref minZ, ref maxZ);
            AccumulateFirstPass(TransparentMesh, ref meanX, ref meanY, ref minZ, ref maxZ);
            meanX /= pointCount;
            meanY /= pointCount;

            // L'axe principal horizontal retrouve la largeur du bloc-porte même
            // quand celui-ci est tourné ou provient d'une maquette liée.
            double covarianceXX = 0.0;
            double covarianceXY = 0.0;
            double covarianceYY = 0.0;
            AccumulateCovariance(
                OpaqueMesh,
                meanX,
                meanY,
                ref covarianceXX,
                ref covarianceXY,
                ref covarianceYY);
            AccumulateCovariance(
                TransparentMesh,
                meanX,
                meanY,
                ref covarianceXX,
                ref covarianceXY,
                ref covarianceYY);

            double principalAngle = 0.5 * Math.Atan2(
                2.0 * covarianceXY,
                covarianceXX - covarianceYY);
            double axisX = Math.Cos(principalAngle);
            double axisY = Math.Sin(principalAngle);
            double perpendicularX = -axisY;
            double perpendicularY = axisX;

            double minAlongAxis = double.MaxValue;
            double maxAlongAxis = double.MinValue;
            double perpendicularTotal = 0.0;
            AccumulateProjections(
                OpaqueMesh,
                axisX,
                axisY,
                perpendicularX,
                perpendicularY,
                ref minAlongAxis,
                ref maxAlongAxis,
                ref perpendicularTotal);
            AccumulateProjections(
                TransparentMesh,
                axisX,
                axisY,
                perpendicularX,
                perpendicularY,
                ref minAlongAxis,
                ref maxAlongAxis,
                ref perpendicularTotal);

            double meanPerpendicular = perpendicularTotal / pointCount;
            double middleAlongAxis = (minAlongAxis + maxAlongAxis) * 0.5;
            double middleZ = (minZ + maxZ) * 0.5;

            Center = new Point3D(
                axisX * middleAlongAxis + perpendicularX * meanPerpendicular,
                axisY * middleAlongAxis + perpendicularY * meanPerpendicular,
                middleZ);
            Hinge = new Point3D(
                axisX * minAlongAxis + perpendicularX * meanPerpendicular,
                axisY * minAlongAxis + perpendicularY * meanPerpendicular,
                minZ);
            return true;
        }

        public void Translate(Vector3D delta)
        {
            TranslateMesh(OpaqueMesh, delta);
            TranslateMesh(TransparentMesh, delta);
            Center += delta;
            Hinge += delta;
        }

        public void ReleaseSourceGeometry()
        {
            // Les copies compactes en float sont désormais dans les buffers
            // DirectX. Garder les collections WPF doublerait la mémoire des portes.
            OpaqueMesh = new GameMeshData();
            TransparentMesh = new GameMeshData { IsTransparent = true };
        }

        private static void AccumulateFirstPass(
            GameMeshData mesh,
            ref double meanX,
            ref double meanY,
            ref double minZ,
            ref double maxZ)
        {
            foreach (Point3D point in mesh.Positions)
            {
                meanX += point.X;
                meanY += point.Y;
                minZ = Math.Min(minZ, point.Z);
                maxZ = Math.Max(maxZ, point.Z);
            }
        }

        private static void AccumulateCovariance(
            GameMeshData mesh,
            double meanX,
            double meanY,
            ref double covarianceXX,
            ref double covarianceXY,
            ref double covarianceYY)
        {
            foreach (Point3D point in mesh.Positions)
            {
                double deltaX = point.X - meanX;
                double deltaY = point.Y - meanY;
                covarianceXX += deltaX * deltaX;
                covarianceXY += deltaX * deltaY;
                covarianceYY += deltaY * deltaY;
            }
        }

        private static void AccumulateProjections(
            GameMeshData mesh,
            double axisX,
            double axisY,
            double perpendicularX,
            double perpendicularY,
            ref double minAlongAxis,
            ref double maxAlongAxis,
            ref double perpendicularTotal)
        {
            foreach (Point3D point in mesh.Positions)
            {
                double alongAxis = point.X * axisX + point.Y * axisY;
                minAlongAxis = Math.Min(minAlongAxis, alongAxis);
                maxAlongAxis = Math.Max(maxAlongAxis, alongAxis);
                perpendicularTotal +=
                    point.X * perpendicularX +
                    point.Y * perpendicularY;
            }
        }

        private static void TranslateMesh(GameMeshData mesh, Vector3D delta)
        {
            for (int index = 0; index < mesh.Positions.Count; index++)
                mesh.Positions[index] = mesh.Positions[index] + delta;
        }
    }

    internal sealed class GameTriangle
    {
        public GameTriangle(Point3D a, Point3D b, Point3D c, Vector3D normal, bool preferredWalkable)
        {
            A = a;
            B = b;
            C = c;
            Normal = normal;
            PreferredWalkable = preferredWalkable;
            UpdateBounds();
        }

        public Point3D A { get; private set; }
        public Point3D B { get; private set; }
        public Point3D C { get; private set; }
        public Vector3D Normal { get; }
        public bool PreferredWalkable { get; }

        public double MinX { get; private set; }
        public double MaxX { get; private set; }
        public double MinY { get; private set; }
        public double MaxY { get; private set; }
        public double MinZ { get; private set; }
        public double MaxZ { get; private set; }

        public Point3D Centroid => new Point3D(
            (A.X + B.X + C.X) / 3.0,
            (A.Y + B.Y + C.Y) / 3.0,
            (A.Z + B.Z + C.Z) / 3.0);

        public double HorizontalArea
        {
            get
            {
                double twiceArea = Math.Abs(
                    (B.X - A.X) * (C.Y - A.Y) -
                    (B.Y - A.Y) * (C.X - A.X));
                return twiceArea * 0.5;
            }
        }

        public void Translate(Vector3D delta)
        {
            A += delta;
            B += delta;
            C += delta;
            UpdateBounds();
        }

        private void UpdateBounds()
        {
            MinX = Math.Min(A.X, Math.Min(B.X, C.X));
            MaxX = Math.Max(A.X, Math.Max(B.X, C.X));
            MinY = Math.Min(A.Y, Math.Min(B.Y, C.Y));
            MaxY = Math.Max(A.Y, Math.Max(B.Y, C.Y));
            MinZ = Math.Min(A.Z, Math.Min(B.Z, C.Z));
            MaxZ = Math.Max(A.Z, Math.Max(B.Z, C.Z));
        }
    }

    internal sealed class GameSceneData
    {
        public IList<GameMeshData> Meshes { get; } = new List<GameMeshData>();
        public IList<GameDoorData> Doors { get; } = new List<GameDoorData>();
        public IList<GameTriangle> Triangles { get; } = new List<GameTriangle>();

        public string ViewName { get; set; } = string.Empty;
        public int VisibleElementCount { get; set; }
        public int TriangleCount => Triangles.Count;
        public int OriginalRenderTriangleCount { get; set; }
        public int OptimizedRenderTriangleCount { get; set; }
        public int RenderBucketCount { get; set; }
        public int RenderVertexCount { get; set; }
        public Rect3D Bounds { get; private set; }
        public Point3D ViewEye { get; private set; }
        public Vector3D ViewForward { get; private set; } = new Vector3D(1, 0, 0);
        public Point3D SpawnFootPosition { get; set; }
        public double InitialYawRadians { get; private set; }

        public void NormalizeCoordinates(Point3D viewEye, Vector3D viewForward)
        {
            if (Triangles.Count == 0)
            {
                Bounds = Rect3D.Empty;
                ViewEye = viewEye;
                ViewForward = viewForward;
                return;
            }

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            double maxZ = double.MinValue;

            foreach (GameTriangle triangle in Triangles)
            {
                minX = Math.Min(minX, triangle.MinX);
                minY = Math.Min(minY, triangle.MinY);
                minZ = Math.Min(minZ, triangle.MinZ);
                maxX = Math.Max(maxX, triangle.MaxX);
                maxY = Math.Max(maxY, triangle.MaxY);
                maxZ = Math.Max(maxZ, triangle.MaxZ);
            }

            // Recentrer autour de l'origine améliore fortement la précision WPF sur
            // les projets utilisant de grandes coordonnées partagées.
            var offset = new Vector3D(
                -((minX + maxX) * 0.5),
                -((minY + maxY) * 0.5),
                -minZ);

            foreach (GameMeshData mesh in Meshes)
            {
                for (int i = 0; i < mesh.Positions.Count; i++)
                    mesh.Positions[i] = mesh.Positions[i] + offset;
            }

            foreach (GameDoorData door in Doors)
                door.Translate(offset);

            foreach (GameTriangle triangle in Triangles)
                triangle.Translate(offset);

            ViewEye = viewEye + offset;
            ViewForward = viewForward;
            if (ViewForward.LengthSquared < 1e-12)
                ViewForward = new Vector3D(1, 0, 0);
            else
                ViewForward.Normalize();

            var horizontalForward = new Vector3D(ViewForward.X, ViewForward.Y, 0);
            InitialYawRadians = horizontalForward.LengthSquared > 1e-8
                ? Math.Atan2(horizontalForward.Y, horizontalForward.X)
                : 0.0;

            Bounds = new Rect3D(
                minX + offset.X,
                minY + offset.Y,
                minZ + offset.Z,
                Math.Max(0.01, maxX - minX),
                Math.Max(0.01, maxY - minY),
                Math.Max(0.01, maxZ - minZ));

            foreach (GameMeshData mesh in Meshes)
                mesh.Freeze();
        }
    }
}
