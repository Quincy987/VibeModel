using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class GetCommand : IClaudeCommand
    {
        public string Name => "get";
        public string Description => "Get element by ID";
        public string Usage => "get <id>";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var idString = args?.Trim() ?? "";
            if (!int.TryParse(idString, out int idValue))
                return "ERROR: Invalid element ID '" + idString + "'";

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return "ERROR: Element with ID " + idValue + " not found";

            var sb = new StringBuilder();
            sb.AppendLine("ELEMENT: " + element.Name);
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();
            sb.AppendLine("ID: " + element.Id.IntegerValue);
            sb.AppendLine("Class: " + element.GetType().Name);
            sb.AppendLine("Category: " + (element.Category?.Name ?? "N/A"));

            if (element.Category?.Parent != null)
                sb.AppendLine("Parent Category: " + element.Category.Parent.Name);

            if (element is FamilyInstance fi)
            {
                sb.AppendLine();
                sb.AppendLine("FAMILY INFO:");
                sb.AppendLine("  Family: " + (fi.Symbol?.Family?.Name ?? "N/A"));
                sb.AppendLine("  Type: " + (fi.Symbol?.Name ?? "N/A"));
                sb.AppendLine("  Host: " + (fi.Host?.Name ?? "N/A"));

                if (fi.SuperComponent != null)
                    sb.AppendLine("  Super Component: " + fi.SuperComponent.Id.IntegerValue);

                var subComponents = fi.GetSubComponentIds();
                if (subComponents.Count > 0)
                {
                    sb.AppendLine("  Sub Components: " + string.Join(", ", subComponents.Select(id => id.IntegerValue)));
                }
            }

            var bbox = element.get_BoundingBox(null);
            if (bbox != null)
            {
                sb.AppendLine();
                sb.AppendLine("BOUNDING BOX:");
                sb.AppendLine("  Min: " + FormattingHelper.FormatPoint(bbox.Min));
                sb.AppendLine("  Max: " + FormattingHelper.FormatPoint(bbox.Max));
                var size = bbox.Max - bbox.Min;
                sb.AppendLine("  Size: " + FormattingHelper.FormatLength(size.X) + " x " + FormattingHelper.FormatLength(size.Y) + " x " + FormattingHelper.FormatLength(size.Z));
            }

            if (element.Location is LocationPoint lp)
            {
                sb.AppendLine();
                sb.AppendLine("LOCATION: " + FormattingHelper.FormatPoint(lp.Point));
            }
            else if (element.Location is LocationCurve lc)
            {
                sb.AppendLine();
                sb.AppendLine("LOCATION (Curve):");
                sb.AppendLine("  Start: " + FormattingHelper.FormatPoint(lc.Curve.GetEndPoint(0)));
                sb.AppendLine("  End: " + FormattingHelper.FormatPoint(lc.Curve.GetEndPoint(1)));
                sb.AppendLine("  Length: " + FormattingHelper.FormatLength(lc.Curve.Length));
            }

            return sb.ToString();
        }
    }
}
