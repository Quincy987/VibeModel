using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class LevelsCommand : IClaudeCommand
    {
        public string Name => "levels";
        public string Description => "List all levels in document";
        public string Usage => "levels";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var sb = new StringBuilder();
            sb.AppendLine("LEVELS IN DOCUMENT");
            sb.AppendLine("==================");
            sb.AppendLine();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var level in levels)
            {
                sb.AppendLine("ID " + level.Id.IntegerValue + ": " + level.Name + " (Elevation: " + FormattingHelper.FormatLength(level.Elevation) + ")");
            }

            sb.AppendLine();
            sb.AppendLine("Total levels: " + levels.Count);

            return sb.ToString();
        }
    }
}
