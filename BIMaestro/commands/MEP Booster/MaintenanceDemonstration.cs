using System;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    // Detached teaching scene. Its floor and person are illustrative, never a
    // proposed work position in the document and never passed to the scanner.
    internal static class MaintenanceDemonstration
    {
        private static double Mm(double value) => value / 304.8;
        internal static MaintenanceAccessProfile Create(MaintenanceAccessProfile source, bool hasMechanismFrame)
        {
            if(source==null) throw new ArgumentNullException(nameof(source));
            var frame=Transform.Identity;
            if(hasMechanismFrame)
            {frame.BasisX=source.Frame.BasisX;frame.BasisY=source.Frame.BasisY;frame.BasisZ=source.Frame.BasisZ;}
            else
            {
                XYZ axis=source.Operation==MaintenanceAccessOperation.Filter?new XYZ(0,1,-1).Normalize()
                    :source.Operation==MaintenanceAccessOperation.Actuator||source.Operation==MaintenanceAccessOperation.LeverValve?XYZ.BasisZ:-XYZ.BasisY;
                frame.BasisX=XYZ.BasisX;frame.BasisY=axis.CrossProduct(XYZ.BasisX).Normalize();frame.BasisZ=axis;
            }
            var p=new MaintenanceAccessProfile {
                Operation=source.Operation,Frame=frame,Center=XYZ.Zero,
                HandleRadius=source.HandleRadius,HandleThickness=source.HandleThickness,
                InitialAngleDegrees=source.InitialAngleDegrees,RotationDegrees=source.RotationDegrees,
                GripClearance=source.GripClearance,OperatorHeight=source.OperatorHeight,
                OperatorWidth=source.OperatorWidth,OperatorDepth=source.OperatorDepth,OperatorDistance=source.OperatorDistance,
                OperatorPosture=source.OperatorPosture,BodyYawDegrees=source.BodyYawDegrees,PostureSearch=source.PostureSearch,
                WorkHeight=source.WorkHeight,ChainDrop=source.ChainDrop,ChainSpacing=source.ChainSpacing,
                ChainPullStroke=source.ChainPullStroke,RemovalSize=source.RemovalSize,
                RemovalCenterOffset=source.RemovalCenterOffset,RemovalDirection=source.RemovalDirection,
                RemovalTravel=source.RemovalTravel,Margin=source.Margin,FollowRemoval=source.FollowRemoval
            };
            double height=MaintenanceAccessGeometry.ShoulderHeight(p);
            if(p.Operation==MaintenanceAccessOperation.ChainwheelValve)
            {
                p.WorkHeight=Math.Max(p.WorkHeight,Math.Max(p.GripClearance+p.ChainPullStroke+Mm(200),MaintenanceAccessGeometry.ShoulderHeight(p)+p.ChainPullStroke/2));
                height=p.ChainDrop+p.WorkHeight-p.GripClearance-p.ChainPullStroke;
            }
            if(p.Operation==MaintenanceAccessOperation.Filter||p.Operation==MaintenanceAccessOperation.Actuator)
                height-=p.Frame.OfVector(p.RemovalDirection).Normalize().Z*p.RemovalTravel/2;
            frame.Origin=XYZ.BasisZ*height;
            XYZ side=new XYZ(frame.BasisZ.X,frame.BasisZ.Y,0);
            if(side.GetLength()<1e-8)side=new XYZ(-frame.BasisY.X,-frame.BasisY.Y,0);
            if(side.GetLength()<1e-8)side=-XYZ.BasisY;
            p.OperatorOrigin=side.Normalize()*Mm(350);
            p.Validate();return p;
        }
    }
}
