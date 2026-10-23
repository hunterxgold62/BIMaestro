using System;

// Pure rigid-vector substitutes allow the production scene builder to execute
// outside Revit. They do not test Revit's native solid/Boolean implementation.
namespace Autodesk.Revit.DB
{
    internal sealed class XYZ
    {
        internal double X { get; }
        internal double Y { get; }
        internal double Z { get; }
        internal XYZ(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal static XYZ Zero => new XYZ(0, 0, 0);
        internal static XYZ BasisX => new XYZ(1, 0, 0);
        internal static XYZ BasisY => new XYZ(0, 1, 0);
        internal static XYZ BasisZ => new XYZ(0, 0, 1);
        internal double GetLength() => Math.Sqrt(DotProduct(this));
        internal double DotProduct(XYZ other) => X * other.X + Y * other.Y + Z * other.Z;
        internal XYZ CrossProduct(XYZ other) => new XYZ(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
        internal XYZ Normalize() => this / GetLength();
        public static XYZ operator +(XYZ a, XYZ b) => new XYZ(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static XYZ operator -(XYZ a, XYZ b) => new XYZ(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static XYZ operator -(XYZ a) => new XYZ(-a.X, -a.Y, -a.Z);
        public static XYZ operator *(XYZ a, double b) => new XYZ(a.X * b, a.Y * b, a.Z * b);
        public static XYZ operator /(XYZ a, double b) => new XYZ(a.X / b, a.Y / b, a.Z / b);
    }
    internal sealed class Transform
    {
        internal XYZ Origin = XYZ.Zero, BasisX = XYZ.BasisX, BasisY = XYZ.BasisY, BasisZ = XYZ.BasisZ;
        internal static Transform Identity => new Transform();
        internal static Transform CreateRotation(XYZ axis,double angle)
        {
            axis=axis.Normalize();
            XYZ Rotate(XYZ p)=>p*Math.Cos(angle)+axis.CrossProduct(p)*Math.Sin(angle)+axis*(axis.DotProduct(p)*(1-Math.Cos(angle)));
            return new Transform{BasisX=Rotate(XYZ.BasisX),BasisY=Rotate(XYZ.BasisY),BasisZ=Rotate(XYZ.BasisZ)};
        }
        internal XYZ OfVector(XYZ vector) => BasisX * vector.X + BasisY * vector.Y + BasisZ * vector.Z;
        internal XYZ OfPoint(XYZ point) => Origin + OfVector(point);
        internal Transform Inverse
        {
            get
            {
                var result = new Transform { BasisX = new XYZ(BasisX.X, BasisY.X, BasisZ.X),
                    BasisY = new XYZ(BasisX.Y, BasisY.Y, BasisZ.Y), BasisZ = new XYZ(BasisX.Z, BasisY.Z, BasisZ.Z) };
                result.Origin = -result.OfVector(Origin);
                return result;
            }
        }
    }
    internal sealed class Solid { }
}
namespace BIMaestro.MepBooster
{
    internal sealed class MotorEnvelope
    {
        internal Autodesk.Revit.DB.Transform Frame;
        internal Autodesk.Revit.DB.XYZ Center, Size, TranslationDirection;
        internal int Axis, Sign;
        internal double Margin, Travel;
        internal System.Collections.Generic.List<Autodesk.Revit.DB.Solid> SweepParts(double distance)
            => throw new NotSupportedException("Native Revit solids are not simulated by the pure geometry runner.");
    }
}
