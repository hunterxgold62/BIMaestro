// Value-only equivalents for the WPF geometry used by the shared MEP solver.
// The browser does not reference Revit, WPF or the plugin's renderer.
namespace System.Windows.Media
{
    public struct Color
    {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public static Color FromRgb(byte r, byte g, byte b) => new Color { A = 255, R = r, G = g, B = b };
    }
}
namespace System.Windows.Media.Media3D
{
    public struct Point3D
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public Point3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vector3D operator -(Point3D a, Point3D b) => new Vector3D(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Point3D operator +(Point3D a, Vector3D b) => new Point3D(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    }
    public struct Vector3D
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double LengthSquared => X*X + Y*Y + Z*Z;
        public double Length => Math.Sqrt(LengthSquared);
        public void Normalize() { double length = Length; X /= length; Y /= length; Z /= length; }
        public static double DotProduct(Vector3D a, Vector3D b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        public static Vector3D operator -(Vector3D a) => new Vector3D(-a.X,-a.Y,-a.Z);
        public static Vector3D operator *(Vector3D a, double b) => new Vector3D(a.X*b,a.Y*b,a.Z*b);
    }
}
