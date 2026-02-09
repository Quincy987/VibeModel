using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class HideCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "hide";
        public string Description => "Hide a category or specific elements in active view";
        public string Usage => "hide <category> | hide <id> [id2...]";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: hide <category> | hide <id> [id2...]\n\nExamples: hide grids | hide 12345 12346";

            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Detect mode: if first arg parses as int, element mode
            if (int.TryParse(parts[0], out _))
            {
                var ids = FormattingHelper.ParseElementIds(doc, parts);
                if (ids.Count == 0)
                    return "ERROR: No valid element IDs found.";

                var error = VisibilityHelper.SetElementVisibility(doc, uiDoc, ids, hide: true);
                if (error != null) return error;

                return "Hidden " + ids.Count + " element(s) in active view.";
            }
            else
            {
                // Category mode
                var categoryInput = string.Join(" ", parts);
                var (cat, error) = FormattingHelper.ResolveCategory(categoryInput, doc);
                if (cat == null) return error;

                var result = VisibilityHelper.SetCategoryVisibility(doc, uiDoc, cat.Value, hide: true);
                if (result != null) return result;

                return "Hidden category '" + categoryInput + "' in active view.";
            }
        }
    }
}
