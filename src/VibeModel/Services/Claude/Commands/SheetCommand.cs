using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class SheetCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "sheet";
        public string Description => "Create a sheet (optional title block + placed view); --number/--name set the sheet number and name, " +
                                     "--set \"Param=Value\" (repeatable) fills sheet or title-block fields such as the stamp";
        public string Usage => "sheet [titleBlockName] [viewId] [--number <nr>] [--name <name>] [--set \"Param=Value\"]...";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var flags = SheetCreateArgs.Parse(args);
            if (flags.Error != null) return CommandResult.Error("BAD_ARGS", flags.Error, "Usage: " + Usage);
            if (flags.Number != null && new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Any(s => s.SheetNumber.Equals(flags.Number, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Error("BAD_ARGS", "Sheet number '" + flags.Number + "' is already used.", "Pick another --number, or 'list sheets'.");

            var toks = flags.Remaining.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            // A trailing numeric token is the viewId; the rest (if any) is the title-block name.
            int? viewId = null;
            if (toks.Count > 0 && int.TryParse(toks[toks.Count - 1], out int vid))
            {
                viewId = vid;
                toks.RemoveAt(toks.Count - 1);
            }
            string tbName = toks.Count > 0 ? string.Join(" ", toks) : null;

            // Office standard: no title block given → the pack's default title block.
            bool tbFromOffice = false;
            var pack = Standards.StandardsContext.GetActivePack(doc);
            if (string.IsNullOrEmpty(tbName) && !string.IsNullOrEmpty(pack?.Sheets?.DefaultTitleblock))
            {
                tbName = pack.Sheets.DefaultTitleblock;
                tbFromOffice = true;
            }

            // Resolve title block (optional).
            ElementId tbId = ElementId.InvalidElementId;
            string tbLabel = null;
            if (!string.IsNullOrEmpty(tbName))
            {
                var tb = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Name.Equals(tbName, StringComparison.OrdinalIgnoreCase) ||
                                          (fs.Family.Name + " : " + fs.Name).Equals(tbName, StringComparison.OrdinalIgnoreCase) ||
                                          fs.Family.Name.Equals(tbName, StringComparison.OrdinalIgnoreCase));
                if (tb == null && !tbFromOffice)
                    return CommandResult.Error("TYPE_NOT_LOADED",
                        "No title block named '" + tbName + "'.",
                        "Run 'familytypes title' to list loaded title blocks, or omit the name for a blank sheet.");
                if (tb != null)
                {
                    tbId = tb.Id;
                    tbLabel = tb.FamilyName + " : " + tb.Name + (tbFromOffice ? " (office default)" : "");
                }
                else
                {
                    tbLabel = "none — office default '" + tbName + "' is not loaded";
                }
            }

            // Validate the view exists before opening the transaction.
            if (viewId.HasValue)
            {
                var v = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (v == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND",
                        "View " + viewId.Value + " not found.", "Run 'views' to list view IDs.");
            }

            ViewSheet sheet = null;
            ElementId placedView = null;
            var setReport = new List<string>();
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Sheet", () =>
            {
                sheet = ViewSheet.Create(doc, tbId);
                if (flags.Number != null) sheet.SheetNumber = flags.Number;
                if (flags.Name != null) sheet.Name = flags.Name;
                if (flags.Sets.Count > 0)
                {
                    doc.Regenerate();
                    var tbInst = SheetHelper.TitleBlock(doc, sheet);
                    foreach (var kv in flags.Sets) setReport.Add(SetField(sheet, tbInst, kv.Key, kv.Value));
                }

                if (viewId.HasValue)
                {
                    var vId = new ElementId(viewId.Value);
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, vId))
                        throw new Exception("View " + viewId.Value +
                            " cannot be placed on a sheet (already placed, or it is a schedule/legend).");

                    doc.Regenerate();
                    var ol = sheet.Outline;
                    var center = new XYZ((ol.Min.U + ol.Max.U) / 2.0, (ol.Min.V + ol.Max.V) / 2.0, 0);
                    Viewport.Create(doc, sheet.Id, vId, center);
                    placedView = vId;
                }
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("SHEET CREATED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + sheet.Id.IntegerValue);
            sb.AppendLine("Number: " + sheet.SheetNumber);
            sb.AppendLine("Name: " + sheet.Name);
            if (placedView != null)
                sb.AppendLine("Placed view: " + placedView.IntegerValue);
            foreach (var r in setReport) sb.AppendLine(r);
            if (pack != null && tbLabel != null)
                sb.AppendLine("Title block: " + tbLabel);
            if (pack != null && pack.Sheets.StampParameters.Count > 0)
                sb.AppendLine("Stamp fields (" + string.Join(", ", pack.Sheets.StampParameters) + "): set with 'set " +
                              sheet.Id.IntegerValue + " <parameter> <value>'");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { sheet.Id.IntegerValue } },
                { "number", sheet.SheetNumber },
                { "name", sheet.Name },
                { "placedView", placedView != null ? (object)placedView.IntegerValue : null },
                { "fields", setReport.Cast<object>().ToList() }
            };
            sb.AppendLine("Next: 'viewport " + sheet.SheetNumber + " <view>[,<view>...] --grid CxR' to place views, 'sheetinfo " + sheet.SheetNumber + "' to check.");
            return CommandResult.Ok(sb.ToString(), data);
        }

        /// <summary>Write a sheet field: the sheet's own parameter first, else the title block instance's.</summary>
        private static string SetField(ViewSheet sheet, FamilyInstance tb, string name, string value)
        {
            var p = sheet.LookupParameter(name);
            string owner = "sheet";
            if (p == null || p.IsReadOnly) { var q = tb?.LookupParameter(name); if (q != null) { p = q; owner = "title block"; } }
            if (p == null) return "Field '" + name + "': no such parameter on the sheet or its title block — skipped";
            if (p.IsReadOnly) return "Field '" + name + "': read-only — skipped";
            try
            {
                bool ok;
                switch (p.StorageType)
                {
                    case StorageType.String: ok = p.Set(value); break;
                    case StorageType.Integer: ok = int.TryParse(value, out int i) && p.Set(i); break;
                    case StorageType.Double: ok = p.SetValueString(value); break;
                    default: ok = false; break;
                }
                return "Field '" + name + "' = " + value + " (" + owner + ")" + (ok ? "" : " — Revit did not accept the value");
            }
            catch (Exception ex) { return "Field '" + name + "': " + ex.Message; }
        }
    }
}
