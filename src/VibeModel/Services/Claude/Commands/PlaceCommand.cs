using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    public class PlaceCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "place";
        public string Description => "Place a family instance by family/type name";
        public string Usage => "place <family> <type> <x> <y> [z] [--phase <kind>]";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorText;
            args = phase.Remaining;

            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return "ERROR: Usage: place <familyName> <typeName> <x_mm> <y_mm> [z_mm]\n\nUse 'familytypes' to see available families and types.";

            var familyName = parts[0];
            var typeName = parts[1];

            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double xMm) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double yMm))
                return "ERROR: Invalid coordinates";

            double zMm = 0;
            if (parts.Length >= 5 && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                zMm = z;

            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.ToLower().Contains(familyName.ToLower()) &&
                    fs.Name.ToLower().Contains(typeName.ToLower()));

            if (symbol == null)
                return "ERROR: Family/type not found matching '" + familyName + "' / '" + typeName + "'.\nUse 'familytypes' to see available families." +
                       LibraryHint(doc, familyName);

            return PlaceSymbol(doc, uiApp.ActiveUIDocument, symbol, xMm, yMm, zMm, phase);
        }

        /// <summary>
        /// When an office pack is active, a family that is not loaded is looked up in the office
        /// library index, so the answer is "load this file" instead of a dead end.
        /// </summary>
        private static string LibraryHint(Document doc, string familyName)
        {
            try
            {
                var pack = StandardsContext.GetActivePack(doc);
                if (pack == null || pack.LibraryPaths.Count == 0) return "";
                var idx = LibraryIndexService.Get(pack);
                if (idx == null)
                    return "\nThe office library index is not ready (" + LibraryIndexService.NotReadyReason(pack) +
                           ") — try 'library " + familyName + "' in a moment.";
                var hits = idx.Search(familyName, 5);
                if (hits.Count == 0) return "\nNo match in the office library either ('library <term>' searches it).";
                var sb = new StringBuilder("\nFamily not loaded. Found in the office library:");
                foreach (var h in hits)
                    sb.Append("\n  loadfamily \"" + h.Path + "\"");
                sb.Append("\nLoad one, then run 'place' again.");
                return sb.ToString();
            }
            catch { return ""; }
        }

        internal static string PlaceSymbol(Document doc, UIDocument uiDoc, FamilySymbol symbol, double xMm, double yMm, double zMm,
            PhaseRequest phase = null)
        {
            var point = new XYZ(
                RevitUnitHelper.MmToFeet(xMm),
                RevitUnitHelper.MmToFeet(yMm),
                RevitUnitHelper.MmToFeet(zMm));

            // Detect structural type from family category
            var structuralType = StructuralType.NonStructural;
            try
            {
                var familyCatId = symbol.Family.FamilyCategory?.Id?.IntegerValue ?? 0;
                if (familyCatId == (int)BuiltInCategory.OST_StructuralColumns)
                    structuralType = StructuralType.Column;
                else if (familyCatId == (int)BuiltInCategory.OST_StructuralFraming)
                    structuralType = StructuralType.Beam;
                else if (familyCatId == (int)BuiltInCategory.OST_StructuralFoundation)
                    structuralType = StructuralType.Footing;
            }
            catch { /* fallback to NonStructural */ }

            FamilyInstance instance = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Place Family", () =>
            {
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }

                if (structuralType != StructuralType.NonStructural)
                {
                    var level = FormattingHelper.GetPreferredLevel(doc, uiDoc);
                    if (level != null)
                    {
                        instance = doc.Create.NewFamilyInstance(point, symbol, level, structuralType);
                        phase?.Apply(instance);
                        return;
                    }
                }

                instance = doc.Create.NewFamilyInstance(point, symbol, structuralType);
                phase?.Apply(instance);
            });

            if (error != null) return error;

            var sb = new StringBuilder();
            sb.AppendLine("FAMILY PLACED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + instance.Id.IntegerValue);
            sb.AppendLine("Family: " + symbol.Family.Name);
            sb.AppendLine("Type: " + symbol.Name);
            sb.AppendLine("Location: (" + xMm + ", " + yMm + ", " + zMm + ") mm");
            if (structuralType != StructuralType.NonStructural)
                sb.AppendLine("Structural Type: " + structuralType);
            phase?.AppendNote(sb, doc, uiDoc.ActiveView);

            return sb.ToString();
        }
    }

    public class PlaceIdCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "placeid";
        public string Description => "Place a family instance by type ID";
        public string Usage => "placeid <typeId> <x> <y> [z] [--phase <kind>]";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorText;
            args = phase.Remaining;

            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return "ERROR: Usage: placeid <typeId> <x_mm> <y_mm> [z_mm]";

            if (!int.TryParse(parts[0], out int typeId))
                return "ERROR: Invalid type ID";

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double xMm) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double yMm))
                return "ERROR: Invalid coordinates";

            double zMm = 0;
            if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                zMm = z;

            var symbol = doc.GetElement(new ElementId(typeId)) as FamilySymbol;
            if (symbol == null)
                return "ERROR: Element ID " + typeId + " is not a FamilySymbol";

            return PlaceCommand.PlaceSymbol(doc, uiApp.ActiveUIDocument, symbol, xMm, yMm, zMm, phase);
        }
    }
}
