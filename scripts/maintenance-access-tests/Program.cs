using System;
using System.Linq;
using Autodesk.Revit.DB;
using BIMaestro.MepBooster;

internal static class Program
{
    private static int _checks;
    private static double Mm(double value) => value / 304.8;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _checks++; }
    private static bool Near(double x, double y, double tolerance = 1e-8) => Math.Abs(x - y) <= tolerance;
    private static bool Contains(MaintenanceAccessBox box, XYZ world)
    {
        XYZ local = box.Frame.Inverse.OfPoint(world) - box.Center;
        XYZ shift=box.Frame.Inverse.OfVector(box.Translation);double lo=0,hi=1;
        bool Clip(double p,double delta,double half)
        {
            if(Math.Abs(delta)<1e-10)return Math.Abs(p)<=half+1e-8;
            double a=(p-half-1e-8)/delta,b=(p+half+1e-8)/delta;
            lo=Math.Max(lo,Math.Min(a,b));hi=Math.Min(hi,Math.Max(a,b));return lo<=hi;
        }
        return Clip(local.X,shift.X,box.Size.X/2)&&Clip(local.Y,shift.Y,box.Size.Y/2)&&Clip(local.Z,shift.Z,box.Size.Z/2);
    }
    private static MaintenanceAccessProfile Profile(MaintenanceAccessOperation operation)
    {
        var p = MaintenanceAccessProfile.For(operation);
        p.Center = new XYZ(0, 0, Mm(operation == MaintenanceAccessOperation.ChainwheelValve ? 2600 : 1200));
        p.OperatorOrigin = new XYZ(0, -Mm(450), 0);
        return p;
    }
    private static void Throws(Action action, string name)
    { try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception(name); }

    private static void Main()
    {
        foreach (MaintenanceAccessOperation operation in Enum.GetValues(typeof(MaintenanceAccessOperation)))
        {
            var p = Profile(operation);
            var scene = MaintenanceAccessGeometry.Build(p);
            Check(scene.OperatorPlaced && scene.Boxes.Any() && scene.Lines.Any(), operation + ": visible scene");
            Check(scene.CollisionBoxes.All(b => b.Size.X > 0 && b.Size.Y > 0 && b.Size.Z > 0), operation + ": positive collision volumes");
            Check(scene.CollisionBoxes.Any(b => b.Role == MaintenanceAccessRole.HumanArm), operation + ": complete arm movement checked");
            Check(scene.CollisionBoxes.Where(b => b.Role == MaintenanceAccessRole.OperatorSpace).All(b => b.CheckSource), operation + ": operator avoids source body");
            Check(scene.CollisionBoxes.Where(b => b.Role == MaintenanceAccessRole.ExtractionSpace || b.Role == MaintenanceAccessRole.ManipulationSpace).All(b => !b.CheckSource), operation + ": intended contact is explicit");
            var operatorBoxes = scene.CollisionBoxes.Where(b => b.Role == MaintenanceAccessRole.OperatorSpace).ToArray();
            Check(operatorBoxes.Length>=10,operation+": whole articulated body is checked rather than one full-height rectangle");
            Check(operatorBoxes.All(b=>b.BoundsCorners().Min(q => q.Z) >= Mm(1.99)), operation + ": physical body does not penetrate supporting floor");
            for (int i = 0; i <= 96; i++)
            {
                var pose = MaintenanceAccessGeometry.Pose(p, i / 96.0);
                Check(!pose.CollisionBoxes.Any(), operation + ": light animation pose has no swept boxes");
                foreach (var line in pose.Lines.Where(l => l.Role == MaintenanceAccessRole.HumanArm))
                foreach (XYZ joint in line.Points)
                    Check(scene.CollisionBoxes.Any(b => b.Role == MaintenanceAccessRole.HumanArm && Contains(b, joint)), operation + ": swept boxes contain intermediate arm joints");
                Check(scene.CollisionBoxes.Any(b => b.Role == MaintenanceAccessRole.ManipulationSpace && Contains(b, pose.LeftHand)), operation + ": swept hand coverage left");
                Check(scene.CollisionBoxes.Any(b => b.Role == MaintenanceAccessRole.ManipulationSpace && Contains(b, pose.RightHand)), operation + ": swept hand coverage right");
            }
        }

        // Dense points between sampled lever orientations must remain inside the
        // continuously enlarged boxes; endpoint-only tests would miss this.
        var lever = Profile(MaintenanceAccessOperation.LeverValve);
        lever.RotationDegrees = -287;
        var leverScene = MaintenanceAccessGeometry.Build(lever);
        var action = leverScene.CollisionBoxes.Where(b => b.Label == "Course du levier").ToArray();
        for (int i = 0; i <= 1200; i++)
        {
            double a = (lever.InitialAngleDegrees + lever.RotationDegrees * i / 1200.0) * Math.PI / 180;
            XYZ point = lever.WorldCenter + lever.Frame.OfVector(new XYZ(lever.HandleRadius * Math.Cos(a), lever.HandleRadius * Math.Sin(a), 0));
            Check(action.Any(b => Contains(b, point)), "Continuous lever angular coverage including negative rotation");
        }
        // The mechanism frame may be vertical or mirrored; command coordinates
        // remain local while the operator floor point remains in world space.
        var bentLever=Profile(MaintenanceAccessOperation.LeverValve);
        bentLever.InitialAngleDegrees=0;bentLever.RotationDegrees=-90;
        bentLever.HandleRadius=Mm(155);bentLever.HandleThickness=Mm(46);
        bentLever.GripClearance=Mm(2);bentLever.Margin=0;
        var bentSweep=MaintenanceAccessGeometry.Build(bentLever).CollisionBoxes.Where(b=>b.Label=="Course du levier").ToArray();
        // Bounding vertices measured on the detached Antibes handle: the heel
        // reaches20.1mm behind the pivot and the bend23mm below the grip plane.
        foreach(int i in Enumerable.Range(0,361))
        {
            double angle=-i*Math.PI/720;
            var turn=Transform.CreateRotation(XYZ.BasisZ,angle);
            foreach(double x in new[]{Mm(-20.1),Mm(155)})
            foreach(double y in new[]{Mm(-20.1),Mm(20.1)})
            foreach(double z in new[]{Mm(-23),Mm(3)})
            {
                var point=bentLever.WorldCenter+bentLever.Frame.OfVector(turn.OfVector(new XYZ(x,y,z)));
                Check(bentSweep.Any(b=>Contains(b,point)),"Bent small lever heel and tip stay inside continuous sweep with2mm grip clearance");
            }
        }
        var vertical = Profile(MaintenanceAccessOperation.LeverValve);
        vertical.Center = XYZ.Zero;
        vertical.Frame.Origin = new XYZ(Mm(3000), Mm(-1200), Mm(1200));
        vertical.Frame.BasisX = XYZ.BasisX; vertical.Frame.BasisY = XYZ.BasisZ; vertical.Frame.BasisZ = -XYZ.BasisY;
        vertical.OperatorOrigin = vertical.WorldCenter + new XYZ(0, -Mm(450), -Mm(1200));
        foreach (double mirror in new[] { 1.0, -1.0 })
        {
            vertical.Frame.BasisX = XYZ.BasisX * mirror;
            var verticalScene = MaintenanceAccessGeometry.Build(vertical);
            for (int i = 0; i <= 320; i++)
            {
                double a = (vertical.InitialAngleDegrees + vertical.RotationDegrees * i / 320.0) * Math.PI / 180;
                foreach (double x in new[] { 0.0, vertical.HandleRadius })
                foreach (double y in new[] { -vertical.HandleThickness / 2, vertical.HandleThickness / 2 })
                {
                    XYZ rotated = new XYZ(x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a), vertical.HandleThickness / 2);
                    XYZ point = vertical.WorldCenter + vertical.Frame.OfVector(rotated);
                    Check(verticalScene.CollisionBoxes.Any(b => b.Label == "Course du levier" && Contains(b, point)), "Vertical/mirrored frame covers actual lever corners continuously");
                }
            }
            Check((MaintenanceAccessGeometry.Pose(vertical, .5).OperatorFoot - vertical.OperatorOrigin).GetLength() < 1e-8,
                "World floor point is independent of local mechanism frame");
        }

        var filter = Profile(MaintenanceAccessOperation.Filter);
        filter.RemovalDirection = new XYZ(1, .4, .5);
        filter.RemovalTravel = Mm(1500);
        var extraction = MaintenanceAccessGeometry.Build(filter).CollisionBoxes.Single(b => b.Role == MaintenanceAccessRole.ExtractionSpace);
        for (int i = 0; i <= 200; i++)
        {
            var ghost = MaintenanceAccessGeometry.Pose(filter, i / 200.0).Boxes.Single(b => b.Role == MaintenanceAccessRole.EquipmentGhost);
            Check(ghost.Corners().All(q => Contains(extraction, q)), "Continuous diagonal extraction bound contains whole part");
        }
        XYZ foot0 = MaintenanceAccessGeometry.Pose(filter, 0).OperatorFoot, foot1 = MaintenanceAccessGeometry.Pose(filter, 1).OperatorFoot;
        Check(Near(foot0.Z, foot1.Z) && (foot1 - foot0).GetLength() > Mm(500), "Operator can follow horizontal removal without floating vertically");
        var followingBodies=MaintenanceAccessGeometry.Build(filter).CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace).ToArray();
        foreach(double phase in new[]{0.0,.23,.51,.87,1.0})
            foreach(var box in MaintenanceAccessGeometry.Pose(filter,phase).Boxes.Where(b=>b.Role==MaintenanceAccessRole.HumanBody))
                foreach(var point in box.Corners())Check(followingBodies.Any(b=>Contains(b,point)),"Exact component translations cover the moving articulated body throughout withdrawal");
        filter.FollowRemoval = false;
        Check((MaintenanceAccessGeometry.Pose(filter, 0).OperatorFoot - MaintenanceAccessGeometry.Pose(filter, 1).OperatorFoot).GetLength() < 1e-8,
            "Operator can remain fixed during removal");

        Check(!MaintenanceAccessMath.Elbow(.3, .25, .7, out double along, out double bend, out double hand), "Unreachable hand is reported");
        Check(hand < .551 && hand > .549, "Unreachable arm remains its physical length");
        foreach (double d in new[] { .06, .15, .3, .5, .549 })
        {
            Check(MaintenanceAccessMath.Elbow(.3, .25, d, out along, out bend, out hand), "Reachable IK distance");
            Check(Near(along * along + bend * bend, .3 * .3), "Upper arm length is conserved at keyframes");
            Check(Near((hand - along) * (hand - along) + bend * bend, .25 * .25), "Forearm length is conserved at keyframes");
        }
        Check(Near(MaintenanceAccessMath.Pull(0), 0) && Near(MaintenanceAccessMath.Pull(.5), 1) && Near(MaintenanceAccessMath.Pull(1), 0), "Chain pulling returns smoothly");
        var chain = Profile(MaintenanceAccessOperation.ChainwheelValve);
        Check((MaintenanceAccessGeometry.Pose(chain, 0).LeftHand - MaintenanceAccessGeometry.Pose(chain, .5).LeftHand).GetLength() > Mm(100), "Chain hand performs a real vertical stroke");
        Check(MaintenanceAccessGeometry.Build(chain).Warnings.Any(w => w.Contains("roue verticale")), "Horizontal chainwheel plane is flagged instead of accepted as valid");
        chain.Frame.BasisX = XYZ.BasisX; chain.Frame.BasisY = XYZ.BasisZ; chain.Frame.BasisZ = -XYZ.BasisY;
        Check(!MaintenanceAccessGeometry.Build(chain).Warnings.Any(w => w.Contains("roue verticale")), "Vertical chainwheel plane satisfies the explicit model assumption");
        chain.ChainSpacing = 2 * chain.HandleRadius + Mm(30);
        Check(MaintenanceAccessGeometry.Build(chain).Warnings.Any(w => w.Contains("diamètre")), "Branches outside the represented chainwheel are flagged");
        chain.ChainSpacing = 2 * chain.HandleRadius;
        Check(!MaintenanceAccessGeometry.Build(chain).Warnings.Any(w => w.Contains("diamètre")), "Branch spacing equal to diameter is consistent");
        var physicalChain=MaintenanceAccessGeometry.Build(chain);
        var strands=physicalChain.CollisionBoxes.Where(b=>b.Label.StartsWith("Brin physique")).ToArray();
        Check(strands.Length==2&&strands.All(b=>Near(b.Size.X,Mm(16)+2*chain.Margin)&&Near(b.Size.Y,Mm(16)+2*chain.Margin)),
            "Chain is checked as two physical narrow strands rather than a full hand-clearance curtain");
        var highGap=chain.WorldCenter-XYZ.BasisZ*(chain.ChainDrop*.4);
        Check(!physicalChain.CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.ManipulationSpace).Any(b=>Contains(b,highGap)),
            "Empty space between high chain strands is not falsely occupied by hands");
        var wheelVolume=physicalChain.CollisionBoxes.Single(b=>b.Label=="Rotation géométrique de la roue de chaîne");
        Check(Near(wheelVolume.Size.X,2*(chain.HandleRadius+chain.Margin)),"A high chainwheel receives only its physical rotation margin, not70mm of hand clearance");
        var unplaced = Profile(MaintenanceAccessOperation.HandwheelValve); unplaced.OperatorOrigin = null;
        Check(!MaintenanceAccessGeometry.Build(unplaced).OperatorPlaced && MaintenanceAccessGeometry.Build(unplaced).Warnings.Any(), "Unconfirmed operator position is explicit");
        var narrowBody = Profile(MaintenanceAccessOperation.HandwheelValve);
        narrowBody.OperatorHeight = Mm(1700); narrowBody.OperatorWidth = Mm(400); narrowBody.OperatorDepth = Mm(200);
        var narrowScene = MaintenanceAccessGeometry.Build(narrowBody);
        var bodyVolumes = narrowScene.CollisionBoxes.Where(b => b.Role == MaintenanceAccessRole.OperatorSpace).ToArray();
        Check(bodyVolumes.Single(b=>b.Label.StartsWith("Buste")).Size.X >= .31 * narrowBody.OperatorHeight + 2 * narrowBody.Margin - 1e-8,
            "A 400mm operator width cannot be smaller than the 1700mm mannequin torso");
        Check(bodyVolumes.Single(b=>b.Label.StartsWith("Buste")).Size.Y >= .17 * narrowBody.OperatorHeight + 2 * narrowBody.Margin - 1e-8,
            "A 200mm comfort depth cannot shrink the actual represented torso");
        Check(narrowScene.Lines.Any(l=>l.Label=="Confort indicatif")&&narrowScene.Assumptions.Any(w=>w.Contains("confort")),
            "Comfort rectangle remains explicit and separate from the physical body");
        foreach (double phase in new[] { 0.0, .37, 1.0 })
        {
            var bodyPose = MaintenanceAccessGeometry.Pose(narrowBody, phase);
            foreach (var bodyBox in bodyPose.Boxes.Where(b => b.Role == MaintenanceAccessRole.HumanBody))
            foreach (XYZ corner in bodyBox.Corners())
                Check(bodyVolumes.Any(b=>Contains(b, corner)), "Physical components contain every represented body-box corner");
            foreach (var line in bodyPose.Lines.Where(l => l.Role == MaintenanceAccessRole.HumanBody))
            foreach (XYZ joint in line.Points)
            foreach (XYZ axis in new[] { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ })
            foreach (double sign in new[] { -1.0, 1.0 })
                Check(bodyVolumes.Any(b=>Contains(b, joint + axis * (line.Thickness / 2 * sign))),
                    "Physical leg components contain rounded joint extents and floor contact");
        }
        var standing=Profile(MaintenanceAccessOperation.Filter);standing.Center=new XYZ(0,0,Mm(650));standing.OperatorOrigin=new XYZ(0,-Mm(250),0);
        standing.RemovalTravel=Mm(250);standing.FollowRemoval=false;
        var upright=MaintenanceAccessGeometry.Build(standing);
        standing.OperatorPosture=MaintenanceOperatorPosture.Crouched;
        var crouched=MaintenanceAccessGeometry.Build(standing);
        Check(!upright.Reachable&&crouched.Reachable,"A low withdrawal can be reachable crouched without inventing longer arms");
        Check(crouched.CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace).All(b=>b.BoundsCorners().Max(q=>q.Z)<Mm(1400)),
            "Articulated crouch fits under a1400mm overhead obstruction");
        Check(upright.CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace).Any(b=>b.BoundsCorners().Max(q=>q.Z)>Mm(1400)),
            "Standing posture cannot pretend to fit below the same overhead obstruction");
        Check(crouched.ConstraintNotes.Any(w=>w.Contains("accroupie"))&&!crouched.Warnings.Any(w=>w.Contains("accroupie")),
            "Crouch constraint is separate from unknown geometry and reachability");
        foreach(var posture in new[]{MaintenanceOperatorPosture.Standing,MaintenanceOperatorPosture.Crouched})
        {
            standing.OperatorPosture=posture;var pose=MaintenanceAccessGeometry.Pose(standing,0);
            foreach(var leg in pose.Lines.Where(l=>l.Label.StartsWith("Jambe")))
            {
                Check(Near((leg.Points[1]-leg.Points[0]).GetLength(),standing.OperatorHeight*.225),"Femur length is conserved in both postures");
                Check(Near((leg.Points[2]-leg.Points[1]).GetLength(),standing.OperatorHeight*.225),"Shin length is conserved in both postures");
            }
        }
        var profileBody=Profile(MaintenanceAccessOperation.HandwheelValve);profileBody.BodyYawDegrees=90;
        var sideBodies=MaintenanceAccessGeometry.Build(profileBody).CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace).SelectMany(b=>b.BoundsCorners()).ToArray();
        double corridor=(sideBodies.Max(q=>q.X)+sideBodies.Min(q=>q.X))/2;
        Check(sideBodies.All(q=>Math.Abs(q.X-corridor)<Mm(200)),"A full real profile posture including boots fits a400mm test corridor");
        profileBody.BodyYawDegrees=0;
        Check(MaintenanceAccessGeometry.Build(profileBody).CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace).SelectMany(b=>b.BoundsCorners()).Any(q=>Math.Abs(q.X-corridor)>Mm(200)),
            "The same full body facing forward cannot fit the400mm corridor");
        profileBody.BodyYawDegrees=90;var sideScene=MaintenanceAccessGeometry.Build(profileBody);
        Check(sideScene.ConstraintNotes.Any(w=>w.Contains("tourné")),"Profile geometry keeps an explicit constrained-posture note without claiming an ergonomic standard");
        var unreachable = Profile(MaintenanceAccessOperation.HandwheelValve); unreachable.OperatorOrigin = new XYZ(0, -Mm(2000), 0);
        Check(!MaintenanceAccessGeometry.Build(unreachable).Reachable, "Insufficient human reach is explicit");
        var bad = Profile(MaintenanceAccessOperation.Filter); bad.RemovalDirection = new XYZ(double.NaN, 0, 0);
        Throws(() => MaintenanceAccessGeometry.Build(bad), "NaN removal direction rejected");
        bad = Profile(MaintenanceAccessOperation.Filter); bad.Frame.BasisX = new XYZ(2, 0, 0);
        Throws(() => MaintenanceAccessGeometry.Build(bad), "Scaled service frame rejected");
        Throws(() => MaintenanceAccessMath.ClampPhase(double.NaN), "NaN animation phase rejected");
        var extreme = Profile(MaintenanceAccessOperation.Filter); extreme.Center = new XYZ(double.PositiveInfinity, 0, 0);
        Throws(() => MaintenanceAccessGeometry.Build(extreme), "Infinite command point rejected");
        extreme = Profile(MaintenanceAccessOperation.Filter); extreme.Operation = (MaintenanceAccessOperation)999;
        Throws(() => MaintenanceAccessGeometry.Build(extreme), "Unknown operation rejected");
        var callbacks = new MaintenanceAccessCallbackGuard();
        foreach (Exception failure in new Exception[] { new InvalidOperationException("UI callback"), new ArgumentException("UI callback"), new OperationCanceledException() })
        {
            try { callbacks.Report(_ => throw failure, "progress"); throw new Exception("A callback error must propagate"); }
            catch (Exception actual) { Check(ReferenceEquals(actual, failure) && callbacks.IsCallbackFailure(actual), "Progress callback exception propagates unchanged and is not geometry unknown"); }
            try { callbacks.CheckCanceled(() => throw failure); throw new Exception("A canceled callback error must propagate"); }
            catch (Exception actual) { Check(ReferenceEquals(actual, failure) && callbacks.IsCallbackFailure(actual), "Cancellation callback exception propagates unchanged"); }
        }
        Check(!callbacks.IsCallbackFailure(new InvalidOperationException("Native Boolean failure")), "Native geometry errors remain distinguishable from callback errors");
        try { callbacks.CheckCanceled(() => true); throw new Exception("Cancellation must stop scanning"); }
        catch (OperationCanceledException) { Check(true, "Cancellation request stops immediately"); }
        callbacks.CheckCanceled(null); callbacks.CheckCanceled(() => false); callbacks.Report(null, "unused");
        Check(true, "Absent callbacks and false cancellation are harmless");
        _checks += FeatureRecognitionTests.Run();
        foreach(MaintenanceAccessOperation operation in Enum.GetValues(typeof(MaintenanceAccessOperation)))
        {
            var original=MaintenanceAccessProfile.For(operation);original.Frame.Origin=new XYZ(99999,77777,125);
            original.Center=new XYZ(3,4,5);original.OperatorOrigin=null;
            var demonstration=MaintenanceDemonstration.Create(original,false);
            Check(original.OperatorOrigin==null&&original.Frame.Origin.X==99999&&original.Center.X==3,"Demonstration never creates an actual source work position");
            Check(Math.Abs(demonstration.OperatorOrigin.Z)<1e-8&&demonstration.WorldCenter.GetLength()<40,"Demonstration uses a detached floor at zero, not model coordinates");
            var scene=MaintenanceAccessGeometry.Build(demonstration);
            Check(scene.OperatorPlaced&&scene.Boxes.Any(b=>b.Role==MaintenanceAccessRole.HumanBody),"Every demonstration has a visible operator");
            if(operation==MaintenanceAccessOperation.Filter||operation==MaintenanceAccessOperation.Actuator)
            {
                var first=MaintenanceAccessGeometry.Pose(demonstration,0).Boxes.Single(b=>b.Role==MaintenanceAccessRole.EquipmentGhost);
                var last=MaintenanceAccessGeometry.Pose(demonstration,1).Boxes.Single(b=>b.Role==MaintenanceAccessRole.EquipmentGhost);
                Check(Near((last.Frame.OfPoint(last.Center)-first.Frame.OfPoint(first.Center)).GetLength(),demonstration.RemovalTravel),"Demonstration removal actually moves through the whole configured travel");
            }
            if(operation==MaintenanceAccessOperation.ChainwheelValve||operation==MaintenanceAccessOperation.Filter)
                Check(scene.Reachable,operation+": default teaching posture remains within the displayed person's reach");
        }
        Console.WriteLine("Maintenance geometry/features: " + _checks + " checks passed (pure geometry substitutes; native Revit Booleans not exercised).");
    }
}
