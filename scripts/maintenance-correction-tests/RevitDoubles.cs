using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// Minimal deterministic Revit substitutes. Native document/geometry semantics
// still require the separate Revit harness; these tests exercise rigid maths,
// safety/invalidation and actual JSON/file operations without launching Revit.
namespace Autodesk.Revit.DB
{
    internal class XYZ
    {
        internal double X,Y,Z;internal XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
        internal static XYZ Zero=>new XYZ(0,0,0);internal static XYZ BasisX=>new XYZ(1,0,0);internal static XYZ BasisY=>new XYZ(0,1,0);internal static XYZ BasisZ=>new XYZ(0,0,1);
        internal double DotProduct(XYZ p)=>X*p.X+Y*p.Y+Z*p.Z;internal double GetLength()=>Math.Sqrt(DotProduct(this));
        internal XYZ CrossProduct(XYZ p)=>new XYZ(Y*p.Z-Z*p.Y,Z*p.X-X*p.Z,X*p.Y-Y*p.X);internal XYZ Normalize()=>this/GetLength();
        public static XYZ operator +(XYZ a,XYZ b)=>new XYZ(a.X+b.X,a.Y+b.Y,a.Z+b.Z);public static XYZ operator -(XYZ a,XYZ b)=>new XYZ(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static XYZ operator -(XYZ a)=>a*-1;public static XYZ operator *(XYZ a,double b)=>new XYZ(a.X*b,a.Y*b,a.Z*b);public static XYZ operator /(XYZ a,double b)=>a*(1/b);
    }
    internal class Transform
    {
        internal XYZ Origin=XYZ.Zero,BasisX=XYZ.BasisX,BasisY=XYZ.BasisY,BasisZ=XYZ.BasisZ;internal static Transform Identity=>new Transform();
        internal XYZ OfVector(XYZ p)=>BasisX*p.X+BasisY*p.Y+BasisZ*p.Z;internal XYZ OfPoint(XYZ p)=>Origin+OfVector(p);
        internal Transform Multiply(Transform t)=>new Transform{Origin=OfPoint(t.Origin),BasisX=OfVector(t.BasisX),BasisY=OfVector(t.BasisY),BasisZ=OfVector(t.BasisZ)};
        internal Transform Inverse{get{var t=new Transform{BasisX=new XYZ(BasisX.X,BasisY.X,BasisZ.X),BasisY=new XYZ(BasisX.Y,BasisY.Y,BasisZ.Y),BasisZ=new XYZ(BasisX.Z,BasisY.Z,BasisZ.Z)};t.Origin=-t.OfVector(Origin);return t;}}
    }
    internal enum StorageType{None,Integer,Double,String,ElementId}
    internal enum BuiltInParameter{INSTANCE_ELEVATION_PARAM=-1000,INSTANCE_FREE_HOST_OFFSET_PARAM=-1001,FAMILY_PARAMETER=-2000}
    internal enum ViewDetailLevel{Fine}
    internal enum ConnectorType{Logical,End}
    internal enum ConnectorProfileType{Round,Rectangular}
    internal enum Domain{DomainPiping,DomainHvac}
    internal class ElementId{internal int IntegerValue;internal ElementId(int n){IntegerValue=n;}}
    internal class ForgeTypeId{internal string TypeId;internal ForgeTypeId(string t){TypeId=t;}}
    internal class Definition{internal string Name;internal string Spec="autodesk.spec.aec:length-2.0.0";internal ForgeTypeId GetDataType()=>new ForgeTypeId(Spec);}
    internal class Parameter
    {
        internal Definition Definition;internal ElementId Id=new ElementId(1);internal StorageType StorageType=StorageType.Double;internal double Number;internal string Text;
        internal double AsDouble()=>Number;internal int AsInteger()=>(int)Number;internal string AsString()=>Text;
    }
    internal class Element{internal string UniqueId=Guid.NewGuid().ToString();internal List<Parameter> Parameters=new List<Parameter>();}
    internal class Family{internal string Name="Family";}
    internal class FamilySymbol:Element{internal string Name="Type";internal Family Family=new Family();}
    internal class FamilyInstance:Element
    {
        internal FamilySymbol Symbol=new FamilySymbol();internal Transform Frame=Transform.Identity;internal GeometryElement Shape;internal MEPModel MEPModel;
        internal bool Mirrored,HandFlipped,FacingFlipped;
        internal Transform GetTransform()=>Frame;
        internal GeometryElement get_Geometry(Options o)=>Shape==null?null:new GeometryElement(new GeometryInstance{Transform=Frame,Geometry=Shape});
        internal List<ElementId> Children=new List<ElementId>();internal ICollection<ElementId> GetSubComponentIds()=>Children;
    }
    internal class Document
    {
        internal Dictionary<string,Element> Elements=new Dictionary<string,Element>();internal Dictionary<int,Element> Ids=new Dictionary<int,Element>();
        internal Element GetElement(string id)=>Elements.TryGetValue(id,out var e)?e:null;internal Element GetElement(ElementId id)=>Ids.TryGetValue(id.IntegerValue,out var e)?e:null;
    }
    internal class Options:IDisposable{internal ViewDetailLevel DetailLevel;internal bool IncludeNonVisibleObjects,ComputeReferences;public void Dispose(){}}
    internal class MEPModel{internal ConnectorManager ConnectorManager=new ConnectorManager();}
    internal class ConnectorManager{internal List<Connector> Connectors=new List<Connector>();}
    internal class Connector{internal XYZ Origin;internal Transform CoordinateSystem=Transform.Identity;internal ConnectorType ConnectorType=ConnectorType.End;internal Domain Domain=Domain.DomainPiping;internal ConnectorProfileType Shape=ConnectorProfileType.Round;internal double Radius=.1,Width,Height;}
    internal class GeometryObject{ }
    internal class GeometryElement:GeometryObject,IEnumerable<GeometryObject>,IDisposable
    {
        internal List<GeometryObject> Items=new List<GeometryObject>();internal GeometryElement(params GeometryObject[] items){Items.AddRange(items);}public IEnumerator<GeometryObject> GetEnumerator()=>Items.GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();public void Dispose(){}
    }
    internal class GeometryInstance:GeometryObject{internal Transform Transform=Transform.Identity;internal GeometryElement Geometry;internal GeometryElement GetSymbolGeometry()=>Geometry;}
    internal class Point:GeometryObject{ }
    internal class Mesh:GeometryObject
    {
        internal List<XYZ> Vertices=new List<XYZ>();internal List<MeshTriangle> Triangles=new List<MeshTriangle>();internal int NumTriangles=>Triangles.Count;internal MeshTriangle get_Triangle(int i)=>Triangles[i];
    }
    internal class MeshTriangle{internal XYZ[] Points;internal XYZ get_Vertex(int i)=>Points[i];}
    internal class Solid:GeometryObject{internal FaceArray Faces=new FaceArray();internal double Volume;}
    internal class FaceArray:List<Face>{internal int Size=>Count;}
    internal class Face{internal double Area;internal List<EdgeArray> EdgeLoops=new List<EdgeArray>();internal virtual XYZ Evaluate(UV p)=>new XYZ(p.U,p.V,0);internal BoundingBoxUV GetBoundingBox()=>new BoundingBoxUV();}
    internal class CylindricalFace:Face{internal XYZ Axis=XYZ.BasisZ;internal XYZ get_Radius(int i)=>XYZ.BasisX;}
    internal class BoundingBoxUV{internal UV Min=new UV(0,0),Max=new UV(1,1);}
    internal class UV{internal double U,V;internal UV(double u,double v){U=u;V=v;}}
    internal class EdgeArray:List<Edge>{ }
    internal class Edge{internal Curve Value;internal Curve AsCurve()=>Value;}
    internal class Curve:GeometryObject,IDisposable{internal List<XYZ> Points=new List<XYZ>();internal double Length;internal IList<XYZ> Tessellate()=>Points;public void Dispose(){}}
}
