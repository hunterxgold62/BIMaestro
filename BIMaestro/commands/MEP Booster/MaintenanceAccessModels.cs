using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal enum MaintenanceAccessOperation { LeverValve, HandwheelValve, ChainwheelValve, Actuator, Filter }
    internal enum MaintenanceOperatorPosture { Standing, Crouched }
    internal enum MaintenancePostureSearch { Auto, Standing, SidewaysLeft, SidewaysRight, Crouched }
    internal enum MaintenanceAccessRole { OperatorSpace, ManipulationSpace, ExtractionSpace, EquipmentGhost, HumanBody, HumanArm, Handle, Chain, Reference }

    internal sealed class MaintenanceAccessProfile
    {
        internal MaintenanceAccessOperation Operation;
        // Frame orients the service mechanism. Center is local to Frame; it is
        // the command/cap point, not an automatically inferred equipment centre.
        internal Transform Frame = Transform.Identity;
        internal XYZ Center = XYZ.Zero;
        internal XYZ OperatorOrigin; // WORLD foot point, preferably picked on the floor.
        internal MaintenanceOperatorPosture OperatorPosture;
        internal MaintenancePostureSearch PostureSearch = MaintenancePostureSearch.Auto;
        internal double BodyYawDegrees;
        internal XYZ BodyFootWorld { get => OperatorOrigin; set => OperatorOrigin = value; }
        internal double HandleRadius = Mm(200), HandleThickness = Mm(35), GripClearance = Mm(70);
        internal double InitialAngleDegrees, RotationDegrees = 360;
        internal double OperatorHeight = Mm(1700), OperatorWidth = Mm(650), OperatorDepth = Mm(650);
        internal double OperatorDistance = Mm(450), WorkHeight = Mm(1200);
        internal double ChainDrop = Mm(2000), ChainSpacing = Mm(400), ChainPullStroke = Mm(350);
        internal XYZ RemovalSize = new XYZ(Mm(250), Mm(250), Mm(500));
        internal XYZ RemovalCenterOffset = XYZ.Zero;
        internal XYZ RemovalDirection = XYZ.BasisZ; // LOCAL to Frame; the geometry normalizes it in world coordinates.
        internal double RemovalTravel = Mm(700), Margin = Mm(10);
        internal bool FollowRemoval = true;
        internal XYZ WorldCenter => Frame.OfPoint(Center);
        internal static double Mm(double value) => value / 304.8;
        internal static MaintenanceAccessProfile For(MaintenanceAccessOperation operation)
        {
            var profile = new MaintenanceAccessProfile { Operation = operation };
            if (operation == MaintenanceAccessOperation.LeverValve)
            { profile.InitialAngleDegrees = -135; profile.RotationDegrees = 90; profile.HandleRadius = Mm(250); }
            if (operation == MaintenanceAccessOperation.Filter)
                profile.RemovalCenterOffset = new XYZ(0, 0, -profile.RemovalSize.Z / 2);
            if (operation == MaintenanceAccessOperation.Actuator)
            { profile.RemovalSize = new XYZ(Mm(300), Mm(300), Mm(300)); profile.RemovalTravel = Mm(450); }
            return profile;
        }
        internal void Validate()
        {
            if (Frame == null || Center == null || RemovalSize == null || RemovalCenterOffset == null || RemovalDirection == null)
                throw new ArgumentException("Le repère, le point de commande et le volume de dépose doivent être renseignés.");
            bool Finite(XYZ point) => point != null && new[] { point.X, point.Y, point.Z }.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
            if (!Enum.IsDefined(typeof(MaintenanceAccessOperation), Operation) || !Enum.IsDefined(typeof(MaintenanceOperatorPosture), OperatorPosture)
                || !Enum.IsDefined(typeof(MaintenancePostureSearch), PostureSearch) || double.IsNaN(BodyYawDegrees) || double.IsInfinity(BodyYawDegrees)
                || !Finite(Center) || !Finite(RemovalCenterOffset)
                || !Finite(RemovalDirection) || !Finite(Frame.Origin) || !Finite(Frame.BasisX) || !Finite(Frame.BasisY) || !Finite(Frame.BasisZ)
                || (OperatorOrigin != null && !Finite(OperatorOrigin)))
                throw new ArgumentException("Les points, directions et l’opération doivent être valides et finis.");
            double[] positive = { HandleRadius, HandleThickness, GripClearance, OperatorHeight, OperatorWidth, OperatorDepth,
                OperatorDistance, WorkHeight, ChainDrop, ChainSpacing, ChainPullStroke, RemovalSize.X, RemovalSize.Y, RemovalSize.Z, RemovalTravel };
            if (positive.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < Mm(2) || v > Mm(20000)))
                throw new ArgumentException("Les dimensions de maintenance doivent être finies et comprises entre 2 et 20 000 mm.");
            if (double.IsNaN(Margin) || double.IsInfinity(Margin) || Margin < 0 || Margin > Mm(1000))
                throw new ArgumentException("La marge doit être comprise entre 0 et 1 000 mm.");
            if (double.IsNaN(InitialAngleDegrees) || double.IsInfinity(InitialAngleDegrees)
                || double.IsNaN(RotationDegrees) || double.IsInfinity(RotationDegrees) || Math.Abs(RotationDegrees) > 720)
                throw new ArgumentException("La rotation doit être finie et comprise entre −720° et +720°.");
            if (RemovalDirection.GetLength() < 1e-8) throw new ArgumentException("La direction de dépose est indéterminée.");
            if (Math.Abs(Frame.BasisX.GetLength() - 1) > 1e-6 || Math.Abs(Frame.BasisY.GetLength() - 1) > 1e-6
                || Math.Abs(Frame.BasisZ.GetLength() - 1) > 1e-6 || Math.Abs(Frame.BasisX.DotProduct(Frame.BasisY)) > 1e-6
                || Math.Abs(Frame.BasisX.DotProduct(Frame.BasisZ)) > 1e-6 || Math.Abs(Frame.BasisY.DotProduct(Frame.BasisZ)) > 1e-6)
                throw new ArgumentException("Le repère de maintenance doit être orthonormé, sans facteur d’échelle.");
        }
    }

    internal sealed class MaintenanceAccessBox
    {
        internal string Label;
        internal MaintenanceAccessRole Role;
        internal Transform Frame;
        internal XYZ Center, Size;
        internal bool CheckCollision;
        // The operator body must also avoid the selected valve/filter body.
        // Manipulation and extraction exclude it: intended contact/internal mechanics
        // cannot be distinguished reliably in an arbitrary Revit family.
        internal bool CheckSource;
        // Body, arms and hand grips move when a different operator post is tried.
        // Mechanism and withdrawal envelopes remain independent of that post.
        internal bool DependsOnOperator;
        internal XYZ Translation = XYZ.Zero;
        internal XYZ[] Corners()
        {
            var half = Size / 2;
            return Enumerable.Range(0, 8).Select(i => Frame.OfPoint(Center + new XYZ((i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z))).ToArray();
        }
        internal MotorEnvelope ToEnvelope() => new MotorEnvelope { Frame = Frame, Center = Center, Size = Size, Axis = 0, Sign = 1, Margin = 0, Travel = 0 };
        internal XYZ[] BoundsCorners() => Corners().Concat(Corners().Select(p => p + Translation)).ToArray();
        internal IList<Solid> CollisionParts()
        {
            var envelope = ToEnvelope();
            double length = Translation.GetLength();
            if (length > 1e-9) envelope.TranslationDirection = Translation / length;
            return envelope.SweepParts(length);
        }
    }
    internal sealed class MaintenanceAccessLine
    {
        internal string Label;
        internal MaintenanceAccessRole Role;
        internal XYZ[] Points;
        internal double Thickness = MaintenanceAccessProfile.Mm(25);
    }
    internal sealed class MaintenanceAccessMotion
    {
        internal string Label, Kind;
        internal XYZ Origin, Axis, Start, End;
        internal double Degrees;
    }
    internal sealed class MaintenanceAccessScene
    {
        internal readonly List<MaintenanceAccessBox> Boxes = new List<MaintenanceAccessBox>();
        internal readonly List<MaintenanceAccessLine> Lines = new List<MaintenanceAccessLine>();
        internal readonly List<MaintenanceAccessMotion> Motions = new List<MaintenanceAccessMotion>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Assumptions = new List<string>();
        internal readonly List<string> ConstraintNotes = new List<string>();
        internal IEnumerable<MaintenanceAccessBox> CollisionBoxes => Boxes.Where(b => b.CheckCollision);
        internal bool OperatorPlaced, Reachable = true;
        internal XYZ LeftHand, RightHand, OperatorFoot;
    }

    internal sealed class MaintenanceAccessObstacle
    {
        internal string Label, VolumeLabel, LinkName;
        internal MaintenanceAccessRole Role;
        internal long ElementId, LinkId = -1;
        internal XYZ[] Box;
    }
    internal sealed class MaintenanceAccessReport
    {
        internal readonly List<MaintenanceAccessObstacle> Obstacles = new List<MaintenanceAccessObstacle>();
        internal readonly List<string> Unknown = new List<string>();
        internal int CheckedVolumes;
        internal bool Complete => Unknown.Count == 0;
    }
}
