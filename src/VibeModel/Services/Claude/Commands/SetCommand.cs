using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class SetCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "set";
        public string Description => "Set a parameter value on an element";
        public string Usage => SetArgs.Usage;

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            const string usageText = "ERROR: Usage: set <elementId> <paramName> <value>\n\n" +
                                     "Example: set 123 Comments \"Hello World\"   set 123 \"Base Offset\" 500   set 123 Base Offset=500";
            var trimmed = (args ?? "").Trim();
            if (!SetArgs.TryParseId(trimmed, out int idValue, out string rest))
                return trimmed.Contains(" ") ? "ERROR: Invalid element ID '" + trimmed.Split(' ')[0] + "'" : usageText;

            var candidates = SetArgs.Candidates(rest);
            if (candidates.Count == 0)
                return usageText;

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return "ERROR: Element " + idValue + " not found";

            // The first split whose name is a parameter on the element (instance first, then type) wins.
            Parameter param = null;
            bool isTypeParam = false;
            string paramName = null, value = null;
            var elementType = element.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(element.GetTypeId()) : null;
            foreach (var c in candidates)
            {
                param = ParamWriter.Lookup(element, c.Key);
                if (param == null && elementType != null)
                {
                    param = ParamWriter.Lookup(elementType, c.Key);
                    isTypeParam = param != null;
                }
                if (param != null) { paramName = param.Definition.Name; value = c.Value; break; }
            }

            if (param == null)
            {
                var tried = candidates[candidates.Count - 1].Key;
                return "ERROR: Parameter '" + tried + "' not found on element " + idValue +
                       "\n\nA name with spaces: set " + idValue + " \"Base Offset\" 500  (or Base Offset=500)." +
                       "\nUse 'params' command to see available parameters.";
            }

            if (param.IsReadOnly)
                return "ERROR: Parameter '" + paramName + "' is read-only";

            var error = TransactionHelper.Execute(doc, "VibeModel: Set Parameter", () =>
                ParamWriter.Set(doc, param, paramName, value));

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
