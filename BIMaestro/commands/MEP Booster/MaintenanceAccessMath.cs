using System;

namespace BIMaestro.MepBooster
{
    internal static class MaintenanceAccessMath
    {
        internal static double ClampPhase(double phase)
        {
            if (double.IsNaN(phase) || double.IsInfinity(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
            return Math.Max(0, Math.Min(1, phase));
        }
        // Every point of radius <= r is displaced by <= r * angle from the
        // midpoint orientation. Enlarging the midpoint box by this amount on all
        // three axes conservatively covers the complete intermediate rotation.
        internal static double RotationInflation(double radius, double stepRadians)
        {
            if (radius < 0 || double.IsNaN(radius) || double.IsInfinity(radius) || double.IsNaN(stepRadians) || double.IsInfinity(stepRadians))
                throw new ArgumentOutOfRangeException(nameof(radius));
            return radius * Math.Abs(stepRadians) / 2;
        }
        internal static bool Elbow(double upper, double lower, double targetDistance, out double along, out double bend, out double handDistance)
        {
            if (upper <= 0 || lower <= 0 || targetDistance < 0 || double.IsNaN(upper + lower + targetDistance) || double.IsInfinity(upper + lower + targetDistance))
                throw new ArgumentOutOfRangeException(nameof(targetDistance));
            bool reachable = targetDistance <= upper + lower + 1e-8 && targetDistance >= Math.Abs(upper - lower) - 1e-8;
            handDistance = Math.Max(Math.Abs(upper - lower) + 1e-9, Math.Min(upper + lower - 1e-9, targetDistance));
            along = (upper * upper - lower * lower + handDistance * handDistance) / (2 * handDistance);
            bend = Math.Sqrt(Math.Max(0, upper * upper - along * along));
            return reachable;
        }
        internal static double Pull(double phase) => (1 - Math.Cos(ClampPhase(phase) * 2 * Math.PI)) / 2;
    }

    // Native geometry errors may become an incomplete result. UI callback errors
    // and cancellation must instead escape that geometry-error catch unchanged.
    internal sealed class MaintenanceAccessCallbackGuard
    {
        private Exception _failure;
        internal bool IsCallbackFailure(Exception error) => ReferenceEquals(error, _failure);
        internal void Report(Action<string> progress, string message)
        {
            try { progress?.Invoke(message); }
            catch (Exception error) { _failure = error; throw; }
        }
        internal void CheckCanceled(Func<bool> canceled)
        {
            bool requested;
            try { requested = canceled?.Invoke() == true; }
            catch (Exception error) { _failure = error; throw; }
            if (requested) throw new OperationCanceledException("Contrôle de maintenance annulé.");
        }
    }
}
