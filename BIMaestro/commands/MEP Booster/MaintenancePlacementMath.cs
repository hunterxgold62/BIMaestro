using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.MepBooster
{
    internal enum MaintenanceOperatorFailureReason
    {
        None, MissingCommand, MissingFootPoint, NoWalkingSurface, NoSupportedFootprint,
        GestureOutOfReach, BodyBlocked, BodyUnverified, SearchIncomplete
    }

    // These are observations about the evaluated search, never a proof that
    // no real maintenance method exists. In particular, a bounded search must
    // retain its incomplete status even when its last candidate was blocked.
    internal static class MaintenanceOperatorFailure
    {
        internal static MaintenanceOperatorFailureReason Classify(bool limited, bool hasWalkingSurface,
            int supported, int reachable, int unverifiedBodies)
        {
            if (limited) return MaintenanceOperatorFailureReason.SearchIncomplete;
            if (!hasWalkingSurface) return MaintenanceOperatorFailureReason.NoWalkingSurface;
            if (supported == 0) return MaintenanceOperatorFailureReason.NoSupportedFootprint;
            if (reachable == 0) return MaintenanceOperatorFailureReason.GestureOutOfReach;
            return unverifiedBodies > 0 ? MaintenanceOperatorFailureReason.BodyUnverified : MaintenanceOperatorFailureReason.BodyBlocked;
        }

        internal static string Describe(MaintenanceOperatorFailureReason reason, string detail = null)
        {
            string message;
            switch (reason)
            {
                case MaintenanceOperatorFailureReason.MissingCommand:
                    message = "La commande ou le point de prise n’est pas identifié. Aucun personnage proposé avant de préciser ce repère."; break;
                case MaintenanceOperatorFailureReason.MissingFootPoint:
                    message = "Aucun point des pieds à vérifier. Choisissez un appui sur un sol modélisé."; break;
                case MaintenanceOperatorFailureReason.NoWalkingSurface:
                    message = "Aucun plancher, palier, escalier ou terrain exploitable trouvé à portée du geste dans la zone analysée. Choisissez un sol modélisé ou un moyen d’accès adapté."; break;
                case MaintenanceOperatorFailureReason.NoSupportedFootprint:
                    message = "Les sols repérés ne supportent pas toute l’empreinte des deux pieds et le recul testé. Aucun placement en l’air n’est proposé."; break;
                case MaintenanceOperatorFailureReason.GestureOutOfReach:
                    message = "Le geste complet dépasse la portée du mannequin depuis les sols et les postures testés ; atteindre la commande au début ne suffit pas."; break;
                case MaintenanceOperatorFailureReason.BodyBlocked:
                    message = "Le corps ou son déplacement rencontre la maquette pour les postures atteignables testées."; break;
                case MaintenanceOperatorFailureReason.BodyUnverified:
                    message = "Aucun poste entièrement vérifié : une partie de la géométrie dans le volume du corps reste indéterminée. Cela ne prouve pas que le geste est irréalisable."; break;
                case MaintenanceOperatorFailureReason.SearchIncomplete:
                    message = "Recherche incomplète : la limite de calcul a été atteinte sans trouver de poste entièrement contrôlé. D’autres postures peuvent convenir."; break;
                default: return null;
            }
            return string.IsNullOrWhiteSpace(detail) ? message : message + " " + detail.Trim();
        }
    }

    internal static class MaintenancePlacementMath
    {
        // Equal-length femur/shin inverse kinematics, bending toward the chosen
        // body forward direction. Both postures keep the same leg lengths.
        internal static bool LegKnee(MotorVector hip, MotorVector ankle, MotorVector forward, double length, out MotorVector knee)
        {
            var delta = ankle - hip; double distance = Math.Sqrt(delta.Dot(delta));
            var axis = distance > 1e-9 ? delta * (1 / distance) : new MotorVector(0,0,-1);
            var bend = forward - axis * forward.Dot(axis); double bendLength = Math.Sqrt(bend.Dot(bend));
            if (bendLength < 1e-9) bend = new MotorVector(1,0,0); else bend *= 1 / bendLength;
            double along = Math.Min(length, distance / 2);
            knee = hip + axis * along + bend * Math.Sqrt(Math.Max(0, length * length - along * along));
            return distance <= 2 * length + 1e-8;
        }
        internal static double ChainWorkHeight(double current, double commandZ, double footZ, double chainDrop, double gripClearance, double pullStroke)
            => Math.Max(current, commandZ - chainDrop - footZ + gripClearance + pullStroke);

        // XY barycentric projection of a vertical ray onto a triangle. Using
        // coordinates relative to A avoids losing precision in large sites.
        internal static bool TryHeight(MotorTriangle triangle, double x, double y, out double height)
        {
            height = 0;
            var u = triangle.B - triangle.A;
            var v = triangle.C - triangle.A;
            double determinant = u.X * v.Y - u.Y * v.X;
            if (Math.Abs(determinant) < 1e-12) return false;
            double dx = x - triangle.A.X, dy = y - triangle.A.Y;
            double a = (dx * v.Y - dy * v.X) / determinant;
            double b = (u.X * dy - u.Y * dx) / determinant;
            if (a < -1e-8 || b < -1e-8 || a + b > 1 + 1e-8) return false;
            height = triangle.A.Z + a * u.Z + b * v.Z;
            return !double.IsNaN(height) && !double.IsInfinity(height);
        }

        internal static bool NearHorizontal(MotorTriangle triangle)
        {
            var n = (triangle.B - triangle.A).Cross(triangle.C - triangle.A);
            double length = Math.Sqrt(n.Dot(n));
            // Geometric filtering, not a regulatory slope limit. The separate
            // footprint check also requires essentially level contact heights.
            return length > 1e-10 && Math.Abs(n.Z) / length >= .995;
        }

        internal static IEnumerable<MotorVector> Footprint(MotorVector center, MotorVector right,
            MotorVector forward, double width, double depth, double forwardOffset)
        {
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                yield return center + right * (x * width / 2) + forward * (forwardOffset + y * depth / 2);
        }

        internal static bool LevelContact(IEnumerable<double> heights, double tolerance, out double highest)
        {
            var values = heights.ToArray();
            highest = 0;
            if (values.Length == 0 || values.Any(z => double.IsNaN(z) || double.IsInfinity(z))) return false;
            highest = values.Max();
            return highest - values.Min() <= tolerance;
        }
    }

    // A detached, conservative classifier for horizontal CAD linework. One
    // unsupported object or failed read prevents the non-physical conclusion.
    internal sealed class MaintenanceHorizontalLinework
    {
        private double _minZ = double.PositiveInfinity, _maxZ = double.NegativeInfinity;
        private bool _certain = true;
        internal int PrimitiveCount { get; private set; }
        internal int PointCount { get; private set; }
        internal bool ReadingLimited { get; private set; }
        internal bool CanRead => _certain;
        internal double ZSpan => PointCount == 0 ? double.NaN : _maxZ - _minZ;
        internal void AddPrimitive(IEnumerable<MotorVector> points)
        {
            if (!_certain) return;
            PrimitiveCount++;
            if (PrimitiveCount > 25000) { ReadingLimited = true; _certain = false; return; }
            int before = PointCount;
            foreach (var point in points)
            {
                if (PointCount >= 250000) { ReadingLimited = true; _certain = false; return; }
                if (double.IsNaN(point.X + point.Y + point.Z) || double.IsInfinity(point.X) || double.IsInfinity(point.Y) || double.IsInfinity(point.Z))
                { _certain = false; continue; }
                _minZ = Math.Min(_minZ, point.Z); _maxZ = Math.Max(_maxZ, point.Z); PointCount++;
            }
            if (before == PointCount) _certain = false;
        }
        internal void RejectNonLinework() => _certain = false;
        internal bool IsHorizontal2D(double tolerance)
            => _certain && PrimitiveCount > 0 && PointCount > 0 && ZSpan < tolerance;
    }
}
