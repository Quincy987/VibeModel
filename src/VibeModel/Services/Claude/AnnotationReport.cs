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

    /// <summary>
    /// Phase 2: why the guard could not put an annotation back. The user's rule: a reference that is
    /// gone with nothing in its place rightly takes its annotation along; only a replacement in the same
    /// place brings it back.
    /// </summary>
    public enum RemovalReason
    {
        /// <summary>Not recorded (older callers): the line says nothing about why.</summary>
        Unknown,
        /// <summary>The referenced face/edge is gone and nothing came back in its place.</summary>
        NothingInPlace,
        /// <summary>A replacement lay in the same place, but Revit refused the new annotation (or its view is gone).</summary>
        CouldNotAttach
    }

    /// <summary>One annotation that is gone after the command (Revit, the failure policy or the guard removed it).</summary>
    public sealed class RemovedAnnotation
    {
        /// <summary>Why it was not put back (phase 2).</summary>
        public RemovalReason Reason { get; set; }

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
                var groups = Removed.GroupBy(r => new { r.Kind, r.ByRevit, r.Reason }).OrderBy(g => (int)g.Key.Kind).ThenBy(g => g.Key.ByRevit)
                    .ThenBy(g => (int)g.Key.Reason)
                    .Select(g => DescribeRemoved(g.Key.Kind, g.Key.ByRevit, g.Key.Reason, g.ToList()));
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
                        { "by", r.ByRevit ? "revit" : "vibemodel" },
                        { "reason", ReasonKey(r.Reason) }
                    }).ToList() }
            };
        }

        private static string DescribeRehost(RehostedAnnotation r)
        {
            var hosts = r.Hosts.Count == 0 ? "" : " on " + string.Join(", ", r.Hosts.Select(h => AnnotationText.CategoryWord(h.Value) + " " + h.Key));
            return r.From + " → " + r.To + hosts;
        }

        /// <summary>JSON: "nothingInPlace", "couldNotAttach", or null when not recorded.</summary>
        public static string ReasonKey(RemovalReason reason)
        {
            switch (reason)
            {
                case RemovalReason.NothingInPlace: return "nothingInPlace";
                case RemovalReason.CouldNotAttach: return "couldNotAttach";
                default: return null;
            }
        }

        /// <summary>" — reference gone, nothing came back in its place" / " — could not be re-attached"; "" when not recorded.</summary>
        public static string ReasonText(RemovalReason reason)
        {
            switch (reason)
            {
                case RemovalReason.NothingInPlace: return " — reference gone, nothing came back in its place —";
                case RemovalReason.CouldNotAttach: return " — could not be re-attached to the replacement —";
                default: return "";
            }
        }

        private static string DescribeRemoved(AnnotationKind kind, bool byRevit, RemovalReason reason, List<RemovedAnnotation> items)
        {
            var referenced = items.SelectMany(i => i.Referenced).Distinct().ToList();
            var views = items.Select(ViewLabel).Where(v => v != null).Distinct().ToList();
            var text = AnnotationText.Count(items.Count, kind) + (byRevit ? " (Revit)" : "") + ReasonText(reason);
            if (referenced.Count > 0) text += " that referenced " + List(referenced.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList());
            if (views.Count > 0) text += " (" + string.Join("; ", views.Take(MaxListed)) + (views.Count > MaxListed ? "; +" + (views.Count - MaxListed) + " more views" : "") + ")";
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
            if (annotations > 0) text += " " + RehostRule;
            return text;
        }

        /// <summary>How delete/exec/set/join/unjoin/phase treat annotations on what they replace — one wording for every response.</summary>
        public static readonly string RehostRule =
            "delete, exec, set, join, unjoin and phase move them onto a replacement — a spot to a face/edge at the same height (±" +
            Mm(AnnotationMatch.RehostToleranceMm) + " mm) within " + Mm(AnnotationMatch.SearchRadiusMm) + " mm, a dimension to faces on the same planes (±" +
            Mm(AnnotationMatch.RehostToleranceMm) + " mm), a tag to the element covering the old one's middle — and list what they remove.";

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

        /// <summary>The radius (mm) around the old reference in which replacement elements are looked for.</summary>
        public const double SearchRadiusMm = 50.0;

        /// <summary>
        /// The leader a re-hosted spot gets when the old one had none (or its end could not be read):
        /// shoulder this far above the point, text this far to the side (paper mm). Its own constant,
        /// not `AnnotationLayout.SpotLeaderMm`: a new default for placing spots must not move the
        /// spots the guard re-hosts.
        /// </summary>
        public const double RehostSpotLeaderMm = 12;

        /// <summary>A spot elevation's new reference: the same height (±tolerance), mm.</summary>
        public static bool SameHeight(double zMmA, double zMmB, double tolMm = RehostToleranceMm) => System.Math.Abs(zMmA - zMmB) <= tolMm + 1e-9;

        /// <summary>
        /// A spot elevation may move to a replacement whose face/edge lies at the same height
        /// (±1 mm) within the search radius of the old point — a spot measures height.
        /// </summary>
        public static bool SpotMatch(V3 oldPoint, V3 newPoint)
            => (newPoint - oldPoint).Length <= SearchRadiusMm + 1e-9 && SameHeight(oldPoint.Z, newPoint.Z);

        /// <summary>Same plane: parallel normals (either sense) and the second origin within tol of the first plane.</summary>
        public static bool SamePlane(V3 normalA, V3 originA, V3 normalB, V3 originB, double tolMm = RehostToleranceMm)
        {
            double la = normalA.Length, lb = normalB.Length;
            if (la < 1e-12 || lb < 1e-12) return false;
            if (Math.Abs(normalA.Dot(normalB) / (la * lb)) < ParallelDot) return false;
            return Math.Abs((originB - originA).Dot(normalA) / la) <= tolMm + 1e-9;
        }

        /// <summary>
        /// Gives each reference one of the segment ends: the nearest free one to its own position;
        /// references without a position take the free ends that are left, in order. Revit's
        /// reference order does not have to follow the line's direction.
        /// </summary>
        public static List<double?> AssignPositions(List<double> ts, List<double?> estimates)
        {
            var result = new List<double?>(estimates.Select(_ => (double?)null));
            var free = new List<double>(ts);
            for (int i = 0; i < estimates.Count; i++)
            {
                if (!estimates[i].HasValue || free.Count == 0) continue;
                var nearest = free.OrderBy(x => Math.Abs(x - estimates[i].Value)).First();
                result[i] = nearest;
                free.Remove(nearest);
            }
            for (int i = 0; i < estimates.Count && free.Count > 0; i++)
            {
                if (result[i].HasValue) continue;
                result[i] = free[0];
                free.RemoveAt(0);
            }
            return result;
        }

        /// <summary>
        /// Phase 2: a filled region's straight edge "in the same place" as the edge a dimension
        /// referenced (the user's rule — only then does the dimension come back). The new edge
        /// <paramref name="newA"/>–<paramref name="newB"/> must run the same way as the old one (parallel,
        /// both seen across the dimension direction <paramref name="dimDir"/>), lie on the old reference
        /// line (both ends within ±1 mm of it along the dimension) and overlap the old edge along its own
        /// direction (a gap of at most the search radius). The distance between their middles is returned
        /// for picking the closest; negative when they do not match.
        /// </summary>
        public static double SameEdge(V3 oldA, V3 oldB, V3 newA, V3 newB, V3 dimDir, double tolMm = RehostToleranceMm)
        {
            var dl = dimDir.Length;
            var oldDir = oldB - oldA;
            var newDir = newB - newA;
            double lo = oldDir.Length, ln = newDir.Length;
            if (dl < 1e-12 || lo < 1e-9 || ln < 1e-9) return -1;
            if (Math.Abs(oldDir.Dot(newDir) / (lo * ln)) < ParallelDot) return -1;
            // The edge crosses the dimension: it is (nearly) perpendicular to the dimension line.
            if (Math.Abs(oldDir.Dot(dimDir) / (lo * dl)) > 1 - ParallelDot + 1e-9) return -1;
            double tOld = oldA.Dot(dimDir) / dl;
            if (Math.Abs(newA.Dot(dimDir) / dl - tOld) > tolMm + 1e-9 || Math.Abs(newB.Dot(dimDir) / dl - tOld) > tolMm + 1e-9) return -1;
            // Overlap along the edge direction.
            double s0 = 0, s1 = lo;
            double n0 = (newA - oldA).Dot(oldDir) / lo, n1 = (newB - oldA).Dot(oldDir) / lo;
            if (n0 > n1) { var t = n0; n0 = n1; n1 = t; }
            double gap = Math.Max(n0 - s1, s0 - n1);
            if (gap > SearchRadiusMm + 1e-9) return -1;
            var midOld = new V3((oldA.X + oldB.X) / 2, (oldA.Y + oldB.Y) / 2, (oldA.Z + oldB.Z) / 2);
            var midNew = new V3((newA.X + newB.X) / 2, (newA.Y + newB.Y) / 2, (newA.Z + newB.Z) / 2);
            return (midNew - midOld).Length;
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

        // Revit files these under model categories, but they are drawing work: measured, a section
        // adds a viewer, a 3D view a camera, a filled region a detail item.
        private static readonly HashSet<int> DrawingCategories = new HashSet<int>
        {
            (int)Autodesk.Revit.DB.BuiltInCategory.OST_Cameras, (int)Autodesk.Revit.DB.BuiltInCategory.OST_Viewers,
            (int)Autodesk.Revit.DB.BuiltInCategory.OST_Sheets, (int)Autodesk.Revit.DB.BuiltInCategory.OST_Lines,
            (int)Autodesk.Revit.DB.BuiltInCategory.OST_DetailComponents, (int)Autodesk.Revit.DB.BuiltInCategory.OST_VolumeOfInterest,
            (int)Autodesk.Revit.DB.BuiltInCategory.OST_SectionBox, (int)Autodesk.Revit.DB.BuiltInCategory.OST_Views,
            (int)Autodesk.Revit.DB.BuiltInCategory.OST_RasterImages, (int)Autodesk.Revit.DB.BuiltInCategory.OST_Materials
        };

        /// <summary>True for a real model element: not a view, type, view-specific or annotation element, nor drawing work.</summary>
        public static bool IsModelElement(int categoryId, bool modelCategory, bool viewSpecific, bool isView, bool isType)
            => modelCategory && !viewSpecific && !isView && !isType && !DrawingCategories.Contains(categoryId);

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
