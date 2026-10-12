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

    /// <summary>
    /// What an IFC file written by Revit actually contains: one entry per exported element
    /// (IFC class, predefined type, the Revit element id Revit writes in the Tag attribute).
    /// `export ifc` reads its own file back with this, because the class an element ends up as
    /// is decided by the export class mapping and the family's IfcExportAs — piles exported as
    /// IfcSlab.BASESLAB looked "missing" to a grep for IFCPILE/IFCFOOTING. Pure — unit-tested.
    /// </summary>
    internal sealed class IfcContents
    {
        public sealed class Item
        {
            public string IfcClass;     // "IfcSlab"
            public string Predefined;   // "BASESLAB", or null
            public int RevitId;
        }

        public readonly List<Item> Items = new List<Item>();

        // Spatial structure, openings and relations are not "elements" of the model; their
        // 8th attribute is a long name or absent, never a Revit id.
        private static readonly HashSet<string> NotElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IFCPROJECT", "IFCSITE", "IFCBUILDING", "IFCBUILDINGSTOREY", "IFCSPACE", "IFCZONE",
            "IFCOPENINGELEMENT", "IFCGRID", "IFCANNOTATION"
        };

        /// <summary>Streams the file (Revit writes one entity per line; a wrapped entity is joined).</summary>
        public static IfcContents Read(string path)
        {
            using (var r = new StreamReader(path, Encoding.UTF8, true))
                return Read(r);
        }

        public static IfcContents Read(TextReader r)
        {
            var c = new IfcContents();
            var pending = new StringBuilder();
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (pending.Length == 0 && !line.StartsWith("#", StringComparison.Ordinal)) continue;
                pending.Append(line);
                if (!line.TrimEnd().EndsWith(";", StringComparison.Ordinal)) continue;
                var item = ParseElement(pending.ToString());
                pending.Clear();
                if (item != null) c.Items.Add(item);
            }
            return c;
        }

        /// <summary>
        /// "#463541=IFCSLAB('guid',#18,'Name',$,'Type',#1,#2,'1611287',.BASESLAB.);" → IfcSlab,
        /// BASESLAB, 1611287. Null for anything that is not an exported element.
        /// </summary>
        internal static Item ParseElement(string entity)
        {
            int eq = entity.IndexOf('=');
            int open = eq < 0 ? -1 : entity.IndexOf('(', eq);
            if (open < 0) return null;
            var name = entity.Substring(eq + 1, open - eq - 1).Trim();
            if (!name.StartsWith("IFC", StringComparison.OrdinalIgnoreCase) || NotElements.Contains(name)
                || name.StartsWith("IFCREL", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("TYPE", StringComparison.OrdinalIgnoreCase) || name.EndsWith("STYLE", StringComparison.OrdinalIgnoreCase))
                return null;
            var attrs = SplitAttributes(entity, open);
            // IfcProduct: GlobalId, OwnerHistory, Name, Description, ObjectType, ObjectPlacement,
            // Representation, then (IfcElement) Tag — Revit's element id.
            if (attrs.Count < 8 || !attrs[0].StartsWith("'") || !attrs[1].StartsWith("#")) return null;
            var tag = attrs[7];
            if (tag.Length < 3 || tag[0] != '\'' || tag[tag.Length - 1] != '\'') return null;
            if (!int.TryParse(tag.Substring(1, tag.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
            var last = attrs[attrs.Count - 1];
            string predefined = attrs.Count > 8 && last.Length > 2 && last[0] == '.' && last[last.Length - 1] == '.'
                ? last.Substring(1, last.Length - 2) : null;
            if (predefined == "NOTDEFINED" || predefined == "USERDEFINED") predefined = null;
            return new Item { IfcClass = PrettyClass(name), Predefined = predefined, RevitId = id };
        }

        /// <summary>Top-level attributes of a STEP entity; quotes ('' escapes) and nested lists respected.</summary>
        internal static List<string> SplitAttributes(string entity, int open)
        {
            var res = new List<string>();
            var cur = new StringBuilder();
            int depth = 0; bool inStr = false;
            for (int i = open + 1; i < entity.Length; i++)
            {
                char ch = entity[i];
                if (inStr)
                {
                    cur.Append(ch);
                    if (ch == '\'')
                    {
                        if (i + 1 < entity.Length && entity[i + 1] == '\'') { cur.Append('\''); i++; }
                        else inStr = false;
                    }
                    continue;
                }
                if (ch == '\'') { inStr = true; cur.Append(ch); continue; }
                if (ch == '(') depth++;
                if (ch == ')')
                {
                    if (depth == 0) { res.Add(cur.ToString().Trim()); return res; }
                    depth--;
                }
                if (ch == ',' && depth == 0) { res.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(ch);
            }
            return res;
        }

        // IFCWALLSTANDARDCASE → IfcWallStandardCase for the known words (longest match first);
        // an unknown remainder stays lower case.
        private static readonly string[] Words = new[]
        {
            "BUILDING", "ELEMENT", "ASSEMBLY", "COVERING", "CURTAIN", "FOOTING", "FURNISHING", "MEMBER", "COLUMN", "RAILING",
            "FLIGHT", "STAIR", "RAMP", "SLAB", "BEAM", "PILE", "PLATE", "WALL", "ROOF", "DOOR", "WINDOW", "PART", "ACCESSORY",
            "DISCRETE", "FASTENER", "MECHANICAL", "REINFORCING", "BAR", "MESH", "TENDON", "FLOW", "TERMINAL", "SEGMENT",
            "FITTING", "DISTRIBUTION", "CONTROL", "TRANSPORT", "VIRTUAL", "GEOGRAPHIC", "CHIMNEY", "SHADING", "DEVICE",
            "CIVIL", "EARTHWORKS", "SURFACE", "FEATURE", "PROXY", "SYSTEM", "CASE", "STANDARD"
        }.OrderByDescending(x => x.Length).ToArray();

        internal static string PrettyClass(string upper)
        {
            var rest = upper.Substring(3).ToUpperInvariant();
            var sb = new StringBuilder("Ifc");
            int i = 0;
            while (i < rest.Length)
            {
                var w = Words.FirstOrDefault(x => string.CompareOrdinal(rest, i, x, 0, x.Length) == 0);
                if (w == null) { sb.Append(rest.Substring(i).ToLowerInvariant()); break; }
                sb.Append(w[0]).Append(w.Substring(1).ToLowerInvariant());
                i += w.Length;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The response lines: per Revit category the number of Revit elements and the IFC
        /// class(es) they became, then the categories in scope that are (partly) missing from the
        /// file. Counts are Revit elements, not IFC entities: one shape-edited floor can arrive
        /// as dozens of IfcSlab pieces that all carry its id.
        /// <paramref name="categoryOf"/> maps a Revit id to its category name (null = unknown);
        /// <paramref name="inScope"/> is category → ids of the model elements the export covered.
        /// </summary>
        public List<string> Describe(Func<int, string> categoryOf, IDictionary<string, List<int>> inScope, int maxCategories = 12)
        {
            var lines = new List<string>();
            var exported = new HashSet<int>(Items.Select(x => x.RevitId));
            // One entry per Revit element: the class of its first IFC entity.
            var elements = Items.GroupBy(x => x.RevitId).Select(g => g.First()).ToList();
            var byCat = elements.GroupBy(x => categoryOf(x.RevitId) ?? "(unknown)")
                .Select(g => new
                {
                    Cat = g.Key,
                    Count = g.Count(),
                    Classes = string.Join(", ", g.GroupBy(x => x.IfcClass + (x.Predefined != null ? "." + x.Predefined : ""))
                        .OrderByDescending(h => h.Count()).Select(h => h.Key + (g.Count() == h.Count() ? "" : " ×" + h.Count())))
                })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Cat, StringComparer.OrdinalIgnoreCase).ToList();

            int scopeTotal = inScope?.Sum(kv => kv.Value.Count) ?? 0;
            int scopeFound = inScope?.Sum(kv => kv.Value.Count(exported.Contains)) ?? 0;
            lines.Add("IFC contents (read back from the file): " + elements.Count + " Revit element" + (elements.Count == 1 ? "" : "s") +
                      (Items.Count != elements.Count ? " (" + Items.Count + " IFC entities)" : "") +
                      (scopeTotal > 0 ? " — " + scopeFound + " of the " + scopeTotal + " model elements in scope" : ""));
            int w = byCat.Take(maxCategories).Select(x => x.Cat.Length).DefaultIfEmpty(0).Max();
            foreach (var x in byCat.Take(maxCategories))
                lines.Add("  " + x.Cat.PadRight(w) + "  " + x.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " → " + x.Classes);
            if (byCat.Count > maxCategories)
                lines.Add("  … " + (byCat.Count - maxCategories) + " more categories (" + byCat.Skip(maxCategories).Sum(x => x.Count) + " elements)");

            if (inScope != null)
            {
                var missing = inScope.Select(kv => new { Cat = kv.Key, Total = kv.Value.Count, Found = kv.Value.Count(exported.Contains) })
                    .Where(x => x.Total > 0 && x.Found < x.Total)
                    .OrderBy(x => x.Found > 0 ? 1 : 0).ThenByDescending(x => x.Total - x.Found).ToList();
                if (missing.Count > 0)
                {
                    lines.Add("Not in the file: " + string.Join(", ", missing.Take(8).Select(x =>
                        x.Cat + " " + (x.Found == 0 ? "— none of " + x.Total : (x.Total - x.Found) + " of " + x.Total))) +
                        (missing.Count > 8 ? ", … " + (missing.Count - 8) + " more" : ""));
                    if (missing.Any(x => x.Found == 0))
                        lines.Add("  A category with none exported is usually mapped to 'Not Exported' in the IFC export classes " +
                                  "(File → Export → Options → IFC Options), or its elements/types have 'Export to IFC' = Don't Export; " +
                                  "a whole-model export also skips elements demolished in, or created after, the export phase.");
                }
            }
            return lines;
        }
    }
}
