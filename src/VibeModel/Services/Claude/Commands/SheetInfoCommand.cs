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
    /// Read back a sheet (plan 12): paper size, title block and its stamp fields, every
    /// viewport/schedule with its paper outline, the annotation on the sheet, and the checks
    /// that matter when assembling a sheet — overlapping viewports and anything off the paper.
    /// The sensor that verifies `viewport` without looking at the Revit UI.
    /// </summary>
    public class SheetInfoCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "sheetinfo";
        public string Description => "Inspect a sheet: paper size, title block + stamp fields, viewports with paper outlines (mm from lower-left), " +
                                     "text/schedules on it, and overlap / off-paper checks. Without args: the active sheet";
        public string Usage => "sheetinfo [sheetNumber|sheetId]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var q = (args ?? "").Trim();
            ViewSheet sheet = q.Length > 0 ? SheetHelper.FindSheet(doc, q) : uidoc.ActiveView as ViewSheet;
            if (sheet == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", q.Length > 0 ? "No sheet '" + q + "'." : "The active view is not a sheet; give a sheet number.",
                    "Run 'list sheets'.");

            var paper = SheetHelper.PaperRect(doc, sheet, out var origin, out var paperSource);
            var tb = SheetHelper.TitleBlock(doc, sheet);
            var tbType = tb != null ? doc.GetElement(tb.GetTypeId()) as FamilySymbol : null;

            var sb = new StringBuilder();
            sb.AppendLine("SHEET " + SheetHelper.Label(sheet) + " (id " + sheet.Id.IntegerValue + ")");
            sb.AppendLine("=".PadRight(40, '='));
            string pn = SheetLayout.PaperName(paper.W, paper.H);
            sb.AppendLine("Paper: " + Mm.F(paper.W) + "×" + Mm.F(paper.H) + " mm" + (pn != null ? " (" + pn + ")" : "") + " — " + paperSource);
            sb.AppendLine("Title block: " + (tbType != null ? tbType.FamilyName + " : " + tbType.Name + " (instance " + tb.Id.IntegerValue + ")" : "none"));

            // Stamp fields: the office pack's patterns (NL_META_*, X_1..5), else the title block's own text fields.
            var stamp = new List<object>();
            var pack = Standards.StandardsContext.GetActivePack(doc);
            var fields = new Dictionary<string, Parameter>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in new Element[] { sheet, tb })
                if (owner != null)
                    foreach (Parameter p in owner.Parameters)
                        if (p.StorageType == StorageType.String && !p.IsReadOnly && !fields.ContainsKey(p.Definition.Name))
                            fields[p.Definition.Name] = p;
            bool fromPack = pack?.Sheets?.StampParameters != null && pack.Sheets.StampParameters.Count > 0;
            List<string> unmatched;
            var names = fromPack ? StampPatterns.Expand(pack.Sheets.StampParameters, fields.Keys, out unmatched)
                                 : StampPatterns.Expand(tb != null ? tb.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.String && !p.IsReadOnly)
                                                                       .Select(p => p.Definition.Name) : new string[0], fields.Keys, out unmatched);
            if (names.Count > 0 || unmatched.Count > 0)
            {
                sb.AppendLine("Stamp" + (fromPack ? " (office " + pack.Id + ": " + string.Join(", ", pack.Sheets.StampParameters) + ")" : " (title block text fields)") + ":");
                var empty = new List<string>();
                foreach (var n in names)
                {
                    var val = fields[n].HasValue ? fields[n].AsString() : null;   // stamp fields are text; "(no value)" must count as empty
                    bool blank = StampPatterns.IsBlank(val);
                    if (blank) empty.Add(n); else sb.AppendLine("  " + n + ": " + val);
                    stamp.Add(new Dictionary<string, object> { { "name", n }, { "value", blank ? null : val } });
                }
                if (empty.Count > 0)
                    sb.AppendLine("  empty (" + empty.Count + "): " + string.Join(", ", empty.Take(12)) + (empty.Count > 12 ? ", …" : ""));
                foreach (var u in unmatched) sb.AppendLine("  " + u + ": no such field on this sheet or its title block");
                sb.AppendLine("  fill with 'set " + sheet.Id.IntegerValue + " <field> <value>' or at creation 'sheet --set \"Field=Value\"'");
            }

            // Viewports + schedules.
            var rects = new List<KeyValuePair<string, MmRect>>();
            var vps = new List<object>();
            sb.AppendLine();
            var viewports = sheet.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>().ToList();
            sb.AppendLine("VIEWPORTS (" + viewports.Count + ")");
            foreach (var vp in viewports)
            {
                var v = doc.GetElement(vp.ViewId) as View;
                var r = SheetHelper.ToPaper(origin, vp.GetBoxOutline());
                var type = doc.GetElement(vp.GetTypeId()) as ElementType;
                var showLabel = type?.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
                bool title = showLabel == null || showLabel.AsInteger() != 0;
                string label = "'" + (v?.Name ?? "?") + "'";
                rects.Add(new KeyValuePair<string, MmRect>(label, r));
                string scale = v == null ? "?" : v.Scale > 0 ? "1:" + v.Scale : "perspective";
                sb.AppendLine("  " + vp.Id.IntegerValue + "  " + label + " [" + v?.ViewType + "] " + scale + "  " + r +
                              (title ? "" : "  (no title)") + (v != null && !(v is View3D) && SheetHelper.HasCropRegion(v) && !v.CropBoxActive ? "  (crop off)" : ""));
                vps.Add(new Dictionary<string, object>
                {
                    { "id", vp.Id.IntegerValue }, { "view", vp.ViewId.IntegerValue }, { "viewName", v?.Name }, { "viewType", v?.ViewType.ToString() },
                    { "scale", v?.Scale }, { "title", title }, { "outlineMm", r.ToJson() }
                });
            }
            var schedules = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule).ToList();
            foreach (var s in schedules)
            {
                var bb = s.get_BoundingBox(sheet);
                if (bb == null) continue;
                var r = SheetHelper.ToPaper(origin, bb);
                rects.Add(new KeyValuePair<string, MmRect>("schedule '" + s.Name + "'", r));
                sb.AppendLine("  " + s.Id.IntegerValue + "  schedule '" + s.Name + "'  " + r);
                vps.Add(new Dictionary<string, object> { { "id", s.Id.IntegerValue }, { "schedule", s.Name }, { "outlineMm", r.ToJson() } });
            }

            // Annotation directly on the sheet.
            var texts = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
            if (texts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("TEXT ON SHEET (" + texts.Count + ")");
                foreach (var t in texts.Take(30))
                {
                    var c = t.Coord;
                    sb.AppendLine("  " + t.Id.IntegerValue + "  at " + Mm.F(RevitUnitHelper.FeetToMm(c.X - origin.X)) + ", " + Mm.F(RevitUnitHelper.FeetToMm(c.Y - origin.Y)) +
                                  "  \"" + Short(t.Text) + "\"");
                }
            }

            // Checks.
            var overlaps = SheetLayout.Overlaps(rects);
            var overlapLines = SheetLayout.OverlapLines(rects);
            var off = SheetLayout.OffPaper(rects, paper);
            var offLines = SheetLayout.OffPaperLines(rects, paper);
            sb.AppendLine();
            sb.AppendLine("CHECKS");
            if (rects.Count == 0) sb.AppendLine("  INFO  no views on this sheet yet ('viewport " + sheet.SheetNumber + " <view>')");
            foreach (var l in overlapLines) sb.AppendLine("  " + l);
            foreach (var l in offLines) sb.AppendLine("  " + l);
            if (rects.Count > 0 && !overlapLines.Any(l => l.StartsWith("WARN")) && off.Count == 0) sb.AppendLine("  OK    no conflicting overlaps, everything on the paper");

            var data = new Dictionary<string, object>
            {
                { "id", sheet.Id.IntegerValue }, { "number", sheet.SheetNumber }, { "name", sheet.Name },
                { "paperMm", new List<object> { Math.Round(paper.W), Math.Round(paper.H) } }, { "paper", pn },
                { "titleBlock", tbType != null ? tbType.FamilyName + " : " + tbType.Name : null },
                { "stamp", stamp }, { "viewports", vps },
                { "texts", texts.Select(t => (object)new Dictionary<string, object> { { "id", t.Id.IntegerValue }, { "text", t.Text } }).ToList() },
                { "overlaps", overlaps.Select(o => (object)new List<object> { o.Item1, o.Item2 }).ToList() },
                { "offPaper", off.Cast<object>().ToList() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 60 ? s.Substring(0, 57) + "..." : s;
        }
    }
}
