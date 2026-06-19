using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ListCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "list";
        public string Description => "List elements by category (supports any category + --view flag)";
        public string Usage => "list <category> [--view]";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var rawArgs = args?.Trim() ?? "";
            if (string.IsNullOrEmpty(rawArgs))
                return CommandResult.Error("BAD_ARGS",
                    "Specify element category. Examples: walls, floors, foundations, grids, doors, windows, rooms, families, views, sheets",
                    "Add --view to filter to the active view only.");

            // Parse --view flag
            bool viewScoped = false;
            var argParts = rawArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (argParts.Contains("--view", StringComparer.OrdinalIgnoreCase))
            {
                viewScoped = true;
                argParts.RemoveAll(p => p.Equals("--view", StringComparison.OrdinalIgnoreCase));
            }

            var elementType = string.Join(" ", argParts).Trim().ToLower();
            if (string.IsNullOrEmpty(elementType))
                return CommandResult.Error("BAD_ARGS", "Specify element category.", Usage);

            View activeView = null;
            if (viewScoped)
            {
                activeView = FormattingHelper.GetActiveGraphicalView(uiDoc);
                if (activeView == null)
                    return CommandResult.Error("NO_ACTIVE_VIEW",
                        "No graphical view active. Switch to a plan, section, or 3D view to use --view.",
                        "Switch to a plan, section, or 3D view.");
            }

            IEnumerable<Element> elements;
            string label = elementType.ToUpper();

            switch (elementType)
            {
                case "families":
                case "family":
                    elements = GetCollector(doc, activeView).OfClass(typeof(FamilyInstance)).Cast<Element>();
                    label = "FAMILY INSTANCES";
                    break;
                case "views":
                case "view":
                    elements = GetCollector(doc, activeView).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate).Cast<Element>();
                    label = "VIEWS";
                    break;
                case "sheets":
                case "sheet":
                    elements = GetCollector(doc, activeView).OfClass(typeof(ViewSheet)).Cast<Element>();
                    label = "SHEETS";
                    break;
                default:
                    var (cat, error) = FormattingHelper.ResolveCategory(elementType, doc);
                    if (cat == null)
                        // Preserve the resolver's exact text/guidance verbatim.
                        return CommandResult.Legacy(error);

                    elements = GetCollector(doc, activeView)
                        .OfCategory(cat.Value)
                        .WhereElementIsNotElementType()
                        .Cast<Element>();

                    label = elementType.ToUpper();
                    break;
            }

            var list = elements.Take(100).ToList();
            var total = list.Count;
            bool truncated = total >= 100;

            var sb = new StringBuilder();
            sb.AppendLine(label + " (" + total + " shown" + (truncated ? ", may be more" : "") + ")" +
                          (viewScoped ? " [view: " + activeView.Name + "]" : ""));
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            var elementsData = new List<object>();
            foreach (var elem in list)
            {
                sb.Append("ID: " + elem.Id.IntegerValue + " | ");

                var elemData = new Dictionary<string, object> { { "id", elem.Id.IntegerValue } };

                if (elem is FamilyInstance fi)
                {
                    sb.Append(fi.Symbol?.Family?.Name ?? "?");
                    sb.Append(" : ");
                    sb.Append(fi.Symbol?.Name ?? "?");
                    elemData["family"] = fi.Symbol?.Family?.Name;
                    elemData["symbol"] = fi.Symbol?.Name;
                }
                else
                {
                    sb.Append(elem.Name);
                    elemData["name"] = elem.Name;
                    var tid = elem.GetTypeId();
                    if (tid != ElementId.InvalidElementId)
                    {
                        var et = doc.GetElement(tid);
                        if (et != null)
                        {
                            sb.Append(" | Type: " + et.Name);
                            elemData["type"] = et.Name;
                        }
                    }
                }

                var mark = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && mark.HasValue && !string.IsNullOrEmpty(mark.AsString()))
                {
                    sb.Append(" | Mark: " + mark.AsString());
                    elemData["mark"] = mark.AsString();
                }

                sb.AppendLine();
                elementsData.Add(elemData);
            }

            var data = new Dictionary<string, object>
            {
                { "category", label },
                { "view", viewScoped ? activeView.Name : null },
                { "total", total },
                { "truncated", truncated },
                { "elements", elementsData }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }

        private static FilteredElementCollector GetCollector(Document doc, View view)
        {
            if (view != null)
                return new FilteredElementCollector(doc, view.Id);
            return new FilteredElementCollector(doc);
        }
    }
}
