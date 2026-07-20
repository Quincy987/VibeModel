using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Pure point-cloud height analysis: ground-level estimation from a vertical column of
    /// points, and continuity smoothing for ground profiles along a line. Revit-free so the
    /// headless test suite can cover it.
    ///
    /// Ground = the LOWEST dense band of points, not the single lowest point: clutter (cars,
    /// bikes, trees) adds points ABOVE the surface, while scan noise puts stray points BELOW
    /// it. Both must be ignored.
    /// </summary>
    internal static class PointCloudAnalysis
    {
        /// <summary>
        /// Estimate ground elevation from a vertical column of Z samples (mm).
        /// Bins samples (binMm), finds clusters of adjacent occupied bins, and returns the
        /// lowest cluster whose weight >= max(minAbsolute, minFraction * total). Returns null
        /// when there are no samples or no cluster is dense enough to trust.
        /// </summary>
        public static GroundEstimate EstimateGround(IList<double> zsMm, double binMm = 50,
            int minAbsolute = 30, double minFraction = 0.02)
        {
            if (zsMm == null || zsMm.Count == 0) return null;

            double min = zsMm.Min();
            double max = zsMm.Max();

            int binCount = Math.Max(1, (int)Math.Floor((max - min) / binMm) + 1);
            var counts = new int[binCount];
            foreach (var z in zsMm)
            {
                int idx = (int)Math.Floor((z - min) / binMm);
                if (idx >= binCount) idx = binCount - 1;
                counts[idx]++;
            }

            var bins = new List<HistBin>();
            for (int i = 0; i < binCount; i++)
            {
                if (counts[i] > 0)
                    bins.Add(new HistBin { ZStartMm = min + i * binMm, WidthMm = binMm, Count = counts[i] });
            }

            var result = new GroundEstimate
            {
                MinZMm = min,
                MaxZMm = max,
                Count = zsMm.Count,
                Bins = bins,
                GroundZMm = null
            };

            double threshold = Math.Max(minAbsolute, minFraction * zsMm.Count);

            // Clusters = maximal runs of ADJACENT occupied bins; lowest qualifying cluster wins.
            int c = 0;
            while (c < binCount)
            {
                if (counts[c] == 0) { c++; continue; }
                int start = c;
                int weight = 0;
                while (c < binCount && counts[c] > 0) { weight += counts[c]; c++; }
                int end = c - 1;

                if (weight >= threshold)
                {
                    // Weighted mean of bin centers = robust ground level for this band.
                    double sum = 0;
                    for (int i = start; i <= end; i++)
                        sum += counts[i] * (min + (i + 0.5) * binMm);
                    result.GroundZMm = sum / weight;
                    result.GroundBandPoints = weight;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Continuity pass over per-station ground estimates (mm) along a line.
        /// A station whose value jumps more than maxJumpMm away from the midpoint of its
        /// nearest valued neighbors (which themselves agree) is rejected as clutter/occlusion.
        /// Rejected and missing stations are linearly interpolated from surrounding good ones;
        /// ends clamp to the nearest good value.
        /// </summary>
        public static List<ProfileStation> SmoothProfile(IList<double?> groundZsMm, double maxJumpMm = 250)
        {
            int n = groundZsMm?.Count ?? 0;
            var stations = new List<ProfileStation>(n);
            if (n == 0) return stations;

            var accepted = new bool[n];
            for (int i = 0; i < n; i++) accepted[i] = groundZsMm[i].HasValue;

            // Spike rejection for interior stations with a valued neighbor on each side.
            for (int i = 0; i < n; i++)
            {
                if (!accepted[i]) continue;
                int prev = PrevValued(groundZsMm, i);
                int next = NextValued(groundZsMm, i);
                if (prev < 0 || next < 0) continue;

                double pz = groundZsMm[prev].Value;
                double nz = groundZsMm[next].Value;
                // Only reject when the neighbors agree with each other — otherwise we cannot
                // tell which of the three stations is the outlier.
                if (Math.Abs(pz - nz) <= maxJumpMm && Math.Abs(groundZsMm[i].Value - (pz + nz) / 2.0) > maxJumpMm)
                    accepted[i] = false;
            }

            bool anyAccepted = accepted.Any(a => a);
            for (int i = 0; i < n; i++)
            {
                if (accepted[i])
                {
                    stations.Add(new ProfileStation { ZMm = groundZsMm[i].Value, Flag = "ok" });
                    continue;
                }
                if (!anyAccepted)
                {
                    stations.Add(new ProfileStation { ZMm = null, Flag = "nodata" });
                    continue;
                }

                // Interpolate from the nearest accepted stations on each side.
                int lo = i, hi = i;
                while (lo >= 0 && !accepted[lo]) lo--;
                while (hi < n && !accepted[hi]) hi++;

                double z;
                if (lo < 0) z = groundZsMm[hi].Value;
                else if (hi >= n) z = groundZsMm[lo].Value;
                else
                {
                    double t = (i - lo) / (double)(hi - lo);
                    z = groundZsMm[lo].Value + t * (groundZsMm[hi].Value - groundZsMm[lo].Value);
                }
                stations.Add(new ProfileStation { ZMm = z, Flag = "interp" });
            }

            return stations;
        }

        /// <summary>
        /// Stations along a line at a fixed step (all mm). Returns null (with error set) when
        /// the line is degenerate or would need more than maxStations probes.
        /// </summary>
        public static List<(double X, double Y, double Dist)> BuildStations(double x1, double y1,
            double x2, double y2, double stepMm, int maxStations, out string error)
        {
            error = null;
            double dx = x2 - x1, dy = y2 - y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1)
            {
                error = "Line is degenerate (start and end are the same point).";
                return null;
            }
            if (stepMm <= 0)
            {
                error = "Step must be positive.";
                return null;
            }

            int count = (int)Math.Floor(length / stepMm) + 1;
            if (count > maxStations)
            {
                error = "Line needs " + count + " stations at step " + stepMm.ToString("F0") +
                        " mm (max " + maxStations + "). Increase the step to at least " +
                        Math.Ceiling(length / (maxStations - 1)).ToString("F0") + " mm.";
                return null;
            }

            var stations = new List<(double X, double Y, double Dist)>();
            for (int i = 0; i < count; i++)
            {
                double d = i * stepMm;
                stations.Add((x1 + dx * d / length, y1 + dy * d / length, d));
            }
            // Always include the exact endpoint if the step didn't land on it.
            if (length - stations[stations.Count - 1].Dist > 1 && stations.Count < maxStations)
                stations.Add((x2, y2, length));

            return stations;
        }

        private static int PrevValued(IList<double?> v, int i)
        {
            for (int j = i - 1; j >= 0; j--) if (v[j].HasValue) return j;
            return -1;
        }

        private static int NextValued(IList<double?> v, int i)
        {
            for (int j = i + 1; j < v.Count; j++) if (v[j].HasValue) return j;
            return -1;
        }
    }

    /// <summary>One occupied histogram bin (heights in mm).</summary>
    internal sealed class HistBin
    {
        public double ZStartMm { get; set; }
        public double WidthMm { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Outcome of <see cref="PointCloudAnalysis.EstimateGround"/>.</summary>
    internal sealed class GroundEstimate
    {
        public double? GroundZMm { get; set; }   // null = no dense band found
        public int GroundBandPoints { get; set; }
        public double MinZMm { get; set; }
        public double MaxZMm { get; set; }
        public int Count { get; set; }
        public List<HistBin> Bins { get; set; }
    }

    /// <summary>One station of a smoothed ground profile.</summary>
    internal sealed class ProfileStation
    {
        public double? ZMm { get; set; }
        public string Flag { get; set; }  // "ok" | "interp" | "nodata"
    }
}
