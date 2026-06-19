using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ParamsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "params";
        public string Description => "Show parameters of selected element";
        public string Usage => "params [filter]";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
                return CommandResult.Error("NO_SELECTION", "No element selected",
                    "Select an element in Revit, then run params again.");

            var element = doc.GetElement(selectedIds.First());
            if (element == null)
                return CommandResult.Error("INTERNAL", "Could not get element", null);

            var filter = args?.Trim() ?? "";

            var sb = new StringBuilder();
            sb.AppendLine("PARAMETERS FOR: " + element.Name + " (ID: " + element.Id.IntegerValue + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();

            var parameters = element.Parameters.Cast<Parameter>()
                .Where(p => string.IsNullOrEmpty(filter) || p.Definition.Name.ToLower().Contains(filter.ToLower()))
                .OrderBy(p => p.Definition.Name)
                .ToList();

            sb.AppendLine("Instance Parameters (" + parameters.Count + " " +
                          (string.IsNullOrEmpty(filter) ? "total" : "matching '" + filter + "'") + "):");
            sb.AppendLine();

            var instanceData = new List<object>();
            foreach (var param in parameters)
            {
                var value = FormattingHelper.GetParameterValue(param);
                var readOnly = param.IsReadOnly ? " [RO]" : "";
                sb.AppendLine("  " + param.Definition.Name + ": " + value + readOnly);
                instanceData.Add(new Dictionary<string, object>
                {
                    { "name", param.Definition.Name },
                    { "value", value },
                    { "isReadOnly", param.IsReadOnly }
                });
            }

            var typeData = new List<object>();
            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elementType = doc.GetElement(typeId);
                if (elementType != null)
                {
                    var typeParams = elementType.Parameters.Cast<Parameter>()
                        .Where(p => string.IsNullOrEmpty(filter) || p.Definition.Name.ToLower().Contains(filter.ToLower()))
                        .OrderBy(p => p.Definition.Name)
                        .ToList();

                    sb.AppendLine();
                    sb.AppendLine("Type Parameters (" + typeParams.Count + "):");
                    sb.AppendLine();

                    foreach (var param in typeParams)
                    {
                        var value = FormattingHelper.GetParameterValue(param);
                        var readOnly = param.IsReadOnly ? " [RO]" : "";
                        sb.AppendLine("  " + param.Definition.Name + ": " + value + readOnly);
                        typeData.Add(new Dictionary<string, object>
                        {
                            { "name", param.Definition.Name },
                            { "value", value },
                            { "isReadOnly", param.IsReadOnly }
                        });
                    }
                }
            }

            var data = new Dictionary<string, object>
            {
                { "elementId", element.Id.IntegerValue },
                { "name", element.Name },
                { "instanceParameters", instanceData },
                { "typeParameters", typeData }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
