using System;

namespace BIMaestro.MepBooster
{
    internal static class MotorSweepMath
    {
        // Earliest collision of a monotonically growing swept volume. Returns the
        // colliding upper bound, including existing overlaps at zero travel.
        internal static double? FirstContact(double travel, double tolerance, Func<double, bool> collides)
        {
            if (travel <= 0 || tolerance <= 0 || double.IsNaN(travel) || double.IsInfinity(travel))
                throw new ArgumentOutOfRangeException(nameof(travel));
            if (collides(0)) return 0;
            if (!collides(travel)) return null;
            double low = 0, high = travel;
            while (high - low > tolerance)
            {
                double mid = (low + high) / 2;
                if (collides(mid)) high = mid; else low = mid;
            }
            return high;
        }
    }
}
