using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>What part of an annotation a box stands for (plan 21).</summary>
    internal enum AnnoKind
    {
        /// <summary>A text note's box.</summary>
        Text,
        /// <summary>A tag's head (estimated around TagHeadPosition).</summary>
        Tag,
        /// <summary>The value text of one dimension segment.</summary>
        DimensionText,
        /// <summary>The dimension line itself, a thin strip between the outer references.</summary>
        DimensionLine,
        /// <summary>A spot elevation's value text.</summary>
        SpotText,
        /// <summary>A leader segment (spot, tag or text), a thin strip.</summary>
        Leader
    }

    /// <summary>
    /// One box of an annotation in paper mm, in the view's own frame (x along the view's right
    /// direction, y along its up direction). An annotation can have several boxes (a dimension:
    /// one per segment text plus its line; a spot: its text plus the leader segments).
    /// </summary>
    internal sealed class AnnoBox
    {
        public int Id;
        public AnnoKind Kind;
        public MmRect Rect;
        /// <summary>Which element the annotation hangs on (0 = none / unknown).</summary>
        public int Host;
        /// <summary>Text angle in degrees relative to the view's right direction (text and tags only).</summary>
        public double AngleDeg;
        /// <summary>The text the box holds, shortened (for the report).</summary>
        public string Label;
        /// <summary>Lines and leaders: true when the strip runs along the view's x (else along y).</summary>
        public bool AlongX;

        public bool IsText => Kind == AnnoKind.Text || Kind == AnnoKind.Tag || Kind == AnnoKind.DimensionText || Kind == AnnoKind.SpotText;
        public bool IsLine => Kind == AnnoKind.DimensionLine || Kind == AnnoKind.Leader;

        /// <summary>The annotation's word in reports: "spot", "dimension", "tag", "text".</summary>
        public string Word
        {
            get
            {
                switch (Kind)
                {
                    case AnnoKind.Tag: return "tag";
                    case AnnoKind.DimensionText:
                    case AnnoKind.DimensionLine: return "dimension";
                    case AnnoKind.SpotText: return "spot";
                    case AnnoKind.Leader: return LeaderOf ?? "leader";
                    default: return "text";
                }
            }
        }

        /// <summary>For a leader: the word of the annotation it belongs to ("spot", "tag", "text").</summary>
        public string LeaderOf;

        /// <summary>"spot 1730946 text", "dimension 1730940 line", "tag 1731002".</summary>
        public string Name
        {
            get
            {
                string part = Kind == AnnoKind.DimensionText ? " text '" + Label + "'"
                            : Kind == AnnoKind.DimensionLine ? " line"
                            : Kind == AnnoKind.SpotText ? " text"
                            : Kind == AnnoKind.Leader ? " leader" : "";
                return Word + " " + Id.ToString(CultureInfo.InvariantCulture) + part;
            }
        }
    }

    /// <summary>One conflict between two boxes.</summary>
    internal sealed class AnnoOverlap
    {
        public AnnoBox A, B;
        /// <summary>"WARN" or "INFO".</summary>
        public string Severity;
        /// <summary>"overlap", "line-through-text", "stacked".</summary>
        public string Kind;
        public double W, H;

        public bool Involves(int id) => A.Id == id || B.Id == id;
        public bool IsWarn => Severity == "WARN";
    }

    /// <summary>
    /// The layout check behind `annolayout` and the `-- layout:` line (plan 21): which
    /// annotation boxes of one view collide on paper, graded like `sheetinfo`'s viewport check.
    /// Pure — unit-tested; the Revit side (`AnnotationExtents`) only collects the boxes.
    /// </summary>
    internal static class AnnotationLayout
    {
        /// <summary>Measured on NLCS-ISO (plan 21): width per character = 0.69–0.72 × text size × width factor.</summary>
        public const double CharWidth = 0.72;
        /// <summary>Measured: a 2.5 mm text's box rises 4.7 mm above its TextPosition (line spacing included).</summary>
        public const double LineHeight = 1.9;
        /// <summary>An overlap no thicker than this (paper mm) is a touching strip, not a collision.</summary>
        public const double ThinMm = 1.0;
        /// <summary>Two parallel dimension lines closer than this (paper mm) read as one.</summary>
        public const double StackedMm = 2.0;
        /// <summary>Thickness given to a line or leader strip (paper mm).</summary>
        public const double LineMm = 0.3;
        /// <summary>A text or tag more than this off horizontal (degrees) counts as rotated.</summary>
        public const double RotatedDeg = 0.5;

        /// <summary>Estimated width × height (paper mm) of a one-line text.</summary>
        public static void TextExtent(int chars, double sizeMm, double widthFactor, out double w, out double h)
        {
            if (sizeMm <= 0 || double.IsNaN(sizeMm)) sizeMm = 2.5;
            if (widthFactor <= 0 || double.IsNaN(widthFactor)) widthFactor = 1;
            w = Math.Max(1, chars) * sizeMm * widthFactor * CharWidth;
            h = sizeMm * LineHeight;
        }

        /// <summary>The box of a one-line text centred horizontally on (cx, y) and rising from y.</summary>
        public static MmRect TextBox(double cx, double y, string text, double sizeMm, double widthFactor, bool vertical = false)
        {
            TextExtent((text ?? "").Length, sizeMm, widthFactor, out var w, out var h);
            // A vertical dimension's text reads bottom-to-top and stands to the left of its line.
            return vertical ? new MmRect(cx - h, y - w / 2, cx, y + w / 2) : new MmRect(cx - w / 2, y, cx + w / 2, y + h);
        }

        /// <summary>A thin strip along the segment (x0,y0)-(x1,y1).</summary>
        public static MmRect Strip(double x0, double y0, double x1, double y1)
        {
            double t = LineMm / 2;
            return new MmRect(Math.Min(x0, x1) - t, Math.Min(y0, y1) - t, Math.Max(x0, x1) + t, Math.Max(y0, y1) + t);
        }

        /// <summary>
        /// Every conflict between boxes of different annotations: two texts that overlap (a strip
        /// ≤ 1 mm is INFO), a dimension line or leader running through another annotation's text
        /// (WARN), and two parallel dimension lines closer than 2 mm whose spans overlap (WARN,
        /// "stacked"). Lines crossing lines are normal and not reported.
        /// </summary>
        public static List<AnnoOverlap> Overlaps(IList<AnnoBox> boxes)
        {
            var result = new List<AnnoOverlap>();
            var seen = new HashSet<string>();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i]; var b = boxes[j];
                    if (a.Id == b.Id) continue;
                    var o = Judge(a, b);
                    if (o == null) continue;
                    // One finding per pair of annotations and kind (a dimension with five texts
                    // under one spot is one collision, not five) — the biggest one wins.
                    string key = Math.Min(a.Id, b.Id) + "|" + Math.Max(a.Id, b.Id) + "|" + o.Kind;
                    if (!seen.Add(key))
                    {
                        var prev = result.First(r => r.Kind == o.Kind && (r.A.Id == a.Id && r.B.Id == b.Id || r.A.Id == b.Id && r.B.Id == a.Id));
                        if (o.IsWarn && !prev.IsWarn || o.W * o.H > prev.W * prev.H && o.IsWarn == prev.IsWarn)
                            result[result.IndexOf(prev)] = o;
                        continue;
                    }
                    result.Add(o);
                }
            return result.OrderBy(r => r.IsWarn ? 0 : 1).ThenByDescending(r => r.W * r.H).ToList();
        }

        private static AnnoOverlap Judge(AnnoBox a, AnnoBox b)
        {
            if (a.IsText && b.IsText)
            {
                if (!a.Rect.Overlaps(b.Rect, 0.05)) return null;
                a.Rect.OverlapSize(b.Rect, out var w, out var h);
                return new AnnoOverlap { A = a, B = b, Kind = "overlap", W = w, H = h, Severity = Math.Min(w, h) <= ThinMm ? "INFO" : "WARN" };
            }
            if (a.IsLine && b.IsLine)
            {
                if (a.Kind != AnnoKind.DimensionLine || b.Kind != AnnoKind.DimensionLine || a.AlongX != b.AlongX) return null;
                // Parallel dimension lines: spans overlap along the line, and lie close across it.
                double s0 = a.AlongX ? Math.Max(a.Rect.X0, b.Rect.X0) : Math.Max(a.Rect.Y0, b.Rect.Y0);
                double s1 = a.AlongX ? Math.Min(a.Rect.X1, b.Rect.X1) : Math.Min(a.Rect.Y1, b.Rect.Y1);
                if (s1 - s0 <= ThinMm) return null;
                double gap = a.AlongX ? Math.Abs(a.Rect.Cy - b.Rect.Cy) : Math.Abs(a.Rect.Cx - b.Rect.Cx);
                if (gap >= StackedMm) return null;
                return new AnnoOverlap { A = a, B = b, Kind = "stacked", W = s1 - s0, H = gap, Severity = "WARN" };
            }
            // A line or leader through a text.
            var line = a.IsLine ? a : b;
            var text = a.IsLine ? b : a;
            if (!line.Rect.Overlaps(text.Rect, 0.05)) return null;
            line.Rect.OverlapSize(text.Rect, out var lw, out var lh);
            double len = line.AlongX ? lw : lh;
            if (len <= ThinMm) return null;   // only clips a corner
            return new AnnoOverlap { A = line, B = text, Kind = "line-through-text", W = lw, H = lh, Severity = "WARN" };
        }

        /// <summary>Texts and tags that are not horizontal (more than 0.5° off the view's right direction).</summary>
        public static List<AnnoBox> Rotated(IEnumerable<AnnoBox> boxes)
            => boxes.Where(b => (b.Kind == AnnoKind.Text || b.Kind == AnnoKind.Tag) && Math.Abs(Normalize(b.AngleDeg)) > RotatedDeg).ToList();

        /// <summary>An angle in (-180, 180].</summary>
        public static double Normalize(double deg)
        {
            deg %= 360;
            if (deg > 180) deg -= 360;
            if (deg <= -180) deg += 360;
            return deg;
        }

        /// <summary>One report line per conflict, `sheetinfo` style.</summary>
        public static string Line(AnnoOverlap o)
        {
            switch (o.Kind)
            {
                case "stacked":
                    return o.Severity + "  " + o.A.Name + " and " + o.B.Name + " lie " + Mm.F1(o.H) + " mm apart over " + Mm.F(o.W) +
                           " mm (stacked: they read as one) — give one another row ('dimension … --offset')";
                case "line-through-text":
                    return o.Severity + "  " + o.A.Name + " runs through " + o.B.Name + " (" + Mm.F1(o.W) + "×" + Mm.F1(o.H) + " mm)";
                default:
                    return o.Severity + "  " + o.A.Name + " and " + o.B.Name + " overlap " + Mm.F1(o.W) + "×" + Mm.F1(o.H) + " mm" +
                           (o.IsWarn ? "" : " (thin strip — touching)");
            }
        }

        /// <summary>
        /// The one `-- layout:` line after a command: only WARN conflicts that involve an
        /// annotation the command added; null when those sit clear. At most three are named.
        /// </summary>
        public static string LayoutLine(IEnumerable<int> newIds, IList<AnnoOverlap> overlaps, string viewName)
        {
            var ids = new HashSet<int>(newIds ?? Enumerable.Empty<int>());
            var hits = (overlaps ?? new List<AnnoOverlap>()).Where(o => o.IsWarn && (ids.Contains(o.A.Id) || ids.Contains(o.B.Id))).ToList();
            if (hits.Count == 0) return null;
            var named = hits.Take(3).Select(Short).ToList();
            string more = hits.Count > 3 ? "; +" + (hits.Count - 3) + " more" : "";
            return "-- layout: " + hits.Count + (hits.Count == 1 ? " overlap" : " overlaps") + " in '" + viewName + "': " +
                   string.Join("; ", named) + more + " → run 'annolayout " + viewName + "' for the boxes";
        }

        /// <summary>Several views' lines joined into one (a batch annotating more than one view).</summary>
        public static string JoinLines(IList<string> lines)
        {
            var real = (lines ?? new List<string>()).Where(l => l != null).ToList();
            if (real.Count == 0) return null;
            if (real.Count == 1) return real[0];
            return "-- layout: " + string.Join(" | ", real.Select(l => l.StartsWith("-- layout: ", StringComparison.Ordinal) ? l.Substring(11) : l));
        }

        private static string Short(AnnoOverlap o)
        {
            switch (o.Kind)
            {
                case "stacked": return o.A.Word + " " + o.A.Id + " ↔ " + o.B.Word + " " + o.B.Id + " (stacked, " + Mm.F1(o.H) + " mm apart)";
                case "line-through-text": return o.A.Word + " " + o.A.Id + " " + (o.A.Kind == AnnoKind.Leader ? "leader" : "line") + " through " + o.B.Word + " " + o.B.Id;
                default: return o.A.Word + " " + o.A.Id + " ↔ " + o.B.Word + " " + o.B.Id + " (" + Mm.F1(o.W) + "×" + Mm.F1(o.H) + " mm)";
            }
        }

        /// <summary>JSON for the `layout` object / `annolayout`'s overlaps.</summary>
        public static List<object> OverlapsJson(IEnumerable<AnnoOverlap> overlaps)
            => overlaps.Select(o => (object)new Dictionary<string, object>
            {
                { "a", o.A.Id }, { "aPart", o.A.Kind.ToString() }, { "b", o.B.Id }, { "bPart", o.B.Kind.ToString() },
                { "kind", o.Kind }, { "severity", o.Severity }, { "mm", new List<object> { Math.Round(o.W, 1), Math.Round(o.H, 1) } }
            }).ToList();

        // ------------------------------------------------------------------ placement helpers

        /// <summary>
        /// The first candidate box that collides with none of <paramref name="taken"/> (gap kept:
        /// <paramref name="clearMm"/>); -1 when all collide. <paramref name="blocker"/> = the index
        /// of the taken box that blocked the first candidate (for "up-right taken by spot …").
        /// </summary>
        public static int FirstFree(IList<MmRect> candidates, IList<MmRect> taken, out int blocker, double clearMm = 0.5)
        {
            blocker = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = new MmRect(candidates[i].X0 - clearMm, candidates[i].Y0 - clearMm, candidates[i].X1 + clearMm, candidates[i].Y1 + clearMm);
                int hit = -1;
                for (int j = 0; j < taken.Count && hit < 0; j++)
                    if (c.Overlaps(taken[j], 0.05)) hit = j;
                if (hit < 0) return i;
                if (i == 0) blocker = hit;
            }
            return -1;
        }

        /// <summary>A candidate place for a new annotation: its text box and its leader strips (paper mm).</summary>
        internal sealed class Slot
        {
            public string Name;
            public MmRect Text;
            public List<MmRect> Leader = new List<MmRect>();
            /// <summary>Spot slots: the shoulder's height over the point (paper mm, signed) and the leader's side (+1 right, -1 left).</summary>
            public double Rise, Dir;
        }

        /// <summary>
        /// The slot for a new spot or tag: the first whose text clears every taken box and whose
        /// leader runs through no other text. When none is free (<paramref name="free"/> false)
        /// the least crowded one. <paramref name="firstBlocker"/> = what blocked the first slot.
        /// </summary>
        public static int PickSlot(IList<Slot> slots, IList<AnnoBox> taken, out AnnoBox firstBlocker, out bool free)
        {
            firstBlocker = null;
            free = false;
            int best = -1, bestHits = int.MaxValue;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                var hits = new List<AnnoBox>();
                var t = new MmRect(s.Text.X0 - 0.5, s.Text.Y0 - 0.5, s.Text.X1 + 0.5, s.Text.Y1 + 0.5);
                foreach (var b in taken)
                {
                    if (t.Overlaps(b.Rect, 0.05)) { hits.Add(b); continue; }
                    if (!b.IsText) continue;
                    foreach (var l in s.Leader)
                    {
                        if (!l.Overlaps(b.Rect, 0.05)) continue;
                        l.OverlapSize(b.Rect, out var w, out var h);
                        if (Math.Max(w, h) > ThinMm) { hits.Add(b); break; }
                    }
                }
                if (i == 0 && hits.Count > 0) firstBlocker = hits[0];
                if (hits.Count == 0) { free = true; return i; }
                if (hits.Count < bestHits) { best = i; bestHits = hits.Count; }
            }
            return best;
        }

        /// <summary>
        /// The leader slots of a spot elevation at paper point (px, py), measured default first
        /// (shoulder 12 mm above — below for a bottom spot — text 12 mm to the right; the text is
        /// centred 5 mm past the shoulder): up-right, up-left, then 24 and 36 mm high. Each slot's
        /// <c>Name</c> is "up-right", "up-left 24", ….
        /// </summary>
        public static List<Slot> SpotSlots(double px, double py, bool top, double textW, double textH, double step = 12)
        {
            var list = new List<Slot>();
            double s = top ? 1 : -1;
            foreach (double rise in new[] { step, 2 * step, 3 * step })
                foreach (double dir in new[] { 1.0, -1.0 })
                {
                    double sy = py + s * rise, ex = px + dir * step, cx = px + dir * 5;
                    list.Add(new Slot
                    {
                        Name = (top ? "up-" : "down-") + (dir > 0 ? "right" : "left") + (rise > step ? " " + Mm.F(rise) + " mm" : ""),
                        Text = new MmRect(cx - textW / 2, sy, cx + textW / 2, sy + textH),
                        Leader = { Strip(px, py, px, sy), Strip(px, sy, ex, sy) },
                        Rise = s * rise, Dir = dir
                    });
                }
            return list;
        }

        /// <summary>An existing dimension line, as `ChooseRow` sees it: its paper offset across the direction and its span along it.</summary>
        internal struct DimLine
        {
            public int Id;
            public double Cross;
            public double Along0, Along1;
            public DimLine(int id, double cross, double a0, double a1) { Id = id; Cross = cross; Along0 = Math.Min(a0, a1); Along1 = Math.Max(a0, a1); }
        }

        /// <summary>Something else a new dimension row must keep clear of: another annotation's text, in (along, across) paper mm.</summary>
        internal struct Obstacle
        {
            public string Name;
            public MmRect Rect;
            public Obstacle(string name, MmRect rect) { Name = name; Rect = rect; }
        }

        /// <summary>
        /// The dimension row (1-based) for a new string beyond the elements: row k lies
        /// <paramref name="stepMm"/> × k past <paramref name="farMm"/> (paper mm, across the
        /// direction, growing outwards). A row is taken when a parallel line within
        /// <paramref name="takenWithinMm"/> of it overlaps the new span, or when another
        /// annotation's text lies within <paramref name="textBandMm"/> of the row over that span
        /// (the new string's own text would land on it; measured: the text stands 1 mm off the line and
        /// is 1.9 × 2.5 mm high, so 5.75 mm either side — the side depends on the view). <paramref name="takenBy"/> names, per
        /// skipped row, what took it ("dimension 1730940", "spot 1730946 text").
        /// </summary>
        public static int ChooseRow(double farMm, double along0, double along1, IList<DimLine> existing, out List<string> takenBy,
            double stepMm = 8, double takenWithinMm = 4, int maxRows = 12, IList<Obstacle> obstacles = null, double textBandMm = 5.75)
        {
            takenBy = new List<string>();
            double a0 = Math.Min(along0, along1), a1 = Math.Max(along0, along1);
            for (int k = 1; k <= maxRows; k++)
            {
                double row = farMm + stepMm * k;
                string hit = existing.Where(d => Math.Abs(d.Cross - row) < takenWithinMm && Math.Min(a1, d.Along1) - Math.Max(a0, d.Along0) > ThinMm)
                                     .Select(d => "dimension " + d.Id.ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
                if (hit == null && obstacles != null)
                {
                    // The new string's band: its line and the text on either side of it.
                    double c0 = row - textBandMm, c1 = row + textBandMm;
                    hit = obstacles.Where(o => Math.Min(a1, o.Rect.X1) - Math.Max(a0, o.Rect.X0) > ThinMm &&
                                               Math.Min(c1, o.Rect.Y1) - Math.Max(c0, o.Rect.Y0) > ThinMm)
                                   .Select(o => o.Name).FirstOrDefault();
                }
                if (hit == null) return k;
                takenBy.Add(hit);
            }
            return maxRows + 1;
        }
    }
}
