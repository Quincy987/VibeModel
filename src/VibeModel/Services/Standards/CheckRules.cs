using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Standards
{
    public enum CheckSeverity { Ok, Info, Warn, Fail, Skipped }

    /// <summary>
    /// One rule from a pack's checks.json. The add-in implements a fixed set of rule KINDS
    /// (ModelChecks); the pack supplies ids, severities, messages, fixes and parameters, so an
    /// office admin can add or tune rules without a code change.
    /// </summary>
    public sealed class CheckRule
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public CheckSeverity Severity { get; set; } = CheckSeverity.Warn;
        public string Message { get; set; }
        public string Fix { get; set; }
        public bool Enabled { get; set; } = true;
        public Dictionary<string, object> Params { get; set; } =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public string GetString(string key, string fallback = null) => PackJson.GetString(Params, key) ?? fallback;
        public List<string> GetStrings(string key) => PackJson.GetStrings(Params, key);
        public double GetDouble(string key, double fallback) => PackJson.GetDouble(Params, key) ?? fallback;

        /// <summary>
        /// Parse checks.json: either {"rules":[...]} or a bare array. Bad entries are skipped;
        /// a missing kind falls back to the id (so "pinned" can be written once).
        /// </summary>
        public static List<CheckRule> ParseAll(string json, out string error)
        {
            error = null;
            var rules = new List<CheckRule>();
            if (string.IsNullOrWhiteSpace(json)) return rules;
            object raw;
            try { raw = PackJson.Serializer.DeserializeObject(json); }
            catch (Exception ex) { error = "checks.json is not valid JSON: " + ex.Message; return rules; }

            var list = PackJson.AsDict(raw) is Dictionary<string, object> root
                ? PackJson.GetDictList(root, "rules")
                : PackJson.AsDictList(raw);

            foreach (var d in list)
            {
                var id = PackJson.GetString(d, "id");
                var kind = PackJson.GetString(d, "kind") ?? id;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(kind)) continue;
                rules.Add(new CheckRule
                {
                    Id = id.Trim(),
                    Kind = kind.Trim(),
                    Severity = ParseSeverity(PackJson.GetString(d, "severity")),
                    Message = PackJson.GetString(d, "message"),
                    Fix = PackJson.GetString(d, "fix"),
                    Enabled = PackJson.GetBool(d, "enabled") ?? true,
                    Params = PackJson.GetDict(d, "params") ??
                             new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                });
            }
            return rules;
        }

        public static CheckSeverity ParseSeverity(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "fail": case "error": return CheckSeverity.Fail;
                case "info": return CheckSeverity.Info;
                default: return CheckSeverity.Warn;
            }
        }
    }

    /// <summary>The outcome of one rule on one document (or one scoped set of elements).</summary>
    public sealed class CheckFinding
    {
        public string RuleId { get; set; }
        public string Kind { get; set; }
        public CheckSeverity Severity { get; set; }
        /// <summary>What was found (for OK: what was verified).</summary>
        public string Detail { get; set; }
        public string Fix { get; set; }
        public List<int> ElementIds { get; set; } = new List<int>();
        public int Count { get; set; }

        public bool IsProblem => Severity == CheckSeverity.Warn || Severity == CheckSeverity.Fail;

        public static CheckFinding Ok(CheckRule r, string detail) =>
            new CheckFinding { RuleId = r.Id, Kind = r.Kind, Severity = CheckSeverity.Ok, Detail = detail };

        public static CheckFinding Skip(CheckRule r, string why) =>
            new CheckFinding { RuleId = r.Id, Kind = r.Kind, Severity = CheckSeverity.Skipped, Detail = why };

        public static CheckFinding Info(CheckRule r, string detail) =>
            new CheckFinding { RuleId = r.Id, Kind = r.Kind, Severity = CheckSeverity.Info, Detail = detail };

        /// <summary>A problem: severity from the rule, message from the rule when it has one.</summary>
        public static CheckFinding Problem(CheckRule r, string detail, IEnumerable<int> ids = null, int count = -1)
        {
            var list = ids?.ToList() ?? new List<int>();
            return new CheckFinding
            {
                RuleId = r.Id, Kind = r.Kind, Severity = r.Severity,
                Detail = string.IsNullOrWhiteSpace(r.Message) ? detail : r.Message + " — " + detail,
                Fix = r.Fix, ElementIds = list, Count = count >= 0 ? count : list.Count
            };
        }
    }

    /// <summary>Report rendering: one OK/WARN/FAIL/SKIPPED line per rule, a Fix line per problem, a Verdict.</summary>
    public static class CheckReport
    {
        public const int MaxIdsShown = 12;

        public static string Render(string header, IList<CheckFinding> findings)
        {
            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine(new string('=', Math.Min(60, Math.Max(5, header.Length))));
            foreach (var f in findings)
            {
                sb.AppendLine(Prefix(f.Severity) + f.RuleId + " — " + f.Detail + Ids(f));
                if (f.IsProblem && !string.IsNullOrWhiteSpace(f.Fix))
                    sb.AppendLine("         Fix: " + f.Fix);
            }
            sb.AppendLine();
            sb.AppendLine(Verdict(findings));
            return sb.ToString();
        }

        public static string Verdict(IList<CheckFinding> findings)
        {
            int warn = findings.Count(f => f.Severity == CheckSeverity.Warn);
            int fail = findings.Count(f => f.Severity == CheckSeverity.Fail);
            int ok = findings.Count(f => f.Severity == CheckSeverity.Ok);
            if (warn == 0 && fail == 0)
                return "Verdict: clean — " + ok + " rule(s) OK" +
                       Skipped(findings) + ".";
            var worst = findings.Where(f => f.IsProblem)
                .OrderByDescending(f => f.Severity).ThenByDescending(f => f.Count).First();
            return "Verdict: " + fail + " FAIL, " + warn + " WARN, " + ok + " OK" + Skipped(findings) +
                   " — start with " + worst.RuleId + ".";
        }

        /// <summary>
        /// One compact line appended after a modifying command, or null when there is nothing
        /// to say: "-- check: 2 WARN (param-filled ×2) → run 'check' for fixes".
        /// </summary>
        public static string SummaryLine(IList<CheckFinding> findings)
        {
            var problems = findings.Where(f => f.IsProblem).ToList();
            if (problems.Count == 0) return null;
            int warn = problems.Count(f => f.Severity == CheckSeverity.Warn);
            int fail = problems.Count(f => f.Severity == CheckSeverity.Fail);
            var head = (fail > 0 ? fail + " FAIL" : "") + (fail > 0 && warn > 0 ? ", " : "") + (warn > 0 ? warn + " WARN" : "");
            var detail = string.Join(", ", problems.Select(p => p.RuleId + (p.Count > 1 ? " ×" + p.Count : "")));
            return "-- check: " + head + " (" + detail + ") → run 'check' for fixes";
        }

        private static string Prefix(CheckSeverity s)
        {
            switch (s)
            {
                case CheckSeverity.Ok: return "OK       ";
                case CheckSeverity.Info: return "INFO     ";
                case CheckSeverity.Warn: return "WARN:    ";
                case CheckSeverity.Fail: return "FAIL:    ";
                default: return "SKIPPED: ";
            }
        }

        private static string Ids(CheckFinding f)
        {
            if (f.ElementIds == null || f.ElementIds.Count == 0) return "";
            var shown = f.ElementIds.Take(MaxIdsShown).Select(i => i.ToString(CultureInfo.InvariantCulture));
            return " [ids: " + string.Join(", ", shown) +
                   (f.ElementIds.Count > MaxIdsShown ? " +" + (f.ElementIds.Count - MaxIdsShown) + " more" : "") + "]";
        }

        private static string Skipped(IList<CheckFinding> findings)
        {
            int n = findings.Count(f => f.Severity == CheckSeverity.Skipped);
            return n > 0 ? ", " + n + " skipped" : "";
        }

        public static object ToJson(IList<CheckFinding> findings)
        {
            return findings.Select(f => (object)new Dictionary<string, object>
            {
                { "rule", f.RuleId },
                { "kind", f.Kind },
                { "severity", f.Severity.ToString().ToLowerInvariant() },
                { "detail", f.Detail },
                { "fix", f.IsProblem ? f.Fix : null },
                { "count", f.Count },
                { "ids", f.ElementIds.Cast<object>().ToList() }
            }).ToList();
        }
    }

    /// <summary>
    /// Feedback loop (plan 11 §7): every problem a check reports is logged as rule id + count
    /// (no element data, no names) so the most frequent rules can drive the next pack version.
    /// </summary>
    public static class CheckLog
    {
        private static readonly object Sync = new object();

        public static void Append(string path, string packId, string source, IEnumerable<CheckFinding> findings)
        {
            try
            {
                var problems = findings.Where(f => f.IsProblem).ToList();
                if (problems.Count == 0 || string.IsNullOrEmpty(path)) return;
                var sb = new StringBuilder();
                var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (var p in problems)
                    sb.AppendLine(PackJson.Serializer.Serialize(new Dictionary<string, object>
                    {
                        { "utc", now }, { "pack", packId }, { "source", source },
                        { "rule", p.RuleId }, { "severity", p.Severity.ToString().ToLowerInvariant() },
                        { "count", p.Count }
                    }));
                lock (Sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { /* logging must never break a command */ }
        }

        /// <summary>Per-rule totals: (rule, times reported, summed element count), most frequent first.</summary>
        public static List<Tuple<string, int, int>> Stats(string path)
        {
            var hits = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return new List<Tuple<string, int, int>>();
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Dictionary<string, object> d;
                    try { d = PackJson.Parse(line); } catch { continue; }
                    var rule = PackJson.GetString(d, "rule");
                    if (rule == null) continue;
                    if (!hits.TryGetValue(rule, out var h)) hits[rule] = h = new int[2];
                    h[0]++;
                    h[1] += (int)(PackJson.GetDouble(d, "count") ?? 0);
                }
            }
            catch { /* unreadable log → empty stats */ }
            return hits.Select(kv => Tuple.Create(kv.Key, kv.Value[0], kv.Value[1]))
                .OrderByDescending(t => t.Item2).ThenBy(t => t.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
