using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Renders the two per-turn prompt blocks (plan 11 §2): TEMPLATE (what the open document
    /// is) and STANDARDS (the office rules). Both are hard-capped in UTF-8 bytes so a large
    /// pack can never bloat every chat turn. Pure — unit-tested.
    /// </summary>
    public static class StandardsRender
    {
        public const int TemplateMaxBytes = 1024;

        /// <summary>
        /// STANDARDS block: one header line, the core rule text (cut at a line boundary to fit),
        /// and one line naming the on-demand topic sheets.
        /// </summary>
        public static string RenderCore(StandardsPack pack, int maxBytes = -1)
        {
            if (pack == null) return "";
            if (maxBytes <= 0) maxBytes = pack.CoreMaxBytes;

            var header = "STANDARDS: office standard '" + pack.Id + "'" +
                         (string.IsNullOrEmpty(pack.Version) ? "" : " v" + pack.Version) +
                         " is ACTIVE for this document. These office rules override generic Revit habits.";
            var topics = pack.TopicNames.ToList();
            var footer = topics.Count == 0
                ? ""
                : "Full rule sheets on demand: `standards <topic>` — " + string.Join(", ", topics) + ".";
            if (pack.PhaseTable?.Default != null)
                footer += (footer.Length > 0 ? "\n" : "") +
                          "Creating commands default to phase '" + pack.PhaseTable.DefaultKey + "' (" +
                          pack.PhaseTable.Default.Created + "); pass --phase " +
                          string.Join("|", pack.PhaseTable.Keys) + " to override. `check` audits the model against these rules.";

            // Header, footer and three line breaks are fixed; the core gets the rest, so the final
            // cut below never has to drop the footer's phase hint.
            int fixedBytes = Bytes(header) + Bytes(footer) + 3 * Environment.NewLine.Length;
            var core = TruncateAtLine(pack.GetCoreText() ?? "", Math.Max(0, maxBytes - fixedBytes));

            var sb = new StringBuilder();
            sb.AppendLine(header);
            if (core.Length > 0) sb.AppendLine(core.TrimEnd());
            if (footer.Length > 0) sb.AppendLine(footer);
            return TruncateAtLine(sb.ToString(), maxBytes);
        }

        /// <summary>TEMPLATE block: compact live facts about the open document, ≤ maxBytes.</summary>
        public static string RenderTemplateSummary(TemplateFacts f, ActiveViewFacts av, StandardsPack active,
            FingerprintResult match, int maxBytes = TemplateMaxBytes)
        {
            if (f == null) return "";
            var lines = new List<string>();
            lines.Add("TEMPLATE: live facts of the open document '" + f.Title + "' (read from the model, not from a manual).");
            lines.Add("- office standard: " + (active != null
                ? active.Id + " (fingerprint " + match?.Summary + ")"
                : "none matched (generic Revit rules apply)"));

            if (f.Phases.Count > 0)
                lines.Add("- phases: " + string.Join(" | ", f.Phases));
            if (av != null && !string.IsNullOrEmpty(av.Name))
            {
                var l = "- active view: '" + av.Name + "'";
                if (!string.IsNullOrEmpty(av.Phase)) l += " on phase '" + av.Phase + "'";
                if (!string.IsNullOrEmpty(av.PhaseFilter)) l += ", filter '" + av.PhaseFilter + "'";
                if (!string.IsNullOrEmpty(av.Template)) l += ", template '" + av.Template + "'";
                lines.Add(l);
                var existing = active?.PhaseTable?.ExistingPhase;
                if (existing != null && string.Equals(existing, av.Phase, StringComparison.OrdinalIgnoreCase))
                    lines.Add("  ! this view is on the EXISTING phase: elements drawn here by hand become 'existing'; VibeModel commands set the phase explicitly, but new work will not show in this view.");
            }

            if (f.IsFull)
            {
                if (f.ViewTemplates.Count > 0)
                    lines.Add("- view templates: " + f.ViewTemplates.Count + " (" + GroupByPrefix(f.ViewTemplates, 6) + ")");
                if (f.ScopeBoxes.Count > 0)
                    lines.Add("- scope boxes: " + string.Join(", ", f.ScopeBoxes.Take(6)));
                lines.Add("- coordinates: " + DescribeCoordinates(f) + "; CAD links " + f.CadLinks +
                          " (" + f.CadLinksPinned + " pinned), imports " + f.CadImports +
                          ", point clouds " + f.PointClouds);
                if (f.Families.Count > 0)
                    lines.Add("- families: " + f.Families.Count + " loaded (" +
                              PrefixHistogram(f.Families.Select(x => x.Name), active?.FamilyPrefixes, 7) + ")");
                lines.Add("- sheets: " + f.Sheets.Count + "; titleblocks: " +
                          (f.Titleblocks.Count == 0 ? "none" : string.Join(", ", f.Titleblocks.Take(2)) +
                                                              (f.Titleblocks.Count > 2 ? " +" + (f.Titleblocks.Count - 2) : "")));
            }
            lines.Add("Run `template` for the full inventory.");

            // Fit by dropping lines from the end (keeping the closing hint) rather than cutting mid-line.
            var text = string.Join("\n", lines) + "\n";
            while (Bytes(text) > maxBytes && lines.Count > 3)
            {
                lines.RemoveAt(lines.Count - 2);
                text = string.Join("\n", lines) + "\n";
            }
            return TruncateAtLine(text, maxBytes);
        }

        public static string DescribeCoordinates(TemplateFacts f)
        {
            var c = f.Coordinates;
            if (c == null || !c.Available) return "unknown";
            return "survey point " + (c.SurveyAtSharedOrigin ? "at shared 0,0,0" : "NOT at shared 0,0,0 (N " + Mm(c.SurveyNorthMm) + " E " + Mm(c.SurveyEastMm) + ")") +
                   (c.SurveyPinned ? " (pinned)" : "") +
                   ", base point N " + Mm(c.BaseNorthMm) + " E " + Mm(c.BaseEastMm) +
                   (Math.Abs(c.BaseAngleDeg) > 1e-6 ? " angle " + c.BaseAngleDeg.ToString("0.###", CultureInfo.InvariantCulture) + "°" : "") +
                   (c.BasePinned ? " (pinned)" : " (unpinned)") +
                   (c.SharedCoordinates ? ", shared coordinates set" : ", no shared coordinates");
        }

        /// <summary>"EX_W_C ×8, EX_P_S ×6, …" — first three underscore tokens as the group key.</summary>
        public static string GroupByPrefix(IEnumerable<string> names, int maxGroups)
        {
            var groups = names
                .GroupBy(PrefixKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var parts = groups.Take(maxGroups).Select(g => g.Key + " ×" + g.Count()).ToList();
            if (groups.Count > maxGroups) parts.Add("+" + (groups.Count - maxGroups) + " more");
            return string.Join(", ", parts);
        }

        internal static string PrefixKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var t = name.Split('_');
            if (t.Length >= 4) return string.Join("_", t.Take(3));
            if (t.Length >= 2) return string.Join("_", t.Take(t.Length - 1));
            return name;
        }

        /// <summary>Counts names per declared prefix (longest prefix wins); the rest is "other".</summary>
        public static string PrefixHistogram(IEnumerable<string> names, IList<string> prefixes, int max)
        {
            var counts = ClassifyPrefixes(names, prefixes);
            var parts = counts.OrderByDescending(kv => kv.Key == "other" ? -1 : kv.Value)
                .Take(max).Select(kv => kv.Key + " " + kv.Value);
            return string.Join(", ", parts);
        }

        public static Dictionary<string, int> ClassifyPrefixes(IEnumerable<string> names, IList<string> prefixes)
        {
            var ordered = (prefixes ?? new List<string>()).OrderByDescending(p => p.Length).ToList();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                var key = ordered.FirstOrDefault(p => n != null && n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? "other";
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            }
            return counts;
        }

        /// <summary>Cut text to at most maxBytes UTF-8 bytes, ending on a whole line.</summary>
        public static string TruncateAtLine(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text) || Bytes(text) <= maxBytes) return text ?? "";
            const string marker = "[… truncated; see the topic sheets]";
            var budget = maxBytes - Bytes(marker) - 1;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (Bytes(sb.ToString()) + Bytes(line) + 1 > budget) break;
                sb.Append(line).Append('\n');
            }
            if (budget > 0) sb.Append(marker);
            var result = sb.ToString();
            // Pathological case (budget smaller than the marker): hard cut on characters.
            while (Bytes(result) > maxBytes && result.Length > 0) result = result.Substring(0, result.Length - 1);
            return result;
        }

        /// <summary>True when mm is a whole multiple of step (within half a millimetre).</summary>
        public static bool IsRound(double mm, double step)
        {
            if (step <= 0) return true;
            var r = Math.Abs(mm % step);
            return r < 0.5 || Math.Abs(r - step) < 0.5;
        }

        public static int Bytes(string s) => string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);

        private static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
