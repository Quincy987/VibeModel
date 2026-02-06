using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class CategoriesCommand : IClaudeCommand
    {
        public string Name => "categories";
        public string Description => "List all categories in document";
        public string Usage => "categories";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var sb = new StringBuilder();
            sb.AppendLine("DOCUMENT CATEGORIES");
            sb.AppendLine("===================");
            sb.AppendLine();

            var categories = doc.Settings.Categories
                .Cast<Category>()
                .Where(c => c.Parent == null)
                .OrderBy(c => c.Name)
                .ToList();

            foreach (var cat in categories)
            {
                var subCount = cat.SubCategories?.Size ?? 0;
                sb.AppendLine(cat.Name + " (ID: " + cat.Id.IntegerValue + ")" + (subCount > 0 ? " [" + subCount + " subcats]" : ""));
            }

            sb.AppendLine();
            sb.AppendLine("Total categories: " + categories.Count);
            sb.AppendLine();
            sb.AppendLine("Use 'subcats <category name>' to see subcategories.");

            return sb.ToString();
        }
    }
}
