using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class TagCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "tag";
        public string Description => "Tag an element by ID in the active view (also rebar; a multi-category tag when the category has none); --at x y places the head " +
                                     "(same coordinates as 'text'), --leader draws a leader (without --at the head goes to a free spot next to the element), " +
                                     "--elbow x y bends it there, --type picks the tag type";
        public string Usage => TagArgs.Usage;

        /// <summary>Plan 21: how far the head stands off the element when it picks a free spot (paper mm).</summary>
        private const double SlotGapMm = 8;

        // Explicit element-category -> tag-category map (correct, not a name heuristic).
        private static readonly Dictionary<BuiltInCategory, BuiltInCategory> TagMap =
            new Dictionary<BuiltInCategory, BuiltInCategory>
            {
                { BuiltInCategory.OST_Walls, BuiltInCategory.OST_WallTags },
                { BuiltInCategory.OST_Doors, BuiltInCategory.OST_DoorTags },
                { BuiltInCategory.OST_Windows, BuiltInCategory.OST_WindowTags },
                { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_RoomTags },
                { BuiltInCategory.OST_Floors, BuiltInCategory.OST_FloorTags },
                { BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_CeilingTags },
                { BuiltInCategory.OST_Roofs, BuiltInCategory.OST_RoofTags },
                { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralColumnTags },
                { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingTags },
                { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralFoundationTags },
                { BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureTags },
                { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_GenericModelTags },
                { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags },
                { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PlumbingFixtureTags },
                { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingFixtureTags },
                { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_RebarTags },
                { BuiltInCategory.OST_Parts, BuiltInCategory.OST_PartTags },
            };

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var ta = TagArgs.Parse(args);
            if (ta.Error != null)
                return CommandResult.Error("BAD_ARGS", ta.Error, Usage);
            int eid = ta.Id;

            var element = doc.GetElement(new ElementId(eid));
            if (element == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", "Element " + eid + " not found.",
                    "Run 'list <category>' to find valid element IDs.");

            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return CommandResult.Error("NO_ACTIVE_VIEW",
                    "No active graphical view to place the tag in.",
                    "Switch to a plan, section, elevation, or 3D view.");

            if (element.Category == null)
                return CommandResult.Error("BAD_ARGS", "Element has no category to tag.", null);

            var elemBic = (BuiltInCategory)element.Category.Id.IntegerValue;
            // The category's own tags first; a multi-category tag tags anything (plan 21).
            var candidates = new List<FamilySymbol>();
            if (TagMap.TryGetValue(elemBic, out var tagBic))
                candidates.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(tagBic).Cast<FamilySymbol>());
            bool categoryTags = candidates.Count > 0;
            candidates.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_MultiCategoryTags).Cast<FamilySymbol>());
            if (candidates.Count == 0)
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "No tag family loaded for " + element.Category.Name + " (and no multi-category tag).",
                    "Load a " + element.Category.Name + " tag family or a multi-category tag first ('library tag', then 'loadfamily').");

            FamilySymbol tagSymbol;
            if (ta.Type != null)
            {
                tagSymbol = PickType(candidates, ta.Type, out var typeError);
                if (tagSymbol == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", typeError, null);
            }
            else tagSymbol = candidates[0];
            string typeNote = ta.Type == null && !categoryTags ? "multi-category tag (no " + element.Category.Name + " tag loaded)" : null;

            var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            XYZ mid = bbox != null ? (bbox.Min + bbox.Max) * 0.5 : XYZ.Zero;
            if (ta.At != null && view is View3D)
                return CommandResult.Error("BAD_ARGS", "--at needs a plan, section or elevation (a 3D view has no 2D frame to place it in).",
                    "Leave out --at to tag at the element.");
            if (ta.Elbow != null && view is View3D)
                return CommandResult.Error("BAD_ARGS", "--elbow needs a plan, section or elevation.", null);
            string atConvention = null;
            XYZ head = ta.At != null ? SheetHelper.ViewPoint(doc, view, ta.At[0], ta.At[1], out atConvention) : mid;
            XYZ elbow = ta.Elbow != null ? SheetHelper.ViewPoint(doc, view, ta.Elbow[0], ta.Elbow[1], out atConvention) : null;
            bool autoSlot = ta.Leader && ta.At == null && !(view is View3D);
            string slotNote = null;

            IndependentTag tag = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Tag", () =>
            {
                if (!tagSymbol.IsActive)
                {
                    tagSymbol.Activate();
                    doc.Regenerate();
                }
                tag = IndependentTag.Create(doc, tagSymbol.Id, view.Id,
                    new Reference(element), ta.Leader, TagOrientation.Horizontal, head);
                // With a leader Revit places the head next to the element; put it where asked.
                if (ta.At != null)
                {
                    doc.Regenerate();
                    tag.TagHeadPosition = head;
                }
                else if (autoSlot)
                {
                    // Plan 21: not Revit's default spot (on or right next to the element), but a free one.
                    doc.Regenerate();
                    var placed = FreeSlot(doc, view, element, tag, out slotNote);
                    if (placed != null) tag.TagHeadPosition = placed;
                }
                if (elbow != null)
                {
                    doc.Regenerate();
                    var r = tag.GetTaggedReferences().FirstOrDefault();
                    if (r != null) tag.SetLeaderElbow(r, elbow);
                }
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("TAG CREATED");
            sb.AppendLine("===========");
            sb.AppendLine();
            sb.AppendLine("ID: " + tag.Id.IntegerValue);
            sb.AppendLine("Tags element: " + eid);
            sb.AppendLine("Type: " + tagSymbol.Family.Name + " : " + tagSymbol.Name + (typeNote != null ? " — " + typeNote : ""));
            sb.AppendLine("View: " + view.Name);
            if (ta.At != null || ta.Leader)
                sb.AppendLine("Head: " + (ta.At != null ? Mm.F(ta.At[0]) + ", " + Mm.F(ta.At[1]) + " (" + atConvention + ")"
                                          : slotNote ?? "at the element") + (ta.Leader ? ", with leader" : ""));
            if (ta.Elbow != null)
                sb.AppendLine("Elbow: " + Mm.F(ta.Elbow[0]) + ", " + Mm.F(ta.Elbow[1]) + " (" + atConvention + ")");
            string tagText = null;
            try { tagText = tag.TagText; } catch { }
            if (!string.IsNullOrEmpty(tagText)) sb.AppendLine("Text: " + tagText.Replace("\r", " ").Replace("\n", " "));
            bool vmCreated = Standards.VibeModelCreated.Contains(doc, element.Id);
            sb.AppendLine("Attached to: " + AnnotationText.CategoryWord(element.Category.Name) + " " + eid + " '" + element.Name + "'" +
                          (vmCreated ? " (created by VibeModel this session)" : ""));
            if (vmCreated || AnnotationText.LikelyReplaced(element.Category.Id.IntegerValue))
                sb.AppendLine("Note: tags " + eid + " — a replacement re-tags the new element when it covers the same spot, else removes this tag (the replacing command's response says so)");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { tag.Id.IntegerValue } },
                { "taggedElement", eid },
                { "type", tagSymbol.Family.Name + " : " + tagSymbol.Name },
                { "view", view.Name },
                { "vibeModelCreated", vmCreated }
            };
            if (slotNote != null) data["head"] = slotNote;
            return CommandResult.Ok(sb.ToString(), data);
        }

        /// <summary>
        /// --type: "Family : Type", the type name or the family name — exact first, else one
        /// unique case-insensitive substring; otherwise the candidates are listed.
        /// </summary>
        internal static FamilySymbol PickType(List<FamilySymbol> candidates, string q, out string error)
        {
            error = null;
            q = q.Trim();
            Func<FamilySymbol, string> full = s => s.FamilyName + " : " + s.Name;
            var exact = candidates.Where(s => full(s).Equals(q, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 0) exact = candidates.Where(s => s.FamilyName.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count >= 1) return exact[0];
            var subs = candidates.Where(s => full(s).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (subs.Count == 1) return subs[0];
            error = (subs.Count == 0 ? "No tag type '" + q + "' for this element." : "More than one tag type matches '" + q + "'.") +
                    " Available: " + string.Join(", ", (subs.Count > 0 ? subs : candidates).Select(full).Distinct().Take(20));
            return null;
        }

        /// <summary>
        /// Plan 21: a head position next to the element that keeps clear of every other annotation
        /// in the view — right above, left above, right below, left below the element's paper box
        /// at 8 mm, then right / left above at 16 mm (the same slot choice as a spot's leader).
        /// Null (Revit's own spot stays) when none is clear.
        /// </summary>
        private static XYZ FreeSlot(Document doc, View view, Element element, IndependentTag tag, out string note)
        {
            note = null;
            var frame = new PaperFrame(view);
            var bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return null;
            var eb = frame.Rect(bb);
            AnnotationExtents.TagHeadExtent(tag, out var w, out var h);
            var others = AnnotationExtents.OccupiedBoxes(doc, view).Where(b => b.Id != tag.Id.IntegerValue).ToList();

            double g1 = SlotGapMm, g2 = 2 * SlotGapMm;
            var slots = new List<AnnotationLayout.Slot>
            {
                Slot("right above", eb.X1 + g1, eb.Y1 + g1 / 2, w, h),
                Slot("left above", eb.X0 - g1 - w, eb.Y1 + g1 / 2, w, h),
                Slot("right below", eb.X1 + g1, eb.Y0 - g1 / 2 - h, w, h),
                Slot("left below", eb.X0 - g1 - w, eb.Y0 - g1 / 2 - h, w, h),
                Slot("right above (further out)", eb.X1 + g2, eb.Y1 + g2 / 2, w, h),
                Slot("left above (further out)", eb.X0 - g2 - w, eb.Y1 + g2 / 2, w, h),
            };
            int i = AnnotationLayout.PickSlot(slots, others, out var blocker, out bool free);
            if (!free) { note = "Revit's spot next to the element (every free spot around it is taken — check 'annolayout')"; return null; }
            var c = slots[i].Text;
            note = "free spot " + slots[i].Name + " the element" + (i > 0 && blocker != null ? " (" + slots[0].Name + " taken by " + blocker.Word + " " + blocker.Id + ")" : "");
            // Paper centre → model point at the tag's own depth (the head is taken as the label's centre).
            var cur = tag.TagHeadPosition;
            double dx = frame.Feet(c.Cx - frame.X(cur)), dy = frame.Feet(c.Cy - frame.Y(cur));
            return cur + frame.R * dx + frame.U * dy;
        }

        private static AnnotationLayout.Slot Slot(string name, double x0, double y0, double w, double h)
            => new AnnotationLayout.Slot { Name = name, Text = new MmRect(x0, y0, x0 + w, y0 + h) };
    }
}
