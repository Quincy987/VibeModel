using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class SelectedCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "selected";
        public string Description => "Inspect currently selected elements";
        public string Usage => "selected";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
                return CommandResult.Ok(
                    "No elements selected.\n\nSelect one or more elements in Revit and run this command again.",
                    new Dictionary<string, object> { { "count", 0 }, { "elements", new List<object>() } });

            var elementsData = new List<object>();
            var sb = new StringBuilder();
            sb.AppendLine("SELECTED ELEMENTS: " + selectedIds.Count);
            sb.AppendLine("==================");
            sb.AppendLine();

            foreach (var id in selectedIds)
            {
                var element = doc.GetElement(id);
                if (element == null) continue;

                sb.AppendLine("Element ID: " + id.IntegerValue);
                sb.AppendLine("  Class: " + element.GetType().Name);
                sb.AppendLine("  Name: " + element.Name);
                sb.AppendLine("  Category: " + (element.Category?.Name ?? "N/A"));

                var elemData = new Dictionary<string, object>
                {
                    { "id", id.IntegerValue },
                    { "class", element.GetType().Name },
                    { "name", element.Name },
                    { "category", element.Category?.Name }
                };

                if (element is FamilyInstance fi)
                {
                    sb.AppendLine("  Family: " + (fi.Symbol?.Family?.Name ?? "N/A"));
                    sb.AppendLine("  Type: " + (fi.Symbol?.Name ?? "N/A"));
                    elemData["family"] = fi.Symbol?.Family?.Name;
                    elemData["type"] = fi.Symbol?.Name;
                }

                var bbox = element.get_BoundingBox(null);
                if (bbox != null)
                {
                    var size = bbox.Max - bbox.Min;
                    sb.AppendLine("  BBox Size: " + FormattingHelper.FormatLength(size.X) + " x " +
                                  FormattingHelper.FormatLength(size.Y) + " x " + FormattingHelper.FormatLength(size.Z));
                }

                sb.AppendLine();
                elementsData.Add(elemData);
            }

            var data = new Dictionary<string, object>
            {
                { "count", selectedIds.Count },
                { "elements", elementsData }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
