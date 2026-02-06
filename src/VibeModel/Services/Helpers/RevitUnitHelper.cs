namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Conversion between mm (user-facing) and feet (Revit internal units).
    /// </summary>
    public static class RevitUnitHelper
    {
        private const double MmPerFoot = 304.8;

        public static double MmToFeet(double mm) => mm / MmPerFoot;
        public static double FeetToMm(double feet) => feet * MmPerFoot;
    }
}
