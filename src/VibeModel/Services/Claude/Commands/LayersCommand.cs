using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class LayersCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "layers";
        public string Description => "List DWG layers of a CAD import with object counts";
        public string Usage => "layers <importId>";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var trimmed = (args ?? "").Trim();
            if (trimmed.Length == 0 || !int.TryParse(trimmed, out int idValue))
                return CommandResult.Error("BAD_ARGS",
                    "Specify a CAD import element ID.",
                    "Run 'imports' to list CAD imports and their IDs.");

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return CommandResult.Error("NOT_FOUND",
                    "No element with ID " + idValue + ".",
                    "Run 'imports' to list CAD imports and their IDs.");

            if (!(element is ImportInstance inst))
                return CommandResult.Error("NOT_AN_IMPORT",
                    "Element " + idValue + " is not a CAD import (" + element.GetType().Name + ").",
                    "Run 'imports' to list CAD imports and their IDs.");

            var chains = DwgGeometryHelper.GetChains(inst, doc);
            var name = inst.Category?.Name ?? inst.Name;

            var groups = chains
                .GroupBy(c => c.Layer)
                .Select(g => new { Layer = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("LAYERS: " + name + " (ID: " + idValue + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();

            if (groups.Count == 0)
            {
                sb.AppendLine("No curve geometry found in this import.");
            }
            else
            {
                foreach (var g in groups)
                    sb.AppendLine("  " + g.Count.ToString().PadLeft(6) + "  " + g.Layer);
                sb.AppendLine();
                sb.AppendLine("Total: " + chains.Count + " curves on " + groups.Count + " layers");
                sb.AppendLine("Next: 'curves " + idValue + " <layer> --closed' to extract footprint loops.");
            }

            var data = new Dictionary<string, object>
            {
                { "id", idValue },
                { "name", name },
                { "layers", groups.Select(g => (object)new Dictionary<string, object>
                    {
                        { "name", g.Layer },
                        { "curves", g.Count }
                    }).ToList() }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
