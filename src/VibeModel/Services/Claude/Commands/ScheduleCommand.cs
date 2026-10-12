using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// A quantity schedule (hoeveelhedenstaat) as a real Revit schedule: fields by name
    /// (built-in, project and shared parameters as Revit lists them), an optional filter, one
    /// row per group with totals, and the first rows read back as text with the units in the
    /// headers — so the chat can check the numbers without opening the schedule.
    /// `schedule &lt;name|id&gt; --rows` reads an existing schedule. Place it with `viewport`.
    /// </summary>
    public class ScheduleCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "schedule";
        public string Description => "Quantity schedule: 'schedule <category> --fields \"Family and Type,Count,Length\"' [--filter \"Length gt 12000\"] " +
                                     "[--group <field>] [--total] [--name n]; prints the first rows with units. '<name|id> --rows' reads one back";
        public string Usage => ScheduleArgs.Usage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = ScheduleArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            return a.ReadTarget != null ? Read(doc, a) : Create(doc, a);
        }

        // ------------------------------------------------------------------ create

        private static CommandResult Create(Document doc, ScheduleArgs a)
        {
            var (bic, catError) = FormattingHelper.ResolveCategory(a.Category, doc);
            if (bic == null) return CommandResult.Error("BAD_ARGS", catError.Replace("ERROR: ", ""), "Run 'categories' for the names.");
            var catId = new ElementId(bic.Value);
            string catName = Category.GetCategory(doc, catId)?.Name ?? bic.Value.ToString();
            if (!ViewSchedule.IsValidCategoryForSchedule(catId))
                return CommandResult.Error("BAD_ARGS", "Category '" + catName + "' cannot be scheduled.", "Pick a model category (walls, structural foundations, ...).");
            if (a.Name != null && new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<View>()
                    .Any(v => v.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Error("BAD_ARGS", "A schedule named '" + a.Name + "' already exists.", "Pick another --name, or read it with 'schedule \"" + a.Name + "\" --rows'.");

            ViewSchedule vs = null;
            var notes = new List<string>();
            CommandResult refused = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Schedule", () =>
            {
                vs = ViewSchedule.CreateSchedule(doc, catId);
                if (a.Name != null) vs.Name = a.Name;
                var def = vs.Definition;
                var schedulable = def.GetSchedulableFields().ToList();
                var names = schedulable.Select(s => s.GetName(doc)).ToList();

                // ---- fields, in the asked order ----
                var added = new List<ScheduleField>();
                var unknown = new List<string>();
                foreach (var want in a.Fields)
                {
                    int i = MatchField(names, want);
                    if (i < 0) { unknown.Add(want); continue; }
                    added.Add(def.AddField(schedulable[i]));
                }
                if (unknown.Count > 0)
                {
                    refused = CommandResult.Error("BAD_ARGS", "Unknown field(s) for " + catName + ": " + string.Join(", ", unknown.Select(u => "'" + u + "'")) +
                        ". " + ScheduleText.FieldHelp(names, unknown), "Use the names exactly as listed (case does not matter).");
                    throw new OperationCanceledException();
                }

                // ---- filter (its field may be outside --fields: then it is added hidden) ----
                if (a.Filter != null)
                {
                    int fi = MatchField(names, a.Filter.Field);
                    if (fi < 0)
                    {
                        refused = CommandResult.Error("BAD_ARGS", "--filter field '" + a.Filter.Field + "' is not schedulable for " + catName + ". " +
                            ScheduleText.FieldHelp(names, new[] { a.Filter.Field }), ScheduleFilterSpec.Help);
                        throw new OperationCanceledException();
                    }
                    var ff = added.FirstOrDefault(x => x.GetName().Equals(names[fi], StringComparison.OrdinalIgnoreCase));
                    if (ff == null) { ff = def.AddField(schedulable[fi]); ff.IsHidden = true; notes.Add("filter field '" + names[fi] + "' added as a hidden column"); }
                    var why = AddFilter(doc, def, ff, catId, a.Filter);
                    if (why != null)
                    {
                        refused = CommandResult.Error("BAD_ARGS", "Revit refused --filter '" + a.Filter + "': " + why, ScheduleFilterSpec.Help);
                        throw new OperationCanceledException();
                    }
                }

                // ---- grouping / totals ----
                if (a.Group != null)
                {
                    var gf = added.First(x => x.GetName().Equals(names[MatchField(names, a.Group)], StringComparison.OrdinalIgnoreCase));
                    def.AddSortGroupField(new ScheduleSortGroupField(gf.FieldId, ScheduleSortOrder.Ascending) { ShowHeader = false, ShowFooter = false });
                    // One row per group: the count and the summed quantities are that group's.
                    def.IsItemized = false;
                }
                else if (added.Count > 0)
                {
                    try { def.AddSortGroupField(new ScheduleSortGroupField(added[0].FieldId, ScheduleSortOrder.Ascending)); }
                    catch { /* not every field can sort; rows then keep Revit's order */ }
                }
                if (a.Group != null || a.Total)
                    foreach (var f in added)
                        if (f.FieldType != ScheduleFieldType.Count && f.CanTotal())
                            f.DisplayType = ScheduleFieldDisplayType.Totals;
                if (a.Total)
                {
                    def.ShowGrandTotal = true;
                    def.ShowGrandTotalTitle = true;
                    def.ShowGrandTotalCount = true;
                }
                // Revit's default columns are narrow: a type name wraps over five lines on the sheet.
                doc.Regenerate();
                FitColumns(vs);
            });
            if (refused != null) return refused;
            if (error != null) return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("SCHEDULE CREATED");
            sb.AppendLine("================");
            sb.AppendLine("ID: " + vs.Id.IntegerValue);
            sb.AppendLine("Name: " + vs.Name);
            sb.AppendLine("Category: " + catName);
            if (a.Filter != null) sb.AppendLine("Filter: " + a.Filter);
            if (a.Group != null) sb.AppendLine("Grouped by: " + a.Group + " (one row per value; numeric columns summed)");
            if (a.Total) sb.AppendLine("Grand total: on");
            foreach (var n in notes) sb.AppendLine("Note: " + n);
            var data = Rows(doc, vs, ScheduleArgs.PreviewRows, sb, out int total);
            sb.AppendLine("Next: 'viewport <sheet> " + vs.Id.IntegerValue + "' to put it on a sheet; 'schedule " + vs.Id.IntegerValue + " --rows' for every row.");
            data["created"] = new List<object> { vs.Id.IntegerValue };
            data["category"] = catName;
            data["filter"] = a.Filter?.ToString();
            data["group"] = a.Group;
            data["grandTotal"] = a.Total;
            return CommandResult.Ok(sb.ToString(), data);
        }

        /// <summary>Widen each visible column to its longest cell (paper mm, see ScheduleText.ColumnWidthMm).</summary>
        private static void FitColumns(ViewSchedule vs)
        {
            var def = vs.Definition;
            var visible = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i)).Where(f => !f.IsHidden).ToList();
            var body = vs.GetTableData().GetSectionData(SectionType.Body);
            for (int c = 0; c < visible.Count; c++)
            {
                int longest = 0;
                for (int r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
                {
                    int col = body.FirstColumnNumber + c;
                    if (col > body.LastColumnNumber) break;
                    // The heading row may wrap, so it only counts up to its longest word.
                    var text = vs.GetCellText(SectionType.Body, r, col) ?? "";
                    longest = Math.Max(longest, r == body.FirstRowNumber ? text.Split(' ').Max(w => w.Length) : text.Length);
                }
                try { visible[c].GridColumnWidth = RevitUnitHelper.MmToFeet(ScheduleText.ColumnWidthMm(longest)); }
                catch { /* a template-controlled width stays */ }
            }
        }

        /// <summary>Exact (case-insensitive) name first, then a unique substring; -1 when none or ambiguous.</summary>
        internal static int MatchField(IList<string> names, string want)
        {
            for (int i = 0; i < names.Count; i++)
                if (names[i].Equals(want, StringComparison.OrdinalIgnoreCase)) return i;
            var hits = Enumerable.Range(0, names.Count).Where(i => names[i].IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return hits.Count == 1 ? hits[0] : -1;
        }

        /// <summary>
        /// Adds the filter with the value typed as the field needs it: a number in the project's
        /// display unit for measurable fields, text, or (for type fields such as "Family and
        /// Type") the matching type's id. Returns null on success, else Revit's reason.
        /// </summary>
        private static string AddFilter(Document doc, ScheduleDefinition def, ScheduleField field, ElementId catId, ScheduleFilterSpec spec)
        {
            var type = FilterType(spec.Op);
            var attempts = new List<Func<ScheduleFilter>>();
            if (double.TryParse(spec.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                var specId = SpecOf(field);
                if (specId != null && UnitUtils.IsMeasurableSpec(specId))
                {
                    var unit = UnitOf(doc, field, specId);
                    double internalValue = unit != null ? UnitUtils.ConvertToInternalUnits(num, unit) : num;
                    attempts.Add(() => new ScheduleFilter(field.FieldId, type, internalValue));
                }
                else
                {
                    if (num == Math.Floor(num) && Math.Abs(num) < int.MaxValue) attempts.Add(() => new ScheduleFilter(field.FieldId, type, (int)num));
                    attempts.Add(() => new ScheduleFilter(field.FieldId, type, num));
                }
            }
            attempts.Add(() => new ScheduleFilter(field.FieldId, type, spec.Value));
            var typeId = MatchType(doc, catId, spec.Value);
            if (typeId != null) attempts.Add(() => new ScheduleFilter(field.FieldId, type, typeId));

            string last = null;
            foreach (var make in attempts)
            {
                try { def.AddFilter(make()); return null; }
                catch (Exception ex) { last = ex.Message; }
            }
            return last ?? "no usable value";
        }

        private static ScheduleFilterType FilterType(ScheduleOp op)
        {
            switch (op)
            {
                case ScheduleOp.Equal: return ScheduleFilterType.Equal;
                case ScheduleOp.NotEqual: return ScheduleFilterType.NotEqual;
                case ScheduleOp.Greater: return ScheduleFilterType.GreaterThan;
                case ScheduleOp.GreaterOrEqual: return ScheduleFilterType.GreaterThanOrEqual;
                case ScheduleOp.Less: return ScheduleFilterType.LessThan;
                case ScheduleOp.LessOrEqual: return ScheduleFilterType.LessThanOrEqual;
                case ScheduleOp.Contains: return ScheduleFilterType.Contains;
                case ScheduleOp.NotContains: return ScheduleFilterType.NotContains;
                case ScheduleOp.BeginsWith: return ScheduleFilterType.BeginsWith;
                default: return ScheduleFilterType.EndsWith;
            }
        }

        /// <summary>A type of the category named "Family : Type" or just the type name.</summary>
        private static ElementId MatchType(Document doc, ElementId catId, string value)
        {
            foreach (var t in new FilteredElementCollector(doc).OfCategoryId(catId).WhereElementIsElementType().Cast<ElementType>())
            {
                var full = t.FamilyName + ": " + t.Name;
                if (value.Equals(full, StringComparison.OrdinalIgnoreCase) || value.Equals(t.FamilyName + " : " + t.Name, StringComparison.OrdinalIgnoreCase) ||
                    value.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
                    return t.Id;
            }
            return null;
        }

        private static ForgeTypeId SpecOf(ScheduleField f)
        {
            try
            {
                var s = f.GetSpecTypeId();
                return s == null || string.IsNullOrEmpty(s.TypeId) ? null : s;
            }
            catch { return null; }
        }

        private static ForgeTypeId UnitOf(Document doc, ScheduleField f, ForgeTypeId spec)
        {
            try
            {
                var fo = f.GetFormatOptions();
                if (fo != null && !fo.UseDefault) return fo.GetUnitTypeId();
                return doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ read back

        private static CommandResult Read(Document doc, ScheduleArgs a)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule && !v.IsInternalKeynoteSchedule).ToList();
            ViewSchedule vs = null;
            if (int.TryParse(a.ReadTarget, out int id)) vs = doc.GetElement(new ElementId(id)) as ViewSchedule;
            if (vs == null) vs = all.FirstOrDefault(v => v.Name.Equals(a.ReadTarget, StringComparison.OrdinalIgnoreCase));
            if (vs == null)
            {
                var hits = all.Where(v => v.Name.IndexOf(a.ReadTarget, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 1) vs = hits[0];
                else
                    return CommandResult.Error("ELEMENT_NOT_FOUND", (hits.Count > 1 ? "'" + a.ReadTarget + "' matches several schedules: " : "No schedule '" + a.ReadTarget + "'. Schedules: ") +
                        string.Join(", ", (hits.Count > 1 ? hits : all).Take(30).Select(v => "'" + v.Name + "' (" + v.Id.IntegerValue + ")")) + ".",
                        "Give the exact name or the id.");
            }
            var sb = new StringBuilder();
            sb.AppendLine("SCHEDULE " + vs.Name + " (" + vs.Id.IntegerValue + ")");
            sb.AppendLine("=".PadRight(40, '='));
            var cat = Category.GetCategory(doc, vs.Definition.CategoryId);
            if (cat != null) sb.AppendLine("Category: " + cat.Name);
            var data = Rows(doc, vs, a.RowLimit, sb, out _);
            var on = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => s.ScheduleId == vs.Id).Select(s => doc.GetElement(s.OwnerViewId) as ViewSheet).Where(s => s != null)
                .Select(SheetHelper.Label).Distinct().ToList();
            sb.AppendLine("On sheet(s): " + (on.Count > 0 ? string.Join(", ", on) : "none — 'viewport <sheet> " + vs.Id.IntegerValue + "' places it"));
            data["sheets"] = on.Cast<object>().ToList();
            return CommandResult.Ok(sb.ToString(), data);
        }

        /// <summary>Appends the field list and the row table to <paramref name="sb"/>; returns the JSON part.</summary>
        private static Dictionary<string, object> Rows(Document doc, ViewSchedule vs, int maxRows, StringBuilder sb, out int totalRows)
        {
            var def = vs.Definition;
            var visible = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i)).Where(f => !f.IsHidden).ToList();
            var headings = visible.Select(f => f.ColumnHeading).ToList();
            var units = visible.Select(f =>
            {
                var spec = SpecOf(f);
                return spec != null && UnitUtils.IsMeasurableSpec(spec) ? ScheduleText.UnitSymbol(UnitOf(doc, f, spec)?.TypeId) : null;
            }).ToList();
            var headers = headings.Select((h, i) => ScheduleText.Header(h, units[i])).ToList();

            var body = vs.GetTableData().GetSectionData(SectionType.Body);
            var raw = new List<IList<string>>();
            for (int r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
            {
                var row = new List<string>();
                for (int c = body.FirstColumnNumber; c <= body.LastColumnNumber; c++)
                    row.Add(vs.GetCellText(SectionType.Body, r, c));
                raw.Add(row);
            }
            var rows = ScheduleText.DataRows(raw, headings);
            totalRows = rows.Count;
            if (rows.Count == 0)
            {
                // An empty schedule is usually its filter or phase, not a missing category.
                var phase = vs.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsValueString();
                sb.AppendLine("Empty: " + def.GetFilterCount() + " filter(s)" + (phase != null ? ", phase '" + phase + "'" : "") +
                              " — elements outside the phase or the filter are not listed.");
            }
            sb.AppendLine("Columns: " + string.Join(", ", headers));
            sb.AppendLine("Rows: " + rows.Count + (rows.Count > maxRows ? " (first " + maxRows + " shown)" : ""));
            sb.AppendLine();
            foreach (var l in ScheduleText.Table(headers, rows, maxRows, "'schedule " + vs.Id.IntegerValue + " --rows' for all")) sb.AppendLine(l);
            sb.AppendLine();
            return new Dictionary<string, object>
            {
                { "id", vs.Id.IntegerValue },
                { "name", vs.Name },
                { "columns", visible.Select((f, i) => (object)new Dictionary<string, object> { { "field", f.GetName() }, { "heading", headings[i] }, { "unit", units[i] } }).ToList() },
                { "rows", rows.Take(maxRows).Select(r => (object)r.Cast<object>().ToList()).ToList() },
                { "totalRows", rows.Count },
                { "truncated", rows.Count > maxRows }
            };
        }
    }
}
