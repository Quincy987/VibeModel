using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class SetCommand : IClaudeCommand
    {
        public string Name => "set";
        public string Description => "Set a parameter value on an element";
        public string Usage => "set <elementId> <paramName> <value>";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parts = (args ?? "").Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return "ERROR: Usage: set <elementId> <paramName> <value>\n\nExample: set 123 Comments \"Hello World\"";

            if (!int.TryParse(parts[0], out int idValue))
                return "ERROR: Invalid element ID '" + parts[0] + "'";

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return "ERROR: Element " + idValue + " not found";

            var paramName = parts[1];
            var value = parts[2];

            // Find parameter (instance first, then type)
            var param = element.Parameters.Cast<Parameter>()
                .FirstOrDefault(p => p.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase));

            bool isTypeParam = false;
            if (param == null)
            {
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var elementType = doc.GetElement(typeId);
                    if (elementType != null)
                    {
                        param = elementType.Parameters.Cast<Parameter>()
                            .FirstOrDefault(p => p.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase));
                        if (param != null)
                            isTypeParam = true;
                    }
                }
            }

            if (param == null)
                return "ERROR: Parameter '" + paramName + "' not found on element " + idValue + "\n\nUse 'params' command to see available parameters.";

            if (param.IsReadOnly)
                return "ERROR: Parameter '" + paramName + "' is read-only";

            bool success = false;
            var error = TransactionHelper.Execute(doc, "VibeModel: Set Parameter", () =>
            {
                // Try SetValueString first — handles unit conversion automatically
                try
                {
                    success = param.SetValueString(value);
                }
                catch (Exception ex)
                {
                    Infrastructure.Logger.Info("SetValueString failed for '" + paramName + "': " + ex.Message);
                }

                if (success) return;

                // Fallback to typed set
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value);
                        success = true;
                        break;
                    case StorageType.Integer:
                        if (int.TryParse(value, out int intVal))
                        {
                            param.Set(intVal);
                            success = true;
                        }
                        break;
                    case StorageType.Double:
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblVal))
                        {
                            // Only convert to feet for dimension parameters
                            if (FormattingHelper.IsDimensionRelated(paramName))
                                dblVal = RevitUnitHelper.MmToFeet(dblVal);
                            param.Set(dblVal);
                            success = true;
                        }
                        break;
                    case StorageType.ElementId:
                        if (int.TryParse(value, out int elemIdVal))
                        {
                            param.Set(new ElementId(elemIdVal));
                            success = true;
                        }
                        break;
                }

                if (!success)
                    throw new InvalidOperationException("Could not set parameter '" + paramName + "' to '" + value + "'");
            });

            if (error != null) return error;

            var sb = new StringBuilder();
            sb.AppendLine("PARAMETER SET");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("Element: " + element.Name + " (ID: " + idValue + ")");
            sb.AppendLine("Parameter: " + paramName + (isTypeParam ? " [TYPE]" : " [INSTANCE]"));
            sb.AppendLine("New Value: " + FormattingHelper.GetParameterValue(param));

            return sb.ToString();
        }
    }
}
