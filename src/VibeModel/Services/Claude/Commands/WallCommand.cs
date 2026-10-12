using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    public class WallCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "wall";
        public string Description => "Create a wall (coordinates in mm)";
        public string Usage => WallArgs.Usage;

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            // Office standard: strip --phase and resolve the phase new elements get (inert
            // when no pack is active and no --phase was given).
            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorText;
            args = phase.Remaining;

            var a = WallArgs.Parse(args);
            if (a.Error != null) return "ERROR: " + a.Error;
            double x1mm = a.X1, y1mm = a.Y1, x2mm = a.X2, y2mm = a.Y2;
            double heightMm = a.Height ?? 4000;

            var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            WallType wallType;
            if (a.Type != null)
            {
                var hit = NameMatch.Find(wallTypes.Select(t => new NameMatch.Item(t.Id.IntegerValue, t.Name)), a.Type, "wall type", out var typeErr);
                if (hit == null) return "ERROR: " + typeErr;
                wallType = wallTypes.First(t => t.Id.IntegerValue == hit.Id);
            }
            else
            {
                wallType = wallTypes.FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                if (wallType == null)
                    return "ERROR: No basic wall type found in document";
            }

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            Level level;
            if (a.Level != null)
            {
                level = FindLevel(levels, a.Level, out var levelErr);
                if (level == null) return "ERROR: " + levelErr;
            }
            else level = FormattingHelper.GetPreferredLevel(doc, uiApp.ActiveUIDocument);

            if (level == null)
                return "ERROR: No level found in document";

            Level topLevel = null;
            if (a.TopLevel != null)
            {
                topLevel = FindLevel(levels, a.TopLevel, out var topErr);
                if (topLevel == null) return "ERROR: --top-level: " + topErr;
                if (topLevel.Elevation + RevitUnitHelper.MmToFeet(a.TopOffset ?? 0) <= level.Elevation + RevitUnitHelper.MmToFeet(a.BaseOffset ?? 0))
                    return "ERROR: The top (" + topLevel.Name + " + " + (a.TopOffset ?? 0) + " mm) is not above the base (" + level.Name + " + " + (a.BaseOffset ?? 0) + " mm).";
            }

            double x1 = RevitUnitHelper.MmToFeet(x1mm);
            double y1 = RevitUnitHelper.MmToFeet(y1mm);
            double x2 = RevitUnitHelper.MmToFeet(x2mm);
            double y2 = RevitUnitHelper.MmToFeet(y2mm);
            double height = RevitUnitHelper.MmToFeet(heightMm);
            double baseOffset = RevitUnitHelper.MmToFeet(a.BaseOffset ?? 0);

            Wall newWall = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Wall", () =>
            {
                var startPoint = new XYZ(x1, y1, level.Elevation);
                var endPoint = new XYZ(x2, y2, level.Elevation);
                var line = Line.CreateBound(startPoint, endPoint);
                newWall = Wall.Create(doc, line, wallType.Id, level.Id, height, baseOffset, false, false);
                if (topLevel != null)
                {
                    newWall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(topLevel.Id);
                    newWall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(RevitUnitHelper.MmToFeet(a.TopOffset ?? 0));
                }
                phase.Apply(newWall);
            });

            if (error != null) return error;

            var sb = new StringBuilder();
            sb.AppendLine("WALL CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + newWall.Id.IntegerValue);
            sb.AppendLine("Type: " + wallType.Name + " (id " + wallType.Id.IntegerValue + (a.Type != null ? ", --type" : ", default: first basic wall type; --type <name> to choose") + ")");
            sb.AppendLine("Level: " + level.Name);
            sb.AppendLine("Start: (" + x1mm + ", " + y1mm + ") mm");
            sb.AppendLine("End: (" + x2mm + ", " + y2mm + ") mm");
            sb.AppendLine("Length: " + FormattingHelper.FormatLength(newWall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0));
            if (topLevel != null)
            {
                sb.AppendLine("Top: " + topLevel.Name + (a.TopOffset.HasValue ? " + " + a.TopOffset + " mm" : "") +
                              " (height " + FormattingHelper.FormatLength(newWall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0) + ")");
            }
            else sb.AppendLine("Height: " + heightMm + " mm");
            if (a.BaseOffset.HasValue) sb.AppendLine("Base offset: " + a.BaseOffset + " mm");
            phase.AppendNote(sb, doc, uiApp.ActiveUIDocument.ActiveView);

            return sb.ToString();
        }

        internal static Level FindLevel(System.Collections.Generic.List<Level> levels, string query, out string error)
        {
            var hit = NameMatch.Find(levels.Select(l => new NameMatch.Item(l.Id.IntegerValue, l.Name)), query, "level", out error);
            return hit == null ? null : levels.First(l => l.Id.IntegerValue == hit.Id);
        }
    }
}
