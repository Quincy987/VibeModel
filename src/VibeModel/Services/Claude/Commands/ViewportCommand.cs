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
    /// Put views on a sheet (plan 12): one view at a paper position, or several in a grid over
    /// the free part of the sheet. Every placed view is reported with its paper outline and
    /// whether it fits its cell — a view that does not fit is still placed and gets a WARN
    /// with the scale that would fit, never silently skipped.
    /// </summary>
    public class ViewportCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "viewport";
        public string Description => "Place views on a sheet: one at --at x y, or several in a --grid CxR (inside --area). Paper mm from the " +
                                     "sheet's lower-left (A0 = 1189×841). --scale n sets 1:n first. Also --move / --remove a viewport. " +
                                     "Check the result with 'sheetinfo <sheet>'";
        public string Usage => ViewportArgs.Usage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = ViewportArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            if (a.MoveId.HasValue) return Move(doc, a);
            if (a.RemoveId.HasValue) return Remove(doc, a.RemoveId.Value);

            var sheet = SheetHelper.FindSheet(doc, a.Sheet);
            if (sheet == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", "No sheet '" + a.Sheet + "'.", "Run 'list sheets', or create one with 'sheet --number <nr> --name <name>'.");

            // ---- resolve + pre-check every view before touching the document ----
            var views = new List<View>();
            var problems = new List<string>();
            foreach (var q in a.Views)
            {
                var v = SheetHelper.FindView(doc, q);
                if (v == null) { problems.Add("'" + q + "': no such view (id or exact name)"); continue; }
                if (v is ViewSheet) { problems.Add("'" + v.Name + "' is a sheet; sheets cannot go on sheets"); continue; }
                if (views.Any(x => x.Id == v.Id)) continue;
                if (!(v is ViewSchedule) && !Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                {
                    var on = SheetHelper.SheetOf(doc, v);
                    problems.Add("'" + v.Name + "' (" + v.Id.IntegerValue + ") " + (on != null
                        ? "is already on sheet " + SheetHelper.Label(on) + " — a view can be on one sheet only; duplicate it ('exec' Duplicate(ViewDuplicateOption.WithDetailing)) or 'viewport --remove' it there"
                        : "cannot be placed on a sheet (" + v.ViewType + ")"));
                    continue;
                }
                views.Add(v);
            }
            if (views.Count == 0)
                return CommandResult.Error("BAD_ARGS", "Nothing to place: " + string.Join("; ", problems), "Run 'views' for view ids and names.");

            // ---- layout ----
            var paper = SheetHelper.PaperRect(doc, sheet, out var origin, out var paperSource);
            double reserve = a.Reserve ?? SheetLayout.DefaultReserve(paper.W);
            var area = a.Area != null ? new MmRect(a.Area[0], a.Area[1], a.Area[2], a.Area[3])
                                         : SheetLayout.UsableArea(paper, a.Margin, reserve);
            List<MmRect> cells;
            int cols = a.Cols, rows = a.Rows;
            if (a.At != null && views.Count == 1)
            {
                // A single view at a point: its "cell" for the fit check is the usable area.
                cells = new List<MmRect> { area };
            }
            else
            {
                if (cols == 0)
                {
                    if (views.Count == 1) { cols = rows = 1; }
                    else SheetLayout.AutoGrid(views.Count, out cols, out rows);
                }
                cells = SheetLayout.Cells(area, cols, rows, a.Margin);
            }

            var lines = new List<string>();
            var placed = new List<object>();
            ElementType titleOffType = null;
            string titleNote = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Place Viewports", () =>
            {
                if (a.Title == false) titleOffType = NoTitleType(doc, out titleNote);
                for (int i = 0; i < views.Count; i++)
                {
                    var v = views[i];
                    var cell = cells[i];
                    double cx = a.At != null ? a.At[0] : cell.Cx, cy = a.At != null ? a.At[1] : cell.Cy;
                    string scaleNote = null;
                    if (a.Scale.HasValue && !(v is ViewSchedule) && v.Scale != a.Scale.Value)
                        scaleNote = SectionCommand.SetScale(v, a.Scale.Value);
                    try
                    {
                        if (v is ViewSchedule)
                        {
                            var ssi = ScheduleSheetInstance.Create(doc, sheet.Id, v.Id, SheetHelper.ToSheet(origin, cx, cy));
                            doc.Regenerate();
                            var r = SheetHelper.ToPaper(origin, ssi.get_BoundingBox(sheet));
                            // Schedules are placed by their top-left corner: shift the centre onto the target.
                            ElementTransformUtils.MoveElement(doc, ssi.Id, SheetHelper.ToSheet(XYZ.Zero, cx - r.Cx, cy - r.Cy));
                            doc.Regenerate();
                            r = SheetHelper.ToPaper(origin, ssi.get_BoundingBox(sheet));
                            lines.Add("OK   schedule '" + v.Name + "' → " + r + "  (id " + ssi.Id.IntegerValue + ")");
                            placed.Add(Json(ssi.Id, v, r, true, null));
                            continue;
                        }
                        var vp = Viewport.Create(doc, sheet.Id, v.Id, SheetHelper.ToSheet(origin, cx, cy));
                        if (titleOffType != null) vp.ChangeTypeId(titleOffType.Id);
                        else if (a.Title == true) EnsureTitleOn(doc, vp);
                        doc.Regenerate();
                        // Viewport.Create centres the box incl. annotation crop; re-centre on the view box.
                        vp.SetBoxCenter(SheetHelper.ToSheet(origin, cx, cy));
                        doc.Regenerate();
                        var box = SheetHelper.ToPaper(origin, vp.GetBoxOutline());
                        bool fits = SheetLayout.Fits(box.W, box.H, cell);
                        string tag = fits ? "OK  " : "WARN";
                        lines.Add(tag + " '" + v.Name + "' 1:" + v.Scale + " → " + box + "  (viewport " + vp.Id.IntegerValue + ")" +
                                  (scaleNote != null ? "  [" + scaleNote + "]" : ""));
                        if (!fits)
                        {
                            int sug = SheetLayout.SuggestScale(box.W * v.Scale, box.H * v.Scale, cell);
                            lines.Add("     " + SheetLayout.FitWarning("'" + v.Name + "'", v.Scale, box.W, box.H, cell, sug) +
                                      (v.CropBoxActive || v is View3D ? "" : " (its crop region is off, so it spans everything visible)"));
                        }
                        placed.Add(Json(vp.Id, v, box, fits, scaleNote));
                    }
                    catch (Exception ex)
                    {
                        lines.Add("ERROR '" + v.Name + "': " + ex.Message);
                        placed.Add(new Dictionary<string, object> { { "view", v.Id.IntegerValue }, { "error", ex.Message } });
                    }
                }
            });
            if (error != null) return CommandResult.FromTransactionError(error);

            int ok = placed.Count(p => p is Dictionary<string, object> d && !d.ContainsKey("error"));
            if (ok == 0)
                return CommandResult.Error("TRANSACTION_FAILED", "No view could be placed: " + string.Join(" | ", lines), null);

            var sb = new StringBuilder();
            sb.AppendLine("VIEWPORTS PLACED on " + SheetHelper.Label(sheet) + " (" + ok + "/" + views.Count + ")");
            sb.AppendLine("=".PadRight(40, '='));
            string pn = SheetLayout.PaperName(paper.W, paper.H);
            sb.AppendLine("Paper: " + Mm.F(paper.W) + "×" + Mm.F(paper.H) + " mm" + (pn != null ? " (" + pn + ")" : "") + " from the " + paperSource);
            sb.AppendLine("Layout area: " + area + (a.Area != null ? " (--area)" : " (margin " + Mm.F(a.Margin) + ", " + Mm.F(reserve) +
                          " mm kept free on the right for the stamp; --reserve/--area to change)"));
            if (a.At == null || views.Count > 1) sb.AppendLine("Grid: " + cols + "×" + rows + " (left-to-right, top-to-bottom)");
            if (titleNote != null) sb.AppendLine("Titles: " + titleNote);
            foreach (var l in lines) sb.AppendLine(l);
            foreach (var p in problems) sb.AppendLine("SKIPPED " + p);
            sb.AppendLine("Next: 'sheetinfo " + sheet.SheetNumber + "' to check overlaps; 'viewport --move <id> <x> <y>' to nudge; " +
                          "'screenshot --view " + sheet.SheetNumber + "' to look at the sheet.");

            var data = new Dictionary<string, object>
            {
                { "sheet", sheet.Id.IntegerValue },
                { "sheetNumber", sheet.SheetNumber },
                { "paperMm", new List<object> { Math.Round(paper.W), Math.Round(paper.H) } },
                { "placed", placed },
                { "skipped", problems.Cast<object>().ToList() },
                { "created", placed.OfType<Dictionary<string, object>>().Where(d => d.ContainsKey("id")).Select(d => d["id"]).ToList() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static Dictionary<string, object> Json(ElementId id, View v, MmRect r, bool fits, string scaleNote)
            => new Dictionary<string, object>
            {
                { "id", id.IntegerValue }, { "view", v.Id.IntegerValue }, { "viewName", v.Name }, { "scale", v is ViewSchedule ? 0 : v.Scale },
                { "outlineMm", new List<object> { Math.Round(r.X0), Math.Round(r.Y0), Math.Round(r.X1), Math.Round(r.Y1) } },
                { "fits", fits }, { "note", scaleNote }
            };

        /// <summary>A viewport type that shows no title: an existing one, or a duplicate of the default with Show Title off.</summary>
        private static ElementType NoTitleType(Document doc, out string note)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .Where(t => t.FamilyName == "Viewport" || t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL) != null).ToList();
            var off = types.FirstOrDefault(t => t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger() == 0);
            if (off != null) { note = "viewport type '" + off.Name + "' (shows no title)"; return off; }
            var src = types.FirstOrDefault(t => t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL) != null);
            if (src == null) { note = "no viewport type found — titles left as they are"; return null; }
            var dup = src.Duplicate(src.Name + " - geen titel") as ElementType;
            dup.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL).Set(0);
            note = "created viewport type '" + dup.Name + "' (copy of '" + src.Name + "' with Show Title off)";
            return dup;
        }

        private static void EnsureTitleOn(Document doc, Viewport vp)
        {
            var t = doc.GetElement(vp.GetTypeId()) as ElementType;
            if (t?.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger() != 0) return;
            var on = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .FirstOrDefault(x => x.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger() == 1);
            if (on != null) vp.ChangeTypeId(on.Id);
        }

        private static CommandResult Move(Document doc, ViewportArgs a)
        {
            var el = doc.GetElement(new ElementId(a.MoveId.Value));
            var vp = el as Viewport;
            var ssi = el as ScheduleSheetInstance;
            if (vp == null && ssi == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", a.MoveId + " is not a viewport or placed schedule.", "Run 'sheetinfo <sheet>' for viewport ids.");
            var sheet = doc.GetElement(vp != null ? vp.SheetId : ssi.OwnerViewId) as ViewSheet;
            SheetHelper.PaperRect(doc, sheet, out var origin, out _);
            MmRect r = default(MmRect);
            var error = TransactionHelper.Execute(doc, "VibeModel: Move Viewport", () =>
            {
                if (vp != null)
                {
                    vp.SetBoxCenter(SheetHelper.ToSheet(origin, a.MoveTo[0], a.MoveTo[1]));
                    doc.Regenerate();
                    r = SheetHelper.ToPaper(origin, vp.GetBoxOutline());
                }
                else
                {
                    var cur = SheetHelper.ToPaper(origin, ssi.get_BoundingBox(sheet));
                    ElementTransformUtils.MoveElement(doc, ssi.Id, SheetHelper.ToSheet(XYZ.Zero, a.MoveTo[0] - cur.Cx, a.MoveTo[1] - cur.Cy));
                    doc.Regenerate();
                    r = SheetHelper.ToPaper(origin, ssi.get_BoundingBox(sheet));
                }
            });
            if (error != null) return CommandResult.FromTransactionError(error);
            return CommandResult.Ok("VIEWPORT MOVED\n==============\n" + a.MoveId + " on " + SheetHelper.Label(sheet) + " → " + r + "\n",
                new Dictionary<string, object> { { "id", a.MoveId.Value }, { "outlineMm", new List<object> { Math.Round(r.X0), Math.Round(r.Y0), Math.Round(r.X1), Math.Round(r.Y1) } } });
        }

        private static CommandResult Remove(Document doc, int id)
        {
            var el = doc.GetElement(new ElementId(id));
            if (!(el is Viewport) && !(el is ScheduleSheetInstance))
                return CommandResult.Error("ELEMENT_NOT_FOUND", id + " is not a viewport or placed schedule.", "Run 'sheetinfo <sheet>' for viewport ids.");
            string what = el is Viewport vp ? "view '" + doc.GetElement(vp.ViewId)?.Name + "'" : "schedule '" + el.Name + "'";
            var error = TransactionHelper.Execute(doc, "VibeModel: Remove Viewport", () => doc.Delete(el.Id));
            if (error != null) return CommandResult.FromTransactionError(error);
            return CommandResult.Ok("VIEWPORT REMOVED\n================\n" + id + " (" + what + ") — the view itself is kept.\n",
                new Dictionary<string, object> { { "removed", id } });
        }
    }
}
