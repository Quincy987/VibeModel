using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>An axis-aligned rectangle in paper (or view) millimetres.</summary>
    internal struct MmRect
    {
        public double X0, Y0, X1, Y1;

        public MmRect(double x0, double y0, double x1, double y1)
        {
            X0 = Math.Min(x0, x1); Y0 = Math.Min(y0, y1);
            X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1);
        }

        public double W => X1 - X0;
        public double H => Y1 - Y0;
        public double Cx => (X0 + X1) / 2;
        public double Cy => (Y0 + Y1) / 2;

        /// <summary>Overlap area beyond a tolerance (touching edges are not an overlap).</summary>
        public bool Overlaps(MmRect o, double tol = 0.5)
            => Math.Min(X1, o.X1) - Math.Max(X0, o.X0) > tol && Math.Min(Y1, o.Y1) - Math.Max(Y0, o.Y0) > tol;

        /// <summary>Width × height of the shared area (0 when apart).</summary>
        public void OverlapSize(MmRect o, out double w, out double h)
        {
            w = Math.Max(0, Math.Min(X1, o.X1) - Math.Max(X0, o.X0));
            h = Math.Max(0, Math.Min(Y1, o.Y1) - Math.Max(Y0, o.Y0));
        }

        /// <summary>How far the rectangle sticks out of the outer one (max over the four sides).</summary>
        public double Overhang(MmRect outer)
            => Math.Max(Math.Max(outer.X0 - X0, outer.Y0 - Y0), Math.Max(X1 - outer.X1, Y1 - outer.Y1));

        public bool Inside(MmRect outer, double tol = 0.5)
            => X0 >= outer.X0 - tol && Y0 >= outer.Y0 - tol && X1 <= outer.X1 + tol && Y1 <= outer.Y1 + tol;

        /// <summary>[x0, y0, x1, y1] rounded to whole mm — the JSON shape every sheet command uses.</summary>
        public List<object> ToJson() => new List<object> { Math.Round(X0), Math.Round(Y0), Math.Round(X1), Math.Round(Y1) };

        public override string ToString()
            => "(" + Mm.F(X0) + "," + Mm.F(Y0) + ")-(" + Mm.F(X1) + "," + Mm.F(Y1) + ") " + Mm.F(W) + "×" + Mm.F(H) + " mm";
    }

    internal static class Mm
    {
        public static string F(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        public static string F1(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Paper-space layout maths for `viewport` and the checks of `sheetinfo` (plan 12).
    /// Everything is in paper mm measured from the sheet's lower-left corner. Pure — unit-tested.
    /// </summary>
    internal static class SheetLayout
    {
        /// <summary>
        /// Width kept free on the right for the stamp when the title block is a full-sheet
        /// border (the common case — then its bounding box says nothing about where the stamp
        /// is). A-series stamps are ~180-190 mm wide; small sheets keep nothing.
        /// </summary>
        public static double DefaultReserve(double paperWidth) => paperWidth >= 400 ? 200 : 0;

        /// <summary>The area views may use: paper inset by the margin, minus the reserved stamp strip.</summary>
        public static MmRect UsableArea(MmRect paper, double margin, double reserveRight)
        {
            // Check the raw extent first: MmRect normalises, so an over-large reserve would flip it.
            double x1 = paper.X1 - margin - reserveRight;
            if (x1 - (paper.X0 + margin) < 10) x1 = paper.X1 - margin;
            return new MmRect(paper.X0 + margin, paper.Y0 + margin, x1, paper.Y1 - margin);
        }

        /// <summary>Square-ish grid that holds n views (wider than tall on landscape paper).</summary>
        public static void AutoGrid(int n, out int cols, out int rows)
        {
            cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
            rows = Math.Max(1, (int)Math.Ceiling(n / (double)cols));
        }

        /// <summary>Cells left-to-right, top-to-bottom, each shrunk by half the gap on every side.</summary>
        public static List<MmRect> Cells(MmRect area, int cols, int rows, double gap)
        {
            var list = new List<MmRect>();
            double cw = area.W / cols, ch = area.H / rows, g = gap / 2;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    double x0 = area.X0 + c * cw, y1 = area.Y1 - r * ch;
                    list.Add(new MmRect(x0 + g, y1 - ch + g, x0 + cw - g, y1 - g));
                }
            return list;
        }

        public static bool Fits(double w, double h, MmRect cell, double tol = 0.5) => w <= cell.W + tol && h <= cell.H + tol;

        /// <summary>The largest standard scale (1:n) at which a view of modelW×modelH mm fits the cell.</summary>
        public static int SuggestScale(double modelW, double modelH, MmRect cell)
        {
            int[] std = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000 };
            foreach (var s in std)
                if (modelW / s <= cell.W && modelH / s <= cell.H) return s;
            return std[std.Length - 1];
        }

        public static string FitWarning(string view, int scale, double w, double h, MmRect cell, int suggested)
            => "WARN: view " + view + " at 1:" + scale + " is " + Mm.F(w) + "×" + Mm.F(h) + " mm, cell is " +
               Mm.F(cell.W) + "×" + Mm.F(cell.H) + " mm — use --scale " + suggested + ", a bigger grid/--area, or crop the view";

        /// <summary>Index pairs (each once) of labelled rectangles that overlap, with the shared width and height.</summary>
        private static IEnumerable<Tuple<int, int, double, double>> OverlapPairs(IList<KeyValuePair<string, MmRect>> rects)
        {
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    if (!rects[i].Value.Overlaps(rects[j].Value)) continue;
                    rects[i].Value.OverlapSize(rects[j].Value, out double w, out double h);
                    yield return Tuple.Create(i, j, w, h);
                }
        }

        /// <summary>Label pairs of rectangles that overlap (each pair once).</summary>
        public static List<Tuple<string, string>> Overlaps(IList<KeyValuePair<string, MmRect>> rects)
            => OverlapPairs(rects).Select(t => Tuple.Create(rects[t.Item1].Key, rects[t.Item2].Key)).ToList();

        public static List<string> OffPaper(IList<KeyValuePair<string, MmRect>> rects, MmRect paper)
            => rects.Where(r => !r.Value.Inside(paper)).Select(r => r.Key).ToList();

        /// <summary>
        /// One line per overlapping pair. Viewport boxes include empty crop margin, so a thin
        /// overlap strip (≤ 30 mm across) is graded INFO and anything bigger WARN. Measured on a
        /// finished IB A0: its harmless crop-margin overlaps were 10–27 mm strips.
        /// </summary>
        public static List<string> OverlapLines(IList<KeyValuePair<string, MmRect>> rects)
            => OverlapPairs(rects).Select(t =>
            {
                bool thin = Math.Min(t.Item3, t.Item4) <= 30;
                return (thin ? "INFO  " : "WARN  ") + rects[t.Item1].Key + " and " + rects[t.Item2].Key + " boxes overlap " +
                       Mm.F(t.Item3) + "×" + Mm.F(t.Item4) + " mm" +
                       (thin ? " (thin strip — usually empty crop margin)" : " — check the screenshot whether the drawings collide");
            }).ToList();

        /// <summary>One WARN line per rectangle that leaves the paper, with how far it sticks out.</summary>
        public static List<string> OffPaperLines(IList<KeyValuePair<string, MmRect>> rects, MmRect paper)
            => rects.Where(r => !r.Value.Inside(paper))
                    .Select(r => "WARN  " + r.Key + " runs " + Mm.F(r.Value.Overhang(paper)) + " mm off the paper").ToList();

        /// <summary>
        /// The elements a `crop --fit` should show: the largest cluster of boxes, where boxes within
        /// <paramref name="gapMm"/> of each other belong together. Scattered context (trees, street
        /// furniture, site objects tens to hundreds of metres out) then cannot blow a quay-wall plan
        /// up: measured on PRG1002, a plain union was 395×219 m and a 2–98% percentile trim still
        /// 300×219 m for a 185 m quay, because 15% of the elements are spread over 400 m.
        /// Fewer than 10 boxes: no trimming. Returns the indices kept.
        /// More than <paramref name="maxClustered"/> boxes (the pair sweep must stay cheap on Revit's
        /// thread): an even sample is clustered, then every box whose centre lies in the winning
        /// cluster's extent (+ gap) is kept.
        /// </summary>
        public static List<int> FitKeep(IList<MmRect> boxes, double gapMm = 5000, int maxClustered = 4000)
        {
            int n = boxes.Count;
            var all = Enumerable.Range(0, n).ToList();
            if (n < 10) return all;

            var idx = n <= maxClustered ? all
                : Enumerable.Range(0, maxClustered).Select(k => (int)((long)k * n / maxClustered)).ToList();
            int m = idx.Count;
            var parent = Enumerable.Range(0, m).ToArray();
            // Sweep in X0 order so the inner loop stops at the first box beyond reach.
            var order = Enumerable.Range(0, m).OrderBy(k => boxes[idx[k]].X0).ToList();
            for (int a = 0; a < m; a++)
            {
                var ra = boxes[idx[order[a]]];
                for (int b = a + 1; b < m; b++)
                {
                    var rb = boxes[idx[order[b]]];
                    if (rb.X0 > ra.X1 + gapMm) break;
                    if (rb.Y0 <= ra.Y1 + gapMm && ra.Y0 <= rb.Y1 + gapMm)
                    {
                        int pa = Find(parent, order[a]), pb = Find(parent, order[b]);
                        if (pa != pb) parent[pa] = pb;
                    }
                }
            }
            var best = Enumerable.Range(0, m).GroupBy(k => Find(parent, k)).OrderByDescending(g => g.Count()).First()
                                 .Select(k => idx[k]).OrderBy(k => k).ToList();
            if (n <= maxClustered) return best;

            double x0 = best.Min(k => boxes[k].X0) - gapMm, x1 = best.Max(k => boxes[k].X1) + gapMm;
            double y0 = best.Min(k => boxes[k].Y0) - gapMm, y1 = best.Max(k => boxes[k].Y1) + gapMm;
            return all.Where(k => boxes[k].Cx >= x0 && boxes[k].Cx <= x1 && boxes[k].Cy >= y0 && boxes[k].Cy <= y1).ToList();
        }

        /// <summary>Union-find root with path halving (iterative: long chains cannot overflow the stack).</summary>
        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        /// <summary>Common paper names for a width × height in mm (either orientation), or null.</summary>
        public static string PaperName(double w, double h)
        {
            double a = Math.Max(w, h), b = Math.Min(w, h);
            var sizes = new[] { Tuple.Create("A0", 1189.0, 841.0), Tuple.Create("A1", 841.0, 594.0), Tuple.Create("A2", 594.0, 420.0),
                                Tuple.Create("A3", 420.0, 297.0), Tuple.Create("A4", 297.0, 210.0) };
            foreach (var s in sizes)
                if (Math.Abs(a - s.Item2) <= 15 && Math.Abs(b - s.Item3) <= 15) return s.Item1;
            return null;
        }
    }

    /// <summary>
    /// The section frame for `section` (plan 12): a vertical cut plane through the line p1→p2
    /// that looks to the LEFT of the walking direction, with the line reading left-to-right.
    /// Measured in Revit 2023 (ViewSection.CreateSection): the box's BasisZ is the LOOKING
    /// direction, its Min.Z is the cut plane and Max.Z the far clip; the created view then reports
    /// ViewDirection = −BasisZ and RightDirection = −BasisX. So the input basis is
    /// BasisX = −D, BasisY = up, BasisZ = Look = (−Dy, Dx) (right-handed: (−D) × Z = Look).
    /// Pure — unit-tested; the Revit side only turns these numbers into XYZ.
    /// </summary>
    internal sealed class SectionFrame
    {
        public double Ox, Oy;          // midpoint (mm)
        public double Dx, Dy;          // unit line direction
        public double Length;          // mm
        public double Lx, Ly;          // looking direction = left of D = (-Dy, Dx) = the box's BasisZ

        public static SectionFrame FromLine(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return null;
            dx /= len; dy /= len;
            return new SectionFrame
            {
                Ox = (x1 + x2) / 2, Oy = (y1 + y2) / 2, Dx = dx, Dy = dy, Length = len,
                Lx = -dy, Ly = dx
            };
        }

        /// <summary>A section line across a wall: perpendicular to its location line at the midpoint.</summary>
        public static SectionFrame AcrossWall(double sx, double sy, double ex, double ey, double halfLength)
        {
            double dx = ex - sx, dy = ey - sy, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6 || halfLength <= 0) return null;
            double mx = (sx + ex) / 2, my = (sy + ey) / 2, nx = -dy / len, ny = dx / len;
            return FromLine(mx - nx * halfLength, my - ny * halfLength, mx + nx * halfLength, my + ny * halfLength);
        }

        /// <summary>A horizontal direction (e.g. where a section looks) as a compass word; +Y = project north.</summary>
        public static string Compass(double lx, double ly)
        {
            var names = new[] { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
            double ang = Math.Atan2(ly, lx) * 180 / Math.PI;
            if (ang < 0) ang += 360;
            return names[(int)Math.Round(ang / 45.0) % 8];
        }
    }

    /// <summary>Dimension reference bookkeeping (plan 12). Pure — unit-tested.</summary>
    internal static class DimensionMath
    {
        /// <summary>Indices of the outermost two positions (min and max) when they differ by more than tol; one index when all coincide.</summary>
        public static List<int> Extremes(IList<double> positions, double tol)
        {
            var res = new List<int>();
            if (positions == null || positions.Count == 0) return res;
            int lo = 0, hi = 0;
            for (int i = 1; i < positions.Count; i++)
            {
                if (positions[i] < positions[lo]) lo = i;
                if (positions[i] > positions[hi]) hi = i;
            }
            res.Add(lo);
            if (positions[hi] - positions[lo] > tol) res.Add(hi);
            return res;
        }

        /// <summary>Order by position and drop references that sit at the same position (within tol) — Revit refuses zero-length segments.</summary>
        public static List<int> OrderDistinct(IList<double> positions, double tol)
        {
            var order = Enumerable.Range(0, positions.Count).OrderBy(i => positions[i]).ToList();
            var res = new List<int>();
            foreach (var i in order)
                if (res.Count == 0 || positions[i] - positions[res[res.Count - 1]] > tol) res.Add(i);
            return res;
        }

        public static string Segments(IList<double> valuesMm)
            => string.Join(" + ", valuesMm.Select(Mm.F1)) + (valuesMm.Count > 1 ? " = " + Mm.F1(valuesMm.Sum()) : "") + " mm";
    }

    /// <summary>
    /// Office stamp-field patterns (pack `sheets.stampParameters`) against the parameter names a
    /// sheet really has: `NL_META_*` (glob) and `IB_BEPALING_1..5` (numeric range) expand; a
    /// plain name stays as is. Pure — unit-tested.
    /// </summary>
    internal static class StampPatterns
    {
        public static List<string> Expand(IEnumerable<string> patterns, ICollection<string> available, out List<string> unmatched)
        {
            var res = new List<string>();
            unmatched = new List<string>();
            foreach (var p in patterns ?? Enumerable.Empty<string>())
            {
                var names = Match(p, available).ToList();
                if (names.Count == 0) unmatched.Add(p);
                foreach (var n in names) if (!res.Contains(n)) res.Add(n);
            }
            return res;
        }

        private static IEnumerable<string> Match(string pattern, ICollection<string> available)
        {
            var range = System.Text.RegularExpressions.Regex.Match(pattern, @"^(.*?)(\d+)\.\.(\d+)(.*)$");
            if (range.Success)
            {
                int a = int.Parse(range.Groups[2].Value), b = int.Parse(range.Groups[3].Value);
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                {
                    var want = range.Groups[1].Value + i + range.Groups[4].Value;
                    var n = available.FirstOrDefault(x => x.Equals(want, StringComparison.OrdinalIgnoreCase));   // the field's real spelling
                    if (n != null) yield return n;
                }
                yield break;
            }
            if (pattern.IndexOf('*') >= 0)
            {
                var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var n in available.Where(x => rx.IsMatch(x)).OrderBy(x => x, StringComparer.Ordinal)) yield return n;
                yield break;
            }
            var exact = available.FirstOrDefault(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            if (exact != null) yield return exact;
        }

        /// <summary>Blank, or one of the invisible placeholder characters title blocks use to look empty.</summary>
        public static bool IsBlank(string v)
            => string.IsNullOrWhiteSpace(v) || v.All(c => c == '\u3164' || c == '\u200B' || c == '\u00A0' || char.IsWhiteSpace(c));
    }
}
