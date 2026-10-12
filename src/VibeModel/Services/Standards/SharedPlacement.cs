using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Is a Revit link where "Auto – By Shared Coordinates" would have put it? The link sits
    /// right when every link point lands on the same shared coordinates through the host
    /// (host internal → shared) as through the link's own setup (link internal → shared).
    /// Anything that cannot be determined is reported as such — never guessed as a fault.
    /// </summary>
    public static class SharedPlacement
    {
        public enum Verdict { Matches, Differs, Unknown }

        public sealed class Result
        {
            public Verdict Verdict;
            public string Why;
            public double OffsetMm;
            public double AngleDeg;
        }

        private const double FeetPerMm = 1.0 / 304.8;

        /// <summary>
        /// The transform internal → shared coordinates for a location. The API's total transform
        /// is checked against the location's own project position (shared coordinates of the
        /// internal origin) so the direction is measured, not assumed; null when it is ambiguous.
        /// </summary>
        public static Transform InternalToShared(ProjectLocation loc)
        {
            if (loc == null) return null;
            var t = loc.GetTotalTransform();
            var pos = loc.GetProjectPosition(XYZ.Zero);
            if (t == null || pos == null) return null;
            var expected = new XYZ(pos.EastWest, pos.NorthSouth, pos.Elevation);
            var inv = t.Inverse;
            bool forward = t.Origin.IsAlmostEqualTo(expected, FeetPerMm);
            bool backward = inv.Origin.IsAlmostEqualTo(expected, FeetPerMm);
            if (forward && !backward) return t;
            if (backward && !forward) return inv;
            if (forward && backward)
            {
                // Both origins coincide (no offset): only a rotation could tell them apart, and
                // without one the two directions are the same transform.
                if (NorthAngles.IsZero(pos.Angle * 180.0 / Math.PI, 1e-6)) return t;
            }
            return null;
        }

        /// <summary>Compare a link instance with the shared-coordinates position of its document.</summary>
        public static Result Check(Document host, RevitLinkInstance inst, double toleranceMm, double toleranceDeg)
        {
            Document linkDoc = null;
            try { linkDoc = inst.GetLinkDocument(); } catch { }
            if (linkDoc == null) return Unknown("link not loaded");

            var hostShared = InternalToShared(host.ActiveProjectLocation);
            if (hostShared == null) return Unknown("host coordinate transform ambiguous");

            Transform placed;
            try { placed = hostShared.Multiply(inst.GetTotalTransform()); }
            catch (Exception ex) { return Unknown("link transform unreadable: " + ex.Message); }

            // A link may carry several named locations (published sites); any match counts.
            Result best = null;
            var candidates = new List<ProjectLocation>();
            try { foreach (ProjectLocation l in linkDoc.ProjectLocations) candidates.Add(l); } catch { }
            if (candidates.Count == 0 && linkDoc.ActiveProjectLocation != null) candidates.Add(linkDoc.ActiveProjectLocation);

            foreach (var loc in candidates)
            {
                Transform linkShared;
                try { linkShared = InternalToShared(loc); } catch { linkShared = null; }
                if (linkShared == null) continue;
                var offsetMm = placed.Origin.DistanceTo(linkShared.Origin) * 304.8;
                var angle = NorthAngles.Normalize(NorthAngles.PlanAngle(placed.BasisX.X, placed.BasisX.Y) -
                                                  NorthAngles.PlanAngle(linkShared.BasisX.X, linkShared.BasisX.Y));
                var r = new Result { OffsetMm = offsetMm, AngleDeg = angle };
                r.Verdict = offsetMm <= toleranceMm && NorthAngles.IsZero(angle, toleranceDeg) ? Verdict.Matches : Verdict.Differs;
                if (r.Verdict == Verdict.Matches) return r;
                if (best == null || offsetMm < best.OffsetMm) best = r;
            }
            if (best == null) return Unknown("link coordinate transform ambiguous");
            best.Why = "off by " + Math.Round(best.OffsetMm) + " mm, " + NorthAngles.Format(best.AngleDeg) + "°";
            return best;
        }

        private static Result Unknown(string why) => new Result { Verdict = Verdict.Unknown, Why = why };
    }
}
