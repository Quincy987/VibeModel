using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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

        /// <summary>Formats an internal-units (feet) point as bare mm values: "(x, y, z)".</summary>
        public static string FormatPointMmBare(XYZ feetPoint)
        {
            return "(" + FormatMmValue(feetPoint.X) + ", " + FormatMmValue(feetPoint.Y) + ", " +
                   FormatMmValue(feetPoint.Z) + ")";
        }

        /// <summary>Converts an internal-units (feet) point to a [x, y, z] mm array for JSON payloads.</summary>
        public static double[] ToMmArray(XYZ feetPoint)
        {
            return new[]
            {
                feetPoint.X * 304.8,
                feetPoint.Y * 304.8,
                feetPoint.Z * 304.8
            };
        }

        private static string FormatMmValue(double feet)
        {
            return (feet * 304.8).ToString("F1", CultureInfo.InvariantCulture);
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

        /// <summary>
        /// Gets the preferred level for element creation: active view's level first, then lowest level.
        /// </summary>
        public static Level GetPreferredLevel(Document doc, Autodesk.Revit.UI.UIDocument uiDoc)
        {
            // Prefer the active view's associated level (ActiveGraphicalView tracks the current tab)
            View activeView;
            try { activeView = uiDoc?.ActiveGraphicalView; }
            catch { activeView = null; }
            if (activeView == null)
                activeView = uiDoc?.ActiveView;
            if (activeView?.GenLevel != null)
                return activeView.GenLevel;

            // Fallback: lowest level
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();
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

        /// <summary>
        /// Gets the active graphical view, or null if none is available.
        /// Does NOT fall back to ActiveView (schedules/legends cause downstream failures).
        /// Callers must null-check.
        /// </summary>
        public static View GetActiveGraphicalView(UIDocument uiDoc)
        {
            try { return uiDoc.ActiveGraphicalView; }
            catch { return null; }
        }

        private static readonly Dictionary<string, BuiltInCategory> CategoryAliases =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                { "wall", BuiltInCategory.OST_Walls },
                { "walls", BuiltInCategory.OST_Walls },
                { "floor", BuiltInCategory.OST_Floors },
                { "floors", BuiltInCategory.OST_Floors },
                { "roof", BuiltInCategory.OST_Roofs },
                { "roofs", BuiltInCategory.OST_Roofs },
                { "column", BuiltInCategory.OST_StructuralColumns },
                { "columns", BuiltInCategory.OST_StructuralColumns },
                { "beam", BuiltInCategory.OST_StructuralFraming },
                { "beams", BuiltInCategory.OST_StructuralFraming },
                { "foundation", BuiltInCategory.OST_StructuralFoundation },
                { "foundations", BuiltInCategory.OST_StructuralFoundation },
                { "grid", BuiltInCategory.OST_Grids },
                { "grids", BuiltInCategory.OST_Grids },
                { "door", BuiltInCategory.OST_Doors },
                { "doors", BuiltInCategory.OST_Doors },
                { "window", BuiltInCategory.OST_Windows },
                { "windows", BuiltInCategory.OST_Windows },
                { "room", BuiltInCategory.OST_Rooms },
                { "rooms", BuiltInCategory.OST_Rooms },
                { "pipe", BuiltInCategory.OST_PipeCurves },
                { "pipes", BuiltInCategory.OST_PipeCurves },
                { "duct", BuiltInCategory.OST_DuctCurves },
                { "ducts", BuiltInCategory.OST_DuctCurves },
                { "generic model", BuiltInCategory.OST_GenericModel },
                { "generic models", BuiltInCategory.OST_GenericModel },
                { "furniture", BuiltInCategory.OST_Furniture },
                { "rebar", BuiltInCategory.OST_Rebar },
                { "ceiling", BuiltInCategory.OST_Ceilings },
                { "ceilings", BuiltInCategory.OST_Ceilings },
                { "stair", BuiltInCategory.OST_Stairs },
                { "stairs", BuiltInCategory.OST_Stairs },
                { "ramp", BuiltInCategory.OST_Ramps },
                { "ramps", BuiltInCategory.OST_Ramps },
                { "railing", BuiltInCategory.OST_StairsRailing },
                { "railings", BuiltInCategory.OST_StairsRailing },
                { "curtain panel", BuiltInCategory.OST_CurtainWallPanels },
                { "curtain panels", BuiltInCategory.OST_CurtainWallPanels },
                { "structural connection", BuiltInCategory.OST_StructConnections },
                { "structural connections", BuiltInCategory.OST_StructConnections },
                { "parking", BuiltInCategory.OST_Parking },
                { "site", BuiltInCategory.OST_Site },
                { "topography", BuiltInCategory.OST_Topography },
                { "curtain wall", BuiltInCategory.OST_CurtainWallMullions },
                { "mullion", BuiltInCategory.OST_CurtainWallMullions },
                { "mullions", BuiltInCategory.OST_CurtainWallMullions },
            };

        /// <summary>
        /// Resolves a user-friendly category name to a BuiltInCategory.
        /// 1) Exact alias match, 2) Substring on doc categories, 3) Levenshtein suggestions.
        /// </summary>
        public static (BuiltInCategory? category, string error) ResolveCategory(string input, Document doc)
        {
            if (string.IsNullOrWhiteSpace(input))
                return (null, "ERROR: No category specified.");

            var trimmed = input.Trim();

            // 1) Exact alias match
            if (CategoryAliases.TryGetValue(trimmed, out var cat))
                return (cat, null);

            // 2) Substring match on document categories
            var lowerInput = trimmed.ToLower();
            foreach (Category docCat in doc.Settings.Categories)
            {
                if (docCat.Name.ToLower().Contains(lowerInput))
                {
                    var bic = (BuiltInCategory)docCat.Id.IntegerValue;
                    return (bic, null);
                }
            }

            // 3) Levenshtein suggestions from aliases
            var suggestions = CategoryAliases.Keys
                .Select(k => new { Key = k, Dist = EditDistance(lowerInput, k.ToLower()) })
                .Where(x => x.Dist <= 3)
                .OrderBy(x => x.Dist)
                .Take(3)
                .Select(x => x.Key)
                .ToList();

            var msg = "ERROR: Unknown category '" + trimmed + "'.";
            if (suggestions.Count > 0)
                msg += " Did you mean: " + string.Join(", ", suggestions) + "?";

            return (null, msg);
        }

        /// <summary>
        /// Parses element IDs from string tokens, validates each exists in the document.
        /// </summary>
        public static List<ElementId> ParseElementIds(Document doc, string[] parts)
        {
            var ids = new List<ElementId>();
            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out int idVal))
                {
                    var elemId = new ElementId(idVal);
                    if (doc.GetElement(elemId) != null)
                        ids.Add(elemId);
                }
            }
            return ids;
        }

        /// <summary>
        /// Finds a parameter on an element by name: instance first, then type.
        /// </summary>
        public static Parameter FindParameter(Element elem, Document doc, string paramName)
        {
            var param = elem.Parameters.Cast<Parameter>()
                .FirstOrDefault(p => p.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase));

            if (param != null) return param;

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    param = elemType.Parameters.Cast<Parameter>()
                        .FirstOrDefault(p => p.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase));
                }
            }

            return param;
        }

        private static int EditDistance(string a, string b)
        {
            var la = a.Length;
            var lb = b.Length;
            var d = new int[la + 1, lb + 1];
            for (int i = 0; i <= la; i++) d[i, 0] = i;
            for (int j = 0; j <= lb; j++) d[0, j] = j;
            for (int i = 1; i <= la; i++)
                for (int j = 1; j <= lb; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[la, lb];
        }
    }
}
