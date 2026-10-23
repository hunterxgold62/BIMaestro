using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal static class MaintenanceAutoconfiguration
    {
        internal static void Suggest(Document doc, FamilyInstance source, MaintenanceWorkbenchState state, Action<string> progress = null)
        {
            ClearGestureState(state);state.Confirmed=false;
            state.CorrectionApplied=false;state.CorrectionNote="";state.RotationAssumed=false;
            var requestedOperation=state.Operation;
            progress?.Invoke("Identification de la commande dans la famille…");
            var feature = MaintenanceFeatureDetection.Detect(doc, source, state.Operation);
            state.Candidates.Clear();state.Candidates.AddRange(feature.Candidates.Take(8));
            foreach(string key in new[] {"Radius","Thickness","Angle","InitialAngle","ChainDrop","ChainSpacing","RX","RY","RZ","Travel","OffsetX","OffsetY","OffsetZ"}) state.Fields.Remove(key);
            state.FeatureWarnings.Clear(); state.FeatureWarnings.AddRange(feature.Warnings);
            state.DetectionNote = string.Join("\n", feature.Evidence);
            state.Confirmed = false; state.Manufacturer = false;
            state.CommandPoint = feature.HasCommandPoint ? feature.CommandPoint : null;
            state.DetectedFrame = feature.HasAxis ? feature.Frame : null;
            state.Axis = state.DetectedFrame == null ? 4 : 6;
            state.Operation = feature.Operation;
            state.Measured = feature.HasRadius && state.Operation!=MaintenanceAccessOperation.Filter && state.Operation!=MaintenanceAccessOperation.Actuator || feature.HasChain || feature.HasRemoval;
            void Field(string key, double value) => state.Fields[key] = value.ToString("0.###", CultureInfo.CurrentCulture);
            if(feature.HasRadius) Field("Radius", feature.Radius*304.8);
            if(feature.HasHandleThickness) Field("Thickness",feature.HandleThickness*304.8);
            Field("InitialAngle",feature.InitialAngleDegrees);
            Field("Angle",feature.HasRotation?feature.RotationDegrees:state.Operation==MaintenanceAccessOperation.LeverValve?90:360);
            state.RotationAssumed=feature.RotationAssumed||!feature.HasRotation&&state.Operation==MaintenanceAccessOperation.LeverValve;
            if(feature.HasChain)
            {Field("ChainDrop",feature.ChainDrop*304.8);Field("ChainSpacing",feature.ChainSpacing*304.8);}
            if(feature.HasRemoval)
            {
                Field("RX",feature.RemovalSize.X*304.8);Field("RY",feature.RemovalSize.Y*304.8);Field("RZ",feature.RemovalSize.Z*304.8);
                Field("OffsetX",feature.RemovalCenterOffset.X*304.8);Field("OffsetY",feature.RemovalCenterOffset.Y*304.8);Field("OffsetZ",feature.RemovalCenterOffset.Z*304.8);
                if(feature.RemovalTravel>0) Field("Travel",feature.RemovalTravel*304.8);
            }
            state.Notice = "";
            progress?.Invoke("Vérification des corrections mémorisées pour cette géométrie…");
            // The generic valve entry initially requests HandwheelValve. Keep
            // that lookup scope for remembered manual mechanisms. Actuator
            // removal is a separate intervention and must never replace it.
            state.Operation=requestedOperation;
            state.CorrectionApplied=MaintenanceCorrectionMemory.TryApply(doc,source,state,out string correctionNote);
            if(!state.CorrectionApplied)state.Operation=feature.Operation;
            state.CorrectionNote=correctionNote??"";
            Reposition(doc,source,state,progress);
        }
        internal static void Reposition(Document doc, FamilyInstance source, MaintenanceWorkbenchState state, Action<string> progress = null)
        {
            state.FootPoint = null; state.SupportVerified = false; state.SupportNote = ""; state.NoOperatorReason="";state.PlacementWarnings.Clear();
            state.PostureWarnings.Clear(); state.PlacementDiagnostics.Clear(); state.BlockingObstacles.Clear(); state.RejectedFoot = null;
            state.RejectedPosture = MaintenanceOperatorPosture.Standing; state.RejectedBodyYawDegrees = 0;
            ClearGestureState(state);
            state.Confirmed = false;
            if(state.CommandPoint == null)
            {
                state.NoOperatorReason=state.FeatureWarnings.FirstOrDefault(w=>w.IndexOf("Commande motorisée reconnue",StringComparison.OrdinalIgnoreCase)>=0)
                    ?? (state.Candidates.Count>0?"Plusieurs repères restent possibles : choisissez le repère du geste réel avant de rechercher un opérateur."
                        :state.Operation==MaintenanceAccessOperation.Actuator?"Point de dépose de l’actionneur non identifié : précisez sa fixation et le sens de retrait avant de rechercher un opérateur."
                        :state.Operation==MaintenanceAccessOperation.Filter?"Capot du filtre non identifié : précisez son centre et le sens de retrait avant de rechercher un opérateur."
                        :"Commande manuelle non identifiée : aucun volant, levier ou dispositif à chaîne n’a été établi de façon fiable. Précisez l’organe réel avant de rechercher un opérateur.");
                state.PlacementWarnings.Add(state.NoOperatorReason);return;
            }
            progress?.Invoke("Recherche d’un poste pour tout le geste : appuis, corps, bras et mains…");
            var profile=CreateProfile(source,state,state.Operation,state.Axis,key=>Saved(state,key));
            var placement=MaintenanceOperatorPlacement.Suggest(doc,source,profile,progress);
            ApplyPlacementResult(state,placement);
            if(placement.FootPoint!=null)
                state.Fields["WorkHeight"]=(placement.SuggestedWorkHeight*304.8).ToString("0.###",CultureInfo.CurrentCulture);
        }
        internal static void ClearGestureState(MaintenanceWorkbenchState state)
        {
            state.WholeGestureChecked=false;state.WholeGestureClear=false;state.SearchLimited=false;
            state.GestureUnknown.Clear();state.GestureObstacles.Clear();state.FixedObstacles.Clear();
        }
        internal static void TransferGesture(MaintenanceWorkbenchState state,MaintenanceOperatorPlacementResult placement)
        {
            ClearGestureState(state);
            state.WholeGestureChecked=placement.WholeGestureChecked;
            state.WholeGestureClear=placement.WholeGestureChecked&&placement.WholeGestureClear;
            state.SearchLimited=placement.SearchLimited;
            state.GestureUnknown.AddRange(placement.GestureUnknown);
            state.GestureObstacles.AddRange(placement.GestureObstacles);
            state.FixedObstacles.AddRange(placement.FixedObstacles);
        }
        // Shared by automatic search and exact/manual validation. This copies
        // results only: edited dimensions, provenance and confirmation remain
        // the caller's responsibility. A failed result cannot replace the
        // requested posture with the enum's default standing posture.
        internal static void ApplyPlacementResult(MaintenanceWorkbenchState state,MaintenanceOperatorPlacementResult placement)
        {
            state.FootPoint=placement.FootPoint;
            state.NoOperatorReason=placement.FootPoint==null?placement.NoOperatorReason??"":"";
            if(placement.FootPoint!=null)
            {state.OperatorPosture=placement.SuggestedPosture;state.BodyYawDegrees=placement.SuggestedBodyYawDegrees;}
            state.PostureWarnings.Clear();state.PlacementDiagnostics.Clear();state.BlockingObstacles.Clear();state.PlacementWarnings.Clear();
            state.PostureWarnings.AddRange(placement.PostureWarnings);
            state.PlacementDiagnostics.AddRange(placement.Diagnostics);
            state.BlockingObstacles.AddRange(placement.BlockingObstacles);
            state.RejectedFoot=placement.RejectedFoot;
            state.RejectedPosture=placement.RejectedPosture;
            state.RejectedBodyYawDegrees=placement.RejectedBodyYawDegrees;
            state.SupportVerified=placement.FootPoint!=null&&placement.Confidence==MaintenancePlacementConfidence.GeometryChecked;
            state.PlacementWarnings.AddRange(placement.Warnings);
            state.SupportNote=string.Join("\n",new[] {placement.Support?.Label??"Aucun poste proposé sur un sol praticable."}.Concat(placement.Assumptions));
            TransferGesture(state,placement);
        }
        internal static MaintenanceAccessProfile CreateProfile(FamilyInstance source, MaintenanceWorkbenchState state, MaintenanceAccessOperation operation, int axisIndex, Func<string,double> number)
        {
            var t=source.GetTransform();
            using(var box=source.get_BoundingBox(null))
                return CreateProfile(t,box==null?t.Origin:(box.Min+box.Max)/2,state,operation,axisIndex,number);
        }
        internal static MaintenanceAccessProfile ReadSavedProfile(FamilyInstance source,MaintenanceWorkbenchState state)
            => CreateProfile(source,state,state.Operation,state.Axis,key=>Saved(state,key));
        internal static void ApplyCandidate(MaintenanceWorkbenchState state,MaintenanceFeatureCandidate candidate)
        {
            if(candidate?.CommandPoint==null||candidate.Axis==null)throw new ArgumentException("Repère incomplet.");
            XYZ z=candidate.Axis.Normalize(),reference=Math.Abs(z.Z)<.9?XYZ.BasisZ:XYZ.BasisX;
            var frame=Transform.Identity;frame.Origin=candidate.CommandPoint;
            XYZ x=candidate.Frame?.BasisX;
            if(x!=null)x=x-z*x.DotProduct(z);
            frame.BasisX=x!=null&&x.GetLength()>1e-6?x.Normalize():reference.CrossProduct(z).Normalize();
            frame.BasisY=z.CrossProduct(frame.BasisX).Normalize();frame.BasisZ=z;
            state.CommandPoint=candidate.CommandPoint;state.DetectedFrame=frame;state.Axis=6;
            state.FootPoint=null;state.SupportVerified=false;state.Confirmed=false;state.Measured=false;state.Manufacturer=false;
            state.PostureWarnings.Clear();state.PlacementDiagnostics.Clear();state.BlockingObstacles.Clear();state.RejectedFoot=null;
            ClearGestureState(state);
            state.DetectionNote="Repère géométrique choisi par l’utilisateur ; centre, axe et sens à confirmer.";
            state.FeatureWarnings.Clear();state.FeatureWarnings.Add("Repère choisi manuellement : vérifiez sa fonction, le sens et les cotes avant le contrôle.");
            state.CorrectionApplied=false;state.CorrectionNote="";
            state.RotationAssumed=state.Operation==MaintenanceAccessOperation.LeverValve;
        }
        internal static MaintenanceAccessProfile CreateProfile(Transform t, XYZ fallbackCenter, MaintenanceWorkbenchState state, MaintenanceAccessOperation operation, int axisIndex, Func<string,double> number)
        {
            var p=MaintenanceAccessProfile.For(operation);
            if(axisIndex==6&&state.DetectedFrame==null)throw new ArgumentException("Aucun axe automatique disponible : choisissez un axe ou relancez la détection.");
            var axes=new[] {t.BasisX,-t.BasisX,t.BasisY,-t.BasisY,t.BasisZ,-t.BasisZ};
            XYZ z=axisIndex==6&&state.DetectedFrame!=null?state.DetectedFrame.BasisZ:axes[Math.Max(0,Math.Min(5,axisIndex))];
            z=z.Normalize();
            XYZ reference=new[] {XYZ.BasisX,XYZ.BasisY,XYZ.BasisZ}.OrderBy(a=>Math.Abs(a.DotProduct(z))).First();
            XYZ x=axisIndex==6&&state.DetectedFrame!=null?state.DetectedFrame.BasisX:reference.CrossProduct(z).Normalize();
            XYZ y=z.CrossProduct(x).Normalize();
            p.Frame=Transform.Identity;p.Frame.Origin=state.CommandPoint??fallbackCenter;
            p.Frame.BasisX=x;p.Frame.BasisY=y;p.Frame.BasisZ=z;
            p.Center=XYZ.Zero;p.OperatorOrigin=state.FootPoint;
            p.OperatorPosture=state.OperatorPosture;p.BodyYawDegrees=state.BodyYawDegrees;p.PostureSearch=state.PostureSearch;
            p.HandleRadius=number("Radius")/304.8;p.InitialAngleDegrees=number("InitialAngle");p.RotationDegrees=number("Angle");
            p.HandleThickness=number("Thickness")/304.8;
            p.OperatorHeight=number("Height")/304.8;p.OperatorWidth=number("Width")/304.8;p.OperatorDepth=number("Depth")/304.8;
            p.WorkHeight=number("WorkHeight")/304.8;
            p.GripClearance=number("Grip")/304.8;p.Margin=number("Margin")/304.8;
            p.ChainDrop=number("ChainDrop")/304.8;p.ChainSpacing=number("ChainSpacing")/304.8;p.ChainPullStroke=number("Pull")/304.8;
            p.RemovalSize=new XYZ(number("RX"),number("RY"),number("RZ"))/304.8;
            p.RemovalCenterOffset=new XYZ(number("OffsetX"),number("OffsetY"),number("OffsetZ"))/304.8;
            p.RemovalTravel=number("Travel")/304.8;p.RemovalDirection=XYZ.BasisZ;
            p.Validate();return p;
        }
        private static double Saved(MaintenanceWorkbenchState state,string key)
        {
            if(state.Fields.TryGetValue(key,out string text))
            {
                if(double.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value)
                    ||double.TryParse(text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out value))return value;
                throw new ArgumentException("Valeur incorrecte : "+key);
            }
            switch(key)
            {
                case "Radius":return 200;case "Thickness":return 35;case "Angle":return state.Operation==MaintenanceAccessOperation.LeverValve?90:360;
                case "Height":return 1700;case "WorkHeight":return 1200;case "Width":case "Depth":return 650;case "Grip":return 70;case "Margin":return 10;
                case "ChainDrop":return 2000;case "ChainSpacing":return 400;case "Pull":return 350;
                case "RX":case "RY":return 250;case "RZ":return 500;case "Travel":return 700;
                case "OffsetZ":return -250;default:return 0;
            }
        }
    }
}
