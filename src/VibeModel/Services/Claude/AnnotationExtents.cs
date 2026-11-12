using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// A view's paper frame (plan 21): model point → paper mm, x along the view's right
    /// direction, y along its up direction, origin the view's origin, divided by its scale. A
    /// sheet is its own paper (scale 1).
    /// </summary>
    internal sealed class PaperFrame
    {
        public readonly XYZ O, R, U;
        public readonly double Scale;

        public PaperFrame(View view)
        {
            O = view.Origin;
            R = view.RightDirection.Normalize();
            U = view.UpDirection.Normalize();
            Scale = view is ViewSheet ? 1 : Math.Max(1, view.Scale);
        }

        public double X(XYZ p) => RevitUnitHelper.FeetToMm((p - O).DotProduct(R)) / Scale;
        public double Y(XYZ p) => RevitUnitHelper.FeetToMm((p - O).DotProduct(U)) / Scale;

        /// <summary>A paper distance (mm) as a model length (feet) in this view.</summary>
        public double Feet(double paperMm) => RevitUnitHelper.MmToFeet(paperMm * Scale);

        /// <summary>The paper rectangle around a model box (all eight corners projected).</summary>
        public MmRect Rect(BoundingBoxXYZ bb)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            var t = bb.Transform ?? Transform.Identity;
            foreach (var c in SheetHelper.Corners(bb.Min, bb.Max))
            {
                var q = t.OfPoint(c);
                double x = X(q), y = Y(q);
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
            return new MmRect(x0, y0, x1, y1);
        }

        /// <summary>The angle (degrees) of a model direction on paper.</summary>
        public double Angle(XYZ dir) => Math.Atan2(dir.DotProduct(U), dir.DotProduct(R)) * 180 / Math.PI;
    }

    /// <summary>
    /// The Revit side of plan 21's layout check: the paper boxes of a view's annotations, and
    /// the `-- layout:` line after a command. Extents follow what was measured (plan 21): a text
    /// note's bounding box; a dimension's text per segment estimated around its TextPosition (its
    /// own box spans the whole string and the witness lines); a spot's text estimated around its
    /// TextPosition plus its leader; a tag's head estimated around TagHeadPosition from TagText.
    /// </summary>
    internal static class AnnotationExtents
    {
        /// <summary>Above this many annotations in one view the check is skipped (logged once).</summary>
        public const int Cap = 2000;
        private const double DefaultSizeMm = 2.5;
        private static bool _capLogged;

        public static bool IsAnnotation(Element e) => e is TextNote || e is IndependentTag || e is Dimension;

        /// <summary>The annotation elements owned by the view (text notes, tags, dimensions incl. spots).</summary>
        public static List<Element> AnnotationsIn(Document doc, View view)
        {
            var filter = new ElementMulticlassFilter(new List<Type> { typeof(TextNote), typeof(IndependentTag), typeof(Dimension) });
            return new FilteredElementCollector(doc, view.Id).WherePasses(filter).WhereElementIsNotElementType()
                .Where(e => e.OwnerViewId == view.Id).ToList();
        }

        /// <summary>
        /// Every box of the view's annotations. <paramref name="total"/> = how many annotations
        /// the view holds; past <see cref="Cap"/> only the first <see cref="Cap"/> are measured.
        /// </summary>
        public static List<AnnoBox> Collect(Document doc, View view, out int total, IEnumerable<Element> only = null)
        {
            var frame = new PaperFrame(view);
            var elems = only != null ? only.ToList() : AnnotationsIn(doc, view);
            total = elems.Count;
            var boxes = new List<AnnoBox>();
            foreach (var e in elems.Take(Cap))
            {
                try { boxes.AddRange(BoxesOf(doc, view, frame, e)); }
                catch (Exception ex) { Logger.Info("annolayout: skipped " + e.Id.IntegerValue + " (" + ex.Message + ")"); }
            }
            return boxes;
        }

        /// <summary>The boxes of one annotation in this view's paper frame.</summary>
        public static List<AnnoBox> BoxesOf(Document doc, View view, PaperFrame f, Element e)
        {
            var list = new List<AnnoBox>();
            int id = e.Id.IntegerValue;
            if (e is TextNote tn)
            {
                var bb = tn.get_BoundingBox(view);
                var label = Short(tn.Text);
                if (bb != null)
                    list.Add(new AnnoBox { Id = id, Kind = AnnoKind.Text, Rect = f.Rect(bb), AngleDeg = f.Angle(tn.BaseDirection), Label = label });
                foreach (var leader in Leaders(tn))
                    AddPolyline(list, id, "text", f, leader);
                return list;
            }
            if (e is SpotDimension sd)
            {
                var type = doc.GetElement(sd.GetTypeId());
                double size = SizeMm(type);
                double wf = WidthFactor(type);
                double value = AnnotationGuard.SpotValueMm(sd);
                string text = SpotText(sd, value);
                var origin = Try(() => sd.Origin);
                var shoulder = Try(() => sd.LeaderHasShoulder ? sd.LeaderShoulderPosition : null);
                var end = Try(() => sd.LeaderEndPosition);
                var tp = Try(() => sd.TextPosition) ?? end;
                if (tp != null)
                    list.Add(new AnnoBox { Id = id, Kind = AnnoKind.SpotText, Rect = AnnotationLayout.TextBox(f.X(tp), f.Y(tp), text, size, wf), Label = text, Host = HostOf(sd) });
                if (origin != null && end != null)
                    AddPolyline(list, id, "spot", f, shoulder != null ? new[] { origin, shoulder, end } : new[] { origin, end });
                return list;
            }
            if (e is Dimension d)
            {
                var type = doc.GetElement(d.GetTypeId());
                double size = SizeMm(type);
                double wf = WidthFactor(type);
                var line = d.Curve as Line;
                if (line == null)
                {
                    // Angular / radial / arc length: only its text.
                    var tp = Try(() => d.TextPosition);
                    if (tp != null) list.Add(new AnnoBox { Id = id, Kind = AnnoKind.DimensionText, Rect = AnnotationLayout.TextBox(f.X(tp), f.Y(tp), d.ValueString, size, wf), Label = d.ValueString });
                    return list;
                }
                var dir = line.Direction.Normalize();
                bool alongX = Math.Abs(dir.DotProduct(f.R)) >= Math.Abs(dir.DotProduct(f.U));
                XYZ first = null, last = null;
                if (d.NumberOfSegments > 1)
                {
                    foreach (DimensionSegment s in d.Segments)
                    {
                        double half = (s.Value ?? 0) / 2;
                        var a = s.Origin - dir * half; var b = s.Origin + dir * half;
                        if (first == null) first = a;
                        last = b;
                        var tp = Try(() => s.TextPosition) ?? s.Origin;
                        list.Add(new AnnoBox { Id = id, Kind = AnnoKind.DimensionText, Rect = AnnotationLayout.TextBox(f.X(tp), f.Y(tp), s.ValueString, size, wf, vertical: !alongX), Label = s.ValueString });
                    }
                }
                else
                {
                    double half = (d.Value ?? 0) / 2;
                    first = d.Origin - dir * half; last = d.Origin + dir * half;
                    var tp = Try(() => d.TextPosition) ?? d.Origin;
                    list.Add(new AnnoBox { Id = id, Kind = AnnoKind.DimensionText, Rect = AnnotationLayout.TextBox(f.X(tp), f.Y(tp), d.ValueString, size, wf, vertical: !alongX), Label = d.ValueString });
                }
                if (first != null && last != null)
                    list.Add(new AnnoBox { Id = id, Kind = AnnoKind.DimensionLine, Rect = AnnotationLayout.Strip(f.X(first), f.Y(first), f.X(last), f.Y(last)), AlongX = alongX });
                return list;
            }
            if (e is IndependentTag tag)
            {
                var head = Try(() => tag.TagHeadPosition);
                string text = LongestLine(Try(() => tag.TagText));
                double angle = tag.TagOrientation == TagOrientation.Vertical ? 90
                             : tag.TagOrientation == TagOrientation.AnyModelDirection ? (Try(() => (double?)tag.RotationAngle) ?? 0) * 180 / Math.PI : 0;
                int host = tag.GetTaggedLocalElementIds().Where(i => i != ElementId.InvalidElementId).Select(i => i.IntegerValue).FirstOrDefault();
                MmRect rect;
                AnnotationLayout.TextExtent(Math.Max(1, text.Length), DefaultSizeMm, 1, out var w, out var h);
                var bb = tag.HasLeader ? null : tag.get_BoundingBox(view);
                if (bb != null) rect = f.Rect(bb);
                else if (head != null) rect = new MmRect(f.X(head) - w / 2, f.Y(head) - h / 2, f.X(head) + w / 2, f.Y(head) + h / 2);
                else return list;
                list.Add(new AnnoBox { Id = id, Kind = AnnoKind.Tag, Rect = rect, AngleDeg = angle, Label = text, Host = host });
                if (tag.HasLeader && head != null)
                    foreach (var r in Try(() => tag.GetTaggedReferences()) ?? new List<Reference>())
                    {
                        var elbow = Try(() => tag.HasLeaderElbow(r) ? tag.GetLeaderElbow(r) : null);
                        var end = Try(() => tag.GetLeaderEnd(r));
                        if (end == null) continue;   // an attached end Revit does not report: nothing to draw
                        AddPolyline(list, id, "tag", f, elbow != null ? new[] { head, elbow, end } : new[] { head, end });
                    }
                return list;
            }
            return list;
        }

        /// <summary>
        /// The boxes other annotations in the view already occupy — what a new spot or tag must
        /// keep clear of. Texts, tags and dimension texts; lines and leaders too when asked.
        /// </summary>
        public static List<MmRect> Occupied(Document doc, View view, bool withLines = true)
        {
            var boxes = Collect(doc, view, out _);
            return boxes.Where(b => b.IsText || withLines && b.IsLine).Select(b => b.Rect).ToList();
        }

        /// <summary>The annotation boxes with their owners — for "taken by spot 1730946".</summary>
        public static List<AnnoBox> OccupiedBoxes(Document doc, View view)
            => Collect(doc, view, out _);

        /// <summary>Spot elevation text as Revit prints it by default: signed metres, 3 decimals ("+2.500").</summary>
        public static string SpotText(SpotDimension sd, double valueMm)
        {
            var s = Try(() => sd.ValueString);
            if (!string.IsNullOrWhiteSpace(s)) return s;
            if (double.IsNaN(valueMm)) return "+0.000";
            double m = valueMm / 1000;
            return (m >= 0 ? "+" : "") + m.ToString("0.000", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ -- layout: line

        /// <summary>The layout report after a command: the line and the additive JSON object.</summary>
        internal sealed class LayoutReport
        {
            public string Line;
            public Dictionary<string, object> Json;
        }

        /// <summary>
        /// The `-- layout:` line for the annotations a command added (null when none was added or
        /// they all sit clear): per view, the new ones against everything in that view. Never
        /// throws — a report never fails the response it belongs to.
        /// </summary>
        public static LayoutReport ForAdded(Document doc, IEnumerable<ElementId> added)
        {
            if (doc == null || added == null) return null;
            try
            {
                var news = added.Select(i => doc.GetElement(i)).Where(e => e != null && IsAnnotation(e) && e.OwnerViewId != ElementId.InvalidElementId).ToList();
                if (news.Count == 0) return null;
                var lines = new List<string>();
                var views = new List<object>();
                foreach (var g in news.GroupBy(e => e.OwnerViewId.IntegerValue).Take(5))
                {
                    var view = doc.GetElement(new ElementId(g.Key)) as View;
                    if (view == null || view is View3D) continue;
                    var all = AnnotationsIn(doc, view);
                    if (all.Count > Cap)
                    {
                        if (!_capLogged) { Logger.Info("layout check skipped: view '" + view.Name + "' holds " + all.Count + " annotations (cap " + Cap + ")"); _capLogged = true; }
                        continue;
                    }
                    var boxes = Collect(doc, view, out _, all);
                    var ids = g.Select(e => e.Id.IntegerValue).ToList();
                    var overlaps = AnnotationLayout.Overlaps(boxes).Where(o => o.IsWarn && ids.Any(o.Involves)).ToList();
                    var line = AnnotationLayout.LayoutLine(ids, overlaps, view.Name);
                    if (line == null) continue;
                    lines.Add(line);
                    views.Add(new Dictionary<string, object>
                    {
                        { "view", view.Id.IntegerValue }, { "viewName", view.Name }, { "overlaps", AnnotationLayout.OverlapsJson(overlaps) }
                    });
                }
                var joined = AnnotationLayout.JoinLines(lines);
                if (joined == null) return null;
                return new LayoutReport { Line = joined, Json = new Dictionary<string, object> { { "views", views } } };
            }
            catch (Exception ex)
            {
                Logger.Warn("Layout line skipped: " + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<XYZ[]> Leaders(TextNote tn)
        {
            IList<Leader> leaders;
            try { leaders = tn.GetLeaders(); } catch { yield break; }
            foreach (var l in leaders)
            {
                var anchor = Try(() => l.Anchor);
                var elbow = Try(() => l.Elbow);
                var end = Try(() => l.End);
                if (anchor == null || end == null) continue;
                yield return elbow != null ? new[] { anchor, elbow, end } : new[] { anchor, end };
            }
        }

        private static void AddPolyline(List<AnnoBox> list, int id, string owner, PaperFrame f, XYZ[] pts)
        {
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                double x0 = f.X(pts[i]), y0 = f.Y(pts[i]), x1 = f.X(pts[i + 1]), y1 = f.Y(pts[i + 1]);
                if (Math.Abs(x1 - x0) < 0.05 && Math.Abs(y1 - y0) < 0.05) continue;
                list.Add(new AnnoBox { Id = id, Kind = AnnoKind.Leader, LeaderOf = owner, Rect = AnnotationLayout.Strip(x0, y0, x1, y1), AlongX = Math.Abs(x1 - x0) >= Math.Abs(y1 - y0) });
            }
        }

        private static int HostOf(SpotDimension sd)
        {
            try
            {
                var refs = sd.References;
                if (refs != null && refs.Size > 0) return refs.get_Item(0).ElementId.IntegerValue;
            }
            catch { }
            return 0;
        }

        internal static double SizeMm(Element type)
        {
            var p = type?.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return p != null && p.HasValue && p.AsDouble() > 0 ? RevitUnitHelper.FeetToMm(p.AsDouble()) : DefaultSizeMm;
        }

        internal static double WidthFactor(Element type)
        {
            var p = type?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE);
            return p != null && p.HasValue && p.AsDouble() > 0 ? p.AsDouble() : 1;
        }

        private static string LongestLine(string s)
            => (s ?? "").Replace("\r", "\n").Split('\n').OrderByDescending(l => l.Length).FirstOrDefault() ?? "";

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 30 ? s.Substring(0, 27) + "..." : s;
        }

        private static T Try<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        private static double? Try(Func<double?> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
