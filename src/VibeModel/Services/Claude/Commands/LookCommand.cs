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
    /// Aim a 3D view: the camera stands at a compass direction from the target ("zw" = south-west
    /// of it, looking north-east, 30° down) or at a given point, looks at the target (default: the
    /// model elements in the view), shaded with edges. The crop region is fitted to the target as
    /// seen from the new camera — a crop region only means something for one camera direction —
    /// and --box adds a section box. The workshop case: "camera from the water onto the corner".
    /// </summary>
    public class LookCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "look";
        public string Description => "Aim a 3D view from a compass direction (n|no|o|zo|z|zw|w|nw|top|iso) or a camera point at the model, " +
                                     "some elements (--at ids) or a point; shaded with edges, cropped to the target";
        public string Usage => LookSpec.LookUsage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var spec = LookSpec.ParseLook(args, out var viewName);
            if (spec.Error != null) return CommandResult.Error("BAD_ARGS", spec.Error, "Usage: " + Usage);

            View view = viewName == null ? FormattingHelper.GetActiveGraphicalView(uidoc) : SheetHelper.FindView(doc, viewName);
            if (view == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", viewName == null ? "No active view." : "No view '" + viewName + "'.", "Run 'views'.");
            if (!(view is View3D v3) || v3.IsTemplate)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' is not a 3D view (" + view.ViewType + ").",
                    "Name a 3D view (look <view> " + (spec.Key ?? "zw") + "), or create one: view 3d --look " + (spec.Key ?? "zw") + ".");
            return Orient(doc, uidoc, v3, spec);
        }

        /// <summary>Orient an existing 3D view. Used by `look` and by `view 3d --look`.</summary>
        internal static CommandResult Orient(Document doc, UIDocument uidoc, View3D view, LookSpec spec)
        {
            if (view.IsLocked)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' has a locked orientation (it is tagged).", "Unlock the view, or use 'view 3d --look …' for a new one.");

            // ---- target ----
            XYZ min = null, max = null, target;
            string targetNote;
            List<int> ids = null;
            if (spec.AtSelected)
            {
                ids = uidoc.Selection.GetElementIds().Select(i => i.IntegerValue).ToList();
                if (ids.Count == 0) return CommandResult.Error("BAD_ARGS", "--at selected: nothing is selected.", "Select elements, or give ids with --at.");
            }
            else if (spec.AtNumbers != null && spec.AtIsIds(v => doc.GetElement(new ElementId((int)v)) != null))
                ids = spec.AtNumbers.Select(v => (int)v).ToList();

            if (ids != null)
            {
                var missing = ids.Where(i => doc.GetElement(new ElementId(i)) == null).ToList();
                if (missing.Count > 0)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "--at: element(s) " + string.Join(",", missing) + " not found.", null);
                CropCommand.FitExtent(doc, view, ids, out min, out max, out _, out _);
                if (min == null) return CommandResult.Error("BAD_ARGS", "--at: the given elements have no 3D extent.", null);
                target = (min + max) / 2;
                targetNote = ids.Count + " element(s)" + (spec.AtSelected ? " (selection)" : "") + " — centre " + P(target);
            }
            else if (spec.AtNumbers != null)
            {
                target = new XYZ(Ft(spec.AtNumbers[0]), Ft(spec.AtNumbers[1]), Ft(spec.AtNumbers[2]));
                targetNote = "point " + P(target);
            }
            else
            {
                CropCommand.FitExtent(doc, view, null, out min, out max, out int used, out int outliers);
                if (min == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "No model elements in '" + view.Name + "' to look at.", "Give a target with --at.");
                target = (min + max) / 2;
                targetNote = "the model (" + used + " element(s)" + (outliers > 0 ? ", " + outliers + " far-off ignored" : "") + ") — centre " + P(target);
            }

            // ---- camera ----
            double[] f;
            XYZ eye;
            double distMm;
            if (spec.Eye != null)
            {
                eye = new XYZ(Ft(spec.Eye[0]), Ft(spec.Eye[1]), Ft(spec.Eye[2]));
                var d = target - eye;
                if (d.GetLength() < Ft(1)) return CommandResult.Error("BAD_ARGS", "The camera point is the target itself.", null);
                f = CameraMath.Unit(new[] { d.X, d.Y, d.Z });
                distMm = RevitUnitHelper.FeetToMm(d.GetLength());
            }
            else
            {
                f = CameraMath.Forward(spec.Key, spec.Pitch);
                double diag = min != null ? RevitUnitHelper.FeetToMm((max - min).GetLength()) : 0;
                distMm = spec.Distance ?? Math.Max(10000, diag * 1.5);
                var e = CameraMath.Eye(new[] { target.X, target.Y, target.Z }, f, Ft(distMm));
                eye = new XYZ(e[0], e[1], e[2]);
            }
            var up = CameraMath.Up(f);

            // ---- apply ----
            var notes = new List<string>();
            double cropW = 0, cropH = 0;
            bool cropped = false;
            var err = TransactionHelper.Execute(doc, "VibeModel: Look", () =>
            {
                view.SetOrientation(new ViewOrientation3D(eye, new XYZ(up[0], up[1], up[2]), new XYZ(f[0], f[1], f[2])));
                try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; }
                catch (Exception ex) { notes.Add("Display style kept (" + ex.Message.Trim() + " — the view template may control it)"); }

                double m = Ft(spec.Margin);
                XYZ bmin = min != null ? min - new XYZ(m, m, m) : null, bmax = max != null ? max + new XYZ(m, m, m) : null;
                if (spec.Box)
                {
                    if (bmin == null) notes.Add("--box needs a target with an extent (elements or the model), not a point — no section box");
                    else
                    {
                        view.SetSectionBox(new BoundingBoxXYZ { Min = bmin, Max = bmax });
                        view.IsSectionBoxActive = true;
                    }
                }
                doc.Regenerate();

                if (view.IsPerspective)
                {
                    notes.Add("Perspective view: framed by the camera, crop region left as it is");
                    return;
                }
                if (bmin != null)
                {
                    CropCommand.CropTo(view, bmin, bmax, false, out cropW, out cropH);
                    cropped = true;
                }
                else view.CropBoxActive = false;   // a crop region fitted to the old camera would cut the wrong part
            });
            if (err != null)
                return CommandResult.FromTransactionError(err, "REVIT_ERROR", "The view template may lock the orientation or crop of this view.");

            var sb = new StringBuilder();
            sb.AppendLine("3D VIEW AIMED");
            sb.AppendLine("=============");
            sb.AppendLine("View: " + view.Name + " (id " + view.Id.IntegerValue + ")");
            sb.AppendLine("Camera: " + (spec.Key != null ? "'" + spec.Key + "' " : "") + CameraMath.Describe(f));
            sb.AppendLine("Target: " + targetNote);
            sb.AppendLine("Eye: " + P(eye) + ", " + Mm.F(distMm) + " mm from the target");
            sb.AppendLine("Display: " + view.DisplayStyle);
            if (cropped)
                sb.AppendLine("Cropped to the target + " + Mm.F(spec.Margin) + " mm as seen from this camera: " + Mm.F(cropW) + " × " + Mm.F(cropH) + " mm" +
                              (spec.Box ? "; section box on" : ""));
            else if (!view.IsPerspective) sb.AppendLine("Crop region off (the target is a point; everything visible is shown)");
            foreach (var n in notes) sb.AppendLine("Note: " + n);
            sb.AppendLine("Picture: screenshot --view " + view.Id.IntegerValue);

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "view", view.Id.IntegerValue }, { "name", view.Name }, { "key", spec.Key },
                { "forward", f.Select(c => (object)Math.Round(c, 4)).ToList() },
                { "eyeMm", MmList(eye) }, { "targetMm", MmList(target) }, { "distanceMm", Math.Round(distMm) },
                { "cropped", cropped }, { "cropMm", cropped ? new List<object> { Math.Round(cropW), Math.Round(cropH) } : null },
                { "sectionBox", view.IsSectionBoxActive }
            });
        }

        private static double Ft(double mm) => RevitUnitHelper.MmToFeet(mm);

        private static string P(XYZ p)
            => "(" + Mm.F(RevitUnitHelper.FeetToMm(p.X)) + ", " + Mm.F(RevitUnitHelper.FeetToMm(p.Y)) + ", " + Mm.F(RevitUnitHelper.FeetToMm(p.Z)) + ") mm";

        private static List<object> MmList(XYZ p)
            => new List<object> { Math.Round(RevitUnitHelper.FeetToMm(p.X)), Math.Round(RevitUnitHelper.FeetToMm(p.Y)), Math.Round(RevitUnitHelper.FeetToMm(p.Z)) };
    }
}
