using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    public class FloorCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "floor";
        public string Description => "Create a floor from points (coordinates in mm)";
        public string Usage => FloorArgs.Usage;

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorText;
            args = phase.Remaining;

            var fa = FloorArgs.Parse(args);
            if (fa.Error != null) return "ERROR: " + fa.Error;
            var points = ParsePoints(fa.Points);
            if (points == null || points.Count < 3)
                return "ERROR: Need at least 3 points.\nUsage: floor <x1,y1> <x2,y2> <x3,y3> [<x4,y4>] ...\nCoordinates in mm, e.g.: floor 0,0 5000,0 5000,5000 0,5000";

            // Validate no consecutive duplicate points (would create zero-length lines)
            for (int i = 0; i < points.Count; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % points.Count];
                if (Math.Abs(p1.X - p2.X) < 0.1 && Math.Abs(p1.Y - p2.Y) < 0.1)
                    return "ERROR: Points " + (i + 1) + " and " + ((i + 1) % points.Count + 1) + " are identical or too close.";
            }

            var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
            FloorType floorType;
            if (fa.Type != null)
            {
                var hit = NameMatch.Find(floorTypes.Select(t => new NameMatch.Item(t.Id.IntegerValue, t.Name)), fa.Type, "floor type", out var typeErr);
                if (hit == null) return "ERROR: " + typeErr;
                floorType = floorTypes.First(t => t.Id.IntegerValue == hit.Id);
            }
            else
            {
                floorType = floorTypes.FirstOrDefault();
                if (floorType == null)
                    return "ERROR: No floor type found in document";
            }

            Level level;
            if (fa.Level != null)
            {
                level = WallCommand.FindLevel(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList(), fa.Level, out var levelErr);
                if (level == null) return "ERROR: " + levelErr;
            }
            else level = FormattingHelper.GetPreferredLevel(doc, uiApp.ActiveUIDocument);

            if (level == null)
                return "ERROR: No level found in document";

            Floor newFloor = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Floor", () =>
            {
                var curveLoop = new CurveLoop();
                for (int i = 0; i < points.Count; i++)
                {
                    var p1 = points[i];
                    var p2 = points[(i + 1) % points.Count];
                    curveLoop.Append(Line.CreateBound(
                        new XYZ(RevitUnitHelper.MmToFeet(p1.X), RevitUnitHelper.MmToFeet(p1.Y), level.Elevation),
                        new XYZ(RevitUnitHelper.MmToFeet(p2.X), RevitUnitHelper.MmToFeet(p2.Y), level.Elevation)
                    ));
                }

                newFloor = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorType.Id, level.Id);
                phase.Apply(newFloor);
            });

            if (error != null) return error;

            var sb = new StringBuilder();
            sb.AppendLine("FLOOR CREATED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + newFloor.Id.IntegerValue);
            sb.AppendLine("Type: " + floorType.Name + " (id " + floorType.Id.IntegerValue + (fa.Type != null ? ", --type" : ", default: first floor type; --type <name> to choose") + ")");
            sb.AppendLine("Level: " + level.Name);
            sb.AppendLine("Points: " + points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                sb.AppendLine("  P" + (i + 1) + ": (" + points[i].X + ", " + points[i].Y + ") mm");
            }
            phase.AppendNote(sb, doc, uiApp.ActiveUIDocument.ActiveView);

            return sb.ToString();
        }

        private List<PointXY> ParsePoints(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
                return null;

            var points = new List<PointXY>();
            var tokens = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                var coords = token.Split(',');
                if (coords.Length == 2 &&
                    double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    points.Add(new PointXY(x, y));
                }
            }

            return points;
        }

        private class PointXY
        {
            public double X { get; }
            public double Y { get; }
            public PointXY(double x, double y) { X = x; Y = y; }
        }
    }
}
