using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class FamilyCommand : IClaudeCommand
    {
        public string Name => "family";
        public string Description => "Family info for selected element";
        public string Usage => "family";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
                return "ERROR: No element selected";

            var element = doc.GetElement(selectedIds.First());
            if (!(element is FamilyInstance fi))
                return "ERROR: Selected element is not a FamilyInstance (it's a " + element.GetType().Name + ")";

            var sb = new StringBuilder();
            var family = fi.Symbol?.Family;
            var symbol = fi.Symbol;

            sb.AppendLine("FAMILY INFO");
            sb.AppendLine("===========");
            sb.AppendLine();
            sb.AppendLine("Family Name: " + (family?.Name ?? "N/A"));
            sb.AppendLine("Type Name: " + (symbol?.Name ?? "N/A"));
            sb.AppendLine("Category: " + (family?.FamilyCategory?.Name ?? "N/A"));
            sb.AppendLine("Family ID: " + (family?.Id.IntegerValue.ToString() ?? "N/A"));
            sb.AppendLine("Type ID: " + (symbol?.Id.IntegerValue.ToString() ?? "N/A"));

            if (family != null)
            {
                try
                {
                    var isShared = family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1;
                    sb.AppendLine("Is Shared: " + isShared);
                }
                catch { }
            }

            if (fi.SuperComponent != null)
            {
                sb.AppendLine();
                sb.AppendLine("NESTED FAMILY - Host:");
                sb.AppendLine("  Host ID: " + fi.SuperComponent.Id.IntegerValue);
                if (fi.SuperComponent is FamilyInstance hostFi)
                {
                    sb.AppendLine("  Host Family: " + (hostFi.Symbol?.Family?.Name ?? "N/A"));
                }
            }

            var subComponents = fi.GetSubComponentIds();
            if (subComponents.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("SUB COMPONENTS (" + subComponents.Count + "):");
                foreach (var subId in subComponents)
                {
                    var subElem = doc.GetElement(subId);
                    if (subElem is FamilyInstance subFi)
                    {
                        sb.AppendLine("  ID " + subId.IntegerValue + ": " + (subFi.Symbol?.Family?.Name ?? "Unknown") + " - " + (subFi.Symbol?.Name ?? ""));
                    }
                }
            }

            if (symbol != null)
            {
                sb.AppendLine();
                sb.AppendLine("TYPE PARAMETERS (dimension-related):");
                var dimParams = symbol.Parameters.Cast<Parameter>()
                    .Where(p => FormattingHelper.IsDimensionRelated(p.Definition.Name))
                    .OrderBy(p => p.Definition.Name)
                    .ToList();

                foreach (var param in dimParams)
                {
                    sb.AppendLine("  " + param.Definition.Name + ": " + FormattingHelper.GetParameterValue(param));
                }
            }

            return sb.ToString();
        }
    }
}
