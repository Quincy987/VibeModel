using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeModel.Markdown
{
    /// <summary>Where a cited fact came from (the first word of a <c>[bron: …]</c> marker).</summary>
    public enum CitationKind
    {
        /// <summary><c>leidraad 4.2</c> — a chapter of an indexed office document (<c>standards leidraad 4.2</c>).</summary>
        Leidraad,
        /// <summary><c>standaard phasing</c> — an office-pack topic sheet (<c>standards phasing</c>).</summary>
        Standaard,
        /// <summary><c>document memo.pdf p.3</c> — a project document read with <c>readdoc</c>.</summary>
        Document,
        /// <summary><c>revit 12345</c> / <c>revit Lengte</c> — a value read from the model.</summary>
        Revit,
        /// <summary><c>revit-api</c> — Revit API behaviour or a limitation; tooltip only.</summary>
        RevitApi,
        /// <summary>Anything else the model wrote; tooltip only.</summary>
        Other
    }

    /// <summary>One numbered source of a message (repeated markers of the same source share it).</summary>
    public sealed class Citation
    {
        public int Number { get; internal set; }
        public CitationKind Kind { get; internal set; }
        /// <summary>The marker text as written, trimmed: "leidraad 4.2".</summary>
        public string Source { get; internal set; }
        /// <summary>The source minus its kind word: "4.2", "memo.pdf", "12345".</summary>
        public string Target { get; internal set; }
        /// <summary>leidraad: the chapter number ("4.2"), when one was given.</summary>
        public string Chapter { get; internal set; }
        /// <summary>document: the page of "p.3" / "blz. 3" / "pagina 3".</summary>
        public int? Page { get; internal set; }
        /// <summary>document: the section of "§4.2".</summary>
        public string Section { get; internal set; }
        /// <summary>revit: the element id, when the target names one.</summary>
        public long? ElementId { get; internal set; }

        /// <summary>"¹", "²", … — how the marker shows in the text.</summary>
        public string Superscript => CitationParser.ToSuperscript(Number);

        /// <summary>
        /// The VibeModel command that opens this source in the pane, or false when it only has a
        /// tooltip (revit-api, an unknown kind, a model value without an element id).
        /// </summary>
        public bool TryGetLookup(out string command, out string args)
        {
            command = null;
            args = null;
            switch (Kind)
            {
                case CitationKind.Leidraad:
                    command = "standards";
                    args = "leidraad" + (string.IsNullOrEmpty(Chapter)
                        ? (string.IsNullOrEmpty(Target) ? "" : " " + Target)
                        : " " + Chapter);
                    return true;
                case CitationKind.Standaard:
                    if (string.IsNullOrEmpty(Target)) return false;
                    command = "standards";
                    args = Target.Split(' ')[0];
                    return true;
                case CitationKind.Document:
                    if (string.IsNullOrEmpty(Target)) return false;
                    command = "readdoc";
                    args = Target;
                    return true;
                case CitationKind.Revit:
                    if (ElementId == null) return false;
                    command = "select";
                    args = ElementId.Value.ToString(CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>A message's markdown with its <c>[bron: …]</c> markers swapped for placeholders.</summary>
    public sealed class CitationParseResult
    {
        /// <summary>The markdown to render: every marker replaced by <see cref="CitationParser.Placeholder"/>s.</summary>
        public string Text { get; internal set; }
        /// <summary>Unique sources in order of first appearance, numbered from 1.</summary>
        public IReadOnlyList<Citation> Citations { get; internal set; }

        public Citation ByNumber(int number) =>
            number >= 1 && number <= Citations.Count ? Citations[number - 1] : null;
    }

    /// <summary>
    /// Finds the source markers the model writes after a sentence — <c>[bron: leidraad 4.2]</c>,
    /// several sources separated by <c>;</c> — outside code spans and code fences. Markers are
    /// display-only: the stored chat keeps the raw text, so a replayed chat parses identically.
    /// Malformed markers (no colon, nothing after it, no closing bracket, escaped <c>\[</c>) stay
    /// as text. WPF-free.
    /// </summary>
    public static class CitationParser
    {
        // Private-use characters: Markdig treats them as plain text, so a placeholder survives
        // parsing inside one literal and the inline renderer can swap it for the superscript.
        public const char PlaceholderStart = '\uE000';
        public const char PlaceholderEnd = '\uE001';

        private static readonly Regex MarkerRegex = new Regex(
            @"\G\[(?:bron|source)\s*:([^\]\n]*)\]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex FenceRegex = new Regex(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.CultureInvariant);

        private static readonly Regex PageRegex = new Regex(
            @"[\s,;(]*(?:p\.?|pag\.?|pagina|page|blz\.?)\s*(\d+)\)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SectionRegex = new Regex(
            @"[\s,;(]*§\s*([\w.\-]+)\)?\s*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex ChapterRegex = new Regex(@"\d+(?:\.\d+)*", RegexOptions.CultureInvariant);
        private static readonly Regex ElementIdRegex = new Regex(@"(?<![\w.])\d{2,}(?![\w.])", RegexOptions.CultureInvariant);

        public static string Placeholder(int number) =>
            PlaceholderStart + number.ToString(CultureInfo.InvariantCulture) + PlaceholderEnd;

        public static CitationParseResult Parse(string markdown)
        {
            var citations = new List<Citation>();
            var result = new CitationParseResult { Text = markdown ?? "", Citations = citations };
            if (string.IsNullOrEmpty(markdown) || markdown.IndexOf('[') < 0)
                return result;

            var byKey = new Dictionary<string, Citation>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(markdown.Length);
            string fence = null; // the opening fence while inside a fenced code block
            int i = 0;
            while (i < markdown.Length)
            {
                // Line start: a fence opens or closes a code block; inside one, copy verbatim.
                if (i == 0 || markdown[i - 1] == '\n')
                {
                    var lineEnd = markdown.IndexOf('\n', i);
                    var line = lineEnd < 0 ? markdown.Substring(i) : markdown.Substring(i, lineEnd - i);
                    var m = FenceRegex.Match(line);
                    if (fence != null || m.Success)
                    {
                        if (fence == null)
                            fence = m.Groups[1].Value;
                        else if (m.Success && m.Groups[1].Value[0] == fence[0] && m.Groups[1].Value.Length >= fence.Length)
                            fence = null;
                        var end = lineEnd < 0 ? markdown.Length : lineEnd + 1;
                        sb.Append(markdown, i, end - i);
                        i = end;
                        continue;
                    }
                }

                var c = markdown[i];
                if (c == '\\' && i + 1 < markdown.Length)
                {
                    // An escaped character (\[bron: …] stays text) — copy both.
                    sb.Append(c).Append(markdown[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '`')
                {
                    // A code span: copy through its matching closing run (same length).
                    var run = 0;
                    while (i + run < markdown.Length && markdown[i + run] == '`') run++;
                    var close = FindClosingBackticks(markdown, i + run, run);
                    var end = close < 0 ? i + run : close + run;
                    sb.Append(markdown, i, end - i);
                    i = end;
                    continue;
                }

                if (c == '[')
                {
                    var m = MarkerRegex.Match(markdown, i);
                    if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                    {
                        var parts = m.Groups[1].Value.Split(';');
                        var placeholders = new StringBuilder();
                        foreach (var part in parts)
                        {
                            var source = CollapseSpaces(part);
                            if (source.Length == 0) continue;
                            if (!byKey.TryGetValue(source, out var citation))
                            {
                                citation = Describe(source);
                                citation.Number = citations.Count + 1;
                                citations.Add(citation);
                                byKey[source] = citation;
                            }
                            placeholders.Append(Placeholder(citation.Number));
                        }
                        // The space the model puts before the marker would sit between the
                        // sentence and its superscript — but not the one after a list bullet
                        // ("- [bron: …]"), which keeps the line a list item.
                        if (!OnlyLinePrefixBefore(sb))
                            while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                        sb.Append(placeholders);
                        i += m.Length;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            result.Text = sb.ToString();
            return result;
        }

        /// <summary>Classifies one source ("document memo.pdf p.3") — public for tests.</summary>
        public static Citation Describe(string source)
        {
            source = CollapseSpaces(source);
            var lower = source.ToLowerInvariant();
            var citation = new Citation { Source = source, Kind = CitationKind.Other, Target = source };

            string rest;
            if (StartsWithWord(lower, "revit-api", out rest, source) || StartsWithWord(lower, "revit api", out rest, source))
            {
                citation.Kind = CitationKind.RevitApi;
                citation.Target = rest;
            }
            else if (StartsWithWord(lower, "leidraad", out rest, source))
            {
                citation.Kind = CitationKind.Leidraad;
                citation.Target = rest;
                var ch = ChapterRegex.Match(rest);
                if (ch.Success) citation.Chapter = ch.Value;
            }
            else if (StartsWithWord(lower, "standaard", out rest, source) || StartsWithWord(lower, "standards", out rest, source)
                     || StartsWithWord(lower, "standard", out rest, source))
            {
                citation.Kind = CitationKind.Standaard;
                citation.Target = rest;
            }
            else if (StartsWithWord(lower, "document", out rest, source) || StartsWithWord(lower, "doc", out rest, source))
            {
                citation.Kind = CitationKind.Document;
                var page = PageRegex.Match(rest);
                var section = SectionRegex.Match(rest);
                if (page.Success && page.Index > 0)
                {
                    citation.Page = int.Parse(page.Groups[1].Value, CultureInfo.InvariantCulture);
                    rest = rest.Substring(0, page.Index);
                }
                else if (section.Success && section.Index > 0)
                {
                    citation.Section = section.Groups[1].Value;
                    rest = rest.Substring(0, section.Index);
                }
                citation.Target = rest.Trim().Trim(',', ';').Trim();
            }
            else if (StartsWithWord(lower, "revit", out rest, source) || StartsWithWord(lower, "model", out rest, source))
            {
                citation.Kind = CitationKind.Revit;
                citation.Target = rest;
                var id = ElementIdRegex.Match(rest);
                if (id.Success && long.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var eid))
                    citation.ElementId = eid;
            }
            return citation;
        }

        /// <summary>Placeholders → "¹" etc., for plain-text uses (table TSV).</summary>
        public static string PlaceholdersToSuperscript(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(PlaceholderStart) < 0) return text;
            return SplitPlaceholders(text, (s, sb) => sb.Append(s), (n, sb) => sb.Append(ToSuperscript(n)));
        }

        /// <summary>
        /// Walks <paramref name="text"/>, calling <paramref name="onText"/> for plain stretches
        /// and <paramref name="onMarker"/> for each placeholder number.
        /// </summary>
        public static void Split(string text, Action<string> onText, Action<int> onMarker)
        {
            SplitPlaceholders(text, (s, _) => onText(s), (n, _) => onMarker(n));
        }

        private static string SplitPlaceholders(string text, Action<string, StringBuilder> onText, Action<int, StringBuilder> onMarker)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                var start = text.IndexOf(PlaceholderStart, i);
                var end = start < 0 ? -1 : text.IndexOf(PlaceholderEnd, start + 1);
                if (start < 0 || end < 0)
                {
                    onText(text.Substring(i), sb);
                    break;
                }
                if (start > i) onText(text.Substring(i, start - i), sb);
                if (int.TryParse(text.Substring(start + 1, end - start - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    onMarker(n, sb);
                i = end + 1;
            }
            return sb.ToString();
        }

        private static readonly string SuperDigits = "⁰¹²³⁴⁵⁶⁷⁸⁹";

        public static string ToSuperscript(int number)
        {
            var digits = number.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder(digits.Length);
            foreach (var d in digits) sb.Append(SuperDigits[d - '0']);
            return sb.ToString();
        }

        private static bool StartsWithWord(string lower, string word, out string rest, string original)
        {
            rest = null;
            if (!lower.StartsWith(word, StringComparison.Ordinal)) return false;
            if (lower.Length > word.Length && !char.IsWhiteSpace(lower[word.Length]) && lower[word.Length] != ':')
                return false;
            rest = original.Substring(word.Length).TrimStart(' ', ':').Trim();
            return true;
        }

        // A code span closes within its paragraph: a lone backtick never swallows the markers
        // of the paragraphs after it.
        private static int FindClosingBackticks(string text, int from, int run)
        {
            var limit = ParagraphEnd(text, from);
            int i = from;
            while (i < limit)
            {
                var j = text.IndexOf('`', i, limit - i);
                if (j < 0) return -1;
                var len = 0;
                while (j + len < text.Length && text[j + len] == '`') len++;
                if (len == run) return j;
                i = j + len;
            }
            return -1;
        }

        // The index of the next blank line (or the end of the text).
        private static int ParagraphEnd(string text, int from)
        {
            var i = from;
            while (true)
            {
                var nl = text.IndexOf('\n', i);
                if (nl < 0) return text.Length;
                var k = nl + 1;
                while (k < text.Length && (text[k] == ' ' || text[k] == '\t' || text[k] == '\r')) k++;
                if (k >= text.Length || text[k] == '\n') return nl;
                i = nl + 1;
            }
        }

        // True when the current line so far holds nothing but indentation and a list bullet
        // ("- ", "* ", "1. ") — the marker opens the line's content.
        private static bool OnlyLinePrefixBefore(StringBuilder sb)
        {
            var start = sb.Length;
            while (start > 0 && sb[start - 1] != '\n') start--;
            var line = sb.ToString(start, sb.Length - start);
            return Regex.IsMatch(line, @"^\s*(?:[-*+]|\d+[.)])?\s*$");
        }

        private static string CollapseSpaces(string s) =>
            Regex.Replace((s ?? "").Trim(), @"\s+", " ");
    }

    /// <summary>What the source popup shows: a command response without its trailing stamp lines, and where to scroll.</summary>
    public static class CitationSourceText
    {
        /// <summary>Drops the model-state stamp ("-- model #47 | …") and post-command check lines.</summary>
        public static string Clean(string response)
        {
            if (string.IsNullOrEmpty(response)) return "";
            var lines = response.Replace("\r\n", "\n").Split('\n');
            var end = lines.Length;
            while (end > 0 && (lines[end - 1].Trim().Length == 0
                               || lines[end - 1].StartsWith("-- model #", StringComparison.Ordinal)
                               || lines[end - 1].StartsWith("-- check:", StringComparison.Ordinal)))
                end--;
            return string.Join("\n", lines, 0, end);
        }

        /// <summary>
        /// The character index of the cited page ("--- page 3 ---", as readdoc prints PDFs) or
        /// section ("4.2" / "§4.2" at a line start, else anywhere); -1 when not found or not asked.
        /// </summary>
        public static int FindLocation(string text, Citation citation, out int length)
        {
            length = 0;
            if (string.IsNullOrEmpty(text) || citation == null) return -1;
            Match m = null;
            if (citation.Page != null)
                m = Regex.Match(text, @"(?m)^--- page " + citation.Page.Value.ToString(CultureInfo.InvariantCulture) + " ---");
            else if (!string.IsNullOrEmpty(citation.Section))
            {
                var section = Regex.Escape(citation.Section);
                m = Regex.Match(text, @"(?m)^[ \t#]*§?\s*" + section + @"(?![\d.]?\d)");
                if (!m.Success) m = Regex.Match(text, @"§?\s*" + section + @"(?![\d.]?\d)");
            }
            if (m == null || !m.Success) return -1;
            length = m.Length;
            return m.Index;
        }
    }
}
