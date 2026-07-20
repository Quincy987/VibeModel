using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Fetches a vertical column of point-cloud points around (x, y) and returns their
    /// model-space Z values in mm.
    ///
    /// The Revit point-cloud filter API is ambiguous about which coordinate space the filter
    /// planes live in (cloud-local vs model) and which side of the planes passes, and clouds
    /// carry a placement transform. Rather than hard-coding an assumption, this helper tries
    /// the combinations until one yields points, then caches the winning combination for the
    /// session. Returned coordinates are sanity-checked against the instance's model-space
    /// bounding box to decide whether the instance transform must be applied.
    /// </summary>
    public static class PointCloudHelper
    {
        // Winning filter strategy, cached after first successful fetch (per Revit session).
        private static int _knownStrategy = -1;

        public static List<double> GetColumnZsMm(PointCloudInstance cloud, double xMm, double yMm,
            double radiusMm, int maxPoints, out string diagnostics)
        {
            diagnostics = null;
            var bbox = cloud.get_BoundingBox(null);
            if (bbox == null)
            {
                diagnostics = "Point cloud has no bounding box.";
                return new List<double>();
            }

            double xFt = RevitUnitHelper.MmToFeet(xMm);
            double yFt = RevitUnitHelper.MmToFeet(yMm);
            double rFt = RevitUnitHelper.MmToFeet(radiusMm);
            // Pad Z so points at the exact extremes are not clipped.
            double zMinFt = bbox.Min.Z - 3;
            double zMaxFt = bbox.Max.Z + 3;

            var transform = cloud.GetTotalTransform();
            var inverse = transform.Inverse;

            // Strategies: filter planes in (cloud space | model space) x (inward | outward normals).
            var strategies = _knownStrategy >= 0
                ? new List<int> { _knownStrategy }
                : new List<int> { 0, 1, 2, 3 };

            foreach (int strategy in strategies)
            {
                bool cloudSpace = strategy < 2;
                bool inward = strategy % 2 == 0;

                var planes = BuildBoxPlanes(xFt, yFt, rFt, zMinFt, zMaxFt, inward,
                    cloudSpace ? inverse : null);

                PointCollection collection;
                try
                {
                    var filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
                    // ~10mm average spacing keeps a street-column sample dense but bounded.
                    collection = cloud.GetPoints(filter, RevitUnitHelper.MmToFeet(10), maxPoints);
                }
                catch (Exception ex)
                {
                    diagnostics = "GetPoints failed: " + ex.Message;
                    continue;
                }

                var raw = new List<XYZ>();
                foreach (CloudPoint p in collection)
                    raw.Add(new XYZ(p.X, p.Y, p.Z));

                if (raw.Count == 0) continue;

                // A wrong space/orientation combo can still return points — just not ones
                // inside the probe column. Accept the strategy only if points survive the
                // column check, otherwise a bad combination would be cached as the winner.
                var zs = ToModelZsMm(raw, transform, bbox, xMm, yMm, radiusMm);
                if (zs.Count == 0) continue;

                _knownStrategy = strategy;
                diagnostics = "filter=" + (cloudSpace ? "cloud" : "model") + "-space/" +
                              (inward ? "inward" : "outward") + ", points=" + zs.Count + "/" + raw.Count;

                return zs;
            }

            // Nothing worked with the cached strategy — clear it so the next call re-probes.
            _knownStrategy = -1;
            return new List<double>();
        }

        /// <summary>
        /// Decide whether the fetched points are cloud-local (need the instance transform) or
        /// already model-space, using the instance's model bbox as ground truth, then return
        /// model-space Zs in mm. Points outside the probe column are discarded (a filter that
        /// over-returns still yields a correct column).
        /// </summary>
        private static List<double> ToModelZsMm(List<XYZ> raw, Transform transform,
            BoundingBoxXYZ modelBbox, double xMm, double yMm, double radiusMm)
        {
            var transformed = raw.Select(p => transform.OfPoint(p)).ToList();

            int fitTransformed = transformed.Count(p => InBbox(p, modelBbox));
            int fitRaw = raw.Count(p => InBbox(p, modelBbox));

            var modelPts = fitTransformed >= fitRaw ? transformed : raw;

            var zs = new List<double>();
            foreach (var p in modelPts)
            {
                double pxMm = RevitUnitHelper.FeetToMm(p.X);
                double pyMm = RevitUnitHelper.FeetToMm(p.Y);
                if (Math.Abs(pxMm - xMm) <= radiusMm && Math.Abs(pyMm - yMm) <= radiusMm)
                    zs.Add(RevitUnitHelper.FeetToMm(p.Z));
            }
            return zs;
        }

        private static bool InBbox(XYZ p, BoundingBoxXYZ b)
        {
            const double pad = 3; // feet
            return p.X >= b.Min.X - pad && p.X <= b.Max.X + pad &&
                   p.Y >= b.Min.Y - pad && p.Y <= b.Max.Y + pad &&
                   p.Z >= b.Min.Z - pad && p.Z <= b.Max.Z + pad;
        }

        private static IList<Plane> BuildBoxPlanes(double xFt, double yFt, double rFt,
            double zMinFt, double zMaxFt, bool inward, Transform toCloudSpace)
        {
            double s = inward ? 1 : -1;
            var defs = new List<(XYZ Origin, XYZ Normal)>
            {
                (new XYZ(xFt - rFt, yFt, 0), new XYZ(s, 0, 0)),
                (new XYZ(xFt + rFt, yFt, 0), new XYZ(-s, 0, 0)),
                (new XYZ(xFt, yFt - rFt, 0), new XYZ(0, s, 0)),
                (new XYZ(xFt, yFt + rFt, 0), new XYZ(0, -s, 0)),
                (new XYZ(xFt, yFt, zMinFt), new XYZ(0, 0, s)),
                (new XYZ(xFt, yFt, zMaxFt), new XYZ(0, 0, -s)),
            };

            var planes = new List<Plane>();
            foreach (var (origin, normal) in defs)
            {
                if (toCloudSpace == null)
                {
                    planes.Add(Plane.CreateByNormalAndOrigin(normal, origin));
                }
                else
                {
                    var o = toCloudSpace.OfPoint(origin);
                    var n = toCloudSpace.OfVector(normal).Normalize();
                    planes.Add(Plane.CreateByNormalAndOrigin(n, o));
                }
            }
            return planes;
        }
    }
}
