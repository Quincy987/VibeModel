using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Plan 18: what Revit's API genuinely cannot do, and the workaround that was measured, said
    /// where the AI is already reading — an <c>apilookup</c> answer and the hint under a failed
    /// <c>exec</c> compile — instead of in every turn's prompt. Pure: a table and a matcher.
    /// </summary>
    public static class KnownApiGaps
    {
        /// <summary>Lines one gap block may take (header included).</summary>
        public const int MaxLines = 12;

        /// <summary>One gap: the queries it answers and what to tell the AI.</summary>
        public sealed class Gap
        {
            public string Name { get; set; }
            public string[] Keys { get; set; }
            public string Title { get; set; }
            public string Cannot { get; set; }
            public string[] Works { get; set; }
            public string Manual { get; set; }
        }

        /// <summary>The table (measured in Revit 2023.1.2, 2026-10-20 — docs/plans/18-known-api-gaps.md).</summary>
        public static readonly IReadOnlyList<Gap> All = new[]
        {
            new Gap
            {
                Name = "labels",
                Keys = new[] { "Label", "Labels", "NewLabel", "CreateLabel", "FamilyItemFactory.NewLabel", "TAG_TAG", "TextElement", "LabelType" },
                Title = "labels in annotation families (tags, generic annotations, title blocks)",
                Cannot = "the Revit API cannot create a label or change which parameter it shows: there is no Label class and no FamilyItemFactory.NewLabel. " +
                         "A label is a TextElement without a category (not a TextNote); what it shows is readable only through its read-only Sample Text (BuiltInParameter.TAG_SAMPLE_TEXT). " +
                         "Trap: its 'Label' parameter (TAG_TAG, StorageType.None) accepts Set(string), Set(ElementId) and Set(int) and returns true, but nothing changes.",
                Works = new[]
                {
                    "in exec: trans.RollBack(); var fd = doc.EditFamily(family); then a Transaction of its own on fd; fd.Close(false) after",
                    "fd.FamilyManager.AddParameter(\"Betonkwaliteit\", GroupTypeId.Text, SpecTypeId.String.Text, false)",
                    "ElementTransformUtils.CopyElement(fd, labelId, offset) - the copy keeps its parameter (same Sample Text)",
                    "ElementTransformUtils.CopyElements(srcView, ids, fdView, Transform.Identity, new CopyPasteOptions()) from another family of the SAME category (other categories: \"Can't paste Labels across Families of different Categories.\")",
                    "TextNote.Create(fd, viewId, point, \"(LABEL Betonkwaliteit)\", fd.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) next to the copy, as a placeholder",
                },
                Manual = "the user selects the copied label -> Edit Label -> picks the parameter (one dialog per label), then Load into Project. Do the parameters, types and copies first and say exactly which label gets which parameter."
            },
            new Gap
            {
                Name = "legends",
                Keys = new[] { "Legend", "Legends", "ViewLegend", "LegendComponent", "ViewType.Legend", "ViewFamily.Legend", "NewLegend", "CreateLegend" },
                Title = "legend views",
                Cannot = "the Revit API has no call that creates a legend view and no LegendComponent class; ViewType.Legend only recognises existing legends.",
                Works = new[]
                {
                    "duplicate an existing legend: new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().First(v => v.ViewType == ViewType.Legend && !v.IsTemplate)",
                    "legend.Duplicate(ViewDuplicateOption.WithDetailing) = a full copy, ViewDuplicateOption.Duplicate = an empty legend (AsDependent is refused)",
                    "then View.Name, View.Scale, TextNote.Create, FilledRegion.Create, doc.Create.NewDetailCurve and view-based family instances in it",
                    "legend components only by copying an existing one (ElementTransformUtils.CopyElements, also from another document) and setting LEGEND_COMPONENT",
                },
                Manual = "in a project without any legend the user makes the first one (View -> Legends -> Legend)."
            },
            new Gap
            {
                Name = "phases-design-options",
                Keys = new[] { "Phase", "Phase.Create", "NewPhase", "CreatePhase", "DesignOption", "DesignOption.Create", "NewDesignOption", "CreateDesignOption" },
                Title = "creating phases and design options",
                Cannot = "Phase and DesignOption exist (read them: doc.Phases, new FilteredElementCollector(doc).OfClass(typeof(DesignOption))) but cannot be created through the API: neither class has a creation member.",
                Works = new[]
                {
                    "use the phases the document has: Phase Created / Phase Demolished on elements (VibeModel's phase command, --phase on the creating commands)",
                },
                Manual = "the user adds phases in Manage -> Phases and design options in Manage -> Design Options; ask before assuming one exists."
            },
        };

        private static readonly Dictionary<string, Gap> ByKey = BuildIndex();

        private static Dictionary<string, Gap> BuildIndex()
        {
            var map = new Dictionary<string, Gap>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in All)
                foreach (var k in g.Keys) map[k] = g;
            return map;
        }

        /// <summary>
        /// The gap a lookup query is about, or null. Matched case-insensitively on the whole query and
        /// on each suffix after a dot ("Autodesk.Revit.Creation.FamilyItemFactory.NewLabel" →
        /// "FamilyItemFactory.NewLabel" → "NewLabel"), never on a substring: LabelUtils and Phase.Name
        /// are real API that works.
        /// </summary>
        public static Gap Match(string query)
        {
            var q = (query ?? "").Trim().Trim('.');
            int lt = q.IndexOf('<');
            if (lt > 0) q = q.Substring(0, lt);
            if (q.Length == 0) return null;
            while (true)
            {
                if (ByKey.TryGetValue(q, out var gap)) return gap;
                int dot = q.IndexOf('.');
                if (dot < 0 || dot == q.Length - 1) return null;
                q = q.Substring(dot + 1);
            }
        }

        /// <summary>The text block: header, what cannot, what works, the manual step.</summary>
        public static List<string> Lines(Gap gap)
        {
            var lines = new List<string>
            {
                "Known gap: " + gap.Title + " (measured in Revit 2023)",
                "Cannot: " + gap.Cannot,
            };
            if (gap.Works.Length > 0)
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
        /// The lookup result with the gap the query is about: a found type or member gets the block at
        /// the end; anything not found (no type, several types, no such member) right after its first
        /// line, so it lands inside the first lines exec's compile hint quotes. No gap → unchanged.
        /// </summary>
        public static ApiLookup.Result Attach(ApiLookup.Result result, string query)
        {
            var gap = Match(query);
            if (gap == null || result == null) return result;
            var block = Describe(gap);
            var text = (result.Text ?? "").Replace("\r\n", "\n");
            if (result.Found)
                text = text + "\n" + block;
            else
            {
                int nl = text.IndexOf('\n');
                text = nl < 0 ? text + "\n" + block : text.Substring(0, nl) + "\n" + block + text.Substring(nl);
            }
            result.Text = text;
            if (result.Data != null) result.Data["knownGap"] = Data(gap);
            return result;
        }
    }
}
