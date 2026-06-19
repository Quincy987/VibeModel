using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class GridCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "grid";
        public string Description => "Create a grid line (coordinates in mm)";
        public string Usage => "grid <x1> <y1> <x2> <y2> [name]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parts = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return CommandResult.Error("BAD_ARGS",
                    "Usage: grid <x1> <y1> <x2> <y2> [name] — coordinates in mm.", Usage);

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x1mm) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y1mm) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x2mm) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y2mm))
                return CommandResult.Error("BAD_ARGS", "Invalid coordinates. Use numbers in mm.", Usage);

            if (Math.Abs(x1mm - x2mm) < 0.1 && Math.Abs(y1mm - y2mm) < 0.1)
                return CommandResult.Error("BAD_ARGS", "Start and end points are identical. Grid needs length.", null);

            string name = parts.Length >= 5 ? parts[4] : null;

            double x1 = RevitUnitHelper.MmToFeet(x1mm), y1 = RevitUnitHelper.MmToFeet(y1mm);
            double x2 = RevitUnitHelper.MmToFeet(x2mm), y2 = RevitUnitHelper.MmToFeet(y2mm);

            Grid grid = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Grid", () =>
            {
                var line = Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0));
                grid = Grid.Create(doc, line);
                if (!string.IsNullOrEmpty(name)) grid.Name = name;
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "TRANSACTION_FAILED",
                    "A grid with that name may already exist; try a different name.");

            var sb = new StringBuilder();
            sb.AppendLine("GRID CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + grid.Id.IntegerValue);
            sb.AppendLine("Name: " + grid.Name);
            sb.AppendLine("Start: (" + x1mm + ", " + y1mm + ") mm");
            sb.AppendLine("End: (" + x2mm + ", " + y2mm + ") mm");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { grid.Id.IntegerValue } },
                { "name", grid.Name }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
