using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class FamilyTypesCommand : IClaudeCommand
    {
        public string Name => "familytypes";
        public string Description => "List available family types";
        public string Usage => "familytypes [category]";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var filter = (args ?? "").Trim();

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => string.IsNullOrEmpty(filter) ||
                    (fs.Family.FamilyCategory?.Name ?? "").ToLower().Contains(filter.ToLower()) ||
                    fs.Family.Name.ToLower().Contains(filter.ToLower()))
                .OrderBy(fs => fs.Family.FamilyCategory?.Name ?? "")
                .ThenBy(fs => fs.Family.Name)
                .ThenBy(fs => fs.Name)
                .ToList();

            if (symbols.Count == 0)
                return "No family types found" + (string.IsNullOrEmpty(filter) ? "" : " matching '" + filter + "'");

            var sb = new StringBuilder();
            sb.AppendLine("FAMILY TYPES" + (string.IsNullOrEmpty(filter) ? "" : " matching '" + filter + "'"));
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            string lastCategory = null;
            string lastFamily = null;
            int count = 0;

            foreach (var fs in symbols)
            {
                if (count >= 200)
                {
                    sb.AppendLine("... truncated (200 shown of " + symbols.Count + ")");
                    break;
                }

                var catName = fs.Family.FamilyCategory?.Name ?? "(none)";
                if (catName != lastCategory)
                {
                    if (lastCategory != null) sb.AppendLine();
                    sb.AppendLine("[" + catName + "]");
                    lastCategory = catName;
                    lastFamily = null;
                }

                if (fs.Family.Name != lastFamily)
                {
                    sb.AppendLine("  " + fs.Family.Name + ":");
                    lastFamily = fs.Family.Name;
                }

                sb.AppendLine("    - " + fs.Name + " (ID: " + fs.Id.IntegerValue + ")");
                count++;
            }

            sb.AppendLine();
            sb.AppendLine("Total: " + symbols.Count + " types");
            sb.AppendLine();
            sb.AppendLine("Use 'place <family> <type> <x> <y>' or 'placeid <typeId> <x> <y>' to place.");

            return sb.ToString();
        }
    }
}
