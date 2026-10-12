using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>Plane polygon maths for `region --points`. Pure — unit-tested.</summary>
    internal static class Polygon
    {
        /// <summary>Signed area (shoelace; counter-clockwise positive) of an open polygon, in mm².</summary>
        public static double Area(IList<double[]> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                a += p[0] * q[1] - q[0] * p[1];
            }
            return a / 2;
        }

        /// <summary>True when two non-adjacent edges cross — Revit refuses such a boundary.</summary>
        public static bool SelfIntersects(IList<double[]> pts)
        {
            int n = pts.Count;
            if (n < 4) return false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;   // neighbours share a corner
                    if (Cross(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n])) return true;
                }
            return false;
        }

        private static bool Cross(double[] a, double[] b, double[] c, double[] d)
        {
            double d1 = Orient(c, d, a), d2 = Orient(c, d, b), d3 = Orient(a, b, c), d4 = Orient(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Orient(double[] a, double[] b, double[] c)
            => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
    }

    /// <summary>
    /// Schedule read-back as text (`schedule`): column headers with their display unit and the
    /// rows as an aligned table, cut off after a row limit. Pure — unit-tested.
    /// </summary>
    internal static class ScheduleText
    {
        private const int MaxCell = 60;

        /// <summary>
        /// A short unit symbol for a Revit unit type id ("autodesk.unit.unit:millimeters-1.0.1"
        /// → "mm"); unknown units fall back to the readable name, null/empty to null.
        /// </summary>
        public static string UnitSymbol(string unitTypeId)
        {
            if (string.IsNullOrWhiteSpace(unitTypeId)) return null;
            var s = unitTypeId;
            int colon = s.LastIndexOf(':');
            if (colon >= 0) s = s.Substring(colon + 1);
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            switch (s)
            {
                case "millimeters": return "mm";
                case "centimeters": return "cm";
                case "decimeters": return "dm";
                case "meters": return "m";
                case "metersCentimeters": return "m";
                case "kilometers": return "km";
                case "squareMillimeters": return "mm²";
                case "squareCentimeters": return "cm²";
                case "squareMeters": return "m²";
                case "cubicMillimeters": return "mm³";
                case "cubicCentimeters": return "cm³";
                case "cubicMeters": return "m³";
                case "liters": return "L";
                case "feet": return "ft";
                case "inches": return "in";
                case "feetFractionalInches": return "ft-in";
                case "fractionalInches": return "in";
                case "squareFeet": return "ft²";
                case "cubicFeet": return "ft³";
                case "degrees": return "°";
                case "radians": return "rad";
                case "percentage": return "%";
                case "kilograms": return "kg";
                case "tonnes": return "t";
                case "kilonewtons": return "kN";
                case "newtons": return "N";
                case "kilogramsPerCubicMeter": return "kg/m³";
                case "general": case "fixed": case "currency": return null;
            }
            // camelCase → words ("kilonewtonMeters" → "kilonewton meters")
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>"Length (mm)"; a heading that already names its unit ("P.P.N. (m) t.o.v. NAP") is left alone.</summary>
        public static string Header(string name, string unit)
        {
            if (string.IsNullOrEmpty(unit)) return name;
            if ((name ?? "").IndexOf("(" + unit + ")", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (name ?? "").IndexOf("[" + unit + "]", StringComparison.OrdinalIgnoreCase) >= 0) return name;
            return name + " (" + unit + ")";
        }

        /// <summary>
        /// Column width on the sheet for a longest cell of n characters, 14–150 mm. Measured on the
        /// IB A0 schedule text: 2 mm a character still wrapped "1094850"; 2.6 mm + 6 mm padding does not.
        /// </summary>
        public static double ColumnWidthMm(int longestChars) => Math.Max(14, Math.Min(150, longestChars * 2.6 + 6));

        /// <summary>
        /// The field list for an unknown field name: close matches first, then all names — or,
        /// for categories with hundreds of (shared) parameters, the first 60 and the total.
        /// </summary>
        public static string FieldHelp(IList<string> names, IEnumerable<string> unknown)
        {
            var all = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var close = new List<string>();
            foreach (var u in unknown)
            {
                var words = (u ?? "").Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToList();
                foreach (var n in all)
                {
                    bool hit = n.IndexOf(u, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               (n.Length >= 3 && u.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) ||
                               words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit && !close.Contains(n)) close.Add(n);
                }
            }
            var sb = new StringBuilder();
            if (close.Count > 0) sb.Append("Close matches: " + string.Join(", ", close.Take(15)) + (close.Count > 15 ? " …" : "") + ". ");
            const int max = 60;
            sb.Append(all.Count <= max ? "Schedulable fields: " + string.Join(", ", all) + "."
                                       : "Schedulable fields (first " + max + " of " + all.Count + "): " + string.Join(", ", all.Take(max)) + ", …");
            return sb.ToString();
        }

        /// <summary>
        /// Data rows of a schedule body: Revit's body section repeats the column headings in its
        /// first row and has blank spacer rows; both are dropped. Rows keep their cell count.
        /// </summary>
        public static List<List<string>> DataRows(IList<IList<string>> body, IList<string> headings)
        {
            var rows = new List<List<string>>();
            bool headingSkipped = false;
            foreach (var raw in body)
            {
                var row = raw.Select(c => (c ?? "").Trim()).ToList();
                if (row.All(c => c.Length == 0)) continue;
                if (!headingSkipped && headings != null && headings.Count > 0 && row.Count >= headings.Count &&
                    headings.Select((h, i) => string.Equals(h ?? "", row[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
                { headingSkipped = true; continue; }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// An aligned text table: header line, dashes, up to <paramref name="maxRows"/> rows, and
        /// a "… N more row(s)" line when cut off. Long cells are shortened with "…".
        /// </summary>
        public static List<string> Table(IList<string> headers, IList<List<string>> rows, int maxRows, string moreHint = null)
        {
            int cols = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
            var shown = rows.Take(Math.Max(0, maxRows)).ToList();
            Func<IList<string>, int, string> cell = (r, i) =>
            {
                var v = i < r.Count ? (r[i] ?? "") : "";
                v = v.Replace("\r", " ").Replace("\n", " ");
                return v.Length > MaxCell ? v.Substring(0, MaxCell - 1) + "…" : v;
            };
            var widths = new int[cols];
            for (int i = 0; i < cols; i++)
                widths[i] = Math.Max(cell(headers, i).Length, shown.Count == 0 ? 0 : shown.Max(r => cell(r, i).Length));
            Func<IList<string>, string> line = r => string.Join(" | ", Enumerable.Range(0, cols).Select(i => cell(r, i).PadRight(widths[i]))).TrimEnd();
            var lines = new List<string> { line(headers), string.Join("-+-", widths.Select(w => new string('-', Math.Max(1, w)))) };
            lines.AddRange(shown.Select(r => line(r)));
            if (rows.Count > shown.Count)
                lines.Add("… " + (rows.Count - shown.Count) + " more row(s)" + (moreHint != null ? " — " + moreHint : ""));
            if (rows.Count == 0) lines.Add("(no rows — the filter may exclude everything, or the category has no elements)");
            return lines;
        }
    }

    /// <summary>File names and sizes for `export`. Pure — unit-tested.</summary>
    internal static class ExportNaming
    {
        public const string SheetPattern = "{number} - {name}";
        public const string ViewPattern = "{name}";

        /// <summary>
        /// Expand {number} {name} {project} {date} (yyyy-MM-dd) {format} and make the result a
        /// valid file name (invalid characters → "_", trailing dots/spaces trimmed).
        /// </summary>
        public static string Expand(string pattern, string number, string name, string project, DateTime date, string format)
        {
            var s = (pattern ?? "")
                .Replace("{number}", number ?? "")
                .Replace("{name}", name ?? "")
                .Replace("{project}", project ?? "")
                .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Replace("{format}", format ?? "");
            return Sanitize(s);
        }

        public static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in s ?? "") sb.Append(bad.Contains(c) ? '_' : c);
            var r = sb.ToString().Trim().TrimEnd('.', ' ');
            return r.Length == 0 ? "export" : r;
        }

        /// <summary>Same names (case-insensitive, as Windows sees them) get " (2)", " (3)" ….</summary>
        public static List<string> MakeUnique(IList<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new List<string>();
            foreach (var n in names)
            {
                var c = n; int k = 2;
                while (!seen.Add(c)) c = n + " (" + k++ + ")";
                res.Add(c);
            }
            return res;
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
