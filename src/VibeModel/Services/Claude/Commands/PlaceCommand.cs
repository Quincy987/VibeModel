using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class PlaceCommand : IClaudeCommand
    {
        public string Name => "place";
        public string Description => "Place a family instance by family/type name";
        public string Usage => "place <family> <type> <x> <y> [z]";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return "ERROR: Usage: place <familyName> <typeName> <x_mm> <y_mm> [z_mm]\n\nUse 'familytypes' to see available families and types.";

            var familyName = parts[0];
            var typeName = parts[1];

            if (!double.TryParse(parts[2], out double xMm) ||
                !double.TryParse(parts[3], out double yMm))
                return "ERROR: Invalid coordinates";

            double zMm = 0;
            if (parts.Length >= 5 && double.TryParse(parts[4], out double z))
                zMm = z;

            // Find matching FamilySymbol
            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.ToLower().Contains(familyName.ToLower()) &&
                    fs.Name.ToLower().Contains(typeName.ToLower()));

            if (symbol == null)
                return "ERROR: Family/type not found matching '" + familyName + "' / '" + typeName + "'.\nUse 'familytypes' to see available families.";

            var point = new XYZ(
                RevitUnitHelper.MmToFeet(xMm),
                RevitUnitHelper.MmToFeet(yMm),
                RevitUnitHelper.MmToFeet(zMm));

            FamilyInstance instance = null;
            using (var trans = new Transaction(doc, "VibeModel: Place Family"))
            {
                trans.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    instance = doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return "ERROR: Failed to place family: " + ex.Message;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("FAMILY PLACED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + instance.Id.IntegerValue);
            sb.AppendLine("Family: " + symbol.Family.Name);
            sb.AppendLine("Type: " + symbol.Name);
            sb.AppendLine("Location: (" + xMm + ", " + yMm + ", " + zMm + ") mm");

            return sb.ToString();
        }
    }

    public class PlaceIdCommand : IClaudeCommand
    {
        public string Name => "placeid";
        public string Description => "Place a family instance by type ID";
        public string Usage => "placeid <typeId> <x> <y> [z]";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return "ERROR: Usage: placeid <typeId> <x_mm> <y_mm> [z_mm]";

            if (!int.TryParse(parts[0], out int typeId))
                return "ERROR: Invalid type ID";

            if (!double.TryParse(parts[1], out double xMm) ||
                !double.TryParse(parts[2], out double yMm))
                return "ERROR: Invalid coordinates";

            double zMm = 0;
            if (parts.Length >= 4 && double.TryParse(parts[3], out double z))
                zMm = z;

            var symbol = doc.GetElement(new ElementId(typeId)) as FamilySymbol;
            if (symbol == null)
                return "ERROR: Element ID " + typeId + " is not a FamilySymbol";

            var point = new XYZ(
                RevitUnitHelper.MmToFeet(xMm),
                RevitUnitHelper.MmToFeet(yMm),
                RevitUnitHelper.MmToFeet(zMm));

            FamilyInstance instance = null;
            using (var trans = new Transaction(doc, "VibeModel: Place Family"))
            {
                trans.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    instance = doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return "ERROR: Failed to place family: " + ex.Message;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("FAMILY PLACED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + instance.Id.IntegerValue);
            sb.AppendLine("Family: " + symbol.Family.Name);
            sb.AppendLine("Type: " + symbol.Name);
            sb.AppendLine("Location: (" + xMm + ", " + yMm + ", " + zMm + ") mm");

            return sb.ToString();
        }
    }
}
