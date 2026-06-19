using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class LevelCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "level";
        public string Description => "Create a level at an elevation (mm)";
        public string Usage => "level <elevation_mm> [name]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parts = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
                return CommandResult.Error("BAD_ARGS",
                    "Usage: level <elevation_mm> [name]", Usage);

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double elevMm))
                return CommandResult.Error("BAD_ARGS", "Invalid elevation. Use a number in mm.", Usage);

            // Name may contain spaces — join the remaining tokens.
            string name = parts.Length >= 2 ? string.Join(" ", parts, 1, parts.Length - 1) : null;

            Level level = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Level", () =>
            {
                level = Level.Create(doc, RevitUnitHelper.MmToFeet(elevMm));
                if (!string.IsNullOrEmpty(name)) level.Name = name;
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "TRANSACTION_FAILED",
                    "A level with that name may already exist; try a different name.");

            var sb = new StringBuilder();
            sb.AppendLine("LEVEL CREATED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + level.Id.IntegerValue);
            sb.AppendLine("Name: " + level.Name);
            sb.AppendLine("Elevation: " + elevMm + " mm");
            sb.AppendLine();
            sb.AppendLine("Note: a level has no floor-plan view by default — use 'view plan " +
                          level.Name + "' to create one.");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { level.Id.IntegerValue } },
                { "name", level.Name },
                { "elevation", level.Elevation }   // Revit internal units (feet)
            };
            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
