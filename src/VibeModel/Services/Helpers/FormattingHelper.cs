using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Shared formatting utilities for command output.
    /// </summary>
    public static class FormattingHelper
    {
        public static string FormatLength(double feetValue)
        {
            double mm = feetValue * 304.8;
            return mm.ToString("F1", CultureInfo.InvariantCulture) + " mm";
        }

        public static string FormatPoint(XYZ point)
        {
            return "(" + FormatLength(point.X) + ", " + FormatLength(point.Y) + ", " + FormatLength(point.Z) + ")";
        }

        public static string GetLevelName(Document doc, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId)
                return "(none)";

            if (levelId.IntegerValue < 0)
                return "Unlimited";

            var level = doc.GetElement(levelId) as Level;
            return level?.Name ?? "(ID: " + levelId.IntegerValue + ")";
        }

        public static string GetParameterValue(Parameter param)
        {
            if (!param.HasValue) return "(no value)";

            switch (param.StorageType)
            {
                case StorageType.Double:
                    var doubleVal = param.AsDouble();
                    if (IsDimensionRelated(param.Definition.Name))
                        return FormatLength(doubleVal);
                    return doubleVal.ToString("F4", CultureInfo.InvariantCulture);

                case StorageType.Integer:
                    return param.AsInteger().ToString();

                case StorageType.String:
                    return param.AsString() ?? "(null)";

                case StorageType.ElementId:
                    return "ElementId(" + param.AsElementId().IntegerValue + ")";

                default:
                    return "(unknown)";
            }
        }

        public static bool IsDimensionRelated(string paramName)
        {
            var lower = paramName.ToLower();
            return lower.Contains("size") ||
                   lower.Contains("dim") ||
                   lower.Contains("width") ||
                   lower.Contains("height") ||
                   lower.Contains("length") ||
                   lower.Contains("diameter") ||
                   lower.Contains("radius") ||
                   lower.Contains("offset") ||
                   lower.Contains("depth") ||
                   lower.Contains("thick") ||
                   lower.Contains("elevation") ||
                   lower.Contains("breedte") ||
                   lower.Contains("hoogte") ||
                   lower.Contains("lengte") ||
                   lower.Contains("dikte");
        }

        public static int CountElements<T>(Document doc) where T : Element
        {
            return new FilteredElementCollector(doc).OfClass(typeof(T)).Count();
        }

        public static int CountElements<T>(Document doc, BuiltInCategory category) where T : Element
        {
            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .Count();
        }
    }
}
