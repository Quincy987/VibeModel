using System;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Revit-free angle arithmetic for the orientation rules (Project North = True North, the
    /// reference point on True North, a link placed by shared coordinates). Angles are degrees.
    /// </summary>
    public static class NorthAngles
    {
        /// <summary>The angle folded into (-180, 180], so 359.99° and -0.01° read as the same small turn.</summary>
        public static double Normalize(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return deg;
            var a = deg % 360.0;
            if (a <= -180.0) a += 360.0;
            else if (a > 180.0) a -= 360.0;
            return a;
        }

        /// <summary>True when the angle is a whole number of turns, within <paramref name="toleranceDeg"/>.</summary>
        public static bool IsZero(double deg, double toleranceDeg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return false;
            return Math.Abs(Normalize(deg)) <= Math.Max(0, toleranceDeg);
        }

        /// <summary>Plan angle of a direction vector, measured counter-clockwise from +X (east).</summary>
        public static double PlanAngle(double x, double y) => Math.Atan2(y, x) * 180.0 / Math.PI;

        /// <summary>The angle as text, rounded to 0.0001° and never "-0".</summary>
        public static string Format(double deg)
        {
            var n = Math.Round(Normalize(deg), 4);
            if (n == 0) n = 0;
            return n.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
