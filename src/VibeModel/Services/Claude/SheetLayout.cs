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

        public bool Inside(MmRect outer, double tol = 0.5)
            => X0 >= outer.X0 - tol && Y0 >= outer.Y0 - tol && X1 <= outer.X1 + tol && Y1 <= outer.Y1 + tol;

        public MmRect Offset(double dx, double dy) => new MmRect(X0 + dx, Y0 + dy, X1 + dx, Y1 + dy);

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

        /// <summary>Pairs of labelled rectangles that overlap (each pair once).</summary>
        public static List<Tuple<string, string>> Overlaps(IList<KeyValuePair<string, MmRect>> rects)
        {
            var res = new List<Tuple<string, string>>();
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                    if (rects[i].Value.Overlaps(rects[j].Value)) res.Add(Tuple.Create(rects[i].Key, rects[j].Key));
            return res;
        }

        public static List<string> OffPaper(IList<KeyValuePair<string, MmRect>> rects, MmRect paper)
            => rects.Where(r => !r.Value.Inside(paper)).Select(r => r.Key).ToList();

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
    /// The section frame for `section` (plan 12): a vertical cut plane through the line
    /// p1→p2. BasisX = line direction, BasisY = up, BasisZ = BasisX × BasisY (Revit's
    /// right-handed section frame; the view looks along −BasisZ, i.e. to the LEFT of p1→p2).
    /// Pure — unit-tested; the Revit side only turns these numbers into XYZ.
    /// </summary>
    internal sealed class SectionFrame
    {
        public double Ox, Oy;          // midpoint (mm)
        public double Dx, Dy;          // unit line direction
        public double Length;          // mm
        public double Bx, By;          // BasisZ (horizontal), = (Dy, -Dx)

        public static SectionFrame FromLine(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return null;
            dx /= len; dy /= len;
            return new SectionFrame
            {
                Ox = (x1 + x2) / 2, Oy = (y1 + y2) / 2, Dx = dx, Dy = dy, Length = len,
                // (dx,dy,0) × (0,0,1) = (dy, -dx, 0)
                Bx = dy, By = -dx
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

        /// <summary>Looking direction (−BasisZ) as a compass word in project coordinates (+Y = project north).</summary>
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
}
