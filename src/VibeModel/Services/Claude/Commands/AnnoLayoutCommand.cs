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
    /// Read back where a view's annotations sit on paper and which collide (plan 21) — the
    /// sensor for tag / dimension / spot / text placement, as `sheetinfo` is for viewports.
    /// Paper mm in the view's own frame: x along its right direction, y up, from the view origin.
    /// </summary>
    public class AnnoLayoutCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "annolayout";
        public string Description => "Annotation layout of a view (default: the active view; a sheet: every view on it): the paper box (mm) of every " +
                                     "text, tag, dimension text/line and spot text/leader, then overlaps (WARN/INFO with ids), stacked dimension lines and rotated text";
        public string Usage => "annolayout [view name|view id|sheet number]";

        private const int ListMax = 60;
        /// <summary>JSON carries at most this many boxes per view (the checks still cover all of them).</summary>
        private const int JsonMax = 500;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var q = (args ?? "").Trim();
            View view = q.Length > 0 ? SheetHelper.FindView(doc, q) : FormattingHelper.GetActiveGraphicalView(uidoc);
            if (view == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", q.Length > 0 ? "No view or sheet '" + q + "'." : "No active graphical view.",
                    "Give a view name or id ('views'), or a sheet number ('list sheets').");
            if (view is View3D)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' is a 3D view; annotation layout is read in plans, sections, elevations, drafting views and sheets.", null);

            var views = new List<View>();
            if (view is ViewSheet sheet)
            {
                views.Add(sheet);
                foreach (var vid in sheet.GetAllPlacedViews())
                    if (doc.GetElement(vid) is View v && !(v is View3D) && !(v is ViewSchedule)) views.Add(v);
            }
            else views.Add(view);

            var sb = new StringBuilder();
            var data = new List<object>();
            int warns = 0, infos = 0, rotatedCount = 0, annotationCount = 0;
            string worst = null;
            foreach (var v in views)
            {
                List<AnnoBox> boxes;
                int total;
                try { boxes = AnnotationExtents.Collect(doc, v, out total); }
                catch (Exception ex)
                {
                    // One view Revit will not read (a legend, a broken view) does not sink the sheet's report.
                    sb.AppendLine("ANNOTATION LAYOUT — '" + v.Name + "': not readable (" + ex.Message + ")");
                    sb.AppendLine();
                    continue;
                }
                if (total == 0 && views.Count > 1) continue;
                annotationCount += total;
                var overlaps = AnnotationLayout.Overlaps(boxes);
                var rotated = AnnotationLayout.Rotated(boxes);
                warns += overlaps.Count(o => o.IsWarn) + rotated.Count(AnnotationLayout.IsSkewed);
                infos += overlaps.Count(o => !o.IsWarn) + rotated.Count(r => !AnnotationLayout.IsSkewed(r));
                rotatedCount += rotated.Count;
                if (worst == null && overlaps.Any(o => o.IsWarn)) worst = AnnotationLayout.Line(overlaps.First(o => o.IsWarn)).Substring(6);

                string scale = v is ViewSheet ? "paper" : "1:" + v.Scale;
                sb.AppendLine("ANNOTATION LAYOUT — " + (v is ViewSheet s2 ? "sheet " + SheetHelper.Label(s2) : "'" + v.Name + "' (" + v.ViewType + ", " + scale + ")") +
                              ", " + total + " annotation(s)");
                sb.AppendLine("Paper mm in the view frame: x along the view's right direction, y up, from the view origin.");
                var shown = boxes.Take(ListMax).ToList();
                foreach (var b in shown)
                    sb.AppendLine("  " + b.Name.PadRight(28) + " " + b.Rect + (b.Host != 0 ? "  on " + b.Host : "") +
                                  (rotated.Contains(b) ? "  rotated " + Mm.F1(AnnotationLayout.Normalize(b.AngleDeg)) + "°" : ""));
                if (boxes.Count > ListMax) sb.AppendLine("  " + Truncation.Line(ListMax, boxes.Count, "the checks below cover every box"));
                if (total > AnnotationExtents.Cap) sb.AppendLine("  " + Truncation.Line(AnnotationExtents.Cap, total, "only the first " + AnnotationExtents.Cap + " annotations were measured"));
                sb.AppendLine("CHECKS");
                foreach (var o in overlaps) sb.AppendLine("  " + AnnotationLayout.Line(o));
                foreach (var r in rotated) sb.AppendLine("  " + AnnotationLayout.RotatedLine(r));
                if (worst == null && rotated.Any(AnnotationLayout.IsSkewed)) worst = AnnotationLayout.RotatedLine(rotated.First(AnnotationLayout.IsSkewed)).Substring(6);
                if (overlaps.Count == 0 && rotated.Count == 0) sb.AppendLine("  OK    no overlapping annotations" + (total == 0 ? " (none in this view)" : ""));
                sb.AppendLine();

                data.Add(new Dictionary<string, object>
                {
                    { "view", v.Id.IntegerValue }, { "viewName", v.Name }, { "scale", v is ViewSheet ? 1 : v.Scale }, { "count", total },
                    { "annotations", boxes.Take(JsonMax).Select(b => (object)new Dictionary<string, object>
                        { { "id", b.Id }, { "part", b.Kind.ToString() }, { "rectMm", b.Rect.ToJson() }, { "host", b.Host }, { "label", b.Label } }).ToList() },
                    { "annotationsTruncated", boxes.Count > JsonMax },
                    { "overlaps", AnnotationLayout.OverlapsJson(overlaps) },
                    { "rotated", rotated.Select(r => (object)r.Id).Distinct().ToList() }
                });
            }

            string verdict = warns > 0
                ? warns + " conflict(s) to fix" + (worst != null ? " — first: " + worst : "") + ". Replace the misplaced annotation: 'delete <id>', then place it again ('tag … --at/--elbow', 'spot … --leader x y', 'dimension … --offset', 'text' for skewed text), then run annolayout again."
                : annotationCount == 0 ? "no annotations here yet."
                : "readable: no annotations overlap" + (infos > 0 ? " (" + infos + " INFO: touching strips or vertical text)" : "") + ".";
            sb.AppendLine("Verdict: " + verdict);

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "view", view.Id.IntegerValue }, { "viewName", view.Name }, { "views", data },
                { "warnings", warns }, { "info", infos }, { "rotatedCount", rotatedCount }, { "verdict", verdict }
            });
        }
    }
}
