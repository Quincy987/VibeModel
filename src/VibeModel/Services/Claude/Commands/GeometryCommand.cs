using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class GeometryCommand : IClaudeCommand
    {
        public string Name => "geometry";
        public string Description => "Geometry info for selected element";
        public string Usage => "geometry";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
                return "ERROR: No element selected";

            var element = doc.GetElement(selectedIds.First());
            if (element == null)
                return "ERROR: Could not get element";

            var sb = new StringBuilder();
            sb.AppendLine("GEOMETRY INFO: " + element.Name + " (ID: " + element.Id.IntegerValue + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();

            var options = new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            var geom = element.get_Geometry(options);
            if (geom == null)
            {
                sb.AppendLine("(no geometry)");
                return sb.ToString();
            }

            AnalyzeGeometry(sb, geom, 0);

            return sb.ToString();
        }

        private static void AnalyzeGeometry(StringBuilder sb, GeometryElement geom, int indent)
        {
            var prefix = new string(' ', indent * 2);
            int count = 0;

            foreach (var obj in geom)
            {
                count++;
                if (count > 50)
                {
                    sb.AppendLine(prefix + "... and more (truncated)");
                    break;
                }

                if (obj is Solid solid)
                {
                    if (solid.Volume > 0)
                    {
                        sb.AppendLine(prefix + "Solid: Volume=" + solid.Volume.ToString("F4") + " ft3, Faces=" + solid.Faces.Size + ", Edges=" + solid.Edges.Size);
                    }
                }
                else if (obj is Curve curve)
                {
                    sb.AppendLine(prefix + "Curve: " + curve.GetType().Name + ", Length=" + FormattingHelper.FormatLength(curve.Length));
                }
                else if (obj is Point point)
                {
                    sb.AppendLine(prefix + "Point: " + FormattingHelper.FormatPoint(point.Coord));
                }
                else if (obj is GeometryInstance gi)
                {
                    sb.AppendLine(prefix + "GeometryInstance:");
                    var instanceGeom = gi.GetInstanceGeometry();
                    if (instanceGeom != null)
                    {
                        AnalyzeGeometry(sb, instanceGeom, indent + 1);
                    }
                }
                else if (obj is Mesh mesh)
                {
                    sb.AppendLine(prefix + "Mesh: Triangles=" + mesh.NumTriangles);
                }
                else
                {
                    sb.AppendLine(prefix + obj.GetType().Name);
                }
            }
        }
    }
}
