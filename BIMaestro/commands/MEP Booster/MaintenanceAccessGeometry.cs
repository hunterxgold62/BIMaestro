using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    // Explicit, editable service geometry. Nothing here identifies an operation
    // from a family name, and none of the starting dimensions is a standard.
    internal static class MaintenanceAccessGeometry
    {
        private const int PoseIntervals = 48;
        private static long _humanPoseEvaluations, _preparedMotions, _preparedPoseUpdates;
        internal static long HumanPoseEvaluationCount => Interlocked.Read(ref _humanPoseEvaluations);
        internal static long PreparedMotionCount => Interlocked.Read(ref _preparedMotions);
        internal static long PreparedPoseUpdateCount => Interlocked.Read(ref _preparedPoseUpdates);
        private static double Mm(double value) => MaintenanceAccessProfile.Mm(value);
        internal sealed class HumanPose
        {
            internal XYZ Foot, LeftShoulder, RightShoulder, LeftElbow, RightElbow, LeftWrist, RightWrist;
            internal XYZ Hip, Shoulder, Head, LeftHip, RightHip, LeftKnee, RightKnee, LeftAnkle, RightAnkle;
            internal bool Reachable;
        }

        // Detached, immutable-by-contract movement snapshot. Recreate after any
        // profile edit; animation and collision build share these exact samples.
        internal sealed class PreparedMotion
        {
            internal readonly MaintenanceAccessProfile Profile;
            internal readonly Transform Mechanism, Human;
            internal readonly HumanPose[] Poses;
            internal readonly bool Reachable;
            internal double PreparationMilliseconds { get; private set; }
            internal int SampleCount => Poses.Length;
            internal PreparedMotion(MaintenanceAccessProfile source)
            {
                long started = Stopwatch.GetTimestamp();
                source.Validate();
                var frame = Transform.Identity;
                frame.Origin=source.Frame.Origin;frame.BasisX=source.Frame.BasisX;frame.BasisY=source.Frame.BasisY;frame.BasisZ=source.Frame.BasisZ;
                Profile = new MaintenanceAccessProfile {
                    Operation=source.Operation,Frame=frame,Center=source.Center,OperatorOrigin=source.OperatorOrigin,
                    OperatorPosture=source.OperatorPosture,PostureSearch=source.PostureSearch,BodyYawDegrees=source.BodyYawDegrees,
                    HandleRadius=source.HandleRadius,HandleThickness=source.HandleThickness,GripClearance=source.GripClearance,
                    InitialAngleDegrees=source.InitialAngleDegrees,RotationDegrees=source.RotationDegrees,
                    OperatorHeight=source.OperatorHeight,OperatorWidth=source.OperatorWidth,OperatorDepth=source.OperatorDepth,
                    OperatorDistance=source.OperatorDistance,WorkHeight=source.WorkHeight,ChainDrop=source.ChainDrop,
                    ChainSpacing=source.ChainSpacing,ChainPullStroke=source.ChainPullStroke,RemovalSize=source.RemovalSize,
                    RemovalCenterOffset=source.RemovalCenterOffset,RemovalDirection=source.RemovalDirection,
                    RemovalTravel=source.RemovalTravel,Margin=source.Margin,FollowRemoval=source.FollowRemoval };
                Mechanism=At(Profile.WorldCenter,frame.BasisX,frame.BasisY,frame.BasisZ);
                XYZ foot=Profile.OperatorOrigin??ProposedFoot(Profile);
                Human=OperatorFrame(Profile,foot);
                Poses=new HumanPose[PoseIntervals+1];
                bool reachable=true;
                for(int i=0;i<Poses.Length;i++)
                {Poses[i]=GetHumanPose(Profile,Human,foot,i/(double)PoseIntervals);reachable&=Poses[i].Reachable;}
                Reachable=reachable;
                Interlocked.Increment(ref _preparedMotions);
                PreparationMilliseconds=(Stopwatch.GetTimestamp()-started)*1000.0/Stopwatch.Frequency;
            }
        }

        internal static PreparedMotion Prepare(MaintenanceAccessProfile profile)
            => new PreparedMotion(profile??throw new ArgumentNullException(nameof(profile)));

        internal static MaintenanceAccessScene Build(MaintenanceAccessProfile profile, double phase = 0)
            => BuildCore(Prepare(profile), phase, true);
        internal static MaintenanceAccessScene BuildPrepared(PreparedMotion motion, double phase = 0)
            => BuildCore(motion, phase, true);

        // The animation uses the same piecewise-linear joint paths covered by
        // Build's collision boxes. It does not regenerate all the swept volumes.
        internal static MaintenanceAccessScene Pose(MaintenanceAccessProfile profile, double phase)
            => BuildCore(Prepare(profile), phase, false);
        internal static MaintenanceAccessScene PosePrepared(PreparedMotion motion, double phase)
        {
            Interlocked.Increment(ref _preparedPoseUpdates);
            return BuildCore(motion, phase, false);
        }

        private static MaintenanceAccessScene BuildCore(PreparedMotion motion, double phase, bool swept)
        {
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            var p=motion.Profile;
            phase = MaintenanceAccessMath.ClampPhase(phase);
            var scene = new MaintenanceAccessScene { OperatorPlaced = p.OperatorOrigin != null };
            Transform mechanism=motion.Mechanism,human=motion.Human;
            AddMechanism(scene, p, mechanism, phase, swept);
            var poses=motion.Poses;
            double position = phase * PoseIntervals;
            int first = Math.Min(PoseIntervals - 1, (int)position);
            HumanPose current = Interpolate(poses[first], poses[first + 1], position - first);
            scene.Reachable = motion.Reachable;
            scene.OperatorFoot = current.Foot;
            scene.LeftHand = current.LeftWrist;
            scene.RightHand = current.RightWrist;
            DrawHuman(scene, p, human, current);
            var comfort=At(current.Foot+XYZ.BasisZ*Mm(20),human.BasisX,human.BasisY,XYZ.BasisZ);
            AddLine(scene,"Confort indicatif",MaintenanceAccessRole.Reference,Mm(3),new[]{new XYZ(-p.OperatorWidth/2,-p.OperatorDepth/2,0),
                new XYZ(p.OperatorWidth/2,-p.OperatorDepth/2,0),new XYZ(p.OperatorWidth/2,p.OperatorDepth/2,0),
                new XYZ(-p.OperatorWidth/2,p.OperatorDepth/2,0),new XYZ(-p.OperatorWidth/2,-p.OperatorDepth/2,0)}.Select(comfort.OfPoint).ToArray());
            if (swept)
            {
                AddHumanSweeps(scene, p, human, poses);
                scene.Assumptions.Add("Dimensions initiales réglables : hypothèses de visualisation, pas des cotes imposées par une norme.");
                scene.Assumptions.Add("Les volumes d’action et les bras excluent la famille sélectionnée et ses sous-composants : contacts voulus, assemblage interne et démontabilité mécanique restent à confirmer.");
                scene.Assumptions.Add("Le volume de l’opérateur contrôle aussi le corps de l’équipement sélectionné. Les appuis et la portée sont vérifiés séparément pour le poste réel ; la démonstration utilise un sol et une position illustratifs.");
                scene.Assumptions.Add("Les segments du mannequin sont interpolés entre 49 postures ; les enveloppes de contrôle couvrent en continu cette animation simplifiée, avec des volumes conservateurs.");
                scene.Assumptions.Add("Effort, poids, outillage, consignation, accès par échelle et moyens de levage ne sont pas validés par ce contrôle géométrique.");
                if (!scene.OperatorPlaced) scene.Warnings.Add("Position de l’opérateur proposée : désignez un point réel sur le sol avant de conclure.");
                if (!scene.Reachable) scene.Warnings.Add("Une partie du mouvement dépasse la portée du mannequin réglé. Rapprochez l’opérateur ou prévoyez un moyen d’accès adapté.");
            }
            return scene;
        }

        private static XYZ ProposedFoot(MaintenanceAccessProfile p)
        {
            XYZ side = Horizontal(p.Frame.BasisZ);
            if (side.GetLength() < 1e-6) side = Horizontal(-p.Frame.BasisY);
            if (side.GetLength() < 1e-6) side = -XYZ.BasisY;
            double down = p.Operation == MaintenanceAccessOperation.ChainwheelValve ? p.ChainDrop + Mm(200) : p.WorkHeight;
            return p.WorldCenter + side.Normalize() * p.OperatorDistance - XYZ.BasisZ * down;
        }

        private static Transform HumanFrame(XYZ foot, XYZ command, Transform fallback)
        {
            XYZ facing = Horizontal(command - foot);
            if (facing.GetLength() < 1e-6) facing = Horizontal(fallback.BasisY);
            if (facing.GetLength() < 1e-6) facing = XYZ.BasisY;
            facing = facing.Normalize();
            return At(foot, facing.CrossProduct(XYZ.BasisZ), facing, XYZ.BasisZ);
        }
        internal static Transform OperatorFrame(MaintenanceAccessProfile p, XYZ foot)
        {
            var frame = HumanFrame(foot, p.WorldCenter, p.Frame);
            var rotation = Transform.CreateRotation(XYZ.BasisZ, p.BodyYawDegrees * Math.PI / 180);
            return At(foot, rotation.OfVector(frame.BasisX), rotation.OfVector(frame.BasisY), XYZ.BasisZ);
        }
        internal static double ShoulderHeight(MaintenanceAccessProfile p)
            => p.OperatorHeight*(p.OperatorPosture==MaintenanceOperatorPosture.Crouched?.56:.81);

        private static void AddMechanism(MaintenanceAccessScene scene, MaintenanceAccessProfile p, Transform frame, double phase, bool swept)
        {
            double angle = (p.InitialAngleDegrees + phase * p.RotationDegrees) * Math.PI / 180;
            if (p.Operation == MaintenanceAccessOperation.LeverValve)
            {
                Transform lever = Rotated(frame, angle);
                // The mounting end can extend behind its pivot (flat/canted
                // handles). Keep that heel in both the visible approximation
                // and the continuous sweep, even with very small grip margins.
                double heel=p.HandleThickness/2,centerX=(p.HandleRadius-heel)/2,length=p.HandleRadius+heel;
                AddBox(scene, "Levier", MaintenanceAccessRole.Handle, lever, new XYZ(centerX, 0, 0),
                    new XYZ(length, p.HandleThickness, p.HandleThickness), false);
                if (swept)
                {
                    int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(p.RotationDegrees) / 5.625));
                    double step = p.RotationDegrees * Math.PI / 180 / count;
                    double radius = Math.Sqrt(Math.Pow(Math.Max(p.HandleRadius,heel) + p.GripClearance, 2)
                        + Math.Pow(p.HandleThickness / 2 + p.GripClearance, 2));
                    double padding = p.GripClearance + p.Margin + MaintenanceAccessMath.RotationInflation(radius, step);
                    for (int i = 0; i < count; i++)
                        AddBox(scene, "Course du levier", MaintenanceAccessRole.ManipulationSpace,
                            Rotated(frame, p.InitialAngleDegrees * Math.PI / 180 + (i + .5) * step),
                            new XYZ(centerX, 0, 0),
                            new XYZ(length + 2 * padding, p.HandleThickness + 2 * padding, p.HandleThickness + 2 * padding), true);
                    scene.Assumptions.Add("Course du levier : boîtes tournées et élargies par une borne continue de déplacement entre positions ; elles peuvent signaler un voisin proche sans contact réel.");
                }
                scene.Motions.Add(new MaintenanceAccessMotion { Label = "Rotation du levier", Kind = "rotation", Origin = frame.Origin,
                    Axis = frame.BasisZ, Degrees = p.RotationDegrees });
            }
            else if (p.Operation == MaintenanceAccessOperation.HandwheelValve || p.Operation == MaintenanceAccessOperation.ChainwheelValve)
            {
                DrawWheel(scene, p, frame, angle);
                if (swept)
                    AddBox(scene, p.Operation==MaintenanceAccessOperation.ChainwheelValve?"Rotation géométrique de la roue de chaîne":"Espace de prise autour du volant",
                        MaintenanceAccessRole.ManipulationSpace, frame, XYZ.Zero,
                        new XYZ(2 * (p.HandleRadius + (p.Operation==MaintenanceAccessOperation.ChainwheelValve?0:p.GripClearance) + p.Margin),
                            2 * (p.HandleRadius + (p.Operation==MaintenanceAccessOperation.ChainwheelValve?0:p.GripClearance) + p.Margin),
                            p.HandleThickness + 2 * ((p.Operation==MaintenanceAccessOperation.ChainwheelValve?0:p.GripClearance) + p.Margin)), true);
                scene.Motions.Add(new MaintenanceAccessMotion { Label = p.Operation == MaintenanceAccessOperation.ChainwheelValve ? "Volant commandé par chaîne" : "Rotation avec reprises de prise",
                    Kind = "rotation", Origin = frame.Origin, Axis = frame.BasisZ, Degrees = p.RotationDegrees });
                if (p.Operation == MaintenanceAccessOperation.ChainwheelValve)
                {
                    XYZ tangent = Horizontal(frame.BasisX);
                    if (tangent.GetLength() < 1e-6) tangent = Horizontal(frame.BasisY);
                    if (tangent.GetLength() < 1e-6) tangent = XYZ.BasisX;
                    tangent = tangent.Normalize();
                    XYZ left = frame.Origin - tangent * p.ChainSpacing / 2, right = frame.Origin + tangent * p.ChainSpacing / 2;
                    scene.Lines.Add(new MaintenanceAccessLine { Label = "Chaîne : deux brins verticaux", Role = MaintenanceAccessRole.Chain,
                        Points = new[] { left, left - XYZ.BasisZ * p.ChainDrop, right - XYZ.BasisZ * p.ChainDrop, right }, Thickness = Mm(16) });
                    if (swept)
                    {
                        double width = Mm(16)+2*p.Margin;
                        foreach(var strand in new[]{left,right})
                            AddBox(scene,"Brin physique de chaîne — section représentée16mm",MaintenanceAccessRole.ManipulationSpace,
                                At(strand-XYZ.BasisZ*p.ChainDrop/2,tangent,XYZ.BasisZ.CrossProduct(tangent),XYZ.BasisZ),XYZ.Zero,
                                new XYZ(width,width,p.ChainDrop+2*p.Margin),true);
                        AddBox(scene,"Boucle basse de chaîne — liaison représentée",MaintenanceAccessRole.ManipulationSpace,
                            At(frame.Origin-XYZ.BasisZ*p.ChainDrop,tangent,XYZ.BasisZ.CrossProduct(tangent),XYZ.BasisZ),XYZ.Zero,
                            new XYZ(p.ChainSpacing+width,width,width),true);
                        scene.Assumptions.Add("La chaîne physique est contrôlée par deux brins de16mm et leur liaison basse, avec la marge. Les mains et les bras sont contrôlés uniquement aux hauteurs effectivement représentées ; le dégagement de prise n’est pas appliqué à toute la chaîne ni à sa roue haute.");
                        scene.Assumptions.Add("Chaîne supposée verticale sous gravité, avec deux brins libres : longueur, écartement, boucle, transmission et effort doivent être confirmés sur le matériel.");
                        if (Math.Abs(frame.BasisZ.DotProduct(XYZ.BasisZ)) > 1e-3)
                            scene.Warnings.Add("Le modèle de chaîne suppose une roue verticale avec un axe horizontal. Le repère choisi ne respecte pas cette hypothèse : confirmez l’orientation ou le renvoi de chaîne réel.");
                        if (p.ChainSpacing > 2 * p.HandleRadius + Mm(2))
                            scene.Warnings.Add("L’écartement des deux brins dépasse le diamètre du volant représenté : confirmez le rayon, l’écartement ou les guides de chaîne réels.");
                        if (p.OperatorOrigin != null && frame.Origin.Z - p.ChainDrop < p.OperatorOrigin.Z + Mm(50))
                            scene.Warnings.Add("La boucle de chaîne est au niveau ou sous le sol choisi : confirmez sa longueur réelle.");
                        if (p.ChainPullStroke > p.ChainDrop - 2 * p.GripClearance)
                            scene.Warnings.Add("La course de traction dépasse la longueur de brin disponible ; le mouvement représenté est limité.");
                        double minGrip = frame.Origin.Z - p.ChainDrop + p.GripClearance;
                        double maxGrip = frame.Origin.Z - p.GripClearance;
                        double requestedGrip = (p.OperatorOrigin ?? ProposedFoot(p)).Z + p.WorkHeight;
                        double usablePull = Math.Max(0, Math.Max(minGrip, Math.Min(maxGrip, requestedGrip)) - minGrip);
                        if (usablePull + Mm(1) < p.ChainPullStroke)
                            scene.Warnings.Add("À cette position, le brin disponible ne permet pas toute la course de traction demandée : ajustez la chaîne ou la position de l’opérateur.");
                    }
                }
            }
            else
            {
                XYZ localShift = p.RemovalDirection.Normalize() * p.RemovalTravel;
                AddBox(scene, p.Operation == MaintenanceAccessOperation.Filter ? "Cartouche / panier à extraire" : "Actionneur à déposer",
                    MaintenanceAccessRole.EquipmentGhost, frame, p.RemovalCenterOffset + localShift * phase, p.RemovalSize, false);
                if (swept)
                {
                    AddBox(scene, "Volume balayé de dépose", MaintenanceAccessRole.ExtractionSpace, frame,
                        p.RemovalCenterOffset + localShift / 2, p.RemovalSize + new XYZ(Math.Abs(localShift.X), Math.Abs(localShift.Y), Math.Abs(localShift.Z))
                            + new XYZ(2 * p.Margin, 2 * p.Margin, 2 * p.Margin), true);
                    scene.Assumptions.Add("Dépose par translation sans rotation. Le pavé contient toute la trajectoire ; en diagonale, il surestime l’espace réellement balayé.");
                }
                scene.Motions.Add(new MaintenanceAccessMotion { Label = "Dépose rectiligne", Kind = "translation", Origin = frame.Origin,
                    Axis = frame.OfVector(p.RemovalDirection).Normalize(), Start = frame.OfPoint(p.RemovalCenterOffset),
                    End = frame.OfPoint(p.RemovalCenterOffset + localShift) });
            }
        }

        private static void DrawWheel(MaintenanceAccessScene scene, MaintenanceAccessProfile p, Transform frame, double angle)
        {
            scene.Lines.Add(new MaintenanceAccessLine { Label = "Volant", Role = MaintenanceAccessRole.Handle, Thickness = p.HandleThickness,
                Points = Enumerable.Range(0, 49).Select(i => frame.OfPoint(Radial(p.HandleRadius, i * 2 * Math.PI / 48))).ToArray() });
            for (int i = 0; i < 3; i++)
                scene.Lines.Add(new MaintenanceAccessLine { Label = "Rayon du volant", Role = MaintenanceAccessRole.Handle, Thickness = p.HandleThickness * .65,
                    Points = new[] { frame.Origin, frame.OfPoint(Radial(p.HandleRadius, angle + i * 2 * Math.PI / 3)) } });
        }

        private static HumanPose GetHumanPose(MaintenanceAccessProfile p, Transform human, XYZ foot, double phase)
        {
            Interlocked.Increment(ref _humanPoseEvaluations);
            XYZ shift = XYZ.Zero;
            if (p.FollowRemoval && (p.Operation == MaintenanceAccessOperation.Filter || p.Operation == MaintenanceAccessOperation.Actuator))
                shift = Horizontal(p.Frame.OfVector(p.RemovalDirection).Normalize() * (p.RemovalTravel * phase));
            foot += shift;
            double h = p.OperatorHeight;
            bool crouched = p.OperatorPosture == MaintenanceOperatorPosture.Crouched;
            XYZ hip = foot + human.BasisY * (crouched ? -.10 * h : 0) + XYZ.BasisZ * ((crouched ? .26 : .50) * h);
            XYZ shoulder = foot + human.BasisY * (crouched ? -.02 * h : 0) + XYZ.BasisZ * ((crouched ? .56 : .81) * h);
            double ankle = Math.Sqrt(3) * (h * .065 / 2 + p.Margin) + Mm(2);
            var result = new HumanPose { Foot = foot, Hip = hip, Shoulder = shoulder,
                Head = shoulder + XYZ.BasisZ * (.12 * h) + human.BasisY * (crouched ? .025 * h : 0),
                LeftHip = hip - human.BasisX * (.09 * h), RightHip = hip + human.BasisX * (.09 * h),
                LeftAnkle = foot - human.BasisX * (.11 * h) + human.BasisY * (.06 * h) + XYZ.BasisZ * ankle,
                RightAnkle = foot + human.BasisX * (.11 * h) + human.BasisY * (.06 * h) + XYZ.BasisZ * ankle,
                LeftShoulder = shoulder - human.BasisX * (.16 * h),
                RightShoulder = shoulder + human.BasisX * (.16 * h) };
            bool legsReach = true;
            result.LeftKnee = Knee(result.LeftHip, result.LeftAnkle);
            result.RightKnee = Knee(result.RightHip, result.RightAnkle);
            XYZ Knee(XYZ a, XYZ b)
            {
                legsReach &= MaintenancePlacementMath.LegKnee(new MotorVector(a.X,a.Y,a.Z),new MotorVector(b.X,b.Y,b.Z),
                    new MotorVector(human.BasisY.X,human.BasisY.Y,human.BasisY.Z),h*.225,out var knee);
                return new XYZ(knee.X,knee.Y,knee.Z);
            }
            GetHands(p, human, foot, phase, out XYZ left, out XYZ right);
            bool leftReach = Arm(result.LeftShoulder, left, -human.BasisX, h, out result.LeftElbow, out result.LeftWrist);
            bool rightReach = Arm(result.RightShoulder, right, human.BasisX, h, out result.RightElbow, out result.RightWrist);
            result.Reachable = legsReach && leftReach && rightReach;
            return result;
        }

        private static void GetHands(MaintenanceAccessProfile p, Transform human, XYZ foot, double phase, out XYZ left, out XYZ right)
        {
            XYZ center = p.WorldCenter;
            if (p.Operation == MaintenanceAccessOperation.LeverValve)
            {
                XYZ handle = p.Frame.OfVector(Radial(p.HandleRadius,
                    (p.InitialAngleDegrees + p.RotationDegrees * phase) * Math.PI / 180)) + center;
                // One operating hand; the free hand rests in front of the torso.
                right = handle;
                left = foot + XYZ.BasisZ * ((p.OperatorPosture == MaintenanceOperatorPosture.Crouched ? .40 : .55) * p.OperatorHeight)
                    + human.BasisY * Mm(100) - human.BasisX * Mm(180);
            }
            else if (p.Operation == MaintenanceAccessOperation.HandwheelValve)
            {
                // Hands perform a short working arc then regrip. Following one
                // point for a whole revolution would unrealistically cross arms.
                XYZ near = p.Frame.Inverse.OfVector(foot + XYZ.BasisZ * p.WorkHeight - center);
                double baseAngle = Math.Atan2(near.Y, near.X);
                double rock = Math.Sin(2 * Math.PI * phase) * Math.Min(25, Math.Abs(p.RotationDegrees) / 2) * Math.PI / 180;
                left = center + p.Frame.OfVector(Radial(p.HandleRadius, baseAngle - Math.PI / 3 + rock));
                right = center + p.Frame.OfVector(Radial(p.HandleRadius, baseAngle + Math.PI / 3 + rock));
                if ((left - human.Origin).DotProduct(human.BasisX) > (right - human.Origin).DotProduct(human.BasisX))
                { XYZ swap = left; left = right; right = swap; }
            }
            else if (p.Operation == MaintenanceAccessOperation.ChainwheelValve)
            {
                XYZ tangent = Horizontal(p.Frame.BasisX);
                if (tangent.GetLength() < 1e-6) tangent = Horizontal(p.Frame.BasisY);
                if (tangent.GetLength() < 1e-6) tangent = human.BasisX;
                tangent = tangent.Normalize();
                if (tangent.DotProduct(human.BasisX) < 0) tangent = -tangent;
                double minZ = center.Z - p.ChainDrop + p.GripClearance;
                double maxZ = center.Z - p.GripClearance;
                double operating = Math.Max(minZ, Math.Min(maxZ, foot.Z + p.WorkHeight));
                double down = Math.Min(p.ChainPullStroke, Math.Max(0, operating - minZ));
                left = new XYZ(center.X, center.Y, operating - down * MaintenanceAccessMath.Pull(phase)) - tangent * p.ChainSpacing / 2;
                right = new XYZ(center.X, center.Y, operating - down * (1 - MaintenanceAccessMath.Pull(phase))) + tangent * p.ChainSpacing / 2;
            }
            else
            {
                XYZ command = center + p.Frame.OfVector(p.RemovalDirection).Normalize() * (p.RemovalTravel * phase);
                double grip = Math.Min(p.RemovalSize.X, p.RemovalSize.Y) * .4;
                left = command - human.BasisX * grip;
                right = command + human.BasisX * grip;
            }
        }

        private static bool Arm(XYZ shoulder, XYZ target, XYZ outward, double height, out XYZ elbow, out XYZ wrist)
        {
            double upper = height * .18, lower = height * .16;
            XYZ direction = target - shoulder;
            double distance = direction.GetLength();
            direction = distance > 1e-8 ? direction / distance : -XYZ.BasisZ;
            bool reachable = MaintenanceAccessMath.Elbow(upper, lower, distance, out double along, out double bend, out double handDistance);
            XYZ bendDirection = -XYZ.BasisZ + outward * .4;
            bendDirection -= direction * bendDirection.DotProduct(direction);
            if (bendDirection.GetLength() < 1e-6)
            { bendDirection = outward - direction * outward.DotProduct(direction); }
            if (bendDirection.GetLength() < 1e-6) bendDirection = direction.CrossProduct(XYZ.BasisX);
            if (bendDirection.GetLength() < 1e-6) bendDirection = direction.CrossProduct(XYZ.BasisY);
            elbow = shoulder + direction * along + bendDirection.Normalize() * bend;
            wrist = shoulder + direction * handDistance;
            return reachable;
        }

        private static void DrawHuman(MaintenanceAccessScene scene, MaintenanceAccessProfile p, Transform human, HumanPose pose)
        {
            Transform body = At(pose.Foot, human.BasisX, human.BasisY, XYZ.BasisZ);
            double h = p.OperatorHeight;
            scene.Boxes.AddRange(BodyShapes(p,human,pose).Where(b=>!b.Label.StartsWith("Cuisse",StringComparison.Ordinal)&&!b.Label.StartsWith("Tibia",StringComparison.Ordinal)));
            double legWidth = h * .065;
            AddLine(scene, "Jambe gauche", MaintenanceAccessRole.HumanBody, legWidth, pose.LeftHip,pose.LeftKnee,pose.LeftAnkle);
            AddLine(scene, "Jambe droite", MaintenanceAccessRole.HumanBody, legWidth, pose.RightHip,pose.RightKnee,pose.RightAnkle);
            AddLine(scene, "Bras gauche", MaintenanceAccessRole.HumanArm, h * .045, pose.LeftShoulder, pose.LeftElbow, pose.LeftWrist);
            AddLine(scene, "Bras droit", MaintenanceAccessRole.HumanArm, h * .045, pose.RightShoulder, pose.RightElbow, pose.RightWrist);
            AddBox(scene, "Main gauche", MaintenanceAccessRole.HumanArm, body, body.Inverse.OfPoint(pose.LeftWrist), new XYZ(Mm(85), Mm(45), Mm(100)), false);
            AddBox(scene, "Main droite", MaintenanceAccessRole.HumanArm, body, body.Inverse.OfPoint(pose.RightWrist), new XYZ(Mm(85), Mm(45), Mm(100)), false);
        }

        private static void AddHumanSweeps(MaintenanceAccessScene scene, MaintenanceAccessProfile p, Transform human, HumanPose[] poses)
        {
            // Physical articulated components, not the configurable comfortable
            // work-area rectangle. Their exact linear sweeps preserve gaps
            // between the legs and avoid a global AABB of the withdrawal.
            foreach(var shape in BodyShapes(p,human,poses[0]))
            {
                shape.Role=MaintenanceAccessRole.OperatorSpace;shape.CheckCollision=true;shape.CheckSource=true;shape.DependsOnOperator=true;
                shape.Size+=new XYZ(2*p.Margin,2*p.Margin,shape.Label.StartsWith("Pied",StringComparison.Ordinal)?0:2*p.Margin);
                shape.Translation=poses[poses.Length-1].Foot-poses[0].Foot;scene.Boxes.Add(shape);
            }
            scene.Assumptions.Add("Le rectangle largeur/profondeur réglable représente le confort de travail. Les collisions du corps utilisent les segments réellement représentés et la marge, indépendamment de ce rectangle.");
            if(p.OperatorPosture==MaintenanceOperatorPosture.Crouched)
                scene.ConstraintNotes.Add("Posture accroupie contraignante : hanches abaissées, genoux fléchis et torse incliné. Stabilité, effort, durée et aptitude de l’opérateur restent à évaluer ; aucune conformité ergonomique n’est déduite.");
            if(Math.Abs(p.BodyYawDegrees%360)>1e-6)
                scene.ConstraintNotes.Add("Corps tourné de "+p.BodyYawDegrees.ToString("0")+"° par rapport à la commande : posture de travail contraignante à confirmer, sans déduction normative.");
            double radius = p.OperatorHeight * .045 / 2 + p.Margin;
            for (int i = 0; i < poses.Length - 1; i++)
            {
                HumanPose a = poses[i], b = poses[i + 1];
                AddArmSweep("Bras gauche — mouvement complet", a.LeftShoulder, a.LeftElbow, a.LeftWrist, b.LeftShoulder, b.LeftElbow, b.LeftWrist);
                AddArmSweep("Bras droit — mouvement complet", a.RightShoulder, a.RightElbow, a.RightWrist, b.RightShoulder, b.RightElbow, b.RightWrist);
            }
            void AddArmSweep(string label, XYZ shoulder0, XYZ elbow0, XYZ wrist0, XYZ shoulder1, XYZ elbow1, XYZ wrist1)
            {
                AddBoundedBox(scene, label, MaintenanceAccessRole.HumanArm, human, new[] { shoulder0, elbow0, shoulder1, elbow1 },
                    new XYZ(radius, radius, radius), false, true);
                AddBoundedBox(scene, label, MaintenanceAccessRole.HumanArm, human, new[] { elbow0, wrist0, elbow1, wrist1 },
                    new XYZ(radius, radius, radius), false, true);
                double hand = Math.Max(Mm(100) / 2, radius);
                AddBoundedBox(scene, "Prise de main — mouvement complet", MaintenanceAccessRole.ManipulationSpace, human, new[] { wrist0, wrist1 },
                    new XYZ(hand, hand, hand), false, true);
            }
        }
        private static IEnumerable<MaintenanceAccessBox> BodyShapes(MaintenanceAccessProfile p,Transform human,HumanPose pose)
        {
            double h=p.OperatorHeight;
            MaintenanceAccessBox Shape(string label,Transform frame,XYZ center,XYZ size)=>new MaintenanceAccessBox {
                Label=label,Role=MaintenanceAccessRole.HumanBody,Frame=frame,Center=center,Size=size};
            var trunk=(pose.Shoulder-pose.Hip).Normalize();
            var torso=At((pose.Shoulder+pose.Hip)/2,human.BasisX,trunk.CrossProduct(human.BasisX).Normalize(),trunk);
            yield return Shape("Buste du mannequin",torso,XYZ.Zero,new XYZ(h*.31,h*.17,(pose.Shoulder-pose.Hip).GetLength()));
            var body=At(pose.Foot,human.BasisX,human.BasisY,XYZ.BasisZ);
            yield return Shape("Bassin du mannequin",body,body.Inverse.OfPoint(pose.Hip+XYZ.BasisZ*(h*.01)),new XYZ(h*.24,h*.17,h*.12));
            yield return Shape("Cou du mannequin",body,body.Inverse.OfPoint(pose.Shoulder+XYZ.BasisZ*(h*.04)),new XYZ(h*.08,h*.08,h*.10));
            yield return Shape("Tête du mannequin",body,body.Inverse.OfPoint(pose.Head),new XYZ(h*.13,h*.13,h*.14));
            yield return Segment("Cuisse gauche",pose.LeftHip,pose.LeftKnee);
            yield return Segment("Cuisse droite",pose.RightHip,pose.RightKnee);
            yield return Segment("Tibia gauche",pose.LeftKnee,pose.LeftAnkle);
            yield return Segment("Tibia droite",pose.RightKnee,pose.RightAnkle);
            foreach(int side in new[]{-1,1})
                yield return Shape(side<0?"Pied gauche":"Pied droit",body,new XYZ(side*h*.11,h*.06,h*.055/2+Mm(2)),new XYZ(h*.10,h*.13,h*.055));
            MaintenanceAccessBox Segment(string label,XYZ a,XYZ b)
            {
                var z=(b-a).Normalize();var x=human.BasisX-z*human.BasisX.DotProduct(z);x=x.Normalize();
                return Shape(label,At((a+b)/2,x,z.CrossProduct(x).Normalize(),z),XYZ.Zero,new XYZ(h*.065,h*.065,(b-a).GetLength()+h*.065));
            }
        }

        private static void AddBoundedBox(MaintenanceAccessScene scene, string label, MaintenanceAccessRole role, Transform frame, IEnumerable<XYZ> worldPoints, XYZ padding, bool checkSource, bool dependsOnOperator=false)
        {
            XYZ[] points = worldPoints.Select(frame.Inverse.OfPoint).ToArray();
            XYZ lo = new XYZ(points.Min(q => q.X), points.Min(q => q.Y), points.Min(q => q.Z)) - padding;
            XYZ hi = new XYZ(points.Max(q => q.X), points.Max(q => q.Y), points.Max(q => q.Z)) + padding;
            AddBox(scene, label, role, frame, (lo + hi) / 2, hi - lo, true, checkSource, dependsOnOperator);
        }
        private static void AddBox(MaintenanceAccessScene scene, string label, MaintenanceAccessRole role, Transform frame, XYZ center, XYZ size, bool collision, bool checkSource = false, bool dependsOnOperator=false)
            => scene.Boxes.Add(new MaintenanceAccessBox { Label = label, Role = role, Frame = frame, Center = center, Size = size,
                CheckCollision = collision, CheckSource = checkSource, DependsOnOperator=dependsOnOperator });
        private static void AddLine(MaintenanceAccessScene scene, string label, MaintenanceAccessRole role, double thickness, params XYZ[] points)
            => scene.Lines.Add(new MaintenanceAccessLine { Label = label, Role = role, Thickness = thickness, Points = points });
        private static Transform At(XYZ origin, XYZ x, XYZ y, XYZ z)
        { var t = Transform.Identity; t.Origin = origin; t.BasisX = x; t.BasisY = y; t.BasisZ = z; return t; }
        private static Transform Rotated(Transform frame, double radians)
            => At(frame.Origin, frame.BasisX * Math.Cos(radians) + frame.BasisY * Math.Sin(radians),
                -frame.BasisX * Math.Sin(radians) + frame.BasisY * Math.Cos(radians), frame.BasisZ);
        private static XYZ Radial(double radius, double angle) => new XYZ(radius * Math.Cos(angle), radius * Math.Sin(angle), 0);
        private static XYZ Horizontal(XYZ point) => new XYZ(point.X, point.Y, 0);
        private static HumanPose Interpolate(HumanPose a, HumanPose b, double t)
        {
            XYZ Mix(XYZ x, XYZ y) => x + (y - x) * t;
            return new HumanPose { Foot = Mix(a.Foot, b.Foot), LeftShoulder = Mix(a.LeftShoulder, b.LeftShoulder), RightShoulder = Mix(a.RightShoulder, b.RightShoulder),
                Hip=Mix(a.Hip,b.Hip),Shoulder=Mix(a.Shoulder,b.Shoulder),Head=Mix(a.Head,b.Head),LeftHip=Mix(a.LeftHip,b.LeftHip),RightHip=Mix(a.RightHip,b.RightHip),
                LeftKnee=Mix(a.LeftKnee,b.LeftKnee),RightKnee=Mix(a.RightKnee,b.RightKnee),LeftAnkle=Mix(a.LeftAnkle,b.LeftAnkle),RightAnkle=Mix(a.RightAnkle,b.RightAnkle),
                LeftElbow = Mix(a.LeftElbow, b.LeftElbow), RightElbow = Mix(a.RightElbow, b.RightElbow), LeftWrist = Mix(a.LeftWrist, b.LeftWrist), RightWrist = Mix(a.RightWrist, b.RightWrist),
                Reachable = a.Reachable && b.Reachable };
        }
    }
}
