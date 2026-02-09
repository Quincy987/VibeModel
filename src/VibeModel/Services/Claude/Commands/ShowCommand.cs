using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ShowCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "show";
        public string Description => "Show a hidden category or specific elements in active view";
        public string Usage => "show <category> | show <id> [id2...]";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: show <category> | show <id> [id2...]\n\nExamples: show grids | show 12345 12346";

            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Detect mode: if first arg parses as int, element mode
            if (int.TryParse(parts[0], out _))
            {
                var ids = FormattingHelper.ParseElementIds(doc, parts);
                if (ids.Count == 0)
                    return "ERROR: No valid element IDs found.";

                var error = VisibilityHelper.SetElementVisibility(doc, uiDoc, ids, hide: false);
                if (error != null) return error;

                return "Shown " + ids.Count + " element(s) in active view.";
            }
            else
            {
                // Category mode
                var categoryInput = string.Join(" ", parts);
                var (cat, error) = FormattingHelper.ResolveCategory(categoryInput, doc);
                if (cat == null) return error;

                var result = VisibilityHelper.SetCategoryVisibility(doc, uiDoc, cat.Value, hide: false);
                if (result != null) return result;

                return "Shown category '" + categoryInput + "' in active view.";
            }
        }
    }
}
