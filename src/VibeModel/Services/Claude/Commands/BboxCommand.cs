using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class BboxCommand : IClaudeCommand
    {
        public string Name => "bbox";
        public string Description => "Bounding box of selected element(s)";
        public string Usage => "bbox";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
                return "ERROR: No element selected";

            var sb = new StringBuilder();
            sb.AppendLine("BOUNDING BOXES");
            sb.AppendLine("==============");
            sb.AppendLine();

            foreach (var id in selectedIds)
            {
                var element = doc.GetElement(id);
                if (element == null) continue;

                sb.AppendLine("Element: " + element.Name + " (ID: " + id.IntegerValue + ")");

                var bbox = element.get_BoundingBox(null);
                if (bbox != null)
                {
                    sb.AppendLine("  Min: " + FormattingHelper.FormatPoint(bbox.Min));
                    sb.AppendLine("  Max: " + FormattingHelper.FormatPoint(bbox.Max));
                    var size = bbox.Max - bbox.Min;
                    sb.AppendLine("  Size: " + FormattingHelper.FormatLength(size.X) + " x " + FormattingHelper.FormatLength(size.Y) + " x " + FormattingHelper.FormatLength(size.Z));
                }
                else
                {
                    sb.AppendLine("  (no bounding box)");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
