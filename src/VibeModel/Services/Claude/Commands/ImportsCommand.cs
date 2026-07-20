using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ImportsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "imports";
        public string Description => "List imported/linked CAD files (DWG etc.) with ID, bbox, curve count";
        public string Usage => "imports";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("CAD IMPORTS (" + instances.Count + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();

            if (instances.Count == 0)
            {
                sb.AppendLine("No CAD imports or links found in this document.");
                return CommandResult.Ok(sb.ToString(), new List<object>());
            }

            var data = new List<object>();
            foreach (var inst in instances)
            {
                var name = inst.Category?.Name ?? inst.Name;
                var kind = inst.IsLinked ? "link" : "import";
                var chains = DwgGeometryHelper.GetChains(inst, doc);
                int layerCount = chains.Select(c => c.Layer).Distinct().Count();

                sb.AppendLine("ID: " + inst.Id.IntegerValue + "  " + name + "  [" + kind + "]");

                var bbox = inst.get_BoundingBox(null);
                Dictionary<string, object> bboxData = null;
                if (bbox != null)
                {
                    sb.AppendLine("  BBox min: " + FormattingHelper.FormatPointMmBare(bbox.Min) + " mm");
                    sb.AppendLine("  BBox max: " + FormattingHelper.FormatPointMmBare(bbox.Max) + " mm");
                    bboxData = new Dictionary<string, object>
                    {
                        { "min", FormattingHelper.ToMmArray(bbox.Min) },
                        { "max", FormattingHelper.ToMmArray(bbox.Max) }
                    };
                }
                else
                {
                    sb.AppendLine("  BBox: (none)");
                }

                sb.AppendLine("  Curves: " + chains.Count + " on " + layerCount + " layers");
                sb.AppendLine();

                data.Add(new Dictionary<string, object>
                {
                    { "id", inst.Id.IntegerValue },
                    { "name", name },
                    { "linked", inst.IsLinked },
                    { "bboxMm", bboxData },
                    { "curves", chains.Count },
                    { "layers", layerCount }
                });
            }

            sb.AppendLine("Next: 'layers <id>' to see layer names, then 'curves <id> <layer>' for coordinates.");
            return CommandResult.Ok(sb.ToString(), data);
        }

    }
}
