using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// `schedule --calc "&lt;Name&gt;=&lt;formula&gt;"`: the Revit API cannot add a calculated (formula)
    /// schedule field — measured in Revit 2023, `AddField(ScheduleFieldType.Formula)` throws
    /// "fieldType is not a schedulable field type" and `ScheduleField.IsCalculatedField` is read-only.
    /// So the command builds the rest of the schedule and hands the user the clicks. This class is the
    /// pure part: which schedule fields the formula names (they must be in the schedule — a calculated
    /// field can only use those), which Type the field needs (Revit refuses a mismatch with
    /// "Inconsistent units"), and the one-line manual step. Unit-tested.
    /// </summary>
    internal static class CalculatedFieldHint
    {
        /// <summary>Revit formula functions and operators that are words, never field names.</summary>
        private static readonly HashSet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "and", "or", "not", "sin", "cos", "tan", "asin", "acos", "atan", "sqrt", "abs", "exp", "log", "ln",
            "pi", "round", "roundup", "rounddown", "size_lookup"
        };

        /// <summary>Unit suffixes a formula may carry after a number ("1000 mm").</summary>
        private static readonly Dictionary<string, int> Units = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "mm", 1 }, { "cm", 1 }, { "dm", 1 }, { "m", 1 }, { "m²", 2 }, { "m2", 2 }, { "mm²", 2 }, { "m³", 3 }, { "m3", 3 }, { "mm³", 3 }
        };

        private const string Boundary = " \t+-*/^(),<>=";

        /// <summary>
        /// The schedule field names the formula uses, in formula order, matched EXACTLY (case-insensitive)
        /// on operator/space boundaries, longest name first — never a substring ("Width" is not "Width 2",
        /// the "if" in "if(" is not "Modified"). A name with spaces ("Base Offset") is found as a whole.
        /// </summary>
        public static List<string> FieldsIn(string formula, IEnumerable<string> fieldNames)
        {
            var spans = FindSpans(formula ?? "", fieldNames);
            return spans.OrderBy(s => s.Start).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The formula with every field written exactly as the schedule names it. Matching is
        /// case-insensitive, but Revit's formulas are not: "length*width" would be refused in the
        /// calculated-field dialog, "Length*Width" is what the user must type.
        /// </summary>
        public static string WithExactNames(string formula, IEnumerable<string> fieldNames)
        {
            var text = formula ?? "";
            var sb = new StringBuilder();
            int pos = 0;
            foreach (var s in FindSpans(text, fieldNames).OrderBy(x => x.Start))
            {
                sb.Append(text, pos, s.Start - pos).Append(s.Name);
                pos = s.Start + s.Length;
            }
            return sb.Append(text, pos, text.Length - pos).ToString();
        }

        /// <summary>
        /// Words in the formula that are neither a field of the category, a Revit function nor a unit —
        /// told to the user, since Revit would refuse them in the formula.
        /// </summary>
        public static List<string> UnknownWords(string formula, IEnumerable<string> fieldNames)
        {
            var masked = Mask(formula ?? "", FindSpans(formula ?? "", fieldNames), out _);
            var words = new List<string>();
            foreach (var tok in masked.Split(Boundary.ToCharArray(), StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.IndexOf('\u0001') >= 0) continue;
                if (!tok.Any(char.IsLetter)) continue;
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
                if (Functions.Contains(tok) || Units.ContainsKey(tok)) continue;
                // "2m", "1000mm": a number written against its unit.
                var unitPart = tok.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
                if (unitPart.Length < tok.Length && Units.ContainsKey(unitPart)) continue;
                if (!words.Contains(tok, StringComparer.OrdinalIgnoreCase)) words.Add(tok);
            }
            return words;
        }

        /// <summary>
        /// The Type the calculated field needs, from the formula's dimension: length × length = Area,
        /// three lengths (or area × length) = Volume, a length or a sum of lengths = Length, no measured
        /// field = Number. Null when the formula mixes dimensions or uses a field of another kind
        /// (the user then picks the Type that matches the result).
        /// </summary>
        public static QuantityKind? InferType(string formula, IDictionary<string, QuantityKind> fieldKinds)
        {
            var kinds = fieldKinds ?? new Dictionary<string, QuantityKind>();
            var spans = FindSpans(formula ?? "", kinds.Keys);
            var masked = Mask(formula ?? "", spans, out var names);
            var dims = new List<int?>();
            foreach (var n in names)
            {
                kinds.TryGetValue(n, out var k);
                dims.Add(DimensionOf(k));
            }
            try
            {
                var p = new DimParser(masked, dims);
                int? d = p.Expr();
                if (!p.AtEnd) return null;
                switch (d)
                {
                    case 0: return QuantityKind.Number;
                    case 1: return QuantityKind.Length;
                    case 2: return QuantityKind.Area;
                    case 3: return QuantityKind.Volume;
                    default: return null;
                }
            }
            catch (FormatException) { return null; }
        }

        /// <summary>The Type name Revit's calculated-field dialog uses; null for anything it cannot be.</summary>
        public static string TypeLabel(QuantityKind? kind)
        {
            switch (kind)
            {
                case QuantityKind.Length: return "Length";
                case QuantityKind.Area: return "Area";
                case QuantityKind.Volume: return "Volume";
                case QuantityKind.Number: return "Number";
                default: return null;
            }
        }

        /// <summary>The one-line manual step (text mode). <paramref name="type"/> null = the user picks.</summary>
        public static string ManualStep(string scheduleName, string name, string formula, QuantityKind? type)
        {
            var label = TypeLabel(type);
            var typePart = label != null
                ? "Type \"" + label + "\" (another Type gives \"Inconsistent units\")"
                : "Type = the unit of the result (Length, Area, Volume or Number; a mismatch gives \"Inconsistent units\")";
            return "Manual step (the API cannot add a calculated field): Project Browser → Schedules/Quantities → \"" + scheduleName +
                   "\" → Properties → Fields: Edit… → fx (Add calculated parameter) → Name \"" + name + "\", Discipline Common, " +
                   typePart + ", Formula: " + formula + " → OK. The column then follows every model change.";
        }

        public static Dictionary<string, object> ManualStepJson(string scheduleName, string name, string formula, QuantityKind? type)
            => new Dictionary<string, object>
            {
                { "name", name },
                { "formula", formula },
                { "type", TypeLabel(type) },
                { "apiCannot", true },
                { "clicks", ManualStep(scheduleName, name, formula, type) }
            };

        // ------------------------------------------------------------------ internals

        private struct Span { public int Start, Length; public string Name; }

        private static List<Span> FindSpans(string formula, IEnumerable<string> fieldNames)
        {
            var spans = new List<Span>();
            var taken = new bool[formula.Length];
            foreach (var name in (fieldNames ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
            {
                int from = 0;
                while (from <= formula.Length - name.Length)
                {
                    int i = formula.IndexOf(name, from, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) break;
                    int end = i + name.Length;
                    bool left = i == 0 || Boundary.IndexOf(formula[i - 1]) >= 0;
                    bool right = end == formula.Length || Boundary.IndexOf(formula[end]) >= 0;
                    bool free = true;
                    for (int k = i; k < end; k++) if (taken[k]) { free = false; break; }
                    if (left && right && free)
                    {
                        for (int k = i; k < end; k++) taken[k] = true;
                        spans.Add(new Span { Start = i, Length = name.Length, Name = name });
                    }
                    from = i + 1;
                }
            }
            return spans;
        }

        /// <summary>Replaces each field span with "\u0001&lt;n&gt;\u0002"; <paramref name="names"/>[n] is its field.</summary>
        private static string Mask(string formula, List<Span> spans, out List<string> names)
        {
            names = new List<string>();
            var sb = new StringBuilder();
            int pos = 0;
            foreach (var s in spans.OrderBy(x => x.Start))
            {
                sb.Append(formula, pos, s.Start - pos);
                sb.Append('\u0001').Append(names.Count.ToString(CultureInfo.InvariantCulture)).Append('\u0002');
                names.Add(s.Name);
                pos = s.Start + s.Length;
            }
            sb.Append(formula, pos, formula.Length - pos);
            return sb.ToString();
        }

        private static int? DimensionOf(QuantityKind k)
        {
            switch (k)
            {
                case QuantityKind.Number: return 0;
                case QuantityKind.Length: return 1;
                case QuantityKind.Area: return 2;
                case QuantityKind.Volume: return 3;
                default: return null;
            }
        }

        /// <summary>
        /// Recursive descent over the masked formula, computing the length dimension of the result
        /// (null = unknown/mixed). Comparisons inside if(...) only need to parse, not to agree.
        /// </summary>
        private sealed class DimParser
        {
            private readonly string _s;
            private readonly List<int?> _fields;
            private int _i;

            public DimParser(string s, List<int?> fields) { _s = s; _fields = fields; }

            public bool AtEnd { get { Skip(); return _i >= _s.Length; } }

            private void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
            private char Peek() { Skip(); return _i < _s.Length ? _s[_i] : '\0'; }
            private bool Eat(char c) { if (Peek() == c) { _i++; return true; } return false; }

            private static int? Same(int? a, int? b) => a.HasValue && b.HasValue && a.Value == b.Value ? a : null;

            // comparison := expr [ (<|>|=|<=|>=) expr ]  — a condition, dimension irrelevant
            private void Condition()
            {
                Expr();
                Skip();
                if (_i < _s.Length && "<>=".IndexOf(_s[_i]) >= 0)
                {
                    _i++;
                    if (_i < _s.Length && _s[_i] == '=') _i++;
                    Expr();
                }
            }

            public int? Expr()
            {
                int? d = Term();
                while (true)
                {
                    char c = Peek();
                    if (c == '+' || c == '-') { _i++; d = Same(d, Term()); }
                    else return d;
                }
            }

            private int? Term()
            {
                int? d = Power();
                while (true)
                {
                    char c = Peek();
                    if (c == '*') { _i++; var r = Power(); d = d.HasValue && r.HasValue ? d + r : null; }
                    else if (c == '/') { _i++; var r = Power(); d = d.HasValue && r.HasValue ? d - r : null; }
                    else return d;
                }
            }

            private int? Power()
            {
                int? d = Unary();
                if (Eat('^'))
                {
                    var n = Number();
                    if (n == null) throw new FormatException();
                    d = d.HasValue && Math.Abs(n.Value - Math.Round(n.Value)) < 1e-9 ? d * (int)Math.Round(n.Value) : (d == 0 ? 0 : (int?)null);
                }
                return d;
            }

            private int? Unary()
            {
                if (Eat('-') || Eat('+')) return Unary();
                return Atom();
            }

            private int? Atom()
            {
                char c = Peek();
                if (c == '(') { _i++; var d = Expr(); if (!Eat(')')) throw new FormatException(); return d; }
                if (c == '\u0001')
                {
                    int close = _s.IndexOf('\u0002', _i);
                    if (close < 0) throw new FormatException();
                    int n = int.Parse(_s.Substring(_i + 1, close - _i - 1), CultureInfo.InvariantCulture);
                    _i = close + 1;
                    return _fields[n];
                }
                if (char.IsDigit(c) || c == '.')
                {
                    Number();
                    // An optional unit suffix: "1000 mm", "2 m²".
                    int save = _i;
                    var word = Word();
                    if (word != null && Units.TryGetValue(word, out int ud)) return ud;
                    _i = save;
                    return 0;
                }
                var name = Word();
                if (name == null || !Functions.Contains(name)) throw new FormatException();
                if (name.Equals("pi", StringComparison.OrdinalIgnoreCase)) { if (Eat('(')) { if (!Eat(')')) throw new FormatException(); } return 0; }
                if (!Eat('(')) throw new FormatException();
                int? result;
                if (name.Equals("if", StringComparison.OrdinalIgnoreCase))
                {
                    Condition();
                    if (!Eat(',')) throw new FormatException();
                    var a = Expr();
                    if (!Eat(',')) throw new FormatException();
                    var b = Expr();
                    result = Same(a, b);
                }
                else if (name.Equals("and", StringComparison.OrdinalIgnoreCase) || name.Equals("or", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("not", StringComparison.OrdinalIgnoreCase))
                {
                    Condition();
                    while (Eat(',')) Condition();
                    result = 0;
                }
                else
                {
                    var a = Expr();
                    while (Eat(',')) Expr();
                    if (name.Equals("sqrt", StringComparison.OrdinalIgnoreCase)) result = a.HasValue && a.Value % 2 == 0 ? a / 2 : null;
                    else if (name.Equals("abs", StringComparison.OrdinalIgnoreCase) || name.StartsWith("round", StringComparison.OrdinalIgnoreCase)) result = a;
                    else result = 0;
                }
                if (!Eat(')')) throw new FormatException();
                return result;
            }

            private double? Number()
            {
                Skip();
                int start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                if (_i == start) return null;
                return double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
            }

            private string Word()
            {
                Skip();
                int start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '²' || _s[_i] == '³')) _i++;
                return _i == start ? null : _s.Substring(start, _i - start);
            }
        }
    }
}
