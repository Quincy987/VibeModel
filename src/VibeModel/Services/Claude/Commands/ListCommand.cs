using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ListCommand : IClaudeCommand
    {
        public string Name => "list";
        public string Description => "List elements by category (supports any category + --view flag)";
        public string Usage => "list <category> [--view]";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var rawArgs = args?.Trim() ?? "";
            if (string.IsNullOrEmpty(rawArgs))
                return "ERROR: Specify element category. Examples: walls, floors, foundations, grids, doors, windows, rooms, families, views, sheets\n\nUse --view flag to filter to active view only.";

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
                return "ERROR: Specify element category.";

            View activeView = null;
            if (viewScoped)
            {
                activeView = FormattingHelper.GetActiveGraphicalView(uiDoc);
                if (activeView == null)
                    return "ERROR: No graphical view active. Switch to a plan, section, or 3D view to use --view.";
            }

            IEnumerable<Element> elements;
            string label = elementType.ToUpper();

            // Special cases that use OfClass (not OfCategory)
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
                    // Resolve via shared category resolution
                    var (cat, error) = FormattingHelper.ResolveCategory(elementType, doc);
                    if (cat == null)
                        return error;

                    elements = GetCollector(doc, activeView)
                        .OfCategory(cat.Value)
                        .WhereElementIsNotElementType()
                        .Cast<Element>();

                    label = elementType.ToUpper();
                    break;
            }

            var list = elements.Take(100).ToList();
            var total = list.Count;

            var sb = new StringBuilder();
            sb.AppendLine(label + " (" + total + " shown" + (total >= 100 ? ", may be more" : "") + ")" + (viewScoped ? " [view: " + activeView.Name + "]" : ""));
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            foreach (var elem in list)
            {
                sb.Append("ID: " + elem.Id.IntegerValue + " | ");

                if (elem is FamilyInstance fi)
                {
                    sb.Append(fi.Symbol?.Family?.Name ?? "?");
                    sb.Append(" : ");
                    sb.Append(fi.Symbol?.Name ?? "?");
                }
                else
                {
                    sb.Append(elem.Name);
                    var tid = elem.GetTypeId();
                    if (tid != ElementId.InvalidElementId)
                    {
                        var et = doc.GetElement(tid);
                        if (et != null)
                            sb.Append(" | Type: " + et.Name);
                    }
                }

                var mark = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && mark.HasValue && !string.IsNullOrEmpty(mark.AsString()))
                    sb.Append(" | Mark: " + mark.AsString());

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static FilteredElementCollector GetCollector(Document doc, View view)
        {
            if (view != null)
                return new FilteredElementCollector(doc, view.Id);
            return new FilteredElementCollector(doc);
        }
    }
}
