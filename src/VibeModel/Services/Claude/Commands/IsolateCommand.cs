using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class IsolateCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "isolate";
        public string Description => "Temporarily isolate elements or a category in the active view";
        public string Usage => "isolate <id> [id2...] | isolate <category>";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: isolate <id> [id2...] | isolate <category>\n\nExamples: isolate 12345 12346 | isolate foundations";

            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";

            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            ICollection<ElementId> ids;

            // Detect mode: if first arg parses as int, element mode
            if (int.TryParse(parts[0], out _))
            {
                var elementIds = FormattingHelper.ParseElementIds(doc, parts);
                if (elementIds.Count == 0)
                    return "ERROR: No valid element IDs found.";

                ids = elementIds;
            }
            else
            {
                // Category mode: collect all elements of category in active view
                var categoryInput = string.Join(" ", parts);
                var (cat, error) = FormattingHelper.ResolveCategory(categoryInput, doc);
                if (cat == null) return error;

                ids = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(cat.Value)
                    .WhereElementIsNotElementType()
                    .ToElementIds();

                if (ids.Count == 0)
                    return "ERROR: No elements of category '" + categoryInput + "' found in active view.";
            }

            var txError = TransactionHelper.Execute(doc, "VibeModel: Isolate Elements", () =>
            {
                view.IsolateElementsTemporary(ids);
            });

            if (txError != null) return txError;

            return "Isolated " + ids.Count + " element(s) in view '" + view.Name + "'.\nUse 'unisolate' to restore.";
        }
    }
}
