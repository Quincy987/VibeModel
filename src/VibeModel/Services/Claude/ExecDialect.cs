using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// What exec compiles: C# 5 (the .NET Framework's built-in CodeDom compiler, <c>ExecCommand</c>)
    /// against the RevitAPI of the running Revit year. Measured in a colleague's 45-chat export: the
    /// same C# 6+ constructs and Revit 2024+ members failed over and over (<c>?.</c>,
    /// <c>is T x</c>, <c>$"…"</c>, <c>new ElementId(long)</c>, <c>GetSlabShapeEditor()</c>), and
    /// the plan-16 hint covers only missing members and types. Pure: the compiler's own messages are
    /// Dutch on an office PC, so errors are matched by number and the source line at the error.
    /// </summary>
    public static class ExecDialect
    {
        /// <summary>"C# 5, Revit 2023 API" — the exec description and the prompt file say it.</summary>
        public static string Short(string revitYear) =>
            "C# 5, Revit " + (string.IsNullOrEmpty(revitYear) ? "?" : revitYear) + " API";

        /// <summary>The C# 6+ constructs exec's compiler refuses, in a few words (exec's description).</summary>
        public const string NotInCSharp5 = "no ?., is T x, $\"\", out var, nameof";

        /// <summary>The same in full, with what to write instead (the prompt file).</summary>
        public const string CSharp5Rule =
            "exec compiles C# 5: no ?. or ?[], no pattern matching (is T x), no $\"…\" string interpolation, " +
            "no expression-bodied members (=> on a member), no out var, no nameof — write if/as + null checks, " +
            "string.Format(\"{0}\", x) and declared variables instead.";

        // Syntax-error numbers the C# 5 compiler gives for C# 6+ constructs (measured: ?. → CS1525 +
        // CS1003; is T x / out var → CS1026 + CS1002 + CS1525; $"" → CS1056; nameof → CS0103).
        private static readonly HashSet<string> SyntaxNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CS1525", "CS1003", "CS1026", "CS1002", "CS1056", "CS1519", "CS1513", "CS1514", "CS1001", "CS0103", "CS1031"
        };

        private sealed class Construct
        {
            public Regex Pattern;
            public string Advice;
        }

        private static readonly Construct[] Constructs =
        {
            // "x?[0]" is null-conditional indexing; "int?[]" and "new int?[3]" are valid C# 5 (nullable arrays).
            new Construct { Pattern = new Regex(@"\?\.|(?<!\bnew\s+[\w.]+)\?\[(?!\s*\])", RegexOptions.Compiled),
                Advice = "no ?. / ?[] — write `x != null ? x.Y : null` or an if" },
            new Construct { Pattern = new Regex(@"\bis\s+[A-Za-z_][\w.<>,\[\]]*\s+[a-z_]\w*\b", RegexOptions.Compiled),
                Advice = "no `is T x` — `var x = o as T; if (x != null) …`" },
            new Construct { Pattern = new Regex(@"\$@?""", RegexOptions.Compiled),
                Advice = "no $\"…\" — `string.Format(\"{0} {1}\", a, b)` or `+`" },
            new Construct { Pattern = new Regex(@"\bout\s+(?:var|[A-Za-z_][\w.<>]*)\s+[A-Za-z_]\w*\s*[,)]", RegexOptions.Compiled),
                Advice = "no out var / out T x — declare first: `int n; int.TryParse(s, out n)`" },
            new Construct { Pattern = new Regex(@"\bnameof\s*\(", RegexOptions.Compiled),
                Advice = "no nameof(…) — write the name as a string" },
        };

        /// <summary>
        /// The C# 5 advice for these compile errors: one line per construct found on a line a syntax
        /// error points at (string literals stripped first, so a "?." inside a string never counts).
        /// Empty when none.
        /// </summary>
        public static List<string> CSharp5Advice(IEnumerable<ApiLookup.CompileError> errors)
        {
            var found = new List<string>();
            foreach (var e in errors ?? Enumerable.Empty<ApiLookup.CompileError>())
            {
                if (e == null || string.IsNullOrEmpty(e.Line)) continue;
                if (!SyntaxNumbers.Contains((e.Number ?? "").Trim())) continue;
                var code = StripStrings(e.Line);
                foreach (var c in Constructs)
                    if (c.Pattern.IsMatch(code) && !found.Contains(c.Advice)) found.Add(c.Advice);
            }
            return found;
        }

        // ---------------------------------------------------------------- Revit-year differences

        /// <summary>
        /// One API member that exists only from some Revit year on. Confirmed against the SDK's
        /// RevitAPI.dll per year (2022/2023 lack them, 2024 has them). <see cref="Probe"/> is the
        /// key the caller answers with "does the running RevitAPI have it".
        /// </summary>
        public sealed class YearGap
        {
            public string Probe { get; set; }
            /// <summary>What the snippet used ("Floor.GetSlabShapeEditor()").</summary>
            public string Member { get; set; }
            /// <summary>The first Revit year that has it.</summary>
            public string Since { get; set; }
            /// <summary>What works before that year.</summary>
            public string Instead { get; set; }
            /// <summary>Error numbers this gap produces (CS1061 missing member, CS1503/CS1502 argument types…).</summary>
            public string[] Numbers { get; set; }
            /// <summary>Matched against the error's source line (and its message).</summary>
            public Regex Trigger { get; set; }

            public string Advice(string revitYear) =>
                Member + " is Revit " + Since + "+; " + Instead + " (this is Revit " + (string.IsNullOrEmpty(revitYear) ? "?" : revitYear) + ")";
        }

        public const string ProbeElementIdLong = "ElementId(long)";
        public const string ProbeElementIdValue = "ElementId.Value";
        public const string ProbeGetSlabShapeEditor = "Floor.GetSlabShapeEditor";

        public static readonly IReadOnlyList<YearGap> YearGaps = new[]
        {
            new YearGap
            {
                Probe = ProbeElementIdLong, Member = "new ElementId(long)", Since = "2024",
                Instead = "here ids are int: `new ElementId(123)` / `(int)n`, read them with `id.IntegerValue`",
                Numbers = new[] { "CS1503", "CS1502", "CS1501", "CS1729", "CS0029", "CS0266", "CS1950" },
                Trigger = new Regex(@"ElementId\b.*(?:\blong\b|\d+L\b|Int64)|(?:\blong\b|Int64).*ElementId\b", RegexOptions.Compiled),
            },
            new YearGap
            {
                Probe = ProbeElementIdValue, Member = "ElementId.Value", Since = "2024",
                Instead = "use `id.IntegerValue` (an int)",
                Numbers = new[] { "CS1061", "CS0117" },
                Trigger = new Regex(@"(?:\bId|ElementId|\bid)\b[\w()\[\]]*\.Value\b|ElementId\b.*\bValue\b", RegexOptions.Compiled),
            },
            new YearGap
            {
                Probe = ProbeGetSlabShapeEditor, Member = "Floor.GetSlabShapeEditor()", Since = "2024",
                Instead = "use the floor's `SlabShapeEditor` property",
                Numbers = new[] { "CS1061", "CS0117" },
                Trigger = new Regex(@"GetSlabShapeEditor\s*\(", RegexOptions.Compiled),
            },
        };

        /// <summary>
        /// The year advice for these compile errors: a gap counts when an error of its kind points at
        /// a line matching its trigger AND the running RevitAPI lacks the member (<paramref name="apiHas"/>
        /// answers per <see cref="YearGap.Probe"/>; a 2024 Revit never gets the 2023 advice).
        /// </summary>
        public static List<string> YearAdvice(IEnumerable<ApiLookup.CompileError> errors, string revitYear, Func<string, bool> apiHas)
        {
            var found = new List<string>();
            foreach (var e in errors ?? Enumerable.Empty<ApiLookup.CompileError>())
            {
                if (e == null) continue;
                var number = (e.Number ?? "").Trim();
                var text = StripStrings(e.Line ?? "") + "\n" + (e.Message ?? "");
                foreach (var g in YearGaps)
                {
                    if (!g.Numbers.Contains(number, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!g.Trigger.IsMatch(text)) continue;
                    if (apiHas != null && apiHas(g.Probe)) continue;
                    var line = g.Advice(revitYear);
                    if (!found.Contains(line)) found.Add(line);
                }
            }
            return found;
        }

        /// <summary>
        /// The block exec appends to its compile errors, after the plan-16 lookup hint: null when no
        /// construct or year gap was recognised.
        /// </summary>
        public static string Hint(IEnumerable<ApiLookup.CompileError> errors, string revitYear, Func<string, bool> apiHas)
        {
            var list = (errors ?? Enumerable.Empty<ApiLookup.CompileError>()).ToList();
            var advice = CSharp5Advice(list).Concat(YearAdvice(list, revitYear, apiHas)).ToList();
            if (advice.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append("Hint: exec compiles ").Append(Short(revitYear)).AppendLine(":");
            foreach (var a in advice) sb.Append("  - ").AppendLine(a);
            return sb.ToString().TrimEnd();
        }

        // "a ?. b" stays, "\"x?.y\"" becomes "\"\"". Verbatim and regular strings, char literals and
        // comments go; the $ before an interpolated string is kept (it is the construct itself).
        internal static string StripStrings(string line)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            var sb = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '"' || (c == '@' && i + 1 < line.Length && line[i + 1] == '"'))
                {
                    bool verbatim = c == '@';
                    sb.Append('"');
                    i += verbatim ? 2 : 1;
                    while (i < line.Length)
                    {
                        if (verbatim && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { i += 2; continue; }
                        if (!verbatim && line[i] == '\\') { i += 2; continue; }
                        if (line[i] == '"') { i++; break; }
                        i++;
                    }
                    sb.Append('"');
                    continue;
                }
                if (c == '\'')
                {
                    int end = line.IndexOf('\'', i + (i + 1 < line.Length && line[i + 1] == '\\' ? 3 : 2));
                    if (end > i && end - i <= 8) { sb.Append("' '"); i = end + 1; continue; }
                }
                // Comments are words, not code: "x = y; // y is a wall" holds no `is T x`.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0) break;
                    sb.Append(' ');
                    i = close + 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
