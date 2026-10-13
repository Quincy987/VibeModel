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
    /// Linear dimensions in the active plan/section/elevation (plan 12): through elements
    /// (outer faces, grids, levels, reference planes, family centre planes), between two
    /// points (nearest dimensionable face/grid), or a vertical string through the levels.
    /// The response lists every segment's value and which reference it used; when Revit
    /// refuses, its own message comes back with the likely cause.
    /// </summary>
    public class DimensionCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "dimension";
        public string Description => "Add a linear dimension in the active plan/section/elevation: through element ids (outer faces / grids / levels), " +
                                     "--points x1 y1 x2 y2 (plan, nearest face or grid), or --levels (vertical level string in a section). " +
                                     "--dir x|y = the view's horizontal/vertical";
        public string Usage => DimensionArgs.Usage;

        private const double PaperOffsetMm = 8;      // default distance of the dimension line from the elements, on paper
        private const double SnapMm = 100;           // --points: how far a reference may be from the point
        private static readonly double Tol = RevitUnitHelper.MmToFeet(0.5);

        private sealed class Cand
        {
            public Reference Ref;
            public double Pos;        // feet along the dimension direction
            public int ElementId;
            public string What;
            public double Dist;       // --points only
            public bool Stable;       // a level, grid or reference plane - survives any element replacement
            public int FaceOf;        // the element whose face/plane this is (0 for datums)
            public bool Substituted;  // a level/grid/reference plane standing in for an element face at the same position
        }

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var a = DimensionArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            var view = FormattingHelper.GetActiveGraphicalView(uidoc);
            if (view == null || !SheetHelper.IsDimensionable(view))
                return CommandResult.Error("BAD_ARGS",
                    "The active view " + (view != null ? "'" + view.Name + "' (" + view.ViewType + ") " : "") + "cannot take a linear dimension.",
                    "Dimensions need a plan, section or elevation as the ACTIVE view — 'activate <view>' first (a view switch lands after the current request, so activate in a separate call).");

            var right = view.RightDirection.Normalize();
            var up = view.UpDirection.Normalize();
            var vdir = view.ViewDirection.Normalize();
            var o = Origin(view);
            double offsetFt = RevitUnitHelper.MmToFeet(a.Offset ?? PaperOffsetMm * Math.Max(1, view.Scale));

            XYZ d;
            List<Cand> refs;
            List<Cand> faceRefs = null;   // element mode: the faces themselves, before levels/grids replaced them
            Line line;
            var notes = new List<string>();

            if (a.Levels)
            {
                if (view.ViewType != ViewType.Section && view.ViewType != ViewType.Elevation && view.ViewType != ViewType.Detail)
                    return CommandResult.Error("BAD_ARGS", "--levels needs a section or elevation as the active view (active: " + view.ViewType + ").", null);
                d = up;
                refs = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => new Cand { Ref = l.GetPlaneReference(), Pos = l.ProjectElevation - o.Z, ElementId = l.Id.IntegerValue, What = "level '" + l.Name + "'", Stable = true })
                    .Where(c => c.Ref != null).ToList();
                if (refs.Count < 2)
                    return CommandResult.Error("BAD_ARGS", "Only " + refs.Count + " level(s) are visible in '" + view.Name + "'; a level string needs two.",
                        "Check the levels' extents reach this view, or dimension elements instead ('dimension <id> <id> --dir y').");
                double u;
                // u is relative to o; the user's x is the stable "along the view" coordinate (SheetHelper.Along).
                if (a.LevelsX.HasValue) u = RevitUnitHelper.MmToFeet(a.LevelsX.Value) - SheetHelper.Along(view, o);
                else
                {
                    u = LeftEdge(view) - offsetFt;
                    notes.Add("placed " + Mm.F(RevitUnitHelper.FeetToMm(offsetFt)) + " mm left of the crop region (pass an x to move it)");
                }
                var ordered = Order(refs);
                double lo = ordered.First().Pos, hi = ordered.Last().Pos;
                line = Line.CreateBound(o + right * u + up * (lo - 1), o + right * u + up * (hi + 1));
                refs = ordered;
            }
            else if (a.Points != null)
            {
                if (!(view is ViewPlan))
                    return CommandResult.Error("BAD_ARGS", "--points works in plan views (project x,y mm). In a section dimension element ids with --dir.", null);
                var p1 = new XYZ(RevitUnitHelper.MmToFeet(a.Points[0]), RevitUnitHelper.MmToFeet(a.Points[1]), o.Z);
                var p2 = new XYZ(RevitUnitHelper.MmToFeet(a.Points[2]), RevitUnitHelper.MmToFeet(a.Points[3]), o.Z);
                d = (p2 - p1).Normalize();
                var c1 = Nearest(doc, view, p1, d, o);
                var c2 = Nearest(doc, view, p2, d, o);
                if (c1 == null || c2 == null)
                {
                    var which = c1 == null ? a.Points[0] + "," + a.Points[1] : a.Points[2] + "," + a.Points[3];
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "No dimensionable reference near (" + which + ") — nothing within " + SnapMm +
                        " mm that is perpendicular to the dimension direction.",
                        "Put the point on a wall face or grid line, or dimension element ids instead.");
                }
                if (c1.Ref.ConvertToStableRepresentation(doc) == c2.Ref.ConvertToStableRepresentation(doc))
                    return CommandResult.Error("BAD_ARGS", "Both points snap to the same reference (" + c1.What + ").", null);
                refs = new List<Cand> { c1, c2 };
                line = Line.CreateBound(p1, p2);
            }
            else
            {
                var elems = new List<Element>();
                foreach (var id in a.Ids)
                {
                    var e = doc.GetElement(new ElementId(id));
                    if (e == null) return CommandResult.Error("ELEMENT_NOT_FOUND", "Element " + id + " not found.", null);
                    elems.Add(e);
                }
                d = Direction(a.Dir, elems, right, up, vdir, view);
                refs = new List<Cand>();
                faceRefs = new List<Cand>();
                var datums = DatumsInView(doc, view, d, o);
                foreach (var e in elems)
                {
                    var found = References(view, e, d, vdir, o);
                    if (found.Count == 0)
                        notes.Add("element " + e.Id.IntegerValue + " (" + (e.Category?.Name ?? e.GetType().Name) + ") has no face/plane perpendicular to the dimension direction in this view — skipped");
                    faceRefs.AddRange(found);
                    refs.AddRange(PreferDatums(doc, found, datums));
                }
                refs = Order(refs);
                faceRefs = Order(faceRefs);
                if (refs.Count < 2)
                    return CommandResult.Error("BAD_ARGS", "Found " + refs.Count + " usable reference(s); a dimension needs two." +
                        (notes.Count > 0 ? " " + string.Join("; ", notes) : ""),
                        "Try the other direction (--dir x / --dir y), add a second element, or check the elements are visible in this view.");

                // Dimension line beside the elements: past their far side across the dimension direction.
                // Datums (levels/grids/reference planes) run across the whole view, so they only
                // count when nothing else was given — otherwise the line would land at the view's edge.
                var perp = vdir.CrossProduct(d).Normalize();
                var solids = elems.Where(e => !(e is Level) && !(e is Grid) && !(e is ReferencePlane)).ToList();
                double far = double.MinValue;
                foreach (var e in solids.Count > 0 ? solids : elems)
                {
                    var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                    if (bb == null) continue;
                    foreach (var corner in SheetHelper.Corners(bb.Min, bb.Max)) far = Math.Max(far, (corner - o).DotProduct(perp));
                }
                if (far == double.MinValue) far = 0;
                double q = far + offsetFt;
                line = Line.CreateBound(o + d * (refs.First().Pos - 1) + perp * q, o + d * (refs.Last().Pos + 1) + perp * q);
            }

            var error = Create(doc, view, line, refs, out var dim);
            if (error != null && faceRefs != null && faceRefs.Count >= 2 && refs.Any(c => c.Substituted))
            {
                // Revit refused a level/grid standing in for a face (e.g. its extent does not reach
                // here): dimension the faces themselves, as before.
                var retry = Create(doc, view, line, faceRefs, out dim);
                if (retry == null)
                {
                    notes.Add("dimensioned to the element faces: Revit refused the level/grid at the same position (" + error.Replace("ERROR: ", "") + ")");
                    refs = faceRefs;
                    error = null;
                }
            }
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR",
                    "Usually: the references are not parallel, or not perpendicular to the view plane in '" + view.Name +
                    "'. Try --dir x / --dir y, or dimension other elements.");

            var values = new List<double>();
            if (dim.NumberOfSegments > 1)
                foreach (DimensionSegment s in dim.Segments) values.Add(RevitUnitHelper.FeetToMm(s.Value ?? 0));
            else values.Add(RevitUnitHelper.FeetToMm(dim.Value ?? 0));

            var sb = new StringBuilder();
            sb.AppendLine("DIMENSION CREATED");
            sb.AppendLine("=================");
            sb.AppendLine();
            sb.AppendLine("ID: " + dim.Id.IntegerValue);
            sb.AppendLine("View: " + view.Name + " (1:" + view.Scale + ")");
            sb.AppendLine("Direction: " + DirWord(d, right, up));
            sb.AppendLine("Segments (" + values.Count + "): " + DimensionMath.Segments(values));
            sb.AppendLine("References:");
            foreach (var c in refs) sb.AppendLine("  " + c.ElementId + " " + c.What + (a.Points != null ? " (" + Mm.F(RevitUnitHelper.FeetToMm(c.Dist)) + " mm from the point)" : ""));
            var attached = refs.Select(c => Attached(doc, c)).ToList();
            sb.AppendLine("Attached to:");
            foreach (var t in attached) sb.AppendLine("  " + t.Text);
            var fragile = refs.Where((c, i) => !c.Stable && attached[i].Fragile).Select(c => c.FaceOf != 0 ? c.FaceOf : c.ElementId).Distinct().ToList();
            if (fragile.Count > 0)
                notes.Add("references element faces of " + string.Join(", ", fragile) + " — a replacement re-hosts this dimension when the new faces lie within " +
                          Mm.F1(AnnotationMatch.RehostToleranceMm) + " mm, else removes it (the replacing command's response says so)");
            foreach (var n in notes) sb.AppendLine("Note: " + n);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { dim.Id.IntegerValue } },
                { "view", view.Id.IntegerValue },
                { "segmentsMm", values.Select(v => (object)Math.Round(v, 1)).ToList() },
                { "references", refs.Select((c, i) => (object)new Dictionary<string, object>
                    { { "element", c.ElementId }, { "what", c.What }, { "stable", c.Stable }, { "vibeModelCreated", attached[i].VibeModelCreated } }).ToList() },
                { "notes", notes.Cast<object>().ToList() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static string Create(Document doc, View view, Line line, List<Cand> refs, out Dimension dim)
        {
            var ra = new ReferenceArray();
            foreach (var c in refs) ra.Append(c.Ref);
            Dimension created = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Dimension", () =>
            {
                created = doc.Create.NewDimension(view, line, ra);
                if (created == null) throw new InvalidOperationException("Revit returned no dimension.");
            });
            dim = created;
            return error;
        }

        // ---------------------------------------------------------------- stable references

        /// <summary>The levels (vertical dimension), grids and reference planes (horizontal) the view shows, as dimension candidates.</summary>
        private static List<Cand> DatumsInView(Document doc, View view, XYZ d, XYZ o)
        {
            var datums = new List<Cand>();
            try
            {
                var classes = new ElementMulticlassFilter(new List<Type> { typeof(Level), typeof(Grid), typeof(ReferencePlane) });
                foreach (var e in new FilteredElementCollector(doc, view.Id).WherePasses(classes))
                {
                    if (e is Level l && Math.Abs(d.Z) > 0.99 && l.GetPlaneReference() != null)
                        datums.Add(new Cand { Ref = l.GetPlaneReference(), Pos = (l.ProjectElevation - o.Z) * Math.Sign(d.Z), ElementId = l.Id.IntegerValue, What = "level '" + l.Name + "'", Stable = true });
                    else if (e is Grid g && g.Curve is Line gl && Math.Abs(gl.Direction.DotProduct(d)) < 0.01 && Math.Abs(d.Z) < 0.01)
                        datums.Add(new Cand { Ref = new Reference(g), Pos = (gl.Origin - o).DotProduct(d), ElementId = g.Id.IntegerValue, What = "grid '" + g.Name + "'", Stable = true });
                    else if (e is ReferencePlane rp && Math.Abs(rp.Normal.DotProduct(d)) > 0.999)
                        datums.Add(new Cand { Ref = rp.GetReference(), Pos = (rp.BubbleEnd - o).DotProduct(d), ElementId = rp.Id.IntegerValue, What = "reference plane '" + rp.Name + "'", Stable = true });
                }
            }
            catch { /* no datums: faces only */ }
            return datums;
        }

        /// <summary>
        /// An element face that lies on a level (vertical dimension) or on a grid / reference plane
        /// (horizontal) within 1 mm is dimensioned to that datum instead: same value, and it
        /// survives the element being replaced. Only datums the view shows are used.
        /// </summary>
        private static List<Cand> PreferDatums(Document doc, List<Cand> found, List<Cand> datums)
        {
            if (found.Count == 0 || datums.Count == 0 || found.All(c => c.Stable)) return found;
            double tol = RevitUnitHelper.MmToFeet(1);
            var result = new List<Cand>();
            foreach (var c in found)
            {
                var datum = c.Stable ? null : datums.Where(x => Math.Abs(x.Pos - c.Pos) <= tol).OrderBy(x => Math.Abs(x.Pos - c.Pos)).FirstOrDefault();
                if (datum == null) { result.Add(c); continue; }
                int of = c.FaceOf != 0 ? c.FaceOf : c.ElementId;
                var host = doc.GetElement(new ElementId(of));
                result.Add(new Cand
                {
                    Ref = datum.Ref, Pos = datum.Pos, ElementId = datum.ElementId, Stable = true, Substituted = true,
                    What = datum.What + " (at the " + (host?.Category?.Name ?? "element") + " face of " + of + ")"
                });
            }
            return result;
        }

        private sealed class AttachedText
        {
            public string Text;
            public bool Fragile;            // a floor / foundation / wall, or created by VibeModel this session
            public bool VibeModelCreated;
        }

        private static AttachedText Attached(Document doc, Cand c)
        {
            if (c.Stable)
            {
                var kind = c.What.StartsWith("level", StringComparison.Ordinal) ? "level"
                         : c.What.StartsWith("grid", StringComparison.Ordinal) ? "grid" : "reference plane";
                return new AttachedText { Text = "stable (" + kind + ")" };
            }
            int id = c.FaceOf != 0 ? c.FaceOf : c.ElementId;
            var e = doc.GetElement(new ElementId(id));
            bool vm = Standards.VibeModelCreated.Contains(doc, new ElementId(id));
            int bic = e?.Category?.Id.IntegerValue ?? 0;
            var what = c.What.StartsWith("centre plane", StringComparison.Ordinal) ? "element plane" : "element face";
            return new AttachedText
            {
                Text = what + " of " + id + " (" + (e?.Category?.Name ?? "element") + (vm ? ", created by VibeModel this session" : "") + ")",
                Fragile = vm || AnnotationText.LikelyReplaced(bic),
                VibeModelCreated = vm
            };
        }

        // ---------------------------------------------------------------- frame helpers

        private static XYZ Origin(View view)
        {
            if (view is ViewPlan)
            {
                double z = view.GenLevel != null ? view.GenLevel.ProjectElevation : view.Origin.Z;
                return new XYZ(0, 0, z);
            }
            return SheetHelper.SectionOrigin(view);
        }

        private static double LeftEdge(View view)
        {
            try
            {
                var cb = view.CropBox;
                var pts = SheetHelper.Corners(cb.Min, cb.Max).Select(p => cb.Transform.OfPoint(p));
                var o = SheetHelper.SectionOrigin(view);
                return pts.Min(p => (p - o).DotProduct(view.RightDirection));
            }
            catch { return 0; }
        }

        private static List<Cand> Order(List<Cand> refs)
        {
            var idx = DimensionMath.OrderDistinct(refs.Select(r => r.Pos).ToList(), Tol);
            return idx.Select(i => refs[i]).ToList();
        }

        private static string DirWord(XYZ d, XYZ right, XYZ up)
        {
            if (Math.Abs(d.DotProduct(right)) > 0.999) return "horizontal in the view";
            if (Math.Abs(d.DotProduct(up)) > 0.999) return "vertical in the view";
            return "aligned (" + Math.Round(d.X, 3) + ", " + Math.Round(d.Y, 3) + ", " + Math.Round(d.Z, 3) + ")";
        }

        /// <summary>Resolve x|y|auto into a unit direction in the view plane.</summary>
        private static XYZ Direction(DimDirection dir, List<Element> elems, XYZ right, XYZ up, XYZ vdir, View view)
        {
            if (dir == DimDirection.X) return right;
            if (dir == DimDirection.Y) return up;

            var first = elems[0];
            XYZ d = null;
            XYZ lineDir = null;
            if (first is Grid g && g.Curve is Line gl) lineDir = gl.Direction;
            else if (first is ReferencePlane rp) lineDir = (rp.FreeEnd - rp.BubbleEnd).Normalize();
            else if (first is Level) d = up;
            else if (first.Location is LocationCurve lc && lc.Curve is Line wl)
            {
                var w = InPlane(wl.Direction, vdir);
                if (w == null) d = right;                                   // seen end-on (wall in a cross-section): its thickness
                else if (elems.Count == 1) d = w;                           // one wall: its length
                else lineDir = wl.Direction;                                // several: the distance between them
            }
            if (d == null && lineDir != null)
            {
                var ld = InPlane(lineDir, vdir);
                d = ld != null ? vdir.CrossProduct(ld).Normalize() : right;
            }
            if (d == null)
            {
                var bb = first.get_BoundingBox(view) ?? first.get_BoundingBox(null);
                if (bb == null) return right;
                var corners = SheetHelper.Corners(bb.Min, bb.Max).ToList();
                double ex = corners.Max(p => p.DotProduct(right)) - corners.Min(p => p.DotProduct(right));
                double ey = corners.Max(p => p.DotProduct(up)) - corners.Min(p => p.DotProduct(up));
                d = ex >= ey ? right : up;
            }
            // Read left-to-right / bottom-to-top.
            if (d.DotProduct(right) < -1e-6 || (Math.Abs(d.DotProduct(right)) <= 1e-6 && d.DotProduct(up) < 0)) d = d.Negate();
            return d;
        }

        private static XYZ InPlane(XYZ v, XYZ vdir)
        {
            var p = v - vdir * v.DotProduct(vdir);
            return p.GetLength() < 0.1 ? null : p.Normalize();
        }

        // ---------------------------------------------------------------- references

        private static List<Cand> References(View view, Element e, XYZ d, XYZ vdir, XYZ o)
        {
            var res = new List<Cand>();
            int id = e.Id.IntegerValue;

            if (e is Grid g)
            {
                if (g.Curve is Line gl && Math.Abs(gl.Direction.DotProduct(d)) < 0.01)
                    res.Add(new Cand { Ref = new Reference(g), Pos = (gl.Origin - o).DotProduct(d), ElementId = id, What = "grid '" + g.Name + "'", Stable = true });
                return res;
            }
            if (e is Level l)
            {
                if (Math.Abs(d.Z) > 0.99 && l.GetPlaneReference() != null)
                    res.Add(new Cand { Ref = l.GetPlaneReference(), Pos = (l.ProjectElevation - o.Z) * Math.Sign(d.Z), ElementId = id, What = "level '" + l.Name + "'", Stable = true });
                return res;
            }
            if (e is ReferencePlane rp)
            {
                if (Math.Abs(rp.Normal.DotProduct(d)) > 0.999)
                    res.Add(new Cand { Ref = rp.GetReference(), Pos = (rp.BubbleEnd - o).DotProduct(d), ElementId = id, What = "reference plane '" + rp.Name + "'", Stable = true });
                return res;
            }
            if (e is FamilyInstance fi)
            {
                var t = fi.GetTransform();
                FamilyInstanceReferenceType? type = null;
                if (Math.Abs(t.BasisX.DotProduct(d)) > 0.999) type = FamilyInstanceReferenceType.CenterLeftRight;
                else if (Math.Abs(t.BasisY.DotProduct(d)) > 0.999) type = FamilyInstanceReferenceType.CenterFrontBack;
                else if (Math.Abs(t.BasisZ.DotProduct(d)) > 0.999) type = FamilyInstanceReferenceType.CenterElevation;
                if (type.HasValue)
                {
                    var r = fi.GetReferences(type.Value).FirstOrDefault();
                    if (r != null)
                    {
                        res.Add(new Cand { Ref = r, Pos = (t.Origin - o).DotProduct(d), ElementId = id, What = "centre plane (" + type.Value + ")", FaceOf = id });
                        return res;
                    }
                }
            }

            // Generic: planar faces seen edge-on whose normal runs along the dimension; keep the outer two.
            var faces = PlanarFaces(e, view).Where(f => Math.Abs(f.FaceNormal.DotProduct(d)) > 0.999 && Math.Abs(f.FaceNormal.DotProduct(vdir)) < 0.01).ToList();
            if (faces.Count == 0) return res;
            var pos = faces.Select(f => (f.Origin - o).DotProduct(d)).ToList();
            foreach (var i in DimensionMath.Extremes(pos, Tol))
                res.Add(new Cand { Ref = InstanceRef(e, faces[i].Reference), Pos = pos[i], ElementId = id, What = (e.Category?.Name ?? "element") + " face", FaceOf = id });
            return res;
        }

        /// <summary>
        /// A face of a family instance's geometry references the family symbol; dimensioned as is,
        /// the dimension hangs on the symbol (it outlives the instance and is invisible to a scan
        /// for it). The instance's own reference to the same face is used instead.
        /// </summary>
        private static Reference InstanceRef(Element e, Reference r)
            => e is FamilyInstance fi ? (AnnotationGeometry.AsInstanceReference(fi, r) ?? r) : r;

        private static IEnumerable<PlanarFace> PlanarFaces(Element e, View view)
        {
            GeometryElement ge;
            try { ge = e.get_Geometry(new Options { ComputeReferences = true, View = view }); }
            catch { ge = null; }
            if (ge == null) yield break;
            foreach (var f in Walk(ge)) yield return f;
        }

        private static IEnumerable<PlanarFace> Walk(GeometryElement ge)
        {
            foreach (var go in ge)
            {
                if (go is Solid s && s.Faces.Size > 0)
                {
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.Reference != null) yield return pf;
                }
                else if (go is GeometryInstance gi)
                {
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null) foreach (var f in Walk(inner)) yield return f;
                }
            }
        }

        /// <summary>--points: the closest face or grid within SnapMm of p that is perpendicular to d.</summary>
        private static Cand Nearest(Document doc, View view, XYZ p, XYZ d, XYZ o)
        {
            double snap = RevitUnitHelper.MmToFeet(SnapMm);
            var outline = new Outline(new XYZ(p.X - snap, p.Y - snap, -1e4), new XYZ(p.X + snap, p.Y + snap, 1e4));
            Cand best = null;
            foreach (var e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                if (e is Grid g)
                {
                    if (!(g.Curve is Line gl) || Math.Abs(gl.Direction.DotProduct(d)) > 0.01) continue;
                    var flat = new XYZ(p.X, p.Y, gl.Origin.Z);
                    double dist = gl.Distance(flat);
                    if (dist <= snap && (best == null || dist < best.Dist))
                        best = new Cand { Ref = new Reference(g), Pos = (gl.Origin - o).DotProduct(d), ElementId = e.Id.IntegerValue, What = "grid '" + g.Name + "'", Dist = dist, Stable = true };
                    continue;
                }
                if (!SheetHelper.IsModelContent(e)) continue;
                foreach (var f in PlanarFaces(e, view))
                {
                    if (Math.Abs(f.FaceNormal.DotProduct(d)) < 0.995) continue;
                    var bb = f.GetBoundingBox();
                    double zMid = f.Evaluate(new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2)).Z;
                    var r = f.Project(new XYZ(p.X, p.Y, zMid));
                    if (r == null || r.Distance > snap) continue;
                    if (best == null || r.Distance < best.Dist)
                        best = new Cand { Ref = InstanceRef(e, f.Reference), Pos = (f.Origin - o).DotProduct(d), ElementId = e.Id.IntegerValue, What = (e.Category?.Name ?? "element") + " face", Dist = r.Distance, FaceOf = e.Id.IntegerValue };
                }
            }
            return best;
        }
    }
}
