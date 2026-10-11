using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Revit-side helpers shared by the sheet-assembly commands (plan 12): resolve a sheet by
    /// number or id, a view by id or name, and convert between sheet feet and "paper mm"
    /// (millimetres from the paper's lower-left corner, the unit every sheet command speaks).
    /// </summary>
    internal static class SheetHelper
    {
        /// <summary>Sheet number first (numbers like "0001" look like ids), then element id.</summary>
        public static ViewSheet FindSheet(Document doc, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            q = q.Trim();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var byNumber = sheets.FirstOrDefault(s => s.SheetNumber.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (byNumber != null) return byNumber;
            if (int.TryParse(q, out int id) && doc.GetElement(new ElementId(id)) is ViewSheet byId) return byId;
            return sheets.FirstOrDefault(s => s.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A view by element id, exact name, or (for sheets) sheet number.</summary>
        public static View FindView(Document doc, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            q = q.Trim();
            if (int.TryParse(q, out int id) && doc.GetElement(new ElementId(id)) is View byId && !byId.IsTemplate) return byId;
            var v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
            return v ?? FindSheet(doc, q);
        }

        public static FamilyInstance TitleBlock(Document doc, ViewSheet sheet)
            => new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();

        /// <summary>
        /// The paper in sheet coordinates (feet): the title block's extent when there is one,
        /// otherwise the sheet outline. Paper mm are measured from <c>Min</c>.
        /// </summary>
        public static void Paper(Document doc, ViewSheet sheet, out XYZ min, out XYZ max, out string source)
        {
            var tb = TitleBlock(doc, sheet);
            var bb = tb?.get_BoundingBox(sheet);
            if (bb != null)
            {
                min = new XYZ(bb.Min.X, bb.Min.Y, 0);
                max = new XYZ(bb.Max.X, bb.Max.Y, 0);
                // The title block's own sheet size is the truth when the family reports it.
                double w = Param(tb, BuiltInParameter.SHEET_WIDTH), h = Param(tb, BuiltInParameter.SHEET_HEIGHT);
                if (w > 0 && h > 0 && Math.Abs(w - (max.X - min.X)) < 0.2 && Math.Abs(h - (max.Y - min.Y)) < 0.2)
                    max = new XYZ(min.X + w, min.Y + h, 0);
                source = "title block";
                return;
            }
            var ol = sheet.Outline;
            min = new XYZ(ol.Min.U, ol.Min.V, 0);
            max = new XYZ(ol.Max.U, ol.Max.V, 0);
            source = "sheet outline (no title block)";
        }

        private static double Param(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : 0;
        }

        public static MmRect PaperRect(Document doc, ViewSheet sheet, out XYZ origin, out string source)
        {
            Paper(doc, sheet, out var min, out var max, out source);
            origin = min;
            return new MmRect(0, 0, RevitUnitHelper.FeetToMm(max.X - min.X), RevitUnitHelper.FeetToMm(max.Y - min.Y));
        }

        public static XYZ ToSheet(XYZ origin, double xMm, double yMm)
            => new XYZ(origin.X + RevitUnitHelper.MmToFeet(xMm), origin.Y + RevitUnitHelper.MmToFeet(yMm), 0);

        public static MmRect ToPaper(XYZ origin, Outline o)
            => new MmRect(RevitUnitHelper.FeetToMm(o.MinimumPoint.X - origin.X), RevitUnitHelper.FeetToMm(o.MinimumPoint.Y - origin.Y),
                          RevitUnitHelper.FeetToMm(o.MaximumPoint.X - origin.X), RevitUnitHelper.FeetToMm(o.MaximumPoint.Y - origin.Y));

        public static MmRect ToPaper(XYZ origin, BoundingBoxXYZ bb)
            => new MmRect(RevitUnitHelper.FeetToMm(bb.Min.X - origin.X), RevitUnitHelper.FeetToMm(bb.Min.Y - origin.Y),
                          RevitUnitHelper.FeetToMm(bb.Max.X - origin.X), RevitUnitHelper.FeetToMm(bb.Max.Y - origin.Y));

        /// <summary>The sheet a view is already placed on, or null.</summary>
        public static ViewSheet SheetOf(Document doc, View view)
        {
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (vp.ViewId == view.Id) return doc.GetElement(vp.SheetId) as ViewSheet;
            return null;
        }

        public static string Label(ViewSheet s) => s.SheetNumber + " - " + s.Name;

        /// <summary>
        /// Converts the coordinates the annotation commands accept into a model point in the
        /// view: plan → project x,y (mm); section/elevation → x along the view from its origin
        /// (0 = middle of a section line), y = absolute elevation (mm); sheet/drafting → paper mm.
        /// </summary>
        public static XYZ ViewPoint(Document doc, View view, double xMm, double yMm, out string convention)
        {
            double x = RevitUnitHelper.MmToFeet(xMm), y = RevitUnitHelper.MmToFeet(yMm);
            if (view is ViewSheet sheet)
            {
                PaperRect(doc, sheet, out var origin, out _);
                convention = "paper mm from the sheet's lower-left";
                return ToSheet(origin, xMm, yMm);
            }
            if (view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend)
            {
                convention = "view mm";
                return new XYZ(x, y, 0);
            }
            if (view is ViewPlan)
            {
                double z = view.GenLevel != null ? view.GenLevel.Elevation : view.Origin.Z;
                convention = "project x,y mm (plan)";
                return new XYZ(x, y, z);
            }
            // Section / elevation / detail: horizontal offset along the view, absolute height.
            var right = view.RightDirection;
            var o = SectionOrigin(view);
            var p = new XYZ(o.X, o.Y, 0) + new XYZ(right.X, right.Y, 0).Normalize() * x;
            convention = "x = mm along the view from its origin (0 = middle of a section line), y = elevation mm";
            return new XYZ(p.X, p.Y, y);
        }

        /// <summary>A section's frame origin (the middle of its section line); the view origin otherwise.</summary>
        public static XYZ SectionOrigin(View view)
        {
            try
            {
                var cb = view.CropBox;
                if (cb != null && cb.Transform != null) return cb.Transform.Origin;
            }
            catch { /* some view types have no crop box */ }
            return view.Origin;
        }

        public static bool IsDimensionable(View v)
            => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.EngineeringPlan ||
               v.ViewType == ViewType.AreaPlan || v.ViewType == ViewType.Section || v.ViewType == ViewType.Elevation ||
               v.ViewType == ViewType.Detail || v.ViewType == ViewType.DraftingView;
    }
}
