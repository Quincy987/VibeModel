using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Crop a view to what matters (plan 12). An uncropped view spans everything visible — with
    /// a BGT underlay that is hundreds of metres, so it can never fit a sheet cell. --fit crops
    /// to the model elements (imports, point clouds, links and topography ignored), --box to a
    /// rectangle; 3D views get a section box. The response gives the paper size at the view's
    /// scale, so the next `viewport` can be sized right.
    /// </summary>
    public class CropCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "crop";
        public string Description => "Crop a view so it fits a sheet: --fit to its model elements (or --elements ids; DWG/point clouds ignored), " +
                                     "--box x0 y0 x1 y1 (plan: project mm; section: in-view x, elevation), --off. 3D views get a section box";
        public string Usage => CropArgs.Usage;

        private const double MaxElementExtentMm = 1000000;   // ignore site-wide objects when fitting

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = CropArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            var view = SheetHelper.FindView(doc, a.View);
            if (view == null) return CommandResult.Error("ELEMENT_NOT_FOUND", "No view '" + a.View + "'.", "Run 'views'.");
            if (view is ViewSheet || view is ViewSchedule || view.ViewType == ViewType.Legend)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' (" + view.ViewType + ") has no crop region.", null);
            var v3 = view as View3D;
            if (v3 != null && v3.IsPerspective)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' is a perspective view; its framing is the camera, not a crop box.", null);
            if (v3 != null && a.Box != null)
                return CommandResult.Error("BAD_ARGS", "--box is for plans and sections; for a 3D view use --fit (optionally --elements).", null);

            if (a.Off)
            {
                var err = TransactionHelper.Execute(doc, "VibeModel: Crop Off", () =>
                {
                    if (v3 != null) v3.IsSectionBoxActive = false;
                    else view.CropBoxActive = false;
                });
                if (err != null) return CommandResult.FromTransactionError(err);
                return CommandResult.Ok("CROP OFF\n========\n'" + view.Name + "' now shows everything visible.\n",
                    new Dictionary<string, object> { { "view", view.Id.IntegerValue }, { "cropped", false } });
            }

            // ---- the world-space region to show ----
            XYZ min, max;
            int used = 0, outliers = 0;
            string source;
            double margin = RevitUnitHelper.MmToFeet(a.Margin);
            if (a.Fit)
            {
                IEnumerable<Element> elems = a.Elements.Count > 0
                    ? a.Elements.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null)
                    : new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                        .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model &&
                                    !(e is ImportInstance) && !(e is PointCloudInstance) && !(e is RevitLinkInstance) &&
                                    e.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Topography &&
                                    e.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Cameras);
                min = max = null;
                var boxes = new List<BoundingBoxXYZ>();
                foreach (var e in elems)
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    var ext = bb.Max - bb.Min;
                    if (RevitUnitHelper.FeetToMm(Math.Max(ext.X, ext.Y)) > MaxElementExtentMm) continue;
                    boxes.Add(bb);
                }
                // Explicit --elements are taken as given; a whole view drops far-off stragglers.
                var keep = a.Elements.Count > 0 ? Enumerable.Range(0, boxes.Count).ToList()
                    : SheetLayout.FitKeep(boxes.Select(b => new MmRect(RevitUnitHelper.FeetToMm(b.Min.X), RevitUnitHelper.FeetToMm(b.Min.Y),
                                                                       RevitUnitHelper.FeetToMm(b.Max.X), RevitUnitHelper.FeetToMm(b.Max.Y))).ToList());
                outliers = boxes.Count - keep.Count;
                foreach (var i in keep)
                {
                    var bb = boxes[i];
                    min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                    max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
                    used++;
                }
                if (used == 0)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "No model elements to fit in '" + view.Name + "'" +
                        (a.Elements.Count > 0 ? " (none of the given ids exist)" : " (only underlays/point clouds are visible)") + ".",
                        "Give the elements with --elements, or a rectangle with --box.");
                min = min - new XYZ(margin, margin, margin);
                max = max + new XYZ(margin, margin, margin);
                source = used + " model element(s)" + (a.Elements.Count > 0 ? " (--elements)" : " visible in the view") + " + " + Mm.F(a.Margin) + " mm margin" +
                         (outliers > 0 ? "; " + outliers + " far-off element(s) ignored — use --elements or --box to include them" : "");
            }
            else
            {
                if (view is ViewPlan)
                {
                    min = new XYZ(RevitUnitHelper.MmToFeet(a.Box[0]), RevitUnitHelper.MmToFeet(a.Box[1]), -1e3);
                    max = new XYZ(RevitUnitHelper.MmToFeet(a.Box[2]), RevitUnitHelper.MmToFeet(a.Box[3]), 1e3);
                }
                else
                {
                    var p0 = SheetHelper.ViewPoint(doc, view, a.Box[0], a.Box[1], out _);
                    var p1 = SheetHelper.ViewPoint(doc, view, a.Box[2], a.Box[3], out _);
                    min = new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z));
                    max = new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z));
                }
                source = "--box";
            }

            // ---- apply ----
            double wMm = 0, hMm = 0, viewW = 0, viewH = 0;
            var error = TransactionHelper.Execute(doc, "VibeModel: Crop View", () =>
            {
                if (v3 != null)
                {
                    v3.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
                    v3.IsSectionBoxActive = true;
                    wMm = RevitUnitHelper.FeetToMm(max.X - min.X); hMm = RevitUnitHelper.FeetToMm(max.Y - min.Y);
                    // A section box alone does not shrink the view's extent on a sheet: also crop the
                    // view to the box as seen from the view's orientation.
                    var vcb = v3.CropBox;
                    var vinv = vcb.Transform.Inverse;
                    double vx0 = double.MaxValue, vy0 = double.MaxValue, vx1 = double.MinValue, vy1 = double.MinValue;
                    foreach (var c in Corners(min, max))
                    {
                        var l = vinv.OfPoint(c);
                        vx0 = Math.Min(vx0, l.X); vy0 = Math.Min(vy0, l.Y); vx1 = Math.Max(vx1, l.X); vy1 = Math.Max(vy1, l.Y);
                    }
                    vcb.Min = new XYZ(vx0, vy0, vcb.Min.Z);
                    vcb.Max = new XYZ(vx1, vy1, vcb.Max.Z);
                    v3.CropBox = vcb;
                    v3.CropBoxActive = true;
                    viewW = RevitUnitHelper.FeetToMm(vx1 - vx0); viewH = RevitUnitHelper.FeetToMm(vy1 - vy0);
                    return;
                }
                var cb = view.CropBox;
                var inv = cb.Transform.Inverse;
                double lx0 = double.MaxValue, ly0 = double.MaxValue, lx1 = double.MinValue, ly1 = double.MinValue;
                foreach (var c in Corners(min, max))
                {
                    var l = inv.OfPoint(c);
                    lx0 = Math.Min(lx0, l.X); ly0 = Math.Min(ly0, l.Y); lx1 = Math.Max(lx1, l.X); ly1 = Math.Max(ly1, l.Y);
                }
                if (view is ViewPlan == false && a.Box == null)
                {
                    // Section/elevation fit: keep the horizontal extent to the elements but never wider than the section itself.
                    lx0 = Math.Max(lx0, cb.Min.X); lx1 = Math.Min(lx1, cb.Max.X);
                    if (lx1 <= lx0) throw new InvalidOperationException("The elements lie outside this section's width.");
                }
                cb.Min = new XYZ(lx0, ly0, cb.Min.Z);
                cb.Max = new XYZ(lx1, ly1, cb.Max.Z);
                view.CropBox = cb;
                view.CropBoxActive = true;
                wMm = RevitUnitHelper.FeetToMm(lx1 - lx0); hMm = RevitUnitHelper.FeetToMm(ly1 - ly0);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR", "The view template may lock the crop region.");

            int scale = Math.Max(1, view.Scale);
            var sb = new StringBuilder();
            sb.AppendLine("VIEW CROPPED");
            sb.AppendLine("============");
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ", 1:" + view.Scale + ")");
            sb.AppendLine((v3 != null ? "Section box" : "Crop region") + ": " + Mm.F(wMm) + " × " + Mm.F(hMm) + " mm in the model (" + source + ")");
            if (v3 == null)
                sb.AppendLine("On paper at 1:" + scale + ": " + Mm.F(wMm / scale) + " × " + Mm.F(hMm / scale) + " mm");
            else
                sb.AppendLine("View cropped to the box as seen in this view: " + Mm.F(viewW) + " × " + Mm.F(viewH) + " mm → on paper at 1:" + scale + ": " +
                              Mm.F(viewW / scale) + " × " + Mm.F(viewH / scale) + " mm");

            var data = new Dictionary<string, object>
            {
                { "view", view.Id.IntegerValue }, { "cropped", true }, { "modelMm", new List<object> { Math.Round(wMm), Math.Round(hMm) } },
                { "paperMm", v3 == null ? new List<object> { Math.Round(wMm / scale), Math.Round(hMm / scale) } : new List<object> { Math.Round(viewW / scale), Math.Round(viewH / scale) } },
                { "elementsUsed", used }, { "outliersIgnored", outliers }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static IEnumerable<XYZ> Corners(XYZ a, XYZ b)
        {
            for (int i = 0; i < 8; i++)
                yield return new XYZ((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        }
    }
}
