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

        // Column weights (≈ characters): a long text column wraps instead of pushing the pane
        // into horizontal scrolling; a tiny one keeps room for its header.
        public const int MinWeight = 3;
        public const int MaxWeight = 32;

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

        /// <summary>
        /// Relative column widths (star weights): the longest cell, capped, at least as wide as
        /// the header's longest word so headers never break mid-word.
        /// </summary>
        public static double[] ColumnWeights(IList<string> header, IList<IList<string>> rows, int columns)
        {
            var weights = new double[columns];
            for (int c = 0; c < columns; c++)
            {
                var longestCell = rows.Select(r => c < r.Count ? Clean(r[c]).Length : 0).DefaultIfEmpty(0).Max();
                var headerText = header != null && c < header.Count ? Clean(header[c]) : "";
                var longestWord = headerText.Split(' ').Select(word => word.Length).DefaultIfEmpty(0).Max();
                var w = Math.Max(longestCell, Math.Max(longestWord, Math.Min(headerText.Length, MaxWeight / 2)));
                weights[c] = Math.Max(MinWeight, Math.Min(MaxWeight, w)) + 2; // + cell padding
            }
            return weights;
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
