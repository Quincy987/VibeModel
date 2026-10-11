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
            if (!SheetHelper.HasCropRegion(view))
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
                    : new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().Where(SheetHelper.IsModelContent);
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
            bool wasCropped = v3 != null ? v3.IsSectionBoxActive : view.CropBoxActive;
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
                    CropTo(view, min, max, false, out viewW, out viewH);
                    return;
                }
                // A section/elevation fit keeps to the section's own width (its cut is that wide).
                CropTo(view, min, max, !(view is ViewPlan) && a.Box == null, out wMm, out hMm);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR", "The view template may lock the crop region.");

            int scale = Math.Max(1, view.Scale);
            var sb = new StringBuilder();
            sb.AppendLine("VIEW CROPPED");
            sb.AppendLine("============");
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ", 1:" + view.Scale + ")");
            sb.AppendLine((v3 != null ? "Section box" : "Crop region") + ": " + Mm.F(wMm) + " × " + Mm.F(hMm) + " mm in the model (" + source + ")");
            if (a.Fit && wasCropped && a.Elements.Count == 0)
                sb.AppendLine("Note: the view was already cropped, so --fit only saw what was inside; 'crop " + a.View + " --off' first to grow it.");
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

        /// <summary>
        /// Set the view's crop region (in its own frame) to the world box min..max as seen in the
        /// view; depth is kept. Returns the region's size in mm.
        /// </summary>
        private static void CropTo(View view, XYZ min, XYZ max, bool clampWidth, out double wMm, out double hMm)
        {
            var cb = view.CropBox;
            var inv = cb.Transform.Inverse;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var c in SheetHelper.Corners(min, max))
            {
                var l = inv.OfPoint(c);
                x0 = Math.Min(x0, l.X); y0 = Math.Min(y0, l.Y); x1 = Math.Max(x1, l.X); y1 = Math.Max(y1, l.Y);
            }
            if (clampWidth)
            {
                x0 = Math.Max(x0, cb.Min.X); x1 = Math.Min(x1, cb.Max.X);
                if (x1 <= x0) throw new InvalidOperationException("The elements lie outside this section's width.");
            }
            cb.Min = new XYZ(x0, y0, cb.Min.Z);
            cb.Max = new XYZ(x1, y1, cb.Max.Z);
            view.CropBox = cb;
            view.CropBoxActive = true;
            wMm = RevitUnitHelper.FeetToMm(x1 - x0); hMm = RevitUnitHelper.FeetToMm(y1 - y0);
        }
    }
}
