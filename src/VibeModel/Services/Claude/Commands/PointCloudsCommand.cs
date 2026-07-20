using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class PointCloudsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "pointclouds";
        public string Description => "List loaded point clouds with ID and bounding box (mm)";
        public string Usage => "pointclouds";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var clouds = new FilteredElementCollector(doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("POINT CLOUDS (" + clouds.Count + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();

            if (clouds.Count == 0)
            {
                sb.AppendLine("No point clouds loaded in this document.");
                return CommandResult.Ok(sb.ToString(), new List<object>());
            }

            var data = new List<object>();
            foreach (var cloud in clouds)
            {
                sb.AppendLine("ID: " + cloud.Id.IntegerValue + "  " + cloud.Name);

                var bbox = cloud.get_BoundingBox(null);
                Dictionary<string, object> bboxData = null;
                if (bbox != null)
                {
                    sb.AppendLine("  BBox min: " + FmtMm(bbox.Min) + " mm");
                    sb.AppendLine("  BBox max: " + FmtMm(bbox.Max) + " mm");
                    bboxData = new Dictionary<string, object>
                    {
                        { "min", MmArray(bbox.Min) },
                        { "max", MmArray(bbox.Max) }
                    };
                }
                else
                {
                    sb.AppendLine("  BBox: (none)");
                }
                sb.AppendLine();

                data.Add(new Dictionary<string, object>
                {
                    { "id", cloud.Id.IntegerValue },
                    { "name", cloud.Name },
                    { "bboxMm", bboxData }
                });
            }

            sb.AppendLine("Next: 'pcprobe <id> <x> <y>' for ground level at a spot, or");
            sb.AppendLine("'pcline <id> <x1> <y1> <x2> <y2>' for a ground profile along a line.");
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static string FmtMm(XYZ feet)
        {
            return "(" + RevitUnitHelper.FeetToMm(feet.X).ToString("F1", CultureInfo.InvariantCulture) +
                   ", " + RevitUnitHelper.FeetToMm(feet.Y).ToString("F1", CultureInfo.InvariantCulture) +
                   ", " + RevitUnitHelper.FeetToMm(feet.Z).ToString("F1", CultureInfo.InvariantCulture) + ")";
        }

        private static double[] MmArray(XYZ feet)
        {
            return new[]
            {
                RevitUnitHelper.FeetToMm(feet.X),
                RevitUnitHelper.FeetToMm(feet.Y),
                RevitUnitHelper.FeetToMm(feet.Z)
            };
        }
    }
}
