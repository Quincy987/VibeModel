using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Standards
{
    public sealed class DocChapter
    {
        public string Number { get; set; }
        public string Title { get; set; }
        public int PageFrom { get; set; }
        public int PageTo { get; set; }
        public string Text { get; set; }

        public string Label => (Number == "0" ? "" : Number + " ") + Title;
    }

    /// <summary>
    /// Splits extracted document text into numbered chapters ("2.3.3 Families",
    /// "4.2 Werken met de fasering ..."), the structure office manuals already have.
    ///
    /// A numbered line only counts as a heading when its number is a plausible SUCCESSOR of the
    /// previous heading (next sibling, first child, or the next number at a shallower level).
    /// That rejects numbers that merely start a sentence ("1 regel", "3 lagen"). Table-of-contents
    /// lines (dot leaders or a trailing page number) and pages crowded with numbered lines are
    /// skipped, so the TOC does not swallow the real headings. Pure — unit-tested.
    /// </summary>
    public static class ChapterSplitter
    {
        private static readonly Regex Heading = new Regex(
            @"^\s*(?<num>\d{1,2}(?:\.\d{1,2}){0,3})\.?\s+(?<title>[^\d\s\.\-•][^\r\n]{0,110})$",
            RegexOptions.CultureInvariant);

        private static readonly Regex TocTail = new Regex(@"(\.{3,}|…{2,}|\s\.\s\.\s)|\s\d{1,3}\s*$", RegexOptions.CultureInvariant);

        public const int TocPageHeadingThreshold = 6;

        public static List<DocChapter> Split(IList<string> pages)
        {
            var chapters = new List<DocChapter>();
            var current = new DocChapter { Number = "0", Title = "(front matter)", PageFrom = 1, PageTo = 1 };
            var text = new StringBuilder();
            int[] prev = null;

            for (int p = 0; p < (pages?.Count ?? 0); p++)
            {
                int pageNo = p + 1;
                var lines = (pages[p] ?? "").Replace("\r\n", "\n").Split('\n');

                // A page full of numbered lines is a table of contents: never split on it.
                var numbered = lines.Where(l => l.Length <= 130 && Heading.IsMatch(l.TrimEnd())).ToList();
                bool tocPage = numbered.Count >= TocPageHeadingThreshold &&
                               numbered.Count(l => TocTail.IsMatch(l.TrimEnd())) >= (numbered.Count + 1) / 2;

                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd();
                    if (!tocPage && IsCandidate(line, out var num, out var title) && IsSuccessor(prev, num))
                    {
                        Flush(chapters, current, text);
                        current = new DocChapter
                        {
                            Number = string.Join(".", num),
                            Title = title,
                            PageFrom = pageNo,
                            PageTo = pageNo
                        };
                        text.Clear();
                        prev = num;
                        continue;
                    }
                    if (line.Length > 0) text.AppendLine(line);
                    current.PageTo = pageNo;
                }
            }
            Flush(chapters, current, text);
            return chapters;
        }

        private static void Flush(List<DocChapter> chapters, DocChapter c, StringBuilder text)
        {
            c.Text = text.ToString().TrimEnd();
            // Drop an empty front matter; keep empty numbered chapters (parents of subchapters).
            if (c.Number == "0" && c.Text.Length == 0) return;
            chapters.Add(c);
        }

        internal static bool IsCandidate(string line, out int[] number, out string title)
        {
            number = null;
            title = null;
            if (string.IsNullOrWhiteSpace(line) || line.Length > 130) return false;
            var m = Heading.Match(line);
            if (!m.Success) return false;
            var t = m.Groups["title"].Value.Trim();
            if (t.Length < 2 || !char.IsLetter(t[0])) return false;
            if (TocTail.IsMatch(line)) return false;
            // Sentences are not headings: a heading rarely ends with a period or comma.
            if (t.EndsWith(".", StringComparison.Ordinal) || t.EndsWith(",", StringComparison.Ordinal)) return false;
            try
            {
                number = m.Groups["num"].Value.Split('.').Select(int.Parse).ToArray();
            }
            catch { return false; }
            title = t;
            return true;
        }

        /// <summary>
        /// Plausible next heading after <paramref name="prev"/>: first child (x.y → x.y.1), next
        /// sibling at the same or a shallower level (x.y.z → x.y.z+1 / x.y+1 / x+1). One missing
        /// number is tolerated (+2) because extraction occasionally loses a heading line.
        /// </summary>
        internal static bool IsSuccessor(int[] prev, int[] n)
        {
            if (n == null || n.Length == 0) return false;
            if (prev == null)
                return n[0] <= 1 && n.Skip(1).All(x => x <= 1);

            if (n.Length == prev.Length + 1 && PrefixEqual(prev, n, prev.Length) && n[n.Length - 1] == 1)
                return true;

            for (int k = 1; k <= prev.Length; k++)
            {
                if (n.Length != k) continue;
                if (!PrefixEqual(prev, n, k - 1)) continue;
                int step = n[k - 1] - prev[k - 1];
                if (step >= 1 && step <= 2) return true;
            }
            return false;
        }

        private static bool PrefixEqual(int[] a, int[] b, int len)
        {
            for (int i = 0; i < len; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Find a chapter by number ("4.2"), by label prefix, or by a title substring.
        /// A number also returns its subchapters' text (4.2 includes 4.2.1 ...).
        /// </summary>
        public static string GetChapterText(IList<DocChapter> chapters, string query, out string label)
        {
            label = null;
            if (chapters == null || string.IsNullOrWhiteSpace(query)) return null;
            var q = query.Trim().TrimEnd('.');

            int idx = -1;
            for (int i = 0; i < chapters.Count; i++)
                if (chapters[i].Number == q) { idx = i; break; }
            if (idx < 0)
                for (int i = 0; i < chapters.Count; i++)
                    if (chapters[i].Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { idx = i; break; }
            if (idx < 0) return null;

            var root = chapters[idx];
            label = root.Label + " (p" + root.PageFrom + "–" + root.PageTo + ")";
            var sb = new StringBuilder();
            sb.AppendLine(root.Label);
            if (!string.IsNullOrEmpty(root.Text)) sb.AppendLine(root.Text);
            var prefix = root.Number + ".";
            for (int i = idx + 1; i < chapters.Count && root.Number != "0"; i++)
            {
                if (!chapters[i].Number.StartsWith(prefix, StringComparison.Ordinal)) break;
                sb.AppendLine();
                sb.AppendLine(chapters[i].Label);
                if (!string.IsNullOrEmpty(chapters[i].Text)) sb.AppendLine(chapters[i].Text);
                label = root.Label + " (p" + root.PageFrom + "–" + chapters[i].PageTo + ")";
            }
            return sb.ToString().TrimEnd();
        }
    }
}
