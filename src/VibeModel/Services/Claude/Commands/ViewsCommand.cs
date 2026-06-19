using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ViewsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "views";
        public string Description => "List all views in document";
        public string Usage => "views";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("VIEWS IN DOCUMENT");
            sb.AppendLine("=================");
            sb.AppendLine();

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

            var data = new Dictionary<string, object>
            {
                { "total", views.Count },
                { "views", views.Select(v => new Dictionary<string, object>
                    {
                        { "id", v.Id.IntegerValue },
                        { "name", v.Name },
                        { "type", v.ViewType.ToString() }
                    }).ToList<object>() }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
