using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ViewCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "view";
        public string Description => "Create a view (floor plan or 3D)";
        public string Usage => "view plan|3d [levelName]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parts = (args ?? "").Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
                return CommandResult.Error("BAD_ARGS", "Usage: view plan|3d [levelName]", Usage);

            var kind = parts[0].ToLowerInvariant();
            var levelName = parts.Length >= 2 ? parts[1].Trim() : null;

            if (kind == "plan")
                return CreatePlan(doc, levelName, uiApp.ActiveUIDocument);
            if (kind == "3d")
                return Create3D(doc);

            return CommandResult.Error("BAD_ARGS", "Unknown view type '" + parts[0] + "'.", Usage);
        }

        private CommandResult CreatePlan(Document doc, string levelName, UIDocument uiDoc)
        {
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
            if (vft == null)
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "No floor-plan view type found in the document.", null);

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (levels.Count == 0)
                return CommandResult.Error("BAD_ARGS", "No levels in the document to host a plan view.",
                    "Create a level first with 'level <elevation_mm> [name]'.");

            Level level;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND",
                        "No level named '" + levelName + "'. Available: " +
                        string.Join(", ", levels.Select(l => l.Name)), null);
            }
            else
            {
                // Prefer the active view's level (matches WallCommand); fall back to lowest.
                level = FormattingHelper.GetPreferredLevel(doc, uiDoc) ?? levels.OrderBy(l => l.Elevation).First();
            }

            View view = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create View", () =>
            {
                view = ViewPlan.Create(doc, vft.Id, level.Id);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "TRANSACTION_FAILED",
                    "That level may already have a plan view of this type.");

            var sb = new StringBuilder();
            sb.AppendLine("VIEW CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Name: " + view.Name);
            sb.AppendLine("Type: " + view.ViewType);
            sb.AppendLine("Level: " + level.Name);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { view.Id.IntegerValue } },
                { "name", view.Name },
                { "type", view.ViewType.ToString() },
                { "level", level.Name }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private CommandResult Create3D(Document doc)
        {
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null)
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "No 3D view type found in the document.", null);

            View3D view = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create View", () =>
            {
                view = View3D.CreateIsometric(doc, vft.Id);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("VIEW CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Name: " + view.Name);
            sb.AppendLine("Type: " + view.ViewType);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { view.Id.IntegerValue } },
                { "name", view.Name },
                { "type", view.ViewType.ToString() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
