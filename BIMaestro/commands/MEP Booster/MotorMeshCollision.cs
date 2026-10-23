using System;
using System.Collections.Generic;

namespace BIMaestro.MepBooster
{
    internal readonly struct MotorVector
    {
        internal readonly double X, Y, Z;
        internal MotorVector(double x, double y, double z) { X=x; Y=y; Z=z; }
        public static MotorVector operator +(MotorVector a, MotorVector b) => new MotorVector(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static MotorVector operator -(MotorVector a, MotorVector b) => new MotorVector(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static MotorVector operator *(MotorVector a, double s) => new MotorVector(a.X*s,a.Y*s,a.Z*s);
        internal double Dot(MotorVector b) => X*b.X+Y*b.Y+Z*b.Z;
        internal MotorVector Cross(MotorVector b) => new MotorVector(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
    }
    internal readonly struct MotorTriangle
    {
        internal readonly MotorVector A, B, C, Min, Max;
        internal MotorTriangle(MotorVector a, MotorVector b, MotorVector c)
        {
            A=a; B=b; C=c;
            Min=new MotorVector(Math.Min(a.X,Math.Min(b.X,c.X)),Math.Min(a.Y,Math.Min(b.Y,c.Y)),Math.Min(a.Z,Math.Min(b.Z,c.Z)));
            Max=new MotorVector(Math.Max(a.X,Math.Max(b.X,c.X)),Math.Max(a.Y,Math.Max(b.Y,c.Y)),Math.Max(a.Z,Math.Max(b.Z,c.Z)));
        }
    }
    internal readonly struct MotorCollisionBox
    {
        internal readonly MotorVector Center, X, Y, Z, Half;
        internal MotorCollisionBox(MotorVector center, MotorVector x, MotorVector y, MotorVector z, MotorVector half)
        { Center=center; X=x; Y=y; Z=z; Half=half; }
        private MotorVector Local(MotorVector p)
        { var v=p-Center; return new MotorVector(v.Dot(X),v.Dot(Y),v.Dot(Z)); }
        internal bool Intersects(MotorTriangle triangle)
        {
            var a=Local(triangle.A); var b=Local(triangle.B); var c=Local(triangle.C);
            var half=Half;
            bool Separated(MotorVector axis)
            {
                double pa=a.Dot(axis), pb=b.Dot(axis), pc=c.Dot(axis);
                double radius=half.X*Math.Abs(axis.X)+half.Y*Math.Abs(axis.Y)+half.Z*Math.Abs(axis.Z);
                return Math.Min(pa,Math.Min(pb,pc))>radius+1e-8 || Math.Max(pa,Math.Max(pb,pc)) < -radius-1e-8;
            }
            if(Separated(new MotorVector(1,0,0)) || Separated(new MotorVector(0,1,0)) || Separated(new MotorVector(0,0,1))) return false;
            var ab=b-a; var bc=c-b; var ca=a-c;
            if(Separated(ab.Cross(c-a))) return false;
            foreach(var edge in new[]{ab,bc,ca})
                if(Separated(new MotorVector(0,edge.Z,-edge.Y)) || Separated(new MotorVector(-edge.Z,0,edge.X))
                    || Separated(new MotorVector(edge.Y,-edge.X,0))) return false;
            return true;
        }
    }
    internal static class MotorMeshCollision
    {
        // A ray with non-axis-aligned components avoids the common triangulation
        // diagonals of planar walls. Coincident hits on adjoining facets count once.
        internal static bool Contains(IList<MotorTriangle> triangles, MotorVector point)
        {
            var direction=new MotorVector(1,.3713906763541037,.1732050807568877);
            var hits=new List<double>();
            foreach(var triangle in triangles)
            {
                var e1=triangle.B-triangle.A; var e2=triangle.C-triangle.A;
                var h=direction.Cross(e2); double det=e1.Dot(h);
                if(Math.Abs(det)<1e-12) continue;
                var s=point-triangle.A; double u=s.Dot(h)/det;
                if(u < -1e-9 || u > 1+1e-9) continue;
                var q=s.Cross(e1); double v=direction.Dot(q)/det;
                if(v < -1e-9 || u+v > 1+1e-9) continue;
                double t=e2.Dot(q)/det; if(t>1e-9) hits.Add(t);
            }
            hits.Sort(); int count=0; double previous=double.NegativeInfinity;
            foreach(double hit in hits) if(hit-previous>1e-7) {count++; previous=hit;}
            return count%2==1;
        }
    }
}
