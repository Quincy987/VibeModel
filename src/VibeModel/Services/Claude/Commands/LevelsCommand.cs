using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class LevelsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "levels";
        public string Description => "List all levels in document";
        public string Usage => "levels";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("LEVELS IN DOCUMENT");
            sb.AppendLine("==================");
            sb.AppendLine();
            foreach (var level in levels)
            {
                sb.AppendLine("ID " + level.Id.IntegerValue + ": " + level.Name +
                              " (Elevation: " + FormattingHelper.FormatLength(level.Elevation) + ")");
            }
            sb.AppendLine();
            sb.AppendLine("Total levels: " + levels.Count);

            var data = new Dictionary<string, object>
            {
                { "total", levels.Count },
                { "levels", levels.Select(l => new Dictionary<string, object>
                    {
                        { "id", l.Id.IntegerValue },
                        { "name", l.Name },
                        { "elevation", l.Elevation }   // Revit internal units (feet)
                    }).ToList<object>() }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
