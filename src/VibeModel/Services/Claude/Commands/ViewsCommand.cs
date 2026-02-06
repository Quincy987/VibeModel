using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ViewsCommand : IClaudeCommand
    {
        public string Name => "views";
        public string Description => "List all views in document";
        public string Usage => "views";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var sb = new StringBuilder();
            sb.AppendLine("VIEWS IN DOCUMENT");
            sb.AppendLine("=================");
            sb.AppendLine();

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .ToList();

            var groupedViews = views.GroupBy(v => v.ViewType);

            foreach (var group in groupedViews)
            {
                sb.AppendLine(group.Key.ToString() + " (" + group.Count() + "):");
                foreach (var view in group.Take(20))
                {
                    sb.AppendLine("  ID " + view.Id.IntegerValue + ": " + view.Name);
                }
                if (group.Count() > 20)
                    sb.AppendLine("  ... and " + (group.Count() - 20) + " more");
                sb.AppendLine();
            }

            sb.AppendLine("Total views: " + views.Count);

            return sb.ToString();
        }
    }
}
