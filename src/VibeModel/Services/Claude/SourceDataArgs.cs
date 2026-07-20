using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Pure argument parsing for the source-data commands (curves / pcprobe / pcline).
    /// Revit-free so the headless test suite can cover it. Each parser returns a result
    /// object whose Error is non-null when the args are invalid.
    /// </summary>
    internal static class SourceDataArgs
    {
        private static readonly char[] Separators = { ' ', ',' };

        /// <summary>curves &lt;importId&gt; &lt;layer&gt; [--bbox x1 y1 x2 y2] [--closed] [--page N]</summary>
        public static CurvesArgs ParseCurves(string args)
        {
            var r = new CurvesArgs { Page = 1 };
            var tokens = (args ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Strip flags (with their operands) first; what remains is: importId + layer name.
            for (int i = 0; i < tokens.Count;)
            {
                var t = tokens[i];
                if (t.Equals("--closed", StringComparison.OrdinalIgnoreCase))
                {
                    r.Closed = true;
                    tokens.RemoveAt(i);
                }
                else if (t.Equals("--page", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count || !int.TryParse(tokens[i + 1], out int page) || page < 1)
                    {
                        r.Error = "--page needs a positive number.";
                        return r;
                    }
                    r.Page = page;
                    tokens.RemoveRange(i, 2);
                }
                else if (t.Equals("--bbox", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 4 >= tokens.Count)
                    {
                        r.Error = "--bbox needs 4 numbers: x1 y1 x2 y2 (mm).";
                        return r;
                    }
                    var box = new double[4];
                    for (int j = 0; j < 4; j++)
                    {
                        if (!double.TryParse(tokens[i + 1 + j], NumberStyles.Float, CultureInfo.InvariantCulture, out box[j]))
                        {
                            r.Error = "--bbox needs 4 numbers: x1 y1 x2 y2 (mm).";
                            return r;
                        }
                    }
                    // Normalize so min/max ordering never matters.
                    r.BboxMinX = Math.Min(box[0], box[2]);
                    r.BboxMinY = Math.Min(box[1], box[3]);
                    r.BboxMaxX = Math.Max(box[0], box[2]);
                    r.BboxMaxY = Math.Max(box[1], box[3]);
                    r.HasBbox = true;
                    tokens.RemoveRange(i, 5);
                }
                else if (t.StartsWith("--"))
                {
                    r.Error = "Unknown flag '" + t + "'.";
                    return r;
                }
                else
                {
                    i++;
                }
            }

            if (tokens.Count < 2)
            {
                r.Error = "Need an import ID and a layer name.";
                return r;
            }
            if (!int.TryParse(tokens[0], out int id))
            {
                r.Error = "'" + tokens[0] + "' is not a valid element ID.";
                return r;
            }
            r.ImportId = id;
            r.Layer = string.Join(" ", tokens.Skip(1));
            return r;
        }

        /// <summary>pcprobe &lt;cloudId&gt; &lt;x&gt; &lt;y&gt; [radius]</summary>
        public static PcProbeArgs ParsePcProbe(string args)
        {
            var r = new PcProbeArgs { RadiusMm = 250 };
            var tokens = (args ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length < 3)
            {
                r.Error = "Need a cloud ID and x y coordinates (mm).";
                return r;
            }
            if (!int.TryParse(tokens[0], out int id))
            {
                r.Error = "'" + tokens[0] + "' is not a valid element ID.";
                return r;
            }
            if (!TryMm(tokens[1], out double x) || !TryMm(tokens[2], out double y))
            {
                r.Error = "x and y must be numbers (mm).";
                return r;
            }
            if (tokens.Length >= 4)
            {
                if (!TryMm(tokens[3], out double rad) || rad <= 0)
                {
                    r.Error = "Radius must be a positive number (mm).";
                    return r;
                }
                r.RadiusMm = rad;
            }
            r.CloudId = id;
            r.XMm = x;
            r.YMm = y;
            return r;
        }

        /// <summary>pcline &lt;cloudId&gt; &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; [step]</summary>
        public static PcLineArgs ParsePcLine(string args)
        {
            var r = new PcLineArgs { StepMm = 500 };
            var tokens = (args ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length < 5)
            {
                r.Error = "Need a cloud ID and x1 y1 x2 y2 coordinates (mm).";
                return r;
            }
            if (!int.TryParse(tokens[0], out int id))
            {
                r.Error = "'" + tokens[0] + "' is not a valid element ID.";
                return r;
            }
            var nums = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!TryMm(tokens[1 + i], out nums[i]))
                {
                    r.Error = "Coordinates must be numbers (mm).";
                    return r;
                }
            }
            if (tokens.Length >= 6)
            {
                if (!TryMm(tokens[5], out double step) || step <= 0)
                {
                    r.Error = "Step must be a positive number (mm).";
                    return r;
                }
                r.StepMm = step;
            }
            r.CloudId = id;
            r.X1Mm = nums[0];
            r.Y1Mm = nums[1];
            r.X2Mm = nums[2];
            r.Y2Mm = nums[3];
            return r;
        }

        private static bool TryMm(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    internal sealed class CurvesArgs
    {
        public int ImportId { get; set; }
        public string Layer { get; set; }
        public bool Closed { get; set; }
        public int Page { get; set; }
        public bool HasBbox { get; set; }
        public double BboxMinX { get; set; }
        public double BboxMinY { get; set; }
        public double BboxMaxX { get; set; }
        public double BboxMaxY { get; set; }
        public string Error { get; set; }
    }

    internal sealed class PcProbeArgs
    {
        public int CloudId { get; set; }
        public double XMm { get; set; }
        public double YMm { get; set; }
        public double RadiusMm { get; set; }
        public string Error { get; set; }
    }

    internal sealed class PcLineArgs
    {
        public int CloudId { get; set; }
        public double X1Mm { get; set; }
        public double Y1Mm { get; set; }
        public double X2Mm { get; set; }
        public double Y2Mm { get; set; }
        public double StepMm { get; set; }
        public string Error { get; set; }
    }
}
