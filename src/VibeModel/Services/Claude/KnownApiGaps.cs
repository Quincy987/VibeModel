using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Plan 18: what Revit's API genuinely cannot do, and the workaround that was measured, said
    /// where the AI is already reading — an <c>apilookup</c> answer and the hint under a failed
    /// <c>exec</c> compile — instead of in a prompt rule. Pure: a table and a matcher.
    /// </summary>
    public static class KnownApiGaps
    {
        /// <summary>Lines one gap block may take (header included).</summary>
        public const int MaxLines = 12;

        /// <summary>What a lookup turned out to be about — decides which keys may match.</summary>
        public enum QueryKind
        {
            /// <summary>Nothing resolved (not found, several types): every key may match.</summary>
            Unknown,
            /// <summary>A type was found: only type keys, on its simple name.</summary>
            Type,
            /// <summary>Type.Member (found or not): only member keys — <c>Room.Phase</c> is real API.</summary>
            Member,
        }

        /// <summary>
        /// One gap: the queries it answers and what to tell the AI. Read-only outside this assembly:
        /// the entries of <see cref="All"/> are shared by every lookup result (<c>Result.KnownGap</c>).
        /// </summary>
        public sealed class Gap
        {
            public string Name { get; internal set; }
            /// <summary>Type names (simple, case-insensitive): <c>Label</c>, <c>TextElement</c>.</summary>
            public IReadOnlyList<string> TypeKeys { get; internal set; }
            /// <summary>Members: a bare name (<c>NewLabel</c>) or <c>Type.Member</c> (<c>ViewType.Legend</c>).</summary>
            public IReadOnlyList<string> MemberKeys { get; internal set; }
            public string Title { get; internal set; }
            public string Cannot { get; internal set; }
            public IReadOnlyList<string> Works { get; internal set; }
            public string Manual { get; internal set; }
        }

        /// <summary>The table (measured in Revit 2023.1.2, 2026-10-20 — docs/plans/18-known-api-gaps.md).</summary>
        public static readonly IReadOnlyList<Gap> All = new[]
        {
            new Gap
            {
                Name = "labels",
                TypeKeys = new[] { "Label", "Labels", "TextElement", "LabelType" },
                MemberKeys = new[] { "NewLabel", "CreateLabel", "FamilyItemFactory.NewLabel", "TAG_TAG" },
                Title = "labels in annotation families (tags, generic annotations, title blocks)",
                Cannot = "the Revit API cannot create a label or change which parameter it shows: there is no Label class and no FamilyItemFactory.NewLabel. " +
                         "A label is a TextElement without a category (not a TextNote); what it shows is readable only through its read-only Sample Text (BuiltInParameter.TAG_SAMPLE_TEXT). " +
                         "Trap: its 'Label' parameter (TAG_TAG, StorageType.None) accepts Set(…) and returns true, but setting it to a family parameter's id or name changes nothing.",
                Works = new[]
                {
                    "fd = the family document: doc itself when the family is open in the Family Editor (doc.IsFamilyDocument; exec's own transaction keeps the edits)",
                    "from a project: trans.RollBack(); var fd = doc.EditFamily(family); edit inside a new Transaction(fd, …) + Commit(); fd.LoadFamily(doc, new VibeModel.Services.Helpers.KeepValuesLoadOptions()); fd.Close(false) (without the options Revit answers \"Family loading failed.\")",
                    "fd.FamilyManager.AddParameter(\"Betonkwaliteit\", GroupTypeId.Text, SpecTypeId.String.Text, false)",
                    "ElementTransformUtils.CopyElement(fd, labelId, offset) - the copy keeps its parameter (same Sample Text)",
                    "ElementTransformUtils.CopyElements(srcView, ids, fdView, Transform.Identity, new CopyPasteOptions()) from another family of the SAME category (other categories: \"Can't paste Labels across Families of different Categories.\")",
                    "TextNote.Create(fd, viewId, point, \"(LABEL Betonkwaliteit)\", fd.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) next to the copy, as a placeholder",
                },
                Manual = "the user (after Edit Family, when it was prepared from the project) selects each copied label -> Edit Label -> picks the parameter (one dialog per label), then Load into Project. Do the parameters, types and copies first and say exactly which label gets which parameter."
            },
            new Gap
            {
                Name = "legends",
                TypeKeys = new[] { "Legend", "Legends", "ViewLegend", "LegendComponent" },
                MemberKeys = new[] { "ViewType.Legend", "ViewFamily.Legend", "NewLegend", "CreateLegend" },
                Title = "legend views",
                Cannot = "the Revit API has no call that creates a legend view and no LegendComponent class; ViewType.Legend only recognises existing legends.",
                Works = new[]
                {
                    "duplicate an existing legend: new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().First(v => v.ViewType == ViewType.Legend && !v.IsTemplate)",
                    "legend.Duplicate(ViewDuplicateOption.WithDetailing) = a full copy, ViewDuplicateOption.Duplicate = an empty legend (AsDependent is refused)",
                    "or copy a whole legend view from another open document: ElementTransformUtils.CopyElements(srcDoc, new List<ElementId> { legendId }, doc, Transform.Identity, new CopyPasteOptions())",
                    "then View.Name, View.Scale, TextNote.Create, FilledRegion.Create, doc.Create.NewDetailCurve and view-based family instances in it",
                    "legend components only by copying an existing one (ElementTransformUtils.CopyElements, also from another document) and setting LEGEND_COMPONENT",
                },
                Manual = "when no open document has a legend, the user makes the first one (View -> Legends -> Legend)."
            },
            new Gap
            {
                Name = "phases-design-options",
                TypeKeys = new[] { "Phase", "DesignOption" },
                MemberKeys = new[] { "Phase.Create", "DesignOption.Create", "NewPhase", "CreatePhase", "NewDesignOption", "CreateDesignOption" },
                Title = "creating phases and design options",
                Cannot = "Phase and DesignOption exist (read them: doc.Phases, new FilteredElementCollector(doc).OfClass(typeof(DesignOption))) but cannot be created through the API: neither class has a creation member.",
                Works = new[]
                {
                    "use the phases the document has: Phase Created / Phase Demolished on elements (VibeModel's phase command, --phase on the creating commands)",
                },
                Manual = "the user adds phases in Manage -> Phases and design options in Manage -> Design Options; ask before assuming one exists."
            },
        };

        private static readonly Dictionary<string, Gap> ByType = Index(g => g.TypeKeys);
        private static readonly Dictionary<string, Gap> ByMember = Index(g => g.MemberKeys);

        private static Dictionary<string, Gap> Index(Func<Gap, IReadOnlyList<string>> keys)
        {
            var map = new Dictionary<string, Gap>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in All)
                foreach (var k in keys(g)) map[k] = g;
            return map;
        }

        /// <summary>
        /// The gap a query is about, or null. Case-insensitive, on whole dot-separated names, never a
        /// substring (LabelUtils is real API): a type key against the last name
        /// ("Autodesk.Revit.DB.TextElement"), a member key against "Type.Member" or the member alone
        /// ("Autodesk.Revit.Creation.FamilyItemFactory.NewLabel"). <paramref name="kind"/> says what
        /// the lookup resolved, so a member that happens to be called Phase or Label is not a gap.
        /// </summary>
        public static Gap Match(string query, QueryKind kind = QueryKind.Unknown)
        {
            var q = (query ?? "").Trim();
            int lt = q.IndexOf('<');
            if (lt >= 0) q = q.Substring(0, lt);
            var names = q.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0) return null;
            var last = names[names.Length - 1];
            Gap gap;
            if (kind != QueryKind.Type)
            {
                if (names.Length >= 2 && ByMember.TryGetValue(names[names.Length - 2] + "." + last, out gap)) return gap;
                if (ByMember.TryGetValue(last, out gap)) return gap;
            }
            if (kind != QueryKind.Member && ByType.TryGetValue(last, out gap)) return gap;
            return null;
        }

        /// <summary>The text block: header, what cannot, what works, the manual step.</summary>
        public static List<string> Lines(Gap gap)
        {
            var lines = new List<string>
            {
                "Known gap: " + gap.Title + " (measured in Revit 2023)",
                "Cannot: " + gap.Cannot,
            };
            if (gap.Works.Count > 0)
            {
                lines.Add("What works:");
                lines.AddRange(gap.Works.Select(w => "  " + w));
            }
            lines.Add("Manual step: " + gap.Manual);
            return lines;
        }

        /// <summary><see cref="Lines"/> as one block.</summary>
        public static string Describe(Gap gap) => string.Join("\n", Lines(gap));

        /// <summary>The additive JSON object (<c>data.knownGap</c>).</summary>
        public static Dictionary<string, object> Data(Gap gap)
        {
            return new Dictionary<string, object>
            {
                { "name", gap.Name },
                { "title", gap.Title },
                { "cannot", gap.Cannot },
                { "works", gap.Works.Cast<object>().ToList() },
                { "manual", gap.Manual },
            };
        }

        /// <summary>
        /// The lookup result with the gap it is about. What the lookup resolved decides the match (a
        /// found type by its own name, a found or missing member as Type.Member, anything else by the
        /// query as typed). A found type or member gets the block at the end; anything not found (no
        /// type, several types, no such member) right after its first line, so it lands inside the
        /// lines exec's compile hint quotes. No gap → the result is returned untouched.
        /// </summary>
        public static ApiLookup.Result Attach(ApiLookup.Result result, string query)
        {
            if (result == null) return null;
            var gap = Match(Subject(result, query, out var kind), kind);
            if (gap == null) return result;
            var text = result.Text ?? "";
            // The answer's own line ends; a one-line answer has none, so the platform's (= AppendLine's).
            var newline = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : Environment.NewLine;
            var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
            if (result.Found || lines.Count < 2) lines.AddRange(Lines(gap));
            else lines.InsertRange(1, Lines(gap));
            result.Text = string.Join(newline, lines);
            result.KnownGap = gap;
            if (result.Data != null) result.Data["knownGap"] = Data(gap);
            return result;
        }

        // What a lookup was about: the resolved type (+ the member asked for), else the query as typed.
        private static string Subject(ApiLookup.Result result, string query, out QueryKind kind)
        {
            if (result.ResolvedType == null)
            {
                kind = QueryKind.Unknown;
                return query;
            }
            kind = result.Member != null ? QueryKind.Member : QueryKind.Type;
            return result.Member != null ? result.ResolvedType + "." + result.Member : result.ResolvedType;
        }
    }
}
