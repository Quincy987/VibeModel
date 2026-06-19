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
        public string Description => "Create a sheet (optional title block + placed view)";
        public string Usage => "sheet [titleBlockName] [viewId]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var toks = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            // A trailing numeric token is the viewId; the rest (if any) is the title-block name.
            int? viewId = null;
            if (toks.Count > 0 && int.TryParse(toks[toks.Count - 1], out int vid))
            {
                viewId = vid;
                toks.RemoveAt(toks.Count - 1);
            }
            string tbName = toks.Count > 0 ? string.Join(" ", toks) : null;

            // Resolve title block (optional).
            ElementId tbId = ElementId.InvalidElementId;
            if (!string.IsNullOrEmpty(tbName))
            {
                var tb = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Name.Equals(tbName, StringComparison.OrdinalIgnoreCase) ||
                                          (fs.Family.Name + " : " + fs.Name).Equals(tbName, StringComparison.OrdinalIgnoreCase) ||
                                          fs.Family.Name.Equals(tbName, StringComparison.OrdinalIgnoreCase));
                if (tb == null)
                    return CommandResult.Error("TYPE_NOT_LOADED",
                        "No title block named '" + tbName + "'.",
                        "Run 'familytypes title' to list loaded title blocks, or omit the name for a blank sheet.");
                tbId = tb.Id;
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
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Sheet", () =>
            {
                sheet = ViewSheet.Create(doc, tbId);

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

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { sheet.Id.IntegerValue } },
                { "number", sheet.SheetNumber },
                { "name", sheet.Name },
                { "placedView", placedView != null ? (object)placedView.IntegerValue : null }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
