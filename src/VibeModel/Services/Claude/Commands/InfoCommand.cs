using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class InfoCommand : IClaudeCommand
    {
        public string Name => "info";
        public string Description => "Document info (title, path, phases, element counts)";
        public string Usage => "info";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var sb = new StringBuilder();
            sb.AppendLine("DOCUMENT INFO");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("Title: " + doc.Title);
            sb.AppendLine("Path: " + (doc.PathName ?? "(not saved)"));
            sb.AppendLine("Is Family: " + doc.IsFamilyDocument);
            sb.AppendLine("Is Workshared: " + doc.IsWorkshared);

            var phases = new FilteredElementCollector(doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .ToList();
            sb.AppendLine("Phases: " + phases.Count);
            foreach (var phase in phases)
            {
                sb.AppendLine("  - " + phase.Name + " (ID: " + phase.Id.IntegerValue + ")");
            }

            sb.AppendLine();
            sb.AppendLine("Element Counts:");
            var counts = new Dictionary<string, int>
            {
                { "Walls", FormattingHelper.CountElements<Wall>(doc) },
                { "Floors", FormattingHelper.CountElements<Floor>(doc) },
                { "Roofs", FormattingHelper.CountElements<RoofBase>(doc) },
                { "Columns", FormattingHelper.CountElements<FamilyInstance>(doc, BuiltInCategory.OST_StructuralColumns) },
                { "Beams", FormattingHelper.CountElements<FamilyInstance>(doc, BuiltInCategory.OST_StructuralFraming) },
                { "Views", new FilteredElementCollector(doc).OfClass(typeof(View)).Count() }
            };
            foreach (var kvp in counts)
            {
                sb.AppendLine("  " + kvp.Key + ": " + kvp.Value);
            }

            return sb.ToString();
        }
    }
}
