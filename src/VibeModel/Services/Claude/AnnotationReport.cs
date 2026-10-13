using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>The annotation kinds the guard tracks.</summary>
    public enum AnnotationKind { SpotElevation, Dimension, Tag }

    /// <summary>One annotation the guard put back on a new (or the same, refreshed) element.</summary>
    public sealed class RehostedAnnotation
    {
        public long From { get; set; }
        public long To { get; set; }
        public AnnotationKind Kind { get; set; }
        /// <summary>The element(s) the new annotation references, with their category name.</summary>
        public List<KeyValuePair<long, string>> Hosts { get; set; } = new List<KeyValuePair<long, string>>();
        public string View { get; set; }
    }

    /// <summary>One annotation that is gone after the command (Revit, the failure policy or the guard removed it).</summary>
    public sealed class RemovedAnnotation
    {
        public long Id { get; set; }
        public AnnotationKind Kind { get; set; }
        public List<long> Referenced { get; set; } = new List<long>();
        public string View { get; set; }
        public string Sheet { get; set; }
        /// <summary>True when Revit's failure handling deleted it ("Delete Element(s)"), not the cascade or the guard.</summary>
        public bool ByRevit { get; set; }
    }

    /// <summary>
    /// What the annotation guard did after a command that replaced or changed elements:
    /// re-hosted annotations and annotations that are gone. Renders the response line
    /// ("-- annotations: …") and the additive JSON key. Pure — unit-tested.
    /// </summary>
    public sealed class AnnotationOutcome
    {
        private const int MaxListed = 10;

        public List<RehostedAnnotation> Rehosted { get; } = new List<RehostedAnnotation>();
        public List<RemovedAnnotation> Removed { get; } = new List<RemovedAnnotation>();

        public bool IsEmpty => Rehosted.Count == 0 && Removed.Count == 0;

        /// <summary>
        /// `-- annotations: re-hosted 2 spot elevations (1730944 → 1730970 on floor 1730959, …);
        /// removed 1 dimension that referenced 1730914 (view Doorsnede 1-1, sheet 0003)`. Null when nothing happened.
        /// </summary>
        public string ToTextLine()
        {
            if (IsEmpty) return null;
            var parts = new List<string>();
            if (Rehosted.Count > 0)
            {
                var groups = Rehosted.GroupBy(r => r.Kind).OrderBy(g => (int)g.Key)
                    .Select(g => AnnotationText.Count(g.Count(), g.Key) + " (" + List(g.Select(DescribeRehost).ToList()) + ")");
                parts.Add("re-hosted " + string.Join(", ", groups));
            }
            if (Removed.Count > 0)
            {
                var groups = Removed.GroupBy(r => new { r.Kind, r.ByRevit }).OrderBy(g => (int)g.Key.Kind).ThenBy(g => g.Key.ByRevit)
                    .Select(g => DescribeRemoved(g.Key.Kind, g.Key.ByRevit, g.ToList()));
                parts.Add("removed " + string.Join(", ", groups));
            }
            return "-- annotations: " + string.Join("; ", parts);
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "rehosted", Rehosted.Select(r => (object)new Dictionary<string, object>
                    {
                        { "from", r.From },
                        { "to", r.To },
                        { "kind", AnnotationText.KindKey(r.Kind) },
                        { "host", r.Hosts.Count > 0 ? (object)r.Hosts[0].Key : null },
                        { "hosts", r.Hosts.Select(h => (object)h.Key).ToList() },
                        { "view", r.View }
                    }).ToList() },
                { "removed", Removed.Select(r => (object)new Dictionary<string, object>
                    {
                        { "id", r.Id },
                        { "kind", AnnotationText.KindKey(r.Kind) },
                        { "referenced", r.Referenced.Select(x => (object)x).ToList() },
                        { "view", r.View },
                        { "sheet", r.Sheet },
                        { "by", r.ByRevit ? "revit" : "vibemodel" }
                    }).ToList() }
            };
        }

        private static string DescribeRehost(RehostedAnnotation r)
        {
            var hosts = r.Hosts.Count == 0 ? "" : " on " + string.Join(", ", r.Hosts.Select(h => AnnotationText.CategoryWord(h.Value) + " " + h.Key));
            return r.From + " → " + r.To + hosts;
        }

        private static string DescribeRemoved(AnnotationKind kind, bool byRevit, List<RemovedAnnotation> items)
        {
            var referenced = items.SelectMany(i => i.Referenced).Distinct().ToList();
            var views = items.Select(ViewLabel).Where(v => v != null).Distinct().ToList();
            var text = AnnotationText.Count(items.Count, kind) + (byRevit ? " (Revit)" : "");
            if (referenced.Count > 0) text += " that referenced " + List(referenced.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList());
            if (views.Count > 0) text += " (" + string.Join("; ", views) + ")";
            return text;
        }

        private static string ViewLabel(RemovedAnnotation r)
        {
            if (string.IsNullOrEmpty(r.View)) return null;
            return "view " + r.View + (string.IsNullOrEmpty(r.Sheet) ? "" : ", sheet " + r.Sheet);
        }

        private static string List(List<string> items)
        {
            var shown = string.Join(", ", items.Take(MaxListed));
            return items.Count > MaxListed ? shown + " +" + (items.Count - MaxListed) + " more" : shown;
        }
    }

    /// <summary>Wording shared by the guard, `spot` and `whatdepends`. Pure.</summary>
    public static class AnnotationText
    {
        public static string KindKey(AnnotationKind k)
        {
            switch (k)
            {
                case AnnotationKind.SpotElevation: return "spot";
                case AnnotationKind.Dimension: return "dimension";
                default: return "tag";
            }
        }

        public static string KindWord(AnnotationKind k, int n)
        {
            switch (k)
            {
                case AnnotationKind.SpotElevation: return n == 1 ? "spot elevation" : "spot elevations";
                case AnnotationKind.Dimension: return n == 1 ? "dimension" : "dimensions";
                default: return n == 1 ? "tag" : "tags";
            }
        }

        public static string Count(int n, AnnotationKind k) => n + " " + KindWord(k, n);

        /// <summary>"Structural Foundations" → "structural foundation", "Floors" → "floor".</summary>
        public static string CategoryWord(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return "element";
            var s = category.Trim().ToLowerInvariant();
            if (s.EndsWith("ies", StringComparison.Ordinal) && s.Length > 4) return s.Substring(0, s.Length - 3) + "y";
            if (s.EndsWith("ss", StringComparison.Ordinal)) return s;
            if (s.EndsWith("s", StringComparison.Ordinal) && s.Length > 2) return s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>Elements a VibeModel command is likely to replace: floors, walls, structural foundations (piles).</summary>
        public static bool LikelyReplaced(int builtInCategory)
            => builtInCategory == BicFloors || builtInCategory == BicWalls || builtInCategory == BicStructuralFoundation;

        // BuiltInCategory values (Revit 2023); kept as numbers so this stays Revit-free.
        public const int BicFloors = -2000032;
        public const int BicWalls = -2000011;
        public const int BicStructuralFoundation = -2001300;

        /// <summary>
        /// The closing line of `whatdepends`: "Replacing 1730959 affects 2 annotations in 1 view
        /// (Doorsnede probe, sheet 0003) and 0 hosted elements; …".
        /// </summary>
        public static string DependsVerdict(IList<long> ids, int annotations, IList<string> viewLabels, int hosted)
        {
            var who = ids == null || ids.Count == 0 ? "these elements" : string.Join(", ", ids.Take(10)) + (ids.Count > 10 ? " +" + (ids.Count - 10) + " more" : "");
            int nv = viewLabels?.Count ?? 0;
            var text = "Replacing " + who + " affects " + annotations + (annotations == 1 ? " annotation" : " annotations");
            if (nv > 0) text += " in " + nv + (nv == 1 ? " view" : " views") + " (" + string.Join("; ", viewLabels.Take(5)) + (nv > 5 ? "; …" : "") + ")";
            text += " and " + hosted + (hosted == 1 ? " hosted element" : " hosted elements") + ".";
            if (annotations > 0)
                text += " delete/exec/set re-host spots, dimensions and tags whose reference lands within " +
                        Mm(AnnotationMatch.RehostToleranceMm) + " mm of the new element and list what they remove.";
            return text;
        }

        private static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>A point or vector in model millimetres — keeps the matching maths Revit-free.</summary>
    public struct V3
    {
        public readonly double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double Dot(V3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public double Length => Math.Sqrt(Dot(this));
    }

    /// <summary>The tolerance maths of re-hosting. Pure — unit-tested.</summary>
    public static class AnnotationMatch
    {
        /// <summary>A new reference must pass this close to where the old one was (mm).</summary>
        public const double RehostToleranceMm = 1.0;

        /// <summary>Normals count as parallel at |dot| ≥ this.</summary>
        public const double ParallelDot = 0.999;

        public static bool Within(V3 a, V3 b, double tolMm = RehostToleranceMm) => (a - b).Length <= tolMm + 1e-9;

        /// <summary>Same plane: parallel normals (either sense) and the second origin within tol of the first plane.</summary>
        public static bool SamePlane(V3 normalA, V3 originA, V3 normalB, V3 originB, double tolMm = RehostToleranceMm)
        {
            double la = normalA.Length, lb = normalB.Length;
            if (la < 1e-12 || lb < 1e-12) return false;
            if (Math.Abs(normalA.Dot(normalB) / (la * lb)) < ParallelDot) return false;
            return Math.Abs((originB - originA).Dot(normalA) / la) <= tolMm + 1e-9;
        }

        /// <summary>Same facing: a spot on a top face only moves to another face that also faces up (dot ≥ ParallelDot).</summary>
        public static bool SameFacing(V3 normalA, V3 normalB)
        {
            double la = normalA.Length, lb = normalB.Length;
            if (la < 1e-12 || lb < 1e-12) return false;
            return normalA.Dot(normalB) / (la * lb) >= ParallelDot;
        }
    }

    /// <summary>Which commands the guard wraps and which element ids they name. Pure — unit-tested.</summary>
    public static class AnnotationScope
    {
        private static readonly HashSet<string> Guarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "delete", "exec", "set", "join", "unjoin", "phase" };

        public static bool Applies(string command) => command != null && Guarded.Contains(command.Trim());

        /// <summary>True when any batch item applies.</summary>
        public static bool AppliesToAny(IEnumerable<string> commands) => commands != null && commands.Any(Applies);

        /// <summary>
        /// The element ids the command names, or null when it may touch anything (exec, `selected`,
        /// no readable ids) — the guard then looks at every annotation in the document.
        /// </summary>
        public static List<int> TargetIds(string command, string args)
        {
            if (!Applies(command)) return new List<int>();
            var c = command.Trim().ToLowerInvariant();
            if (c == "exec") return null;
            var tokens = (args ?? "").Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var ids = new List<int>();
            switch (c)
            {
                case "delete":
                    foreach (var t in tokens) if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) ids.Add(id);
                    break;
                case "set":
                    if (tokens.Length > 0 && int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sid)) ids.Add(sid);
                    break;
                case "phase":
                    foreach (var t in tokens)
                    {
                        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) break;
                        ids.Add(pid);
                    }
                    break;
                default: // join / unjoin: the first two ids
                    foreach (var t in tokens)
                    {
                        if (ids.Count == 2) break;
                        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int jid)) ids.Add(jid);
                    }
                    break;
            }
            return ids.Count == 0 ? null : ids.Distinct().ToList();
        }

        /// <summary>
        /// The union over a batch; null as soon as one applicable item may touch anything.
        /// Items that do not apply contribute nothing.
        /// </summary>
        public static List<int> TargetIds(IEnumerable<KeyValuePair<string, string>> items)
        {
            var all = new List<int>();
            foreach (var it in items ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!Applies(it.Key)) continue;
                var ids = TargetIds(it.Key, it.Value);
                if (ids == null) return null;
                all.AddRange(ids);
            }
            return all.Distinct().ToList();
        }
    }
}
