using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class MeasureCommand : IClaudeCommand
    {
        public string Name => "measure";
        public string Description => "Measure distance between two elements or points";
        public string Usage => "measure <id1> <id2> | measure <x1> <y1> <z1> <x2> <y2> <z2> | measure selected";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: measure <id1> <id2> | measure <x1> <y1> <z1> <x2> <y2> <z2> | measure selected\n\nCoordinates in mm.";

            XYZ point1, point2;

            if (input.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                var selectedIds = uiDoc.Selection.GetElementIds().ToList();
                if (selectedIds.Count != 2)
                    return "ERROR: Select exactly 2 elements. Currently selected: " + selectedIds.Count;

                point1 = GetElementPoint(doc, selectedIds[0]);
                point2 = GetElementPoint(doc, selectedIds[1]);

                if (point1 == null || point2 == null)
                    return "ERROR: Could not determine location for one or both selected elements.";
            }
            else
            {
                var parts = input.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2)
                {
                    // Two element IDs
                    if (!int.TryParse(parts[0], out int id1) || !int.TryParse(parts[1], out int id2))
                        return "ERROR: Invalid element IDs.";

                    var elem1 = doc.GetElement(new ElementId(id1));
                    var elem2 = doc.GetElement(new ElementId(id2));
                    if (elem1 == null) return "ERROR: Element " + id1 + " not found.";
                    if (elem2 == null) return "ERROR: Element " + id2 + " not found.";

                    point1 = GetElementPoint(doc, new ElementId(id1));
                    point2 = GetElementPoint(doc, new ElementId(id2));

                    if (point1 == null) return "ERROR: Could not determine location for element " + id1 + ".";
                    if (point2 == null) return "ERROR: Could not determine location for element " + id2 + ".";
                }
                else if (parts.Length == 6)
                {
                    // Two points in mm
                    if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x1) ||
                        !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y1) ||
                        !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z1) ||
                        !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x2) ||
                        !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double y2) ||
                        !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double z2))
                        return "ERROR: Invalid coordinates. Provide 6 numbers in mm: x1 y1 z1 x2 y2 z2";

                    point1 = new XYZ(RevitUnitHelper.MmToFeet(x1), RevitUnitHelper.MmToFeet(y1), RevitUnitHelper.MmToFeet(z1));
                    point2 = new XYZ(RevitUnitHelper.MmToFeet(x2), RevitUnitHelper.MmToFeet(y2), RevitUnitHelper.MmToFeet(z2));
                }
                else
                {
                    return "ERROR: Provide 2 element IDs, 6 coordinates (mm), or 'selected'.";
                }
            }

            // Calculate distances
            var delta = point2 - point1;
            double dist3D = delta.GetLength() * 304.8;
            double distHoriz = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) * 304.8;
            double distVert = Math.Abs(delta.Z) * 304.8;
            double dX = delta.X * 304.8;
            double dY = delta.Y * 304.8;
            double dZ = delta.Z * 304.8;

            // Slope angle (from horizontal)
            double slopeAngle = 0;
            double horizLen = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (horizLen > 1e-9)
                slopeAngle = Math.Atan2(Math.Abs(delta.Z), horizLen) * 180.0 / Math.PI;
            else if (Math.Abs(delta.Z) > 1e-9)
                slopeAngle = 90.0;

            // Bearing angle (from Y-axis / North, clockwise)
            double bearingAngle = 0;
            if (horizLen > 1e-9)
            {
                bearingAngle = Math.Atan2(delta.X, delta.Y) * 180.0 / Math.PI;
                if (bearingAngle < 0) bearingAngle += 360;
            }

            var sb = new StringBuilder();
            sb.AppendLine("MEASUREMENT");
            sb.AppendLine("===========");
            sb.AppendLine();
            sb.AppendLine("Point 1: " + FormattingHelper.FormatPoint(point1));
            sb.AppendLine("Point 2: " + FormattingHelper.FormatPoint(point2));
            sb.AppendLine();
            sb.AppendLine("3D Distance:   " + dist3D.ToString("F1", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("Horizontal:    " + distHoriz.ToString("F1", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("Vertical:      " + distVert.ToString("F1", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine();
            sb.AppendLine("dX: " + dX.ToString("F1", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("dY: " + dY.ToString("F1", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("dZ: " + dZ.ToString("F1", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine();
            sb.AppendLine("Slope Angle:   " + slopeAngle.ToString("F1", CultureInfo.InvariantCulture) + " deg");
            sb.AppendLine("Bearing Angle: " + bearingAngle.ToString("F1", CultureInfo.InvariantCulture) + " deg (from North, clockwise)");

            return sb.ToString();
        }

        private static XYZ GetElementPoint(Document doc, ElementId id)
        {
            var elem = doc.GetElement(id);
            if (elem == null) return null;

            if (elem.Location is LocationPoint lp)
                return lp.Point;

            if (elem.Location is LocationCurve lc)
                return lc.Curve.Evaluate(0.5, true);

            // Fallback: bounding box center
            var bbox = elem.get_BoundingBox(null);
            if (bbox != null)
                return (bbox.Min + bbox.Max) / 2.0;

            return null;
        }
    }
}
