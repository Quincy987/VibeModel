using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ColorCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "color";
        public string Description => "Set element color override in active view";
        public string Usage => "color <elementId> <R> <G> <B> | color <id> reset";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "ERROR: Usage: color <elementId> <R> <G> <B>\n       color <elementId> reset";

            if (!int.TryParse(parts[0], out int idValue))
                return "ERROR: Invalid element ID '" + parts[0] + "'";

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return "ERROR: Element " + idValue + " not found";

            var activeView = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (activeView == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";
            if (!activeView.AreGraphicsOverridesAllowed())
                return "ERROR: Active view does not support graphic overrides";

            var elemId = new ElementId(idValue);
            bool isReset = parts[1].ToLower() == "reset";

            if (isReset)
            {
                var error = TransactionHelper.Execute(doc, "VibeModel: Reset Color", () =>
                {
                    activeView.SetElementOverrides(elemId, new OverrideGraphicSettings());
                });
                return error ?? "Color override RESET for element " + idValue + " in view '" + activeView.Name + "'";
            }

            if (parts.Length < 4)
                return "ERROR: Need R G B values (0-255)";

            if (!byte.TryParse(parts[1], out byte r) ||
                !byte.TryParse(parts[2], out byte g) ||
                !byte.TryParse(parts[3], out byte b))
            {
                return "ERROR: Invalid RGB values. Use 0-255.";
            }

            var color = new Color(r, g, b);
            var errorMsg = TransactionHelper.Execute(doc, "VibeModel: Color Override", () =>
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);

                var solidFill = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

                if (solidFill != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                }

                activeView.SetElementOverrides(elemId, ogs);
            });

            if (errorMsg != null) return errorMsg;

            var sb = new StringBuilder();
            sb.AppendLine("COLOR SET");
            sb.AppendLine("=========");
            sb.AppendLine();
            sb.AppendLine("Element: " + element.Name + " (ID: " + idValue + ")");
            sb.AppendLine("Color: RGB(" + r + ", " + g + ", " + b + ")");
            sb.AppendLine("View: " + activeView.Name);

            return sb.ToString();
        }
    }
}
