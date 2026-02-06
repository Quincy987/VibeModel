using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ListCommand : IClaudeCommand
    {
        public string Name => "list";
        public string Description => "List elements by type";
        public string Usage => "list <type>";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var elementType = args?.Trim() ?? "";
            if (string.IsNullOrEmpty(elementType))
                return "ERROR: Specify element type. Options: walls, floors, roofs, columns, beams, families, views, sheets";

            IEnumerable<Element> elements;

            switch (elementType.ToLower())
            {
                case "walls":
                    elements = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Element>();
                    break;
                case "floors":
                    elements = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Element>();
                    break;
                case "roofs":
                    elements = new FilteredElementCollector(doc).OfClass(typeof(RoofBase)).Cast<Element>();
                    break;
                case "columns":
                    elements = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralColumns)
                        .WhereElementIsNotElementType()
                        .Cast<Element>();
                    break;
                case "beams":
                    elements = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralFraming)
                        .WhereElementIsNotElementType()
                        .Cast<Element>();
                    break;
                case "families":
                    elements = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<Element>();
                    break;
                case "views":
                    elements = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate)
                        .Cast<Element>();
                    break;
                case "sheets":
                    elements = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<Element>();
                    break;
                default:
                    return "ERROR: Unknown element type '" + elementType + "'. Options: walls, floors, roofs, columns, beams, families, views, sheets";
            }

            var list = elements.Take(100).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(elementType.ToUpper() + " (" + list.Count + " shown, may be more)");
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            foreach (var elem in list)
            {
                sb.AppendLine("ID: " + elem.Id.IntegerValue + " | " + elem.Name);
            }

            return sb.ToString();
        }
    }
}
