using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class GetCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "get";
        public string Description => "Get element by ID";
        public string Usage => "get <id>";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var idString = args?.Trim() ?? "";
            if (!int.TryParse(idString, out int idValue))
                return CommandResult.Error("BAD_ARGS", "Invalid element ID '" + idString + "'",
                    "Provide a numeric element ID, e.g. 'get 12345'.");

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", "Element with ID " + idValue + " not found",
                    "Run 'list <category>' to find valid element IDs.");

            var data = new Dictionary<string, object>
            {
                { "id", element.Id.IntegerValue },
                { "name", element.Name },
                { "class", element.GetType().Name },
                { "category", element.Category?.Name }
            };

            var sb = new StringBuilder();
            sb.AppendLine("ELEMENT: " + element.Name);
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();
            sb.AppendLine("ID: " + element.Id.IntegerValue);
            sb.AppendLine("Class: " + element.GetType().Name);
            sb.AppendLine("Category: " + (element.Category?.Name ?? "N/A"));

            if (!(element is FamilyInstance))
            {
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var elemType = doc.GetElement(typeId);
                    if (elemType != null)
                    {
                        sb.AppendLine("Type: " + elemType.Name);
                        data["type"] = elemType.Name;
                    }
                }
            }

            if (element.Category?.Parent != null)
                sb.AppendLine("Parent Category: " + element.Category.Parent.Name);

            if (element is FamilyInstance fi)
            {
                sb.AppendLine();
                sb.AppendLine("FAMILY INFO:");
                sb.AppendLine("  Family: " + (fi.Symbol?.Family?.Name ?? "N/A"));
                sb.AppendLine("  Type: " + (fi.Symbol?.Name ?? "N/A"));
                sb.AppendLine("  Host: " + (fi.Host?.Name ?? "N/A"));
                data["family"] = fi.Symbol?.Family?.Name;
                data["type"] = fi.Symbol?.Name;
                data["host"] = fi.Host?.Name;

                if (fi.SuperComponent != null)
                    sb.AppendLine("  Super Component: " + fi.SuperComponent.Id.IntegerValue);

                var subComponents = fi.GetSubComponentIds();
                if (subComponents.Count > 0)
                {
                    sb.AppendLine("  Sub Components: " + string.Join(", ", subComponents.Select(id => id.IntegerValue)));
                }
            }

            // Key parameters
            var mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            var comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            var levelParam = element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                          ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                          ?? element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            var phaseCreated = element.get_Parameter(BuiltInParameter.PHASE_CREATED);

            if (mark != null || comments != null || levelParam != null || phaseCreated != null)
            {
                sb.AppendLine();
                sb.AppendLine("KEY PARAMETERS:");
                if (mark != null && mark.HasValue && !string.IsNullOrEmpty(mark.AsString()))
                {
                    sb.AppendLine("  Mark: " + mark.AsString());
                    data["mark"] = mark.AsString();
                }
                if (comments != null && comments.HasValue && !string.IsNullOrEmpty(comments.AsString()))
                {
                    sb.AppendLine("  Comments: " + comments.AsString());
                    data["comments"] = comments.AsString();
                }
                if (levelParam != null && levelParam.HasValue)
                {
                    var levelName = FormattingHelper.GetLevelName(doc, levelParam.AsElementId());
                    sb.AppendLine("  Level: " + levelName);
                    data["level"] = levelName;
                }
                if (phaseCreated != null && phaseCreated.HasValue)
                {
                    var phase = doc.GetElement(phaseCreated.AsElementId());
                    if (phase != null)
                    {
                        sb.AppendLine("  Phase: " + phase.Name);
                        data["phase"] = phase.Name;
                    }
                }
            }

            var bbox = element.get_BoundingBox(null);
            if (bbox != null)
            {
                sb.AppendLine();
                sb.AppendLine("BOUNDING BOX:");
                sb.AppendLine("  Min: " + FormattingHelper.FormatPoint(bbox.Min));
                sb.AppendLine("  Max: " + FormattingHelper.FormatPoint(bbox.Max));
                var size = bbox.Max - bbox.Min;
                sb.AppendLine("  Size: " + FormattingHelper.FormatLength(size.X) + " x " +
                              FormattingHelper.FormatLength(size.Y) + " x " + FormattingHelper.FormatLength(size.Z));
            }

            if (element.Location is LocationPoint lp)
            {
                sb.AppendLine();
                sb.AppendLine("LOCATION: " + FormattingHelper.FormatPoint(lp.Point));
            }
            else if (element.Location is LocationCurve lc)
            {
                sb.AppendLine();
                sb.AppendLine("LOCATION (Curve):");
                sb.AppendLine("  Start: " + FormattingHelper.FormatPoint(lc.Curve.GetEndPoint(0)));
                sb.AppendLine("  End: " + FormattingHelper.FormatPoint(lc.Curve.GetEndPoint(1)));
                sb.AppendLine("  Length: " + FormattingHelper.FormatLength(lc.Curve.Length));
            }

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
