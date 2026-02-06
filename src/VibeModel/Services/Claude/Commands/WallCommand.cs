using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class WallCommand : IClaudeCommand
    {
        public string Name => "wall";
        public string Description => "Create a wall (coordinates in mm)";
        public string Usage => "wall <x1> <y1> <x2> <y2> [height]";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var parts = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return "ERROR: Usage: wall <x1> <y1> <x2> <y2> [height_mm]\n\nCoordinates in mm. Default height is 4000mm.";

            if (!double.TryParse(parts[0], out double x1mm) ||
                !double.TryParse(parts[1], out double y1mm) ||
                !double.TryParse(parts[2], out double x2mm) ||
                !double.TryParse(parts[3], out double y2mm))
            {
                return "ERROR: Invalid coordinates. Use numbers in mm.";
            }

            double heightMm = 4000;
            if (parts.Length >= 5 && double.TryParse(parts[4], out double h))
                heightMm = h;

            double x1 = RevitUnitHelper.MmToFeet(x1mm);
            double y1 = RevitUnitHelper.MmToFeet(y1mm);
            double x2 = RevitUnitHelper.MmToFeet(x2mm);
            double y2 = RevitUnitHelper.MmToFeet(y2mm);
            double height = RevitUnitHelper.MmToFeet(heightMm);

            var wallType = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(wt => wt.Kind == WallKind.Basic);

            if (wallType == null)
                return "ERROR: No basic wall type found in document";

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            if (level == null)
                return "ERROR: No level found in document";

            Wall newWall = null;
            using (var trans = new Transaction(doc, "VibeModel: Create Wall"))
            {
                trans.Start();
                try
                {
                    var startPoint = new XYZ(x1, y1, level.Elevation);
                    var endPoint = new XYZ(x2, y2, level.Elevation);
                    var line = Line.CreateBound(startPoint, endPoint);

                    newWall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return "ERROR: Failed to create wall: " + ex.Message;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("WALL CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + newWall.Id.IntegerValue);
            sb.AppendLine("Type: " + wallType.Name);
            sb.AppendLine("Level: " + level.Name);
            sb.AppendLine("Start: (" + x1mm + ", " + y1mm + ") mm");
            sb.AppendLine("End: (" + x2mm + ", " + y2mm + ") mm");
            sb.AppendLine("Length: " + FormattingHelper.FormatLength(newWall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0));
            sb.AppendLine("Height: " + heightMm + " mm");

            return sb.ToString();
        }
    }
}
