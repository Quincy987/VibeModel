using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class WhereUsedCommand : IClaudeCommand
    {
        public string Name => "whereused";
        public string Description => "Find all instances of a type, grouped by level";
        public string Usage => "whereused <typeId> | whereused <family> <type>";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: whereused <typeId> | whereused <familyName> <typeName>";

            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            Element typeElement = null;

            if (parts.Length == 1 && int.TryParse(parts[0], out int typeIdVal))
            {
                // By type ID
                typeElement = doc.GetElement(new ElementId(typeIdVal));
                if (typeElement == null)
                    return "ERROR: Element " + typeIdVal + " not found.";
            }
            else
            {
                // By family + type name — try all possible split points
                // since family names may contain spaces (e.g. "NLI_Funderingspaal hout")
                var allSymbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .ToList();

                for (int split = 1; split <= parts.Length && typeElement == null; split++)
                {
                    var familyName = string.Join(" ", parts.Take(split));
                    var typeName = split < parts.Length ? string.Join(" ", parts.Skip(split)) : "";

                    typeElement = allSymbols.FirstOrDefault(fs =>
                        fs.Family.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (string.IsNullOrEmpty(typeName) || fs.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0));
                }

                if (typeElement == null)
                {
                    // Search system types (WallType, FloorType, etc.)
                    var fullInput = string.Join(" ", parts);
                    typeElement = new FilteredElementCollector(doc)
                        .OfClass(typeof(ElementType))
                        .FirstOrDefault(et =>
                            et.Name.IndexOf(fullInput, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (typeElement == null)
                    return "ERROR: Type not found matching '" + input + "'.\nUse 'familytypes' to see available types.";
            }

            // Find instances
            List<Element> instances;

            if (typeElement is FamilySymbol fs2)
            {
                instances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WherePasses(new FamilyInstanceFilter(doc, fs2.Id))
                    .ToList();
            }
            else
            {
                // System type: filter by GetTypeId()
                var targetTypeId = typeElement.Id;

                // Determine which class to search based on type element class
                IEnumerable<Element> candidates;
                if (typeElement is WallType)
                    candidates = new FilteredElementCollector(doc).OfClass(typeof(Wall));
                else if (typeElement is FloorType)
                    candidates = new FilteredElementCollector(doc).OfClass(typeof(Floor));
                else if (typeElement is RoofType)
                    candidates = new FilteredElementCollector(doc).OfClass(typeof(RoofBase));
                else
                {
                    // Generic fallback: search all elements matching the type's category
                    var cat = typeElement.Category;
                    if (cat != null)
                    {
                        candidates = new FilteredElementCollector(doc)
                            .OfCategory((BuiltInCategory)cat.Id.IntegerValue)
                            .WhereElementIsNotElementType();
                    }
                    else
                    {
                        candidates = new FilteredElementCollector(doc)
                            .WhereElementIsNotElementType();
                    }
                }

                instances = candidates.Where(e => e.GetTypeId() == targetTypeId).ToList();
            }

            // Group by level
            var byLevel = new Dictionary<string, List<Element>>();
            foreach (var inst in instances)
            {
                var levelParam = inst.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                    ?? inst.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                    ?? inst.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);

                string levelName = "(no level)";
                if (levelParam != null && levelParam.HasValue)
                    levelName = FormattingHelper.GetLevelName(doc, levelParam.AsElementId());

                if (!byLevel.ContainsKey(levelName))
                    byLevel[levelName] = new List<Element>();
                byLevel[levelName].Add(inst);
            }

            // Build output
            var sb = new StringBuilder();
            sb.AppendLine("WHERE USED: " + typeElement.Name);
            sb.AppendLine("Type ID: " + typeElement.Id.IntegerValue);
            if (typeElement is FamilySymbol fs3)
                sb.AppendLine("Family: " + fs3.Family.Name);
            sb.AppendLine("Total Instances: " + instances.Count);
            sb.AppendLine("=".PadRight(40, '='));

            if (instances.Count == 0)
            {
                sb.AppendLine("\nNo instances found in the model.");
                return sb.ToString();
            }

            foreach (var kvp in byLevel.OrderBy(x => x.Key))
            {
                sb.AppendLine();
                sb.AppendLine(kvp.Key + " (" + kvp.Value.Count + "):");
                var shown = kvp.Value.Take(20).ToList();
                sb.AppendLine("  IDs: " + string.Join(", ", shown.Select(e => e.Id.IntegerValue)));
                if (kvp.Value.Count > 20)
                    sb.AppendLine("  ... and " + (kvp.Value.Count - 20) + " more");
            }

            return sb.ToString();
        }
    }
}
