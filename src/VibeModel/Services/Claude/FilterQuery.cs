using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// The Revit-free half of `filter` (plan 23): splitting the arguments into category,
    /// parameter, operator and value, comparing a value the way `filter` always has, and — for
    /// `filter any` — finding the spelling of a parameter name. Pure, tested.
    /// </summary>
    public static class FilterQuery
    {
        public static readonly string[] Operators = { ">=", "<=", "!=", "=", ">", "<", "contains" };

        /// <summary>The category token that searches every category: `filter any Mark = K01`.</summary>
        public const string AnyCategory = "any";

        public static bool IsAny(string category) => string.Equals((category ?? "").Trim(), AnyCategory, StringComparison.OrdinalIgnoreCase);

        public sealed class Split
        {
            public string Category, Parameter, Operator, Value, Error;
            public bool HasExpression => Operator != null;
        }

        /// <summary>
        /// Category = the first token; when an operator follows, the parameter is every token
        /// between the category and the operator, the value everything after it. Flags are
        /// already removed.
        /// </summary>
        public static Split Parse(IList<string> tokens)
        {
            var r = new Split();
            if (tokens == null || tokens.Count == 0) { r.Error = "No category specified."; return r; }
            r.Category = tokens[0];
            int opIndex = -1;
            for (int i = 1; i < tokens.Count && opIndex < 0; i++)
                foreach (var op in Operators)
                    if (tokens[i].Equals(op, StringComparison.OrdinalIgnoreCase)) { opIndex = i; r.Operator = op; break; }
            if (opIndex < 0) return r;
            r.Parameter = string.Join(" ", tokens.Skip(1).Take(opIndex - 1));
            r.Value = string.Join(" ", tokens.Skip(opIndex + 1));
            if (string.IsNullOrWhiteSpace(r.Parameter))
                r.Error = "Missing parameter name before operator '" + r.Operator + "'.\nUsage: filter <category> <paramName> " + r.Operator + " <value>";
            else if (string.IsNullOrWhiteSpace(r.Value))
                r.Error = "Missing value after operator '" + r.Operator + "'.\nUsage: filter <category> <paramName> " + r.Operator + " <value>";
            return r;
        }

        /// <summary>A text value (also the name behind a Type/Level/Material parameter): =, != and contains, case-insensitive.</summary>
        public static bool Text(string value, string op, string target)
        {
            value = value ?? "";
            switch (op)
            {
                case "=": return value.Equals(target, StringComparison.OrdinalIgnoreCase);
                case "!=": return !value.Equals(target, StringComparison.OrdinalIgnoreCase);
                case "contains": return value.IndexOf(target ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }

        /// <summary>A measured value in the unit `params` prints (mm, m², m³, °); = and != within half a unit.</summary>
        public static bool Number(double value, string op, string target)
        {
            if (!double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) return false;
            switch (op)
            {
                case "=": return Math.Abs(value - t) < 0.5;
                case "!=": return Math.Abs(value - t) >= 0.5;
                case ">": return value > t;
                case "<": return value < t;
                case ">=": return value >= t;
                case "<=": return value <= t;
                default: return false;
            }
        }

        /// <summary>
        /// An integer value; a target that is no integer (Yes/No, an enum) is compared with the
        /// value as Revit shows it, = and != only.
        /// </summary>
        public static bool Integer(int value, string valueString, string op, string target)
        {
            if (!int.TryParse(target, out int t))
            {
                var shown = valueString ?? "";
                switch (op)
                {
                    case "=": return shown.Equals(target, StringComparison.OrdinalIgnoreCase);
                    case "!=": return !shown.Equals(target, StringComparison.OrdinalIgnoreCase);
                    default: return false;
                }
            }
            switch (op)
            {
                case "=": return value == t;
                case "!=": return value != t;
                case ">": return value > t;
                case "<": return value < t;
                case ">=": return value >= t;
                case "<=": return value <= t;
                default: return false;
            }
        }

        /// <summary>
        /// Revit's parameter lookup by name is case-sensitive: the one name from <paramref name="known"/>
        /// that equals <paramref name="name"/> ignoring case (an exact match wins), else null.
        /// </summary>
        public static string Spelling(string name, IEnumerable<string> known)
        {
            if (string.IsNullOrWhiteSpace(name) || known == null) return null;
            var list = known.Where(k => k != null).Distinct(StringComparer.Ordinal).ToList();
            if (list.Contains(name)) return name;
            var hits = list.Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Count >= 1 ? hits.OrderBy(h => h, StringComparer.Ordinal).First() : null;
        }

        /// <summary>The closest parameter names, for the "no element has a parameter" hint.</summary>
        public static List<string> Closest(string name, IEnumerable<string> known)
            => ApiLookup.Closest(name, (known ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase));

        /// <summary>"Walls 8, Structural Foundations 3": the per-category counts, largest first, then by name.</summary>
        public static List<KeyValuePair<string, int>> ByCount(IEnumerable<string> categories)
            => (categories ?? Enumerable.Empty<string>())
                .GroupBy(c => c ?? "(no category)")
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
