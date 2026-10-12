using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeModel.Markdown
{
    /// <summary>
    /// The layout decisions for a markdown table in the narrow chat pane, on plain cell text:
    /// which columns are numeric (right-aligned), how wide each column gets, how many rows show
    /// before "… nog N rijen", and the TSV the Copy link puts on the clipboard. WPF-free.
    /// </summary>
    public static class TableLayout
    {
        /// <summary>Rows shown before the rest folds behind "… nog N rijen".</summary>
        public const int ShownRows = 30;

        // "3000", "-1.5", "12 345,6 mm", "2,5 m²", "45°", "≈ 300 kg", "1.2e3" is NOT accepted.
        private static readonly Regex NumberWithUnit = new Regex(
            @"^[\-+−±~≈]?\s*\d[\d  ' .,]*(?:\s*(?:mm²|mm2|mm|cm²|cm|m²|m2|m³|m3|km|m|kg|t|kN/m²|kN/m2|kN/m|kNm|kN|N|°|%|s|min|h|ft|in))?$",
            RegexOptions.CultureInvariant);

        private static readonly HashSet<string> EmptyMarkers = new HashSet<string> { "", "-", "–", "—", "n.v.t.", "n/a" };

        /// <summary>True when every non-empty cell is a number with an optional unit (and at least one is).</summary>
        public static bool IsNumericColumn(IEnumerable<string> cells)
        {
            var any = false;
            foreach (var raw in cells)
            {
                var cell = Clean(raw);
                if (EmptyMarkers.Contains(cell.ToLowerInvariant())) continue;
                if (!NumberWithUnit.IsMatch(cell)) return false;
                any = true;
            }
            return any;
        }

        // Cell padding (5 + 5) plus a little slack for the border and rounding.
        public const double CellChrome = 14;

        // Before the pane reports its width, tables are laid out for a narrow pane.
        public const double AssumedWidth = 300;

        /// <summary>What a column needs: its longest unbreakable piece and its longest line (px).</summary>
        public sealed class ColumnNeed
        {
            /// <summary>The longest word (a numeric cell counts whole — numbers never break), incl. padding.</summary>
            public double Min { get; set; }
            /// <summary>The longest cell on one line, incl. padding.</summary>
            public double Max { get; set; }
            /// <summary>A numeric column: when even the words do not fit, it is the last to shrink.</summary>
            public bool Numeric { get; set; }
        }

        /// <summary>
        /// Measures each column with <paramref name="measure"/> (text, bold → px): header and
        /// cells, words and whole lines.
        /// </summary>
        public static ColumnNeed[] MeasureColumns(IList<string> header, IList<IList<string>> rows, int columns,
            bool[] numeric, Func<string, bool, double> measure)
        {
            var needs = new ColumnNeed[columns];
            for (int c = 0; c < columns; c++)
            {
                var cells = rows.Select(r => c < r.Count ? Clean(r[c]) : "").ToList();
                var headerText = header != null && c < header.Count ? Clean(header[c]) : "";
                var isNumeric = numeric != null && c < numeric.Length && numeric[c];
                var minPx = cells.SelectMany(t => isNumeric ? new[] { t } : t.Split(' '))
                    .Select(t => measure(t, false))
                    .Concat(headerText.Split(' ').Select(word => measure(word, true)))
                    .DefaultIfEmpty(0).Max();
                var maxPx = cells.Select(t => measure(t, false)).Concat(new[] { measure(headerText, true) })
                    .DefaultIfEmpty(0).Max();
                needs[c] = new ColumnNeed
                {
                    Min = Math.Ceiling(minPx + CellChrome),
                    Max = Math.Ceiling(Math.Max(minPx, maxPx) + CellChrome),
                    Numeric = isNumeric
                };
            }
            return needs;
        }

        /// <summary>
        /// Column widths (px) for <paramref name="available"/> — the auto table layout browsers
        /// use: everything on one line when it fits (the table then stays narrower than the pane);
        /// otherwise every column keeps its longest word and the rest of the room goes to the
        /// columns that want more, in proportion; only when even the words do not fit do columns
        /// shrink below them — text columns first (a broken word reads better than a broken
        /// number): numeric columns keep their width while they take at most half the room.
        /// </summary>
        public static double[] Fit(IList<ColumnNeed> needs, double available)
        {
            var n = needs.Count;
            var widths = new double[n];
            var sumMin = needs.Sum(x => x.Min);
            var sumMax = needs.Sum(x => x.Max);
            if (available < sumMin)
            {
                var numericMin = needs.Where(x => x.Numeric).Sum(x => x.Min);
                var textMin = sumMin - numericMin;
                var keepNumbers = numericMin > 0 && textMin > 0 && numericMin <= available / 2;
                for (int c = 0; c < n; c++)
                {
                    widths[c] = keepNumbers
                        ? (needs[c].Numeric ? needs[c].Min : needs[c].Min * (available - numericMin) / textMin)
                        : (sumMin > 0 ? needs[c].Min * available / sumMin : available / n);
                    widths[c] = Math.Floor(widths[c]);
                }
                return widths;
            }
            for (int c = 0; c < n; c++)
            {
                if (available >= sumMax)
                    widths[c] = needs[c].Max;
                else
                    widths[c] = needs[c].Min + (sumMax > sumMin
                        ? (available - sumMin) * (needs[c].Max - needs[c].Min) / (sumMax - sumMin)
                        : 0);
                widths[c] = Math.Floor(widths[c]);
            }
            return widths;
        }

        /// <summary>Rows beyond <see cref="ShownRows"/> fold away; 0 when everything shows.</summary>
        public static int HiddenRowCount(int bodyRows) => bodyRows > ShownRows ? bodyRows - ShownRows : 0;

        /// <summary>Tab-separated text (header first), one row per line — pastes into Excel as cells.</summary>
        public static string ToTsv(IList<string> header, IEnumerable<IList<string>> rows)
        {
            var sb = new StringBuilder();
            if (header != null && header.Count > 0)
                sb.Append(string.Join("\t", header.Select(TsvCell))).Append("\r\n");
            foreach (var row in rows)
                sb.Append(string.Join("\t", row.Select(TsvCell))).Append("\r\n");
            return sb.ToString();
        }

        private static string TsvCell(string cell) =>
            Regex.Replace(CitationParser.PlaceholdersToSuperscript(cell ?? ""), @"[\t\r\n]+", " ").Trim();

        // Citation placeholders and the superscript they render as never count as content.
        private static string Clean(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            var sb = new StringBuilder(cell.Length);
            CitationParser.Split(cell, t => sb.Append(t), _ => { });
            return sb.ToString().Trim();
        }
    }
}
