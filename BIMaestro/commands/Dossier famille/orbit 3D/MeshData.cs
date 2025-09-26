using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Famille.Orbit3D
{
    public class MeshData
    {
        public Point3DCollection Positions { get; set; } = new Point3DCollection();
        public Int32Collection Indices { get; set; } = new Int32Collection();
        public Vector3DCollection Normals { get; set; } = new Vector3DCollection();

        // Couleur/alpha (0..1) utilisés par la vue Helix
        public Color DiffuseColor { get; set; } = Color.FromRgb(200, 200, 200);
        public double Opacity { get; set; } = 1.0;
        public void MakeThreadSafe()
        {
            try { Positions?.Freeze(); } catch { }
            try { Indices?.Freeze(); } catch { }
            try { Normals?.Freeze(); } catch { }
        }

    }

}
