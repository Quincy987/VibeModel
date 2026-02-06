using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ColorCommand : IClaudeCommand
    {
        public string Name => "color";
        public string Description => "Set element color override in active view";
        public string Usage => "color <elementId> <R> <G> <B> | color <id> reset";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "ERROR: Usage: color <elementId> <R> <G> <B>\n       color <elementId> reset";

            if (!int.TryParse(parts[0], out int idValue))
                return "ERROR: Invalid element ID '" + parts[0] + "'";

            var element = doc.GetElement(new ElementId(idValue));
            if (element == null)
                return "ERROR: Element " + idValue + " not found";

            var activeView = uiDoc.ActiveView;
            if (!activeView.AreGraphicsOverridesAllowed())
                return "ERROR: Active view does not support graphic overrides";

            bool isReset = parts[1].ToLower() == "reset";

            using (var trans = new Transaction(doc, "VibeModel: Color Override"))
            {
                trans.Start();
                try
                {
                    var elemId = new ElementId(idValue);

                    if (isReset)
                    {
                        activeView.SetElementOverrides(elemId, new OverrideGraphicSettings());
                        trans.Commit();
                        return "Color override RESET for element " + idValue + " in view '" + activeView.Name + "'";
                    }

                    if (parts.Length < 4)
                    {
                        trans.RollBack();
                        return "ERROR: Need R G B values (0-255)";
                    }

                    if (!byte.TryParse(parts[1], out byte r) ||
                        !byte.TryParse(parts[2], out byte g) ||
                        !byte.TryParse(parts[3], out byte b))
                    {
                        trans.RollBack();
                        return "ERROR: Invalid RGB values. Use 0-255.";
                    }

                    var color = new Color(r, g, b);
                    var ogs = new OverrideGraphicSettings();

                    // Set projection line color
                    ogs.SetProjectionLineColor(color);

                    // Set surface foreground pattern color with solid fill
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
                    trans.Commit();

                    var sb = new StringBuilder();
                    sb.AppendLine("COLOR SET");
                    sb.AppendLine("=========");
                    sb.AppendLine();
                    sb.AppendLine("Element: " + element.Name + " (ID: " + idValue + ")");
                    sb.AppendLine("Color: RGB(" + r + ", " + g + ", " + b + ")");
                    sb.AppendLine("View: " + activeView.Name);

                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return "ERROR: Failed to set color: " + ex.Message;
                }
            }
        }
    }
}
