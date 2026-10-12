using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Helpers
{
    /// <summary>What a double parameter measures, as far as VibeModel's text output is concerned.</summary>
    public enum QuantityKind
    {
        /// <summary>Spec unknown (could not be read) — legacy name heuristic decides.</summary>
        Unknown,
        /// <summary>A pure number (SpecTypeId.Number): printed bare, it has no unit.</summary>
        Number,
        Length,
        Area,
        Volume,
        Angle,
        /// <summary>Another measurable spec (slope, force, pipe size, …): Revit's own project-unit string.</summary>
        Other
    }

    /// <summary>
    /// Conversion between the user-facing units VibeModel prints (mm, m², m³, °) and Revit's
    /// internal units (ft, ft², ft³, rad), plus the matching text formatting. Pure — unit-tested.
    /// </summary>
    public static class RevitUnitHelper
    {
        private const double MmPerFoot = 304.8;
        private const double M2PerSqFt = 0.09290304;          // 0.3048²
        private const double M3PerCuFt = 0.028316846592;      // 0.3048³

        public static double MmToFeet(double mm) => mm / MmPerFoot;
        public static double FeetToMm(double feet) => feet * MmPerFoot;

        public static double SqFeetToM2(double sqFt) => sqFt * M2PerSqFt;
        public static double CuFeetToM3(double cuFt) => cuFt * M3PerCuFt;
        public static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

        /// <summary>Internal value (ft, ft², ft³, rad) → the unit VibeModel prints for that kind.</summary>
        public static double ToDisplay(QuantityKind kind, double internalValue)
        {
            switch (kind)
            {
                case QuantityKind.Length: return FeetToMm(internalValue);
                case QuantityKind.Area: return SqFeetToM2(internalValue);
                case QuantityKind.Volume: return CuFeetToM3(internalValue);
                case QuantityKind.Angle: return RadToDeg(internalValue);
                default: return internalValue;
            }
        }

        /// <summary>The inverse of <see cref="ToDisplay"/>: a value typed in mm / m² / m³ / ° → internal.</summary>
        public static double FromDisplay(QuantityKind kind, double displayValue)
        {
            switch (kind)
            {
                case QuantityKind.Length: return MmToFeet(displayValue);
                case QuantityKind.Area: return displayValue / M2PerSqFt;
                case QuantityKind.Volume: return displayValue / M3PerCuFt;
                case QuantityKind.Angle: return displayValue * Math.PI / 180.0;
                default: return displayValue;
            }
        }

        /// <summary>The unit symbol printed for a kind; null for kinds that carry none of their own.</summary>
        public static string Symbol(QuantityKind kind)
        {
            switch (kind)
            {
                case QuantityKind.Length: return "mm";
                case QuantityKind.Area: return "m²";
                case QuantityKind.Volume: return "m³";
                case QuantityKind.Angle: return "°";
                default: return null;
            }
        }

        /// <summary>
        /// An internal value as text with its unit: "3000.0 mm", "12.5 m²", "1.234 m³", "45°".
        /// Number/Unknown/Other kinds come back bare ("0.2500") — the caller decides.
        /// </summary>
        public static string Format(QuantityKind kind, double internalValue)
        {
            double v = ToDisplay(kind, internalValue);
            switch (kind)
            {
                case QuantityKind.Length: return v.ToString("F1", CultureInfo.InvariantCulture) + " mm";
                case QuantityKind.Area: return Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture) + " m²";
                case QuantityKind.Volume: return Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture) + " m³";
                case QuantityKind.Angle: return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture) + "°";
                default: return v.ToString("F4", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// A ForgeTypeId string without its version suffix ("autodesk.spec.aec:length-2.0.0" →
        /// "autodesk.spec.aec:length"), so specs from different schema versions compare equal.
        /// </summary>
        public static string SpecKey(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return "";
            return Regex.Replace(typeId.Trim(), @"-\d+(\.\d+)*$", "");
        }

        /// <summary>
        /// True when a formatted value is just a number ("12.5", "-1 234,5") and so still needs a
        /// unit; false when it already carries one ("12.5 mm", "45.00°", "3' 4\"", "2%").
        /// </summary>
        public static bool IsBareNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            if (!t.Any(char.IsDigit)) return false;
            return t.All(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-' || c == '+' || c == ' ' || c == ' ');
        }
    }
}
