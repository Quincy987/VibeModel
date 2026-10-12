using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Writes a parameter from text the way every VibeModel command reads numbers: a plain
    /// number on a length is mm, on an angle degrees — whatever the project's display units are.
    /// Text with its own unit ("0.5 m", "45°") goes through Revit's own parser. Yes/no takes
    /// yes/no/ja/nee/true/false/1/0; an element-id parameter takes an id or the name of a level,
    /// phase or type. Throws InvalidOperationException with a readable message on failure.
    /// </summary>
    internal static class ParamWriter
    {
        public static void Set(Document doc, Parameter p, string name, string value)
        {
            value = value ?? "";
            if (p.IsReadOnly)
                throw new InvalidOperationException("Parameter '" + name + "' is read-only" + ReadOnlyHint(p) + ".");

            switch (p.StorageType)
            {
                case StorageType.String:
                    p.Set(value);
                    return;

                case StorageType.Integer:
                    if (TryYesNo(value, out int yn) && IsYesNo(p)) { p.Set(yn); return; }
                    if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) { p.Set(iv); return; }
                    if (TryYesNo(value, out yn)) { p.Set(yn); return; }
                    if (TrySetValueString(p, value)) return;
                    break;

                case StorageType.Double:
                    if (TryPlainNumber(value, out double num))
                    {
                        var kind = Kind(p, name);
                        if (kind == "length") { p.Set(RevitUnitHelper.MmToFeet(num)); return; }
                        if (kind == "angle") { p.Set(num * Math.PI / 180); return; }
                        if (TrySetValueString(p, value)) return;   // area, volume, …: project units
                        p.Set(num);
                        return;
                    }
                    if (TrySetValueString(p, value)) return;
                    break;

                case StorageType.ElementId:
                    if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idv)) { p.Set(new ElementId(idv)); return; }
                    var byName = FindByName(doc, value.Trim());
                    if (byName != null) { p.Set(byName.Id); return; }
                    throw new InvalidOperationException("'" + name + "' takes an element: give its id, or the name of a level, phase or type ('" + value + "' matches none).");
            }
            throw new InvalidOperationException("Could not set parameter '" + name + "' to '" + value + "'.");
        }

        /// <summary>"length", "angle" or "other" — from the parameter's spec, falling back to its name.</summary>
        private static string Kind(Parameter p, string name)
        {
            try
            {
                var spec = p.Definition.GetDataType();
                if (spec == SpecTypeId.Length) return "length";
                if (spec == SpecTypeId.Angle) return "angle";
                if (spec != null && !string.IsNullOrEmpty(spec.TypeId)) return "other";
            }
            catch { /* older definitions: fall back to the name */ }
            return FormattingHelper.IsDimensionRelated(name) ? "length" : "other";
        }

        private static bool IsYesNo(Parameter p)
        {
            try { return p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
            catch { return false; }
        }

        private static bool TrySetValueString(Parameter p, string value)
        {
            try { return p.SetValueString(value); }
            catch { return false; }
        }

        internal static bool TryPlainNumber(string s, out double v)
            => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        internal static bool TryYesNo(string s, out int v)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "yes": case "ja": case "true": case "waar": case "on": case "aan": v = 1; return true;
                case "no": case "nee": case "false": case "onwaar": case "off": case "uit": v = 0; return true;
                default: v = 0; return false;
            }
        }

        private static Element FindByName(Document doc, string name)
        {
            if (name.Length == 0) return null;
            foreach (var t in new[] { typeof(Level), typeof(Phase) })
            {
                var e = new FilteredElementCollector(doc).OfClass(t).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (e != null) return e;
            }
            return new FilteredElementCollector(doc).WhereElementIsElementType()
                .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string ReadOnlyHint(Parameter p)
        {
            try
            {
                if (p.Id.IntegerValue == (int)BuiltInParameter.WALL_TOP_OFFSET) return " (it only applies when the wall has a top level)";
            }
            catch { }
            return "";
        }

        /// <summary>Writable instance parameter names, for "no such parameter" answers.</summary>
        public static List<string> WritableNames(Element e)
            => e.Parameters.Cast<Parameter>()
                .Where(p => !p.IsReadOnly && p.Definition != null)
                .Select(p => p.Definition.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static Parameter Lookup(Element e, string name)
            => e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition != null && p.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
