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
    /// Create a section view along a line (mm) or straight across a wall (plan 12). The
    /// response states which way the section looks, its extent and scale, so the next step
    /// (activate / viewport / dimension --levels) needs no guessing.
    /// </summary>
    public class SectionCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "section";
        public string Description => "Create a section view along a line x1 y1 x2 y2 (project mm; it looks to the LEFT of the " +
                                     "direction x1,y1→x2,y2) or across a wall ('section <wallId>'); office template/phase applied";
        public string Usage => SectionArgs.Usage;

        private const double DefaultDepthMm = 3000;
        private const double DefaultWallOffsetMm = 2000;
        private const double VerticalMarginMm = 1000;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var a = SectionArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            if (a.Name != null && new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Any(v => !v.IsTemplate && v.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Error("BAD_ARGS", "A view named '" + a.Name + "' already exists.", "Pick another --name.");

            // ---- the section line + vertical range ----
            SectionFrame frame;
            string through;
            double? autoBottom = null, autoTop = null;
            if (a.WallId.HasValue)
            {
                var wall = doc.GetElement(new ElementId(a.WallId.Value)) as Wall;
                if (wall == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "Element " + a.WallId + " is not a wall.",
                        "Use 'section <x1> <y1> <x2> <y2>' for anything else, or 'list walls' for wall ids.");
                if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line wl))
                    return CommandResult.Error("BAD_ARGS", "Wall " + a.WallId + " is not straight; give the section line as coordinates.", null);
                double half = RevitUnitHelper.FeetToMm(wall.Width) / 2 + (a.Offset ?? DefaultWallOffsetMm);
                var s = wl.GetEndPoint(0); var e = wl.GetEndPoint(1);
                frame = SectionFrame.AcrossWall(RevitUnitHelper.FeetToMm(s.X), RevitUnitHelper.FeetToMm(s.Y),
                                                RevitUnitHelper.FeetToMm(e.X), RevitUnitHelper.FeetToMm(e.Y), half);
                through = "across wall " + a.WallId + " (" + wall.Name + ") at its midpoint";
                var bb = wall.get_BoundingBox(null);
                if (bb != null) { autoBottom = RevitUnitHelper.FeetToMm(bb.Min.Z); autoTop = RevitUnitHelper.FeetToMm(bb.Max.Z); }
            }
            else
            {
                if (a.Offset.HasValue)
                    return CommandResult.Error("BAD_ARGS", "--offset only applies to 'section <wallId>'.", null);
                frame = SectionFrame.FromLine(a.Line[0], a.Line[1], a.Line[2], a.Line[3]);
                through = "along the line";
            }
            if (frame == null) return CommandResult.Error("BAD_ARGS", "The section line has no length.", null);

            double depthMm = a.Depth ?? DefaultDepthMm;
            ModelZRange(doc, frame, depthMm, out var seenBottom, out var seenTop);
            if (seenBottom.HasValue)
            {
                autoBottom = autoBottom.HasValue ? Math.Min(autoBottom.Value, seenBottom.Value) : seenBottom;
                autoTop = autoTop.HasValue ? Math.Max(autoTop.Value, seenTop.Value) : seenTop;
            }
            double bottomMm = a.Bottom ?? (autoBottom.HasValue ? autoBottom.Value - VerticalMarginMm : -5000);
            double topMm = a.Top ?? (autoTop.HasValue ? autoTop.Value + VerticalMarginMm : 10000);
            if (topMm - bottomMm < 100)
                return CommandResult.Error("BAD_ARGS", "Vertical range " + Mm.F(bottomMm) + ".." + Mm.F(topMm) + " mm is too small.",
                    "Pass --bottom and --top (mm).");

            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(vt => vt.ViewFamily == ViewFamily.Section);
            if (vft == null) return CommandResult.Error("TYPE_NOT_LOADED", "No section view type in the document.", null);

            var setup = OfficeViewSetup.Resolve(doc, "section", a.Template, a.NoTemplate, out var setupError);
            if (setupError != null) return setupError;

            // ---- build the box in the section frame ----
            var t = Transform.Identity;
            double midZ = RevitUnitHelper.MmToFeet((bottomMm + topMm) / 2);
            t.Origin = new XYZ(RevitUnitHelper.MmToFeet(frame.Ox), RevitUnitHelper.MmToFeet(frame.Oy), midZ);
            // See SectionFrame: BasisZ = looking direction, Min.Z = cut plane, Max.Z = far clip.
            t.BasisX = new XYZ(-frame.Dx, -frame.Dy, 0);
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = new XYZ(frame.Lx, frame.Ly, 0);
            double halfLen = RevitUnitHelper.MmToFeet(frame.Length / 2);
            double halfH = RevitUnitHelper.MmToFeet((topMm - bottomMm) / 2);
            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-halfLen, -halfH, 0),
                Max = new XYZ(halfLen, halfH, RevitUnitHelper.MmToFeet(depthMm))
            };

            ViewSection view = null;
            string scaleNote = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Section", () =>
            {
                view = ViewSection.CreateSection(doc, vft.Id, box);
                if (a.Name != null) view.Name = a.Name;
                setup?.Apply(doc, view);
                if (a.Scale.HasValue) scaleNote = SetScale(view, (int)a.Scale.Value);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);

            var look = view.ViewDirection.Negate();
            var sb = new StringBuilder();
            sb.AppendLine("SECTION CREATED");
            sb.AppendLine("===============");
            sb.AppendLine();
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Name: " + view.Name);
            sb.AppendLine("Scale: 1:" + view.Scale + (scaleNote != null ? "  (" + scaleNote + ")" : ""));
            sb.AppendLine("Line: (" + Mm.F(frame.Ox - frame.Dx * frame.Length / 2) + ", " + Mm.F(frame.Oy - frame.Dy * frame.Length / 2) + ") → (" +
                          Mm.F(frame.Ox + frame.Dx * frame.Length / 2) + ", " + Mm.F(frame.Oy + frame.Dy * frame.Length / 2) + ") mm, " +
                          Mm.F(frame.Length) + " mm long, " + through);
            bool asIntended = look.X * frame.Lx + look.Y * frame.Ly > 0.99;
            sb.AppendLine("Looking: " + SectionFrame.Compass(look.X, look.Y) + " (project north = +Y)" +
                          (asIntended ? ", i.e. to the left of the line direction" : " — NOT the intended left side; swap x1,y1 and x2,y2 to flip"));
            sb.AppendLine("Depth: " + Mm.F(depthMm) + " mm beyond the cut plane");
            sb.AppendLine("Vertical range: " + Mm.F(bottomMm) + " .. " + Mm.F(topMm) + " mm" +
                          (a.Top.HasValue || a.Bottom.HasValue ? "" : " (from the model elements it cuts, ±" + Mm.F(VerticalMarginMm) + ")"));
            sb.AppendLine("In-view coordinates (text/tag/dimension --levels): x = 0 at the middle of the line, " +
                          Mm.F(-frame.Length / 2) + " .. " + Mm.F(frame.Length / 2) + "; y = elevation mm");
            setup?.AppendReport(sb);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { view.Id.IntegerValue } },
                { "name", view.Name },
                { "scale", view.Scale },
                { "lengthMm", Math.Round(frame.Length) },
                { "depthMm", depthMm },
                { "bottomMm", Math.Round(bottomMm) },
                { "topMm", Math.Round(topMm) },
                { "looking", new List<object> { Math.Round(look.X, 4), Math.Round(look.Y, 4) } }
            };
            if (setup != null) data["office"] = setup.ToJson();

            if (a.Activate)
            {
                var act = ActivateCommand.Activate(uidoc, view);
                data["activated"] = act.Success;
                sb.AppendLine(act.Success ? "Activated: now the active view." : "Not activated: " + act.Message);
            }
            sb.AppendLine("Next: 'viewport <sheet> " + view.Id.IntegerValue + "' to put it on a sheet, 'dimension --levels' with it active, " +
                          "'screenshot --view " + view.Id.IntegerValue + "' to look at it.");
            return CommandResult.Ok(sb.ToString(), data);
        }

        /// <summary>Sets 1:n, or explains why not (a view template that controls the scale).</summary>
        internal static string SetScale(View view, int scale)
        {
            try
            {
                var p = view.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC) ?? view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                if (p != null && p.IsReadOnly && view.ViewTemplateId != ElementId.InvalidElementId)
                    return "scale is set by view template '" + view.Document.GetElement(view.ViewTemplateId)?.Name + "', kept 1:" + view.Scale;
                view.Scale = scale;
                return null;
            }
            catch (Exception ex) { return "could not set 1:" + scale + ": " + ex.Message; }
        }

        /// <summary>Height range of the model elements the section box will see (DWG/point clouds/links excluded).</summary>
        private static void ModelZRange(Document doc, SectionFrame f, double depthMm, out double? bottom, out double? top)
        {
            bottom = top = null;
            double x1 = f.Ox - f.Dx * f.Length / 2, y1 = f.Oy - f.Dy * f.Length / 2;
            double x2 = f.Ox + f.Dx * f.Length / 2, y2 = f.Oy + f.Dy * f.Length / 2;
            // Region seen by the section: the line swept depth mm along the looking direction (−BasisZ).
            double lx = f.Lx * depthMm, ly = f.Ly * depthMm;
            var xs = new[] { x1, x2, x1 + lx, x2 + lx }; var ys = new[] { y1, y2, y1 + ly, y2 + ly };
            var outline = new Outline(
                new XYZ(RevitUnitHelper.MmToFeet(xs.Min()), RevitUnitHelper.MmToFeet(ys.Min()), -1e4),
                new XYZ(RevitUnitHelper.MmToFeet(xs.Max()), RevitUnitHelper.MmToFeet(ys.Max()), 1e4));
            var elems = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model &&
                            !(e is ImportInstance) && !(e is PointCloudInstance) && !(e is RevitLinkInstance) &&
                            e.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Topography)
                .Take(2000);
            foreach (var e in elems)
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                double lo = RevitUnitHelper.FeetToMm(bb.Min.Z), hi = RevitUnitHelper.FeetToMm(bb.Max.Z);
                if (hi - lo > 200000) continue;   // skip absurd extents (site-wide objects)
                bottom = bottom.HasValue ? Math.Min(bottom.Value, lo) : lo;
                top = top.HasValue ? Math.Max(top.Value, hi) : hi;
            }
        }
    }
}
