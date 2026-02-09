using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class FilterCommand : IClaudeCommand
    {
        public string Name => "filter";
        public string Description => "Filter elements by category and parameter value";
        public string Usage => "filter <category> [paramName operator value] [--view] [--select] [--ids] [--count paramName]";

        private static readonly string[] Operators = { ">=", "<=", "!=", "=", ">", "<", "contains" };

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            if (string.IsNullOrWhiteSpace(args))
                return "ERROR: Usage: filter <category> [paramName operator value] [--view] [--select] [--ids] [--count paramName]\n\nOperators: = != > < >= <= contains\nExamples:\n  filter walls\n  filter walls Mark = K01\n  filter walls Height > 3000\n  filter walls --view --select\n  filter walls --count Mark";

            var tokens = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Extract flags
            bool viewScoped = ExtractFlag(tokens, "--view");
            bool selectResults = ExtractFlag(tokens, "--select");
            bool idsOnly = ExtractFlag(tokens, "--ids");

            string countParam = null;
            int countIdx = tokens.FindIndex(t => t.Equals("--count", StringComparison.OrdinalIgnoreCase));
            if (countIdx >= 0 && countIdx + 1 < tokens.Count)
            {
                countParam = tokens[countIdx + 1];
                tokens.RemoveAt(countIdx + 1);
                tokens.RemoveAt(countIdx);
            }
            else if (countIdx >= 0)
            {
                tokens.RemoveAt(countIdx);
                return "ERROR: --count requires a parameter name. Example: --count Mark";
            }

            if (tokens.Count == 0)
                return "ERROR: No category specified.";

            // Find operator position to split category from filter expression
            int opIndex = -1;
            string foundOp = null;
            for (int i = 1; i < tokens.Count; i++)
            {
                foreach (var op in Operators)
                {
                    if (tokens[i].Equals(op, StringComparison.OrdinalIgnoreCase))
                    {
                        opIndex = i;
                        foundOp = op;
                        goto FoundOp;
                    }
                }
            }
            FoundOp:

            // Resolve category (first token, or first few tokens before param name)
            string categoryInput;
            string paramName = null;
            string filterValue = null;

            if (opIndex > 0)
            {
                // Category is the first token, param name is everything between category and operator
                categoryInput = tokens[0];
                paramName = string.Join(" ", tokens.Skip(1).Take(opIndex - 1));
                filterValue = string.Join(" ", tokens.Skip(opIndex + 1));

                if (string.IsNullOrWhiteSpace(paramName))
                    return "ERROR: Missing parameter name before operator '" + foundOp + "'.\nUsage: filter <category> <paramName> " + foundOp + " <value>";
                if (string.IsNullOrWhiteSpace(filterValue))
                    return "ERROR: Missing value after operator '" + foundOp + "'.\nUsage: filter <category> <paramName> " + foundOp + " <value>";
            }
            else
            {
                categoryInput = tokens[0];
            }

            var (cat, error) = FormattingHelper.ResolveCategory(categoryInput, doc);
            if (cat == null)
                return error;

            // Get active view for view-scoped queries
            View activeView = null;
            if (viewScoped)
            {
                activeView = FormattingHelper.GetActiveGraphicalView(uiDoc);
                if (activeView == null)
                    return "ERROR: No graphical view active. Switch to a plan, section, or 3D view to use --view.";
            }

            // Collect elements
            var collector = activeView != null
                ? new FilteredElementCollector(doc, activeView.Id)
                : new FilteredElementCollector(doc);

            var elements = collector
                .OfCategory(cat.Value)
                .WhereElementIsNotElementType()
                .ToList();

            // Apply parameter filter if specified
            if (!string.IsNullOrEmpty(paramName) && foundOp != null && filterValue != null)
            {
                elements = elements.Where(e => MatchesFilter(e, doc, paramName, foundOp, filterValue)).ToList();
            }

            // --count mode: group by parameter value
            if (countParam != null)
            {
                return BuildCountOutput(elements, doc, countParam, categoryInput, viewScoped, activeView);
            }

            // --select mode
            if (selectResults && elements.Count > 0)
            {
                try
                {
                    uiDoc.Selection.SetElementIds(elements.Select(e => e.Id).ToList());
                }
                catch { /* selection may fail in some views */ }
            }

            // --ids mode
            if (idsOnly)
            {
                if (elements.Count == 0)
                    return "No elements found.";
                return string.Join(" ", elements.Select(e => e.Id.IntegerValue));
            }

            // Standard output
            var sb = new StringBuilder();
            var shown = elements.Take(50).ToList();
            sb.AppendLine("FILTER: " + categoryInput.ToUpper() +
                (paramName != null ? " | " + paramName + " " + foundOp + " " + filterValue : "") +
                (viewScoped ? " [view: " + activeView.Name + "]" : ""));
            sb.AppendLine("Found: " + elements.Count + (elements.Count > 50 ? " (showing 50)" : ""));
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            foreach (var elem in shown)
            {
                sb.Append("ID: " + elem.Id.IntegerValue + " | ");
                sb.Append(elem.Name);

                var tid = elem.GetTypeId();
                if (tid != ElementId.InvalidElementId)
                {
                    var et = doc.GetElement(tid);
                    if (et != null)
                        sb.Append(" | Type: " + et.Name);
                }

                var mark = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && mark.HasValue && !string.IsNullOrEmpty(mark.AsString()))
                    sb.Append(" | Mark: " + mark.AsString());

                sb.AppendLine();
            }

            if (selectResults && elements.Count > 0)
                sb.AppendLine("\n(" + elements.Count + " elements selected)");

            return sb.ToString();
        }

        private static bool MatchesFilter(Element elem, Document doc, string paramName, string op, string filterValue)
        {
            var param = FormattingHelper.FindParameter(elem, doc, paramName);
            if (param == null || !param.HasValue) return false;

            // Get comparable values
            if (param.StorageType == StorageType.String)
            {
                var strVal = param.AsString() ?? "";
                switch (op)
                {
                    case "=": return strVal.Equals(filterValue, StringComparison.OrdinalIgnoreCase);
                    case "!=": return !strVal.Equals(filterValue, StringComparison.OrdinalIgnoreCase);
                    case "contains": return strVal.IndexOf(filterValue, StringComparison.OrdinalIgnoreCase) >= 0;
                    default: return false;
                }
            }
            else if (param.StorageType == StorageType.Double)
            {
                var rawVal = param.AsDouble();
                // Convert feet to mm for length parameters
                if (FormattingHelper.IsDimensionRelated(param.Definition.Name))
                    rawVal = rawVal * 304.8;

                if (!double.TryParse(filterValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
                    return false;

                switch (op)
                {
                    case "=": return Math.Abs(rawVal - target) < 0.5;
                    case "!=": return Math.Abs(rawVal - target) >= 0.5;
                    case ">": return rawVal > target;
                    case "<": return rawVal < target;
                    case ">=": return rawVal >= target;
                    case "<=": return rawVal <= target;
                    case "contains": return false;
                    default: return false;
                }
            }
            else if (param.StorageType == StorageType.Integer)
            {
                var intVal = param.AsInteger();
                if (!int.TryParse(filterValue, out int target))
                {
                    // Try matching the value string (e.g., for Yes/No parameters)
                    var valStr = param.AsValueString() ?? "";
                    switch (op)
                    {
                        case "=": return valStr.Equals(filterValue, StringComparison.OrdinalIgnoreCase);
                        case "!=": return !valStr.Equals(filterValue, StringComparison.OrdinalIgnoreCase);
                        default: return false;
                    }
                }

                switch (op)
                {
                    case "=": return intVal == target;
                    case "!=": return intVal != target;
                    case ">": return intVal > target;
                    case "<": return intVal < target;
                    case ">=": return intVal >= target;
                    case "<=": return intVal <= target;
                    default: return false;
                }
            }

            return false;
        }

        private static string BuildCountOutput(List<Element> elements, Document doc, string countParam, string category, bool viewScoped, View activeView)
        {
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elements)
            {
                var param = FormattingHelper.FindParameter(elem, doc, countParam);
                string val;
                if (param == null || !param.HasValue)
                    val = "(empty)";
                else
                    val = FormattingHelper.GetParameterValue(param);

                if (!groups.ContainsKey(val))
                    groups[val] = 0;
                groups[val]++;
            }

            var sb = new StringBuilder();
            sb.AppendLine("COUNT: " + category.ToUpper() + " grouped by " + countParam +
                (viewScoped ? " [view: " + activeView.Name + "]" : ""));
            sb.AppendLine("Total: " + elements.Count);
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            foreach (var kvp in groups.OrderByDescending(x => x.Value))
            {
                sb.AppendLine("  " + kvp.Key + ": " + kvp.Value);
            }

            return sb.ToString();
        }

        private static bool ExtractFlag(List<string> tokens, string flag)
        {
            int idx = tokens.FindIndex(t => t.Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                tokens.RemoveAt(idx);
                return true;
            }
            return false;
        }
    }
}
