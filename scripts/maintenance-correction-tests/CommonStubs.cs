using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

// Only the surrounding UI/controller is substituted. Production memory,
// persistence, fingerprint capture and profile validation run unchanged.
namespace BIMaestro.MepBooster
{
    internal static class BoosterIds { internal static long Value(ElementId id)=>id.IntegerValue; }
    internal sealed class MotorEnvelope
    {
        internal Transform Frame; internal XYZ Center,Size,TranslationDirection; internal int Axis,Sign; internal double Margin,Travel;
        internal IList<Solid> SweepParts(double length) => throw new NotSupportedException();
    }
    internal sealed class MaintenanceFeatureCandidate { }
    internal sealed class MaintenanceWorkbenchState
    {
        internal MaintenanceAccessOperation Operation;
        internal XYZ CommandPoint,FootPoint,RejectedFoot;
        internal Transform DetectedFrame;
        internal int Axis=6;
        internal bool Confirmed,Manufacturer,Measured,RotationAssumed,SupportVerified,WholeGestureChecked,WholeGestureClear,SearchLimited;
        internal MaintenanceOperatorPosture OperatorPosture,RejectedPosture; internal double BodyYawDegrees,RejectedBodyYawDegrees;
        internal string Notice="",DetectionNote="",SupportNote="",NoOperatorReason="";
        internal readonly Dictionary<string,string> Fields=new Dictionary<string,string>();
        internal readonly List<string> FeatureWarnings=new List<string>(),PlacementWarnings=new List<string>(),PostureWarnings=new List<string>(),PlacementDiagnostics=new List<string>(),GestureUnknown=new List<string>();
        internal readonly List<MaintenanceAccessObstacle> BlockingObstacles=new List<MaintenanceAccessObstacle>(),GestureObstacles=new List<MaintenanceAccessObstacle>(),FixedObstacles=new List<MaintenanceAccessObstacle>();
        internal readonly List<MaintenanceFeatureCandidate> Candidates=new List<MaintenanceFeatureCandidate>();
    }
    internal static class MaintenanceAutoconfiguration
    {
        internal static MaintenanceAccessProfile ReadSavedProfile(FamilyInstance source,MaintenanceWorkbenchState state)
        {
            var p=MaintenanceAccessProfile.For(state.Operation);
            double Get(string key,double fallback)=>state.Fields.TryGetValue(key,out string v)?double.Parse(v,CultureInfo.CurrentCulture):fallback;
            var t=source.GetTransform();var axes=new[]{t.BasisX,-t.BasisX,t.BasisY,-t.BasisY,t.BasisZ,-t.BasisZ};
            var z=state.Axis==6?state.DetectedFrame.BasisZ:axes[state.Axis];
            var x=state.Axis==6?state.DetectedFrame.BasisX:(Math.Abs(z.Z)<.9?XYZ.BasisZ:XYZ.BasisX).CrossProduct(z).Normalize();
            p.Frame=Transform.Identity;p.Frame.Origin=state.CommandPoint;p.Frame.BasisX=x;p.Frame.BasisY=z.CrossProduct(x).Normalize();p.Frame.BasisZ=z;
            p.HandleRadius=Get("Radius",200)/304.8;p.HandleThickness=Get("Thickness",35)/304.8;
            p.InitialAngleDegrees=Get("InitialAngle",0);p.RotationDegrees=Get("Angle",state.Operation==MaintenanceAccessOperation.LeverValve?90:360);
            p.ChainDrop=Get("ChainDrop",2000)/304.8;p.ChainSpacing=Get("ChainSpacing",400)/304.8;
            p.ChainPullStroke=Get("Pull",350)/304.8;
            p.GripClearance=Get("Grip",70)/304.8;p.Margin=Get("Margin",10)/304.8;
            p.RemovalSize=new XYZ(Get("RX",250),Get("RY",250),Get("RZ",500))/304.8;
            p.RemovalCenterOffset=new XYZ(Get("OffsetX",0),Get("OffsetY",0),Get("OffsetZ",-250))/304.8;
            p.RemovalTravel=Get("Travel",700)/304.8;p.OperatorHeight=Get("Height",1700)/304.8;p.Validate();return p;
        }
    }
}
