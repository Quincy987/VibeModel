using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ColorSplashCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "colorsplash";
        public string Description => "Color elements by parameter value (visual grouping)";
        public string Usage => "colorsplash <category> <paramName> | colorsplash reset [category]";

        // Static state for reset within same session
        private static List<ElementId> _lastColoredIds;
        private static ElementId _lastColoredViewId;
        private static string _lastColoredCategory;

        private static readonly Color[] Palette = new Color[]
        {
            new Color(228, 26, 28),     // Red
            new Color(55, 126, 184),    // Blue
            new Color(77, 175, 74),     // Green
            new Color(152, 78, 163),    // Purple
            new Color(255, 127, 0),     // Orange
            new Color(255, 255, 51),    // Yellow
            new Color(166, 86, 40),     // Brown
            new Color(247, 129, 191),   // Pink
            new Color(153, 153, 153),   // Gray
            new Color(0, 191, 255),     // Deep Sky Blue
            new Color(50, 205, 50),     // Lime Green
            new Color(220, 20, 60),     // Crimson
        };

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: colorsplash <category> <paramName> | colorsplash reset [category]\n\nExamples:\n  colorsplash walls Mark\n  colorsplash reset\n  colorsplash reset walls";

            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Handle reset
            if (parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                return HandleReset(doc, uiDoc, parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : null);
            }

            if (parts.Length < 2)
                return "ERROR: Specify both category and parameter name.\nUsage: colorsplash <category> <paramName>";

            var categoryInput = parts[0];
            var paramName = string.Join(" ", parts.Skip(1));

            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";

            if (!view.AreGraphicsOverridesAllowed())
                return "ERROR: Active view does not support graphic overrides.";

            var (cat, error) = FormattingHelper.ResolveCategory(categoryInput, doc);
            if (cat == null) return error;

            // Collect elements in active view
            var elements = new FilteredElementCollector(doc, view.Id)
                .OfCategory(cat.Value)
                .WhereElementIsNotElementType()
                .ToList();

            if (elements.Count == 0)
                return "ERROR: No elements of category '" + categoryInput + "' found in active view.";

            // Group by parameter value
            var groups = new Dictionary<string, List<Element>>(StringComparer.OrdinalIgnoreCase);
            foreach (var elem in elements)
            {
                var param = FormattingHelper.FindParameter(elem, doc, paramName);
                string val;
                if (param == null || !param.HasValue)
                    val = "(empty)";
                else
                    val = FormattingHelper.GetParameterValue(param);

                if (!groups.ContainsKey(val))
                    groups[val] = new List<Element>();
                groups[val].Add(elem);
            }

            // Find solid fill pattern
            var solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

            // Apply colors
            var coloredIds = new List<ElementId>();
            var legend = new StringBuilder();
            int colorIndex = 0;

            var txError = TransactionHelper.Execute(doc, "VibeModel: ColorSplash", () =>
            {
                foreach (var kvp in groups.OrderByDescending(x => x.Value.Count))
                {
                    var color = GetColor(colorIndex);
                    colorIndex++;

                    legend.AppendLine("  " + kvp.Key + " (" + kvp.Value.Count + ") -> RGB(" + color.Red + "," + color.Green + "," + color.Blue + ")");

                    foreach (var elem in kvp.Value)
                    {
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(color);

                        if (solidFill != null)
                        {
                            ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                            ogs.SetSurfaceForegroundPatternColor(color);
                        }

                        view.SetElementOverrides(elem.Id, ogs);
                        coloredIds.Add(elem.Id);
                    }
                }
            });

            if (txError != null) return txError;

            // Track state for reset
            _lastColoredIds = coloredIds;
            _lastColoredViewId = view.Id;
            _lastColoredCategory = categoryInput;

            var sb = new StringBuilder();
            sb.AppendLine("COLORSPLASH APPLIED");
            sb.AppendLine("===================");
            sb.AppendLine();
            sb.AppendLine("Category: " + categoryInput);
            sb.AppendLine("Parameter: " + paramName);
            sb.AppendLine("View: " + view.Name);
            sb.AppendLine("Elements: " + coloredIds.Count);
            sb.AppendLine("Groups: " + groups.Count);
            sb.AppendLine();
            sb.AppendLine("LEGEND:");
            sb.Append(legend);
            sb.AppendLine();
            sb.AppendLine("Use 'colorsplash reset' to clear overrides.");

            return sb.ToString();
        }

        private string HandleReset(Document doc, UIDocument uiDoc, string categoryArg)
        {
            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";

            List<ElementId> idsToReset = null;

            // Fast path: use tracked state from same session
            if (categoryArg == null && _lastColoredIds != null && _lastColoredIds.Count > 0 && _lastColoredViewId == view.Id)
            {
                idsToReset = _lastColoredIds;
            }
            else if (categoryArg != null)
            {
                // Category argument: collect all elements of category in view and reset
                var (cat, error) = FormattingHelper.ResolveCategory(categoryArg, doc);
                if (cat == null) return error;

                idsToReset = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(cat.Value)
                    .WhereElementIsNotElementType()
                    .Select(e => e.Id)
                    .ToList();

                if (idsToReset.Count == 0)
                    return "ERROR: No elements of category '" + categoryArg + "' in active view.";
            }
            else
            {
                return "ERROR: No colorsplash state from this session.\nUse 'colorsplash reset <category>' to reset by category.\nExample: colorsplash reset walls";
            }

            var txError = TransactionHelper.Execute(doc, "VibeModel: ColorSplash Reset", () =>
            {
                foreach (var id in idsToReset)
                {
                    view.SetElementOverrides(id, new OverrideGraphicSettings());
                }
            });

            if (txError != null) return txError;

            // Clear tracked state
            _lastColoredIds = null;
            _lastColoredViewId = null;
            _lastColoredCategory = null;

            return "ColorSplash RESET: cleared overrides on " + idsToReset.Count + " elements in view '" + view.Name + "'.";
        }

        private static Color GetColor(int index)
        {
            if (index < Palette.Length)
                return Palette[index];

            // Cycle with darker variants
            var baseColor = Palette[index % Palette.Length];
            int cycle = index / Palette.Length;
            double factor = Math.Max(0.4, 1.0 - cycle * 0.3);
            return new Color(
                (byte)(baseColor.Red * factor),
                (byte)(baseColor.Green * factor),
                (byte)(baseColor.Blue * factor));
        }
    }
}
