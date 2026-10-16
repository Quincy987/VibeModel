using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>One profile point in mm: u = horizontal, positive to the LEFT of the run direction; v = up.</summary>
    internal struct P2
    {
        public double U, V;
        public P2(double u, double v) { U = u; V = v; }
        public override string ToString() => ProfileGeometry.F(U) + "," + ProfileGeometry.F(V);
    }

    /// <summary>A validated, closed, counter-clockwise profile polygon (mm) and its section properties.</summary>
    internal sealed class Profile2D
    {
        public List<P2> Points;           // counter-clockwise in (u, v), not closed (first ≠ last)
        public double AreaMm2;
        public double CentroidU, CentroidV;
        public double MinU, MaxU, MinV, MaxV;
        public bool WasClockwise;         // the input ran clockwise and was reversed

        public double AreaM2 => AreaMm2 / 1e6;
        public double Width => MaxU - MinU;
        public double Height => MaxV - MinV;

        /// <summary>"3.92 m², 2500 x 4000 mm" — for responses.</summary>
        public string Describe() => ProfileGeometry.M2(AreaM2) + " m², " + ProfileGeometry.F(Width) + " x " + ProfileGeometry.F(Height) + " mm";
    }

    /// <summary>The sizes of an L-shaped quay wall (`lwall`), all mm.</summary>
    internal sealed class LwallSizes
    {
        public double Stem;               // stem thickness
        public double Height;             // total: underside of the foot → top of the stem
        public double Foot;               // total foot width (toe + under the stem + heel)
        public double FootThickness;      // at the stem
        public double Toe;                // how far the foot sticks out in front of the stem (water side), default 0
        public double? Slope;             // foot thickness at its back end (sloped foot top); null = flat

        public double Heel => Foot - Toe; // from the stem's front face to the back of the foot
    }

    /// <summary>
    /// Profile maths for `profile` / `extrude` / `sweep` / `lwall` (plan 12): polygon validation
    /// and normalisation, area / centroid, volumes along a line or arc, the L-wall polygon.
    /// Pure — unit-tested.
    /// </summary>
    internal static class ProfileGeometry
    {
        /// <summary>Points closer than this (mm) are the same point.</summary>
        public const double Tol = 0.5;

        public const int MaxPoints = 200;

        /// <summary>
        /// "u,v u,v …" (also "u v u v", ";" separated) → points. Errors name the offending token.
        /// </summary>
        public static List<P2> ParsePoints(string text, out string error)
        {
            error = null;
            var tokens = (text ?? "").Split(new[] { ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var nums = new List<double>();
            var pairs = new List<P2>();
            bool commaPairs = tokens.Length > 0 && tokens.All(t => t.Trim(',').IndexOf(',') > 0);
            if (commaPairs)
            {
                foreach (var t in tokens)
                {
                    var parts = t.Trim(',').Split(',');
                    if (parts.Length != 2 || !FlagArgs.TryNum(parts[0], out double u) || !FlagArgs.TryNum(parts[1], out double v))
                    { error = "'" + t + "' is not a point u,v (mm)."; return null; }
                    pairs.Add(new P2(u, v));
                }
                return pairs;
            }
            foreach (var t in tokens)
                foreach (var part in t.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!FlagArgs.TryNum(part, out double n)) { error = "'" + part + "' is not a number (mm)."; return null; }
                    nums.Add(n);
                }
            if (nums.Count % 2 != 0) { error = "An odd number of coordinates (" + nums.Count + "): give the points as u,v pairs."; return null; }
            for (int i = 0; i < nums.Count; i += 2) pairs.Add(new P2(nums[i], nums[i + 1]));
            return pairs;
        }

        /// <summary>"0,0 500,0 500,250" — the form <see cref="ParsePoints"/> reads back.</summary>
        public static string FormatPoints(IEnumerable<P2> pts) => string.Join(" ", pts.Select(p => p.ToString()));

        /// <summary>
        /// Close, clean and check a polygon: a repeated closing point and consecutive duplicates
        /// are dropped, collinear middle points removed, then fewer than 3 points, zero area or a
        /// self-intersecting outline is refused (naming the segments); clockwise input is reversed.
        /// </summary>
        public static Profile2D Normalize(IList<P2> input, out string error)
        {
            error = null;
            if (input == null || input.Count == 0) { error = "No points given."; return null; }
            if (input.Count > MaxPoints) { error = "At most " + MaxPoints + " points in a profile."; return null; }

            var pts = new List<P2>();
            foreach (var p in input)
                if (pts.Count == 0 || !Same(pts[pts.Count - 1], p)) pts.Add(p);
            while (pts.Count > 1 && Same(pts[0], pts[pts.Count - 1])) pts.RemoveAt(pts.Count - 1);
            pts = DropCollinear(pts);
            if (pts.Count < 3) { error = "A profile needs at least 3 different points (got " + pts.Count + ")."; return null; }

            // Crossing first: a bow tie has zero net area too, and naming the segments is the better answer.
            var cross = FindSelfIntersection(pts);
            if (cross != null) { error = "The outline crosses itself: " + cross + ". List the points in order around the shape."; return null; }

            double a = SignedArea(pts);
            if (Math.Abs(a) < 1) { error = "The profile has no area: its points lie on one line."; return null; }

            bool cw = a < 0;
            if (cw) { pts.Reverse(); a = -a; }
            Centroid(pts, out double cu, out double cv);
            return new Profile2D
            {
                Points = pts,
                AreaMm2 = a,
                CentroidU = cu,
                CentroidV = cv,
                MinU = pts.Min(p => p.U), MaxU = pts.Max(p => p.U),
                MinV = pts.Min(p => p.V), MaxV = pts.Max(p => p.V),
                WasClockwise = cw
            };
        }

        private static bool Same(P2 a, P2 b) => Math.Abs(a.U - b.U) <= Tol && Math.Abs(a.V - b.V) <= Tol;

        private static List<P2> DropCollinear(List<P2> pts)
        {
            bool changed = true;
            while (changed && pts.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count && pts.Count >= 3; i++)
                {
                    var prev = pts[(i - 1 + pts.Count) % pts.Count];
                    var cur = pts[i];
                    var next = pts[(i + 1) % pts.Count];
                    // Distance of cur from the line prev→next, and cur lies between them.
                    double dx = next.U - prev.U, dy = next.V - prev.V;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < Tol) continue;
                    double dist = Math.Abs((cur.U - prev.U) * dy - (cur.V - prev.V) * dx) / len;
                    double t = ((cur.U - prev.U) * dx + (cur.V - prev.V) * dy) / (len * len);
                    if (dist <= Tol && t > 0 && t < 1)
                    {
                        pts.RemoveAt(i);
                        changed = true;
                        i--;
                    }
                }
            }
            return pts;
        }

        /// <summary>Shoelace area, mm²: positive when counter-clockwise.</summary>
        public static double SignedArea(IList<P2> pts)
        {
            double s = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                s += a.U * b.V - b.U * a.V;
            }
            return s / 2;
        }

        public static void Centroid(IList<P2> pts, out double cu, out double cv)
        {
            double a = SignedArea(pts), su = 0, sv = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % pts.Count];
                double c = p.U * q.V - q.U * p.V;
                su += (p.U + q.U) * c;
                sv += (p.V + q.V) * c;
            }
            cu = Math.Abs(a) < 1e-12 ? 0 : su / (6 * a);
            cv = Math.Abs(a) < 1e-12 ? 0 : sv / (6 * a);
        }

        /// <summary>"segment 2 (500,0)→(500,250) crosses segment 5 (…)", or null when the outline is simple.</summary>
        public static string FindSelfIntersection(IList<P2> pts)
        {
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var a1 = pts[i];
                var a2 = pts[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    // Neighbouring segments share a point; that is not a crossing.
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                    var b1 = pts[j];
                    var b2 = pts[(j + 1) % n];
                    if (SegmentsTouch(a1, a2, b1, b2))
                        return "segment " + (i + 1) + " " + Seg(a1, a2) + " crosses segment " + (j + 1) + " " + Seg(b1, b2);
                }
            }
            return null;
        }

        private static string Seg(P2 a, P2 b) => "(" + a + ")→(" + b + ")";

        private static bool SegmentsTouch(P2 p1, P2 p2, P2 q1, P2 q2)
        {
            double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            return (Math.Abs(d1) < 1e-9 && OnSegment(q1, q2, p1)) || (Math.Abs(d2) < 1e-9 && OnSegment(q1, q2, p2)) ||
                   (Math.Abs(d3) < 1e-9 && OnSegment(p1, p2, q1)) || (Math.Abs(d4) < 1e-9 && OnSegment(p1, p2, q2));
        }

        private static double Cross(P2 a, P2 b, P2 c) => (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);

        private static bool OnSegment(P2 a, P2 b, P2 p)
            => Math.Min(a.U, b.U) - 1e-9 <= p.U && p.U <= Math.Max(a.U, b.U) + 1e-9 &&
               Math.Min(a.V, b.V) - 1e-9 <= p.V && p.V <= Math.Max(a.V, b.V) + 1e-9;

        /// <summary>u → −u (the other side of the line), winding kept counter-clockwise.</summary>
        public static List<P2> Mirror(IEnumerable<P2> pts)
        {
            var m = pts.Select(p => new P2(-p.U, p.V)).ToList();
            m.Reverse();
            return m;
        }

        /// <summary>m³ of a profile (mm²) extruded over a length (mm).</summary>
        public static double VolumeM3(double areaMm2, double lengthMm) => areaMm2 * lengthMm / 1e9;

        /// <summary>
        /// Length (mm) of the path the profile's centroid travels along an arc of the given radius
        /// and turning angle (Pappus): the centroid sits <paramref name="centroidU"/> to the left
        /// of the arc, so on a left-turning arc its radius is smaller.
        /// </summary>
        public static double CentroidPathOnArc(double radiusMm, double angleRad, double centroidU, bool turnsLeft)
        {
            double r = turnsLeft ? radiusMm - centroidU : radiusMm + centroidU;
            return Math.Abs(angleRad) * r;
        }

        /// <summary>Volume (m³) of a profile swept along an arc, by Pappus.</summary>
        public static double VolumeOnArcM3(Profile2D p, double radiusMm, double angleRad, bool turnsLeft)
            => VolumeM3(p.AreaMm2, CentroidPathOnArc(radiusMm, angleRad, p.CentroidU, turnsLeft));

        /// <summary>
        /// The L: the placement line is the stem's front face (u = 0); stem u 0..stem from the foot
        /// top to the top; the foot u −toe..(foot − toe), its top flat at the foot thickness or —
        /// with a slope — falling to that thickness at its back end. Heel on the left (+u).
        /// </summary>
        public static List<P2> LwallPolygon(LwallSizes s)
        {
            double t = s.Toe, back = s.Foot - s.Toe, F = s.FootThickness;
            double backTop = s.Slope ?? F;
            return new List<P2>
            {
                new P2(-t, 0), new P2(back, 0), new P2(back, backTop), new P2(s.Stem, F),
                new P2(s.Stem, s.Height), new P2(0, s.Height), new P2(0, F), new P2(-t, F)
            };
        }

        /// <summary>Sizes that make an L, or the reason they do not.</summary>
        public static string CheckLwall(LwallSizes s)
        {
            if (s.Stem <= 0 || s.Height <= 0 || s.Foot <= 0 || s.FootThickness <= 0)
                return "Every size must be positive (stem " + F(s.Stem) + "x" + F(s.Height) + ", foot " + F(s.Foot) + "x" + F(s.FootThickness) + ").";
            if (s.Toe < 0) return "--toe cannot be negative.";
            if (s.FootThickness >= s.Height) return "The foot (" + F(s.FootThickness) + " mm) must be thinner than the total height (" + F(s.Height) + " mm).";
            if (s.Heel < s.Stem)
                return "The foot (" + F(s.Foot) + " mm) is too narrow for the stem (" + F(s.Stem) + " mm)" +
                       (s.Toe > 0 ? " plus the toe (" + F(s.Toe) + " mm)" : "") + ": the foot must reach at least to the back of the stem.";
            if (s.Slope.HasValue && (s.Slope <= 0 || s.Slope > s.FootThickness))
                return "--slope is the foot thickness at its back end: more than 0 and at most the foot thickness (" + F(s.FootThickness) + " mm).";
            return null;
        }

        internal static string F(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        internal static string M2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        internal static string M3(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Names and update logic of generated families (plan 12 §2.3). Pure — unit-tested.
    /// </summary>
    internal static class FamilyNaming
    {
        public const string DefaultPrefix = "VM_";
        public const string DefaultLwallTypePattern = "{stem}x{height} voet {foot}x{footThickness}";
        /// <summary>Bumped when a builder changes what it generates: a stamp of an older version means "regenerate".</summary>
        public const string GeneratorVersion = "vm1";

        // Revit refuses these in family and type names.
        private static readonly char[] Forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Forbidden characters → "-", whitespace collapsed, trimmed. A family name is also a
        /// file name (the .rfa), so <paramref name="fileSafe"/> also replaces / * " .</summary>
        public static string Sanitize(string name, bool fileSafe = false)
        {
            var sb = new StringBuilder();
            foreach (var c in (name ?? "").Trim())
                sb.Append(Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c) || (fileSafe && (c == '/' || c == '*' || c == '"')) ? '-' : c);
            var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return s.Trim('.', ' ');
        }

        /// <summary>"&lt;prefix&gt;&lt;name&gt;", without doubling a prefix the name already carries; file-safe.</summary>
        public static string FamilyName(string prefix, string name)
        {
            var p = string.IsNullOrEmpty(prefix) ? "" : prefix;
            var n = Sanitize(name, true);
            return p.Length > 0 && n.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? n : Sanitize(p + n, true);
        }

        /// <summary>
        /// The type name for a size combination: the pattern with {stem} {height} {foot}
        /// {footThickness} {toe} {slope}. A toe or slope the pattern does not show is appended,
        /// so two different L's never share one type.
        /// </summary>
        public static string LwallTypeName(string pattern, LwallSizes s)
        {
            var p = string.IsNullOrWhiteSpace(pattern) ? DefaultLwallTypePattern : pattern;
            var name = p.Replace("{stem}", ProfileGeometry.F(s.Stem))
                        .Replace("{height}", ProfileGeometry.F(s.Height))
                        .Replace("{foot}", ProfileGeometry.F(s.Foot))
                        .Replace("{footThickness}", ProfileGeometry.F(s.FootThickness))
                        .Replace("{toe}", ProfileGeometry.F(s.Toe))
                        .Replace("{slope}", s.Slope.HasValue ? ProfileGeometry.F(s.Slope.Value) : "");
            if (s.Toe > 0 && p.IndexOf("{toe}", StringComparison.Ordinal) < 0) name += " teen " + ProfileGeometry.F(s.Toe);
            if (s.Slope.HasValue && p.IndexOf("{slope}", StringComparison.Ordinal) < 0) name += " schuin " + ProfileGeometry.F(s.Slope.Value);
            return Sanitize(name);
        }

        /// <summary>A short, stable hash of the parts (12 hex characters).</summary>
        public static string Hash(params string[] parts)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\u001f", parts ?? new string[0])));
                return string.Concat(bytes.Take(6).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>The VM_Source value: "vm1;&lt;spec hash&gt;;&lt;structure hash&gt;".</summary>
        public static string Stamp(string specHash, string structureHash)
            => GeneratorVersion + ";" + specHash + ";" + structureHash;

        public static bool TryParseStamp(string stamp, out string version, out string specHash, out string structureHash)
        {
            version = specHash = structureHash = null;
            var parts = (stamp ?? "").Split(';');
            if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) return false;
            version = parts[0]; specHash = parts[1]; structureHash = parts[2];
            return true;
        }

        public enum Action
        {
            Create,         // no family of that name: generate and load
            Reuse,          // ours, same spec: load nothing, place / add a type
            Regenerate,     // ours, the spec changed (or an older generator): generate and reload, keeping project values
            RefuseEdited,   // ours, but changed in Revit since: only with --replace
            RefuseForeign   // a family of that name we did not generate: only with --replace
        }

        /// <summary>
        /// What to do with a family name. <paramref name="currentStructure"/> is the structure hash
        /// of the loaded family (from opening it), or null when it was not read — it is only
        /// needed when the family would be overwritten.
        /// </summary>
        public static Action Decide(bool exists, string existingStamp, string newSpecHash, string currentStructure, bool replace)
        {
            if (!exists) return Action.Create;
            // --replace always regenerates — also for an unchanged spec: that is how a family edited
            // by hand (live: an extra extrusion, "reused" with its volume) gets its generated shape back.
            if (replace) return Action.Regenerate;
            if (!TryParseStamp(existingStamp, out var version, out var spec, out var structure))
                return Action.RefuseForeign;
            if (version == GeneratorVersion && spec == newSpecHash) return Action.Reuse;
            if (currentStructure != null && currentStructure != structure) return Action.RefuseEdited;
            return Action.Regenerate;
        }

        /// <summary>Does regenerating need the loaded family's current structure (i.e. opening it)?</summary>
        public static bool NeedsStructureCheck(bool exists, string existingStamp, string newSpecHash, bool replace)
            => exists && !replace && TryParseStamp(existingStamp, out var version, out var spec, out _) &&
               !(version == GeneratorVersion && spec == newSpecHash);
    }

    /// <summary>
    /// Orders unordered path pieces (by their end points, mm) into one chain for `sweep`.
    /// Pure — unit-tested.
    /// </summary>
    internal static class PathChain
    {
        public struct Piece
        {
            public double X0, Y0, X1, Y1;
            public Piece(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        }

        /// <summary>
        /// The pieces as one open (or closed) chain: (index, reversed) in travel order, starting
        /// at the piece end no other piece touches (the first given piece's start when it is
        /// closed or ambiguous). Null + error when the pieces do not form one chain.
        /// </summary>
        public static List<KeyValuePair<int, bool>> Order(IList<Piece> pieces, double tolMm, out string error)
        {
            error = null;
            int n = pieces?.Count ?? 0;
            if (n == 0) { error = "No path given."; return null; }
            if (n == 1) return new List<KeyValuePair<int, bool>> { new KeyValuePair<int, bool>(0, false) };

            // Where to start: an end that touches no other piece.
            int startPiece = 0; bool startReversed = false;
            bool found = false;
            for (int i = 0; i < n && !found; i++)
                for (int end = 0; end < 2 && !found; end++)
                {
                    double x = end == 0 ? pieces[i].X0 : pieces[i].X1, y = end == 0 ? pieces[i].Y0 : pieces[i].Y1;
                    int touching = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        if (Near(x, y, pieces[j].X0, pieces[j].Y0, tolMm) || Near(x, y, pieces[j].X1, pieces[j].Y1, tolMm)) touching++;
                    }
                    if (touching == 0) { startPiece = i; startReversed = end == 1; found = true; }
                }

            var order = new List<KeyValuePair<int, bool>> { new KeyValuePair<int, bool>(startPiece, startReversed) };
            var used = new HashSet<int> { startPiece };
            double cx = startReversed ? pieces[startPiece].X0 : pieces[startPiece].X1;
            double cy = startReversed ? pieces[startPiece].Y0 : pieces[startPiece].Y1;
            while (order.Count < n)
            {
                int next = -1; bool rev = false;
                for (int j = 0; j < n && next < 0; j++)
                {
                    if (used.Contains(j)) continue;
                    if (Near(cx, cy, pieces[j].X0, pieces[j].Y0, tolMm)) { next = j; rev = false; }
                    else if (Near(cx, cy, pieces[j].X1, pieces[j].Y1, tolMm)) { next = j; rev = true; }
                }
                if (next < 0)
                {
                    var loose = Enumerable.Range(0, n).Where(j => !used.Contains(j)).Select(j => "#" + (j + 1));
                    error = "The path pieces do not form one connected chain: nothing continues at (" + ProfileGeometry.F(cx) + ", " +
                            ProfileGeometry.F(cy) + "); not connected: " + string.Join(", ", loose) + ".";
                    return null;
                }
                order.Add(new KeyValuePair<int, bool>(next, rev));
                used.Add(next);
                cx = rev ? pieces[next].X0 : pieces[next].X1;
                cy = rev ? pieces[next].Y0 : pieces[next].Y1;
            }
            return order;
        }

        private static bool Near(double x0, double y0, double x1, double y1, double tol)
            => Math.Abs(x0 - x1) <= tol && Math.Abs(y0 - y1) <= tol;
    }

    /// <summary>
    /// The shared-parameter file VibeModel writes for its generated families: fixed GUIDs, so
    /// every generated family schedules into the same VM_Length column; VM_Source is hidden.
    /// Pure — unit-tested.
    /// </summary>
    internal static class GeneratedSharedParameters
    {
        public const string FileName = "VibeModel.SharedParameters.txt";
        public const string LengthName = "VM_Length";
        public const string SourceName = "VM_Source";
        public static readonly Guid LengthGuid = new Guid("5b2f0c1e-7a43-4f9e-9c1d-3e8a6b2d4f10");
        public static readonly Guid SourceGuid = new Guid("c4e8d2a7-0b6f-4b1e-a3d5-8f27e9c1b604");

        public static string FileText()
        {
            var sb = new StringBuilder();
            sb.Append("# This is a Revit shared parameter file.\r\n# Do not edit manually.\r\n");
            sb.Append("*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n");
            sb.Append("*GROUP\tID\tNAME\r\nGROUP\t1\tVibeModel\r\n");
            sb.Append("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n");
            sb.Append("PARAM\t" + LengthGuid + "\t" + LengthName + "\tLENGTH\t\t1\t1\tLength of a VibeModel generated element (its run)\t0\t0\r\n");
            sb.Append("PARAM\t" + SourceGuid + "\t" + SourceName + "\tTEXT\t\t1\t0\tVibeModel generator stamp (do not edit)\t0\t0\r\n");
            return sb.ToString();
        }
    }
}
