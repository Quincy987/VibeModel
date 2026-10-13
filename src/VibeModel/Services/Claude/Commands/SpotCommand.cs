using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// A spot elevation (peilmaat) in the active plan/section/elevation: at a point (snapped to
    /// the nearest face, or edge of a family instance — Revit refuses spots on instance faces in
    /// a section) or on an element's top/bottom. The response says what it is attached to and
    /// warns when that element is one VibeModel may replace; `--fixed` hangs it on a short detail
    /// line instead, which never moves.
    /// </summary>
    public class SpotCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "spot";
        public string Description => "Spot elevation (peilmaat) in the active plan/section/elevation: at x y (snaps to the nearest face/edge within 100 mm) " +
                                     "or on <elementId> top|bottom. --leader x y moves the text, --fixed hangs it on a detail line (does not follow the model), --type <name>";
        public string Usage => SpotArgs.Usage;

        private const double SnapMm = 100;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var a = SpotArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            var view = FormattingHelper.GetActiveGraphicalView(uidoc);
            if (view == null || !SheetHelper.IsDimensionable(view) || view.ViewType == ViewType.DraftingView)
                return CommandResult.Error("BAD_ARGS",
                    "The active view " + (view != null ? "'" + view.Name + "' (" + view.ViewType + ") " : "") + "cannot take a spot elevation.",
                    "Spot elevations need a plan, section, elevation or detail view as the ACTIVE view — 'activate <view>' first (a view switch lands after the current request, so activate in a separate call).");
            bool plan = view is ViewPlan;
            if (a.Fixed && plan)
                return CommandResult.Error("BAD_ARGS", "--fixed needs a section or elevation: a detail line in a plan has the level's height, not the point's.",
                    "Leave out --fixed in a plan, or place the fixed spot in a section.");

            // Type
            var types = new FilteredElementCollector(doc).OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                .Where(t => t.Category != null && t.Category.Id.IntegerValue == (int)BuiltInCategory.OST_SpotElevations).ToList();
            SpotDimensionType type = null;
            if (a.Type != null)
            {
                type = types.FirstOrDefault(t => t.Name.Equals(a.Type, StringComparison.OrdinalIgnoreCase));
                if (type == null)
                {
                    var subs = types.Where(t => t.Name.IndexOf(a.Type, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (subs.Count == 1) type = subs[0];
                    else return CommandResult.Error("ELEMENT_NOT_FOUND",
                        (subs.Count == 0 ? "No spot elevation type '" : "More than one spot elevation type matches '") + a.Type + "'. Available: " +
                        string.Join(", ", types.Select(t => t.Name).Take(20)), null);
                }
            }
            if (type == null)
            {
                var defId = doc.GetDefaultElementTypeId(ElementTypeGroup.SpotElevationType);
                type = (defId != null && defId != ElementId.InvalidElementId ? doc.GetElement(defId) as SpotDimensionType : null) ?? types.FirstOrDefault();
            }
            if (type == null) return CommandResult.Error("TYPE_NOT_LOADED", "No spot elevation types in the document.", null);

            // Where, and on what
            AnnotationGeometry.Hit hit;
            Element host;
            bool top = true;
            string side;
            if (a.ElementId.HasValue)
            {
                host = doc.GetElement(new ElementId(a.ElementId.Value));
                if (host == null) return CommandResult.Error("ELEMENT_NOT_FOUND", "Element " + a.ElementId + " not found.", null);
                if (!SheetHelper.IsModelContent(host))
                    return CommandResult.Error("BAD_ARGS", "Element " + a.ElementId + " (" + (host.Category?.Name ?? host.GetType().Name) + ") is not a model element a spot elevation can sit on.", null);
                top = a.Top;
                hit = TopOrBottom(doc, host, view, top);
                side = top ? "top" : "bottom";
                if (hit == null)
                    return CommandResult.Error("BAD_ARGS", "Element " + a.ElementId + " has no " + side + (host is FamilyInstance ? " edge" : " face") + " a spot elevation can use in '" + view.Name + "'.",
                        "Check the element is visible in this view, or give a point: spot <x> <y>.");
            }
            else
            {
                var p = SheetHelper.ViewPoint(doc, view, a.Point[0], a.Point[1], out _);
                hit = Snap(doc, view, p, plan, out host);
                side = "point";
                if (hit == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "Nothing a spot elevation can sit on within " + SnapMm + " mm of (" + Mm.F(a.Point[0]) + ", " + Mm.F(a.Point[1]) + ") in '" + view.Name + "'.",
                        "Give a point on a face or edge (" + (plan ? "project x,y mm" : SheetHelper.SectionConvention) + "), or use spot <elementId> top|bottom.");
            }

            // Leader
            AnnotationGeometry.DefaultLeader(view, hit.Point, top, out var bend, out var end);
            if (a.Leader != null)
            {
                var e = SheetHelper.ViewPoint(doc, view, a.Leader[0], a.Leader[1], out _);
                var vdir = view.ViewDirection.Normalize();
                e = e + vdir * (hit.Point - e).DotProduct(vdir);      // same depth as the point
                var up = view.UpDirection.Normalize();
                bend = hit.Point + up * (e - hit.Point).DotProduct(up);  // the shoulder keeps the vertical
                end = e;
            }

            SpotDimension spot = null;
            DetailCurve helper = null;
            var reference = hit.Reference;
            var origin = hit.Point;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Spot Elevation", () =>
            {
                if (a.Fixed)
                {
                    var vdir = view.ViewDirection.Normalize();
                    var o = SheetHelper.SectionOrigin(view);
                    var onPlane = origin - vdir * (origin - o).DotProduct(vdir);
                    var right = view.RightDirection.Normalize();
                    double half = RevitUnitHelper.MmToFeet(100);
                    helper = doc.Create.NewDetailCurve(view, Line.CreateBound(onPlane - right * half, onPlane + right * half));
                    reference = helper.GeometryCurve.Reference;
                    var shift = onPlane - origin;
                    origin = onPlane; bend += shift; end += shift;
                }
                spot = doc.Create.NewSpotElevation(view, reference, origin, bend, end, origin, true);
                if (spot == null) throw new InvalidOperationException("Revit returned no spot elevation.");
                if (spot.GetTypeId() != type.Id) spot.ChangeTypeId(type.Id);
                doc.Regenerate();
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR",
                    "Usually: the point is not on the element's face/edge as seen in '" + view.Name + "' (a family instance only takes edges in a section), or the element is not visible here.");

            double value = AnnotationGuard.SpotValueMm(spot);
            bool vmCreated = !a.Fixed && VibeModelCreated.Contains(doc, host.Id);
            int bic = host.Category?.Id.IntegerValue ?? 0;
            bool likely = !a.Fixed && AnnotationText.LikelyReplaced(bic);
            string kind = a.Fixed ? "detail-line" : hit.Curve != null ? "edge" : "face";
            string where = WhereText(view, plan, hit.Point);

            var sb = new StringBuilder();
            sb.AppendLine("SPOT ELEVATION CREATED");
            sb.AppendLine("======================");
            sb.AppendLine();
            sb.AppendLine("ID: " + spot.Id.IntegerValue);
            sb.AppendLine("View: " + view.Name + " (1:" + view.Scale + ")");
            sb.AppendLine("Value: " + (double.IsNaN(value) ? "?" : Signed(value)) + " mm");
            sb.AppendLine("Type: " + type.Name);
            if (a.Fixed)
                sb.AppendLine("Attached to: detail line " + helper.Id.IntegerValue + " at " + where +
                              " (fixed: it does NOT follow the model — it keeps showing the height of " + AnnotationText.CategoryWord(host.Category?.Name) + " " + host.Id.IntegerValue + " as it is now)");
            else
                sb.AppendLine("Attached to: " + AnnotationText.CategoryWord(host.Category?.Name) + " " + host.Id.IntegerValue + " '" + host.Name + "' — " +
                              (side == "point" ? "" : side + " ") + kind + " at " + where + " (follows the element)");
            if (vmCreated || likely)
                sb.AppendLine("Warning: " + host.Id.IntegerValue + (vmCreated ? " was created by VibeModel in this session" : " is a " + AnnotationText.CategoryWord(host.Category?.Name) + " and likely to be replaced") +
                              "; replacing it (placeid + delete, exec) re-hosts this spot when the new element lies within " +
                              Mm.F1(AnnotationMatch.RehostToleranceMm) + " mm, else removes it — the replacing command's response says which. Use --fixed for a reference that never moves.");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { spot.Id.IntegerValue } },
                { "view", view.Id.IntegerValue },
                { "valueMm", double.IsNaN(value) ? null : (object)Math.Round(value, 1) },
                { "reference", new Dictionary<string, object>
                    {
                        { "element", a.Fixed ? helper.Id.IntegerValue : host.Id.IntegerValue },
                        { "category", a.Fixed ? "Lines" : host.Category?.Name },
                        { "kind", kind },
                        { "pointMm", new List<object> { Math.Round(RevitUnitHelper.FeetToMm(hit.Point.X)), Math.Round(RevitUnitHelper.FeetToMm(hit.Point.Y)), Math.Round(RevitUnitHelper.FeetToMm(hit.Point.Z)) } }
                    }
                },
                { "fixed", a.Fixed },
                { "vibeModelCreated", vmCreated }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static string Signed(double mm) => (mm >= 0 ? "+" : "") + Mm.F(mm);

        private static string WhereText(View view, bool plan, XYZ p)
        {
            if (plan) return "x=" + Mm.F(RevitUnitHelper.FeetToMm(p.X)) + ", y=" + Mm.F(RevitUnitHelper.FeetToMm(p.Y));
            return "x=" + Mm.F(RevitUnitHelper.FeetToMm(SheetHelper.Along(view, p))) + " (along the view)";
        }

        /// <summary>The highest (top) or lowest (bottom) horizontal face, or for a family instance the extreme edge.</summary>
        private static AnnotationGeometry.Hit TopOrBottom(Document doc, Element host, View view, bool top)
        {
            var centre = Centre(host, view);
            if (host is FamilyInstance)
            {
                var edges = AnnotationGeometry.Enumerate(host, view, faces: false, edges: true).Where(g => g.Curve != null && g.Curve.IsBound).ToList();
                if (edges.Count == 0) return null;
                double z = top ? edges.Max(g => Math.Max(g.Curve.GetEndPoint(0).Z, g.Curve.GetEndPoint(1).Z))
                               : edges.Min(g => Math.Min(g.Curve.GetEndPoint(0).Z, g.Curve.GetEndPoint(1).Z));
                double tol = RevitUnitHelper.MmToFeet(1);
                // Prefer an edge lying flat at that height (the tip square of a pile), nearest the centre.
                var flat = edges.Where(g => Math.Abs(g.Curve.GetEndPoint(0).Z - z) <= tol && Math.Abs(g.Curve.GetEndPoint(1).Z - z) <= tol).ToList();
                var pool = flat.Count > 0 ? flat : edges.Where(g => Math.Abs(g.Curve.GetEndPoint(0).Z - z) <= tol || Math.Abs(g.Curve.GetEndPoint(1).Z - z) <= tol).ToList();
                var target = new XYZ(centre.X, centre.Y, z);
                AnnotationGeometry.Hit best = null;
                foreach (var g in pool)
                {
                    var r = g.Curve.Project(target);
                    if (r == null) continue;
                    if (best == null || r.Distance < best.Distance)
                        best = new AnnotationGeometry.Hit { Reference = g.Reference, Point = r.XYZPoint, Curve = g.Curve, Distance = r.Distance };
                }
                return best;
            }

            var faces = AnnotationGeometry.Enumerate(host, view, faces: true, edges: false).Where(g => g.Face is PlanarFace).ToList();
            var horizontal = faces.Select(g => (PlanarFace)g.Face)
                .Where(f => top ? f.FaceNormal.Z > 0.99 : f.FaceNormal.Z < -0.99).ToList();
            if (horizontal.Count == 0) return null;
            var face = top ? horizontal.OrderByDescending(f => f.Origin.Z).First() : horizontal.OrderBy(f => f.Origin.Z).First();
            var refOf = faces.First(g => ReferenceEquals(g.Face, face)).Reference;
            var pt = new XYZ(centre.X, centre.Y, face.Origin.Z);
            var proj = face.Project(pt);
            if (proj == null)
            {
                // The centre falls outside the face (an L-shaped slab): the face's own middle.
                var bb = face.GetBoundingBox();
                var mid = face.Evaluate(new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2));
                proj = face.Project(mid);
                if (proj == null) return null;
            }
            return new AnnotationGeometry.Hit { Reference = refOf, Point = proj.XYZPoint, Normal = face.FaceNormal, Distance = proj.Distance };
        }

        /// <summary>
        /// The element's middle in x,y — in a section moved onto the cut plane so the spot sits
        /// where the section shows the element.
        /// </summary>
        private static XYZ Centre(Element e, View view)
        {
            XYZ c;
            if (e.Location is LocationPoint lp) c = lp.Point;
            else if (e.Location is LocationCurve lc) c = lc.Curve.Evaluate(0.5, true);
            else
            {
                var bb = e.get_BoundingBox(null);
                c = bb != null ? (bb.Min + bb.Max) * 0.5 : XYZ.Zero;
            }
            if (!(view is ViewPlan))
            {
                var vdir = view.ViewDirection.Normalize();
                var o = SheetHelper.SectionOrigin(view);
                var flat = new XYZ(vdir.X, vdir.Y, 0);
                if (flat.GetLength() > 1e-6)
                {
                    flat = flat.Normalize();
                    c = c - flat * (c - o).DotProduct(flat);
                }
            }
            return c;
        }

        /// <summary>
        /// The nearest face (system families) or edge (family instances) within SnapMm of p,
        /// measured in the view plane. In a plan only upward horizontal faces count, the highest
        /// one at that x,y first.
        /// </summary>
        private static AnnotationGeometry.Hit Snap(Document doc, View view, XYZ p, bool plan, out Element host)
        {
            host = null;
            double snap = RevitUnitHelper.MmToFeet(SnapMm);
            var vdir = view.ViewDirection.Normalize();
            Outline outline = plan
                ? new Outline(new XYZ(p.X - snap, p.Y - snap, -1e4), new XYZ(p.X + snap, p.Y + snap, 1e4))
                : new Outline(new XYZ(p.X - snap - Math.Abs(vdir.X) * 1e3, p.Y - snap - Math.Abs(vdir.Y) * 1e3, p.Z - snap),
                              new XYZ(p.X + snap + Math.Abs(vdir.X) * 1e3, p.Y + snap + Math.Abs(vdir.Y) * 1e3, p.Z + snap));
            AnnotationGeometry.Hit best = null;
            double bestZ = double.MinValue;
            foreach (var e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                if (!SheetHelper.IsModelContent(e)) continue;
                bool inst = e is FamilyInstance;
                foreach (var g in AnnotationGeometry.Enumerate(e, view, faces: !inst, edges: inst))
                {
                    AnnotationGeometry.Hit h = null;
                    if (g.Face != null)
                    {
                        if (plan)
                        {
                            if (!(g.Face is PlanarFace pf) || pf.FaceNormal.Z < 0.99) continue;
                            var r = pf.Project(new XYZ(p.X, p.Y, pf.Origin.Z));
                            if (r == null || r.Distance > snap) continue;
                            // Plan: the highest face under the point wins (what the plan shows).
                            if (best != null && pf.Origin.Z <= bestZ) continue;
                            bestZ = pf.Origin.Z;
                            best = new AnnotationGeometry.Hit { Reference = g.Reference, Point = r.XYZPoint, Normal = pf.FaceNormal, Distance = r.Distance };
                            host = e;
                            continue;
                        }
                        var rs = g.Face.Project(p);
                        if (rs == null) continue;
                        double d = InPlane(rs.XYZPoint - p, vdir);
                        if (d > snap) continue;
                        XYZ n = null;
                        try { n = g.Face.ComputeNormal(rs.UVPoint); } catch { }
                        h = new AnnotationGeometry.Hit { Reference = g.Reference, Point = rs.XYZPoint, Normal = n, Distance = d };
                    }
                    else if (g.Curve != null && !plan)
                    {
                        var rc = g.Curve.Project(p);
                        if (rc == null) continue;
                        double d = InPlane(rc.XYZPoint - p, vdir);
                        if (d > snap) continue;
                        h = new AnnotationGeometry.Hit { Reference = g.Reference, Point = rc.XYZPoint, Curve = g.Curve, Distance = d };
                    }
                    if (h == null) continue;
                    double tol = RevitUnitHelper.MmToFeet(1);
                    if (best == null || h.Distance < best.Distance - tol ||
                        (Math.Abs(h.Distance - best.Distance) <= tol && IsFlat(h.Curve) && !IsFlat(best.Curve)))
                    {
                        best = h;
                        host = e;
                    }
                }
            }
            return best;
        }

        private static bool IsFlat(Curve c) => c is Line l && Math.Abs(l.Direction.Z) < 0.01;

        private static double InPlane(XYZ v, XYZ vdir)
        {
            var w = v - vdir * v.DotProduct(vdir);
            return w.GetLength();
        }
    }
}
