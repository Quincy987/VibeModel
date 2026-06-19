using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class RoomCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "room";
        public string Description => "Create a room at a point (mm)";
        public string Usage => "room <x> <y> [name] [number]";

        private const double SqFtToSqM = 0.09290304;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            if (doc.IsFamilyDocument)
                return CommandResult.Error("BAD_ARGS", "Rooms can only be created in a project document.", null);

            var parts = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return CommandResult.Error("BAD_ARGS",
                    "Usage: room <x> <y> [name] [number] — coordinates in mm.", Usage);

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double xmm) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ymm))
                return CommandResult.Error("BAD_ARGS", "Invalid coordinates. Use numbers in mm.", Usage);

            string name = parts.Length >= 3 ? parts[2] : null;
            string number = parts.Length >= 4 ? parts[3] : null;

            var level = FormattingHelper.GetPreferredLevel(doc, uiDoc);
            if (level == null)
                return CommandResult.Error("BAD_ARGS", "No level found to place the room on.",
                    "Create a level first with 'level <elevation_mm> [name]'.");

            Room room = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Room", () =>
            {
                room = doc.Create.NewRoom(level,
                    new UV(RevitUnitHelper.MmToFeet(xmm), RevitUnitHelper.MmToFeet(ymm)));
                if (room != null)
                {
                    if (!string.IsNullOrEmpty(name)) room.Name = name;
                    if (!string.IsNullOrEmpty(number)) room.Number = number;
                }
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);
            if (room == null)
                return CommandResult.Error("TRANSACTION_FAILED",
                    "Revit did not create a room at that point.", null);

            double areaM2 = room.Area * SqFtToSqM;
            bool unbounded = room.Area <= 0.0001;

            var sb = new StringBuilder();
            sb.AppendLine("ROOM CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + room.Id.IntegerValue);
            sb.AppendLine("Name: " + room.Name);
            sb.AppendLine("Number: " + room.Number);
            sb.AppendLine("Level: " + level.Name);
            if (unbounded)
                sb.AppendLine("Area: 0 — unbounded (the point is not inside an enclosed region).");
            else
                sb.AppendLine("Area: " + Math.Round(areaM2, 2) + " m²");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { room.Id.IntegerValue } },
                { "name", room.Name },
                { "number", room.Number },
                { "level", level.Name },
                { "areaM2", Math.Round(areaM2, 3) },
                { "unbounded", unbounded }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
