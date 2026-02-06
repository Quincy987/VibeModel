using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class SubcatsCommand : IClaudeCommand
    {
        public string Name => "subcats";
        public string Description => "List subcategories of a category";
        public string Usage => "subcats <category>";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var categoryName = args?.Trim() ?? "";
            if (string.IsNullOrEmpty(categoryName))
                return "ERROR: Specify category name";

            var category = doc.Settings.Categories
                .Cast<Category>()
                .FirstOrDefault(c => c.Name.ToLower().Contains(categoryName.ToLower()));

            if (category == null)
                return "ERROR: Category '" + categoryName + "' not found";

            var sb = new StringBuilder();
            sb.AppendLine("SUBCATEGORIES OF: " + category.Name);
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            if (category.SubCategories == null || category.SubCategories.Size == 0)
            {
                sb.AppendLine("(no subcategories)");
                return sb.ToString();
            }

            foreach (Category subcat in category.SubCategories)
            {
                var isSystem = subcat.Name.StartsWith("<") && subcat.Name.EndsWith(">");
                sb.AppendLine(subcat.Name + " (ID: " + subcat.Id.IntegerValue + ")" + (isSystem ? " [SYSTEM]" : ""));
            }

            sb.AppendLine();
            sb.AppendLine("Total: " + category.SubCategories.Size);

            return sb.ToString();
        }
    }
}
