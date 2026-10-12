using System;
using System.Collections.Generic;
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
        public string Description => "Place a family instance by family/type name; --rotate, --set \"Param=value\", --level, or --along a line with --spacing/--count";
        public string Usage => PlaceArgs.PlaceUsage;

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorText;
            args = phase.Remaining;

            var a = PlaceArgs.Parse(args, false);
            if (a.Error != null) return "ERROR: " + a.Error;

            var familyName = a.Family;
            var typeName = a.TypeName;
            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.ToLower().Contains(familyName.ToLower()) &&
                    fs.Name.ToLower().Contains(typeName.ToLower()));

            if (symbol == null)
                return "ERROR: Family/type not found matching '" + familyName + "' / '" + typeName + "'.\nUse 'familytypes' to see available families." +
                       LibraryHint(doc, familyName);

            return FamilyPlacer.Run(doc, uiApp.ActiveUIDocument, symbol, a, phase);
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
    }

    public class PlaceIdCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "placeid";
        public string Description => "Place a family instance by type ID; same flags as 'place'";
        public string Usage => PlaceArgs.PlaceIdUsage;

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorText;
            args = phase.Remaining;

            var a = PlaceArgs.Parse(args, true);
            if (a.Error != null) return "ERROR: " + a.Error;

            var symbol = doc.GetElement(new ElementId(a.TypeId)) as FamilySymbol;
            if (symbol == null)
                return "ERROR: Element ID " + a.TypeId + " is not a FamilySymbol";

            return FamilyPlacer.Run(doc, uiApp.ActiveUIDocument, symbol, a, phase);
        }
    }

    /// <summary>
    /// The shared body of `place` / `placeid`: one instance at x y [z], or n instances along a
    /// line or arc — always one transaction, so one Ctrl+Z. Rotation is about the insertion
    /// point, counter-clockwise from project +X. z is an elevation in the same frame as x and y;
    /// a level-hosted family gets there through its offset parameter, and the response says which.
    /// </summary>
    internal static class FamilyPlacer
    {
        private const double ZTolFeet = 1 / 304.8;   // 1 mm

        private sealed class Spot
        {
            public XYZ Point;
            public double RotationDeg;
            public double AlongMm;
        }

        public static string Run(Document doc, UIDocument uiDoc, FamilySymbol symbol, PlaceArgs a, PhaseRequest phase)
        {
            // ---- level ----
            Level level;
            if (a.Level != null)
            {
                level = WallCommand.FindLevel(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList(), a.Level, out var levelErr);
                if (level == null) return "ERROR: " + levelErr;
            }
            else level = FormattingHelper.GetPreferredLevel(doc, uiDoc);

            var structuralType = StructuralTypeOf(symbol);
            FamilyPlacementType placement;
            try { placement = symbol.Family.FamilyPlacementType; }
            catch { placement = FamilyPlacementType.Invalid; }
            bool levelBased = placement == FamilyPlacementType.OneLevelBased || placement == FamilyPlacementType.TwoLevelsBased;
            bool useLevel = level != null && (structuralType != StructuralType.NonStructural || levelBased);

            // As before, a family that is not level-based lands at z = 0 unless --level or z says otherwise.
            double baseZ = a.Z.HasValue ? RevitUnitHelper.MmToFeet(a.Z.Value)
                         : useLevel || (a.Level != null && level != null) ? level.Elevation : 0;

            // ---- positions ----
            var spots = new List<Spot>();
            string lineNote = null, spacingNote = null;
            if (!a.Along)
            {
                spots.Add(new Spot
                {
                    Point = new XYZ(RevitUnitHelper.MmToFeet(a.X), RevitUnitHelper.MmToFeet(a.Y), baseZ),
                    RotationDeg = AlongLayout.Normalize(a.Rotate)
                });
            }
            else
            {
                var curve = AlongCurve(doc, a, out lineNote, out var curveErr);
                if (curve == null) return "ERROR: " + curveErr;
                double lengthMm = RevitUnitHelper.FeetToMm(curve.Length);
                var dists = AlongLayout.Distances(lengthMm, a.Spacing, a.Count, a.Offset, out double actual, out var layoutErr);
                if (dists == null) return "ERROR: " + layoutErr;
                foreach (var d in dists)
                {
                    double t = lengthMm < 1e-9 ? 0 : Math.Max(0, Math.Min(1, d / lengthMm));
                    var p = curve.Evaluate(t, true);
                    var tangent = curve.ComputeDerivatives(t, true).BasisX;
                    spots.Add(new Spot
                    {
                        Point = new XYZ(p.X, p.Y, baseZ),
                        RotationDeg = a.AutoRotate ? AlongLayout.AngleDeg(tangent.X, tangent.Y, a.Rotate) : AlongLayout.Normalize(a.Rotate),
                        AlongMm = d
                    });
                }
                spacingNote = SpacingNote(a, dists.Count, actual, lengthMm);
                if (!(curve is Line) && !(curve is Arc))
                    spacingNote += " (spacing follows the curve's parameter, so it is approximate on a spline)";
            }

            // ---- place, rotate, z, --set: one transaction ----
            var placed = new List<FamilyInstance>();
            string zNote = null;
            var setNotes = new List<string>();
            var error = TransactionHelper.Execute(doc, "VibeModel: Place Family", () =>
            {
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }

                foreach (var s in spots)
                {
                    var inst = useLevel
                        ? doc.Create.NewFamilyInstance(s.Point, symbol, level, structuralType)
                        : doc.Create.NewFamilyInstance(s.Point, symbol, structuralType);
                    phase?.Apply(inst);
                    if (Math.Abs(s.RotationDeg) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(s.Point, s.Point + XYZ.BasisZ), s.RotationDeg * Math.PI / 180);
                    placed.Add(inst);
                }

                if (a.Z.HasValue)
                {
                    doc.Regenerate();
                    foreach (var inst in placed)
                    {
                        var n = FixZ(doc, inst, baseZ);
                        if (zNote == null) zNote = n;
                    }
                }

                foreach (var kv in a.Sets)
                {
                    string shown = null;
                    foreach (var inst in placed)
                    {
                        var p = ParamWriter.Lookup(inst, kv.Key);
                        if (p == null) throw new InvalidOperationException(MissingParam(doc, inst, kv.Key));
                        ParamWriter.Set(doc, p, p.Definition.Name, kv.Value);
                        if (shown == null) shown = p.Definition.Name + " = " + FormattingHelper.GetParameterValue(p);
                    }
                    setNotes.Add(shown);
                }
            });

            if (error != null) return error;

            return a.Along
                ? AlongReport(doc, uiDoc, symbol, a, level, useLevel, placed, spots, lineNote, spacingNote, zNote, setNotes, structuralType, phase)
                : SingleReport(doc, uiDoc, symbol, a, level, useLevel, placed[0], spots[0], zNote, setNotes, structuralType, phase);
        }

        private static StructuralType StructuralTypeOf(FamilySymbol symbol)
        {
            try
            {
                var familyCatId = symbol.Family.FamilyCategory?.Id?.IntegerValue ?? 0;
                if (familyCatId == (int)BuiltInCategory.OST_StructuralColumns) return StructuralType.Column;
                if (familyCatId == (int)BuiltInCategory.OST_StructuralFraming) return StructuralType.Beam;
                if (familyCatId == (int)BuiltInCategory.OST_StructuralFoundation) return StructuralType.Footing;
            }
            catch { /* fallback to NonStructural */ }
            return StructuralType.NonStructural;
        }

        /// <summary>The line to place along: a detail/model line, any element with a location curve (wall, beam), or two points.</summary>
        private static Curve AlongCurve(Document doc, PlaceArgs a, out string note, out string error)
        {
            note = null; error = null;
            if (a.AlongLine != null)
            {
                var l = a.AlongLine;
                note = "(" + Mm.F(l[0]) + ", " + Mm.F(l[1]) + ") → (" + Mm.F(l[2]) + ", " + Mm.F(l[3]) + ")";
                return Line.CreateBound(new XYZ(RevitUnitHelper.MmToFeet(l[0]), RevitUnitHelper.MmToFeet(l[1]), 0),
                                        new XYZ(RevitUnitHelper.MmToFeet(l[2]), RevitUnitHelper.MmToFeet(l[3]), 0));
            }
            var e = doc.GetElement(new ElementId(a.AlongId.Value));
            if (e == null) { error = "--along: element " + a.AlongId + " not found."; return null; }
            Curve c = null;
            if (e is CurveElement ce) c = ce.GeometryCurve;
            else if (e.Location is LocationCurve lc) c = lc.Curve;
            if (c == null)
            {
                error = "--along: element " + a.AlongId + " (" + (e.Category?.Name ?? e.GetType().Name) + ") has no line. " +
                        "Give a detail line, model line, wall or beam id — or x1 y1 x2 y2.";
                return null;
            }
            if (!c.IsBound) { error = "--along: element " + a.AlongId + " is a closed or unbounded curve; draw a line or an arc."; return null; }
            var kind = c is Line ? "line" : c is Arc ? "arc" : c.GetType().Name.ToLowerInvariant();
            var what = e is DetailCurve ? "detail line" : e is ModelCurve ? "model line" : (e.Category?.Name ?? e.GetType().Name);
            note = what + " " + a.AlongId + " (" + kind + ", " + Mm.F(RevitUnitHelper.FeetToMm(c.Length)) + " mm, from " +
                   Pt(c.GetEndPoint(0)) + " to " + Pt(c.GetEndPoint(1)) + ")";
            return c;
        }

        private static string SpacingNote(PlaceArgs a, int n, double actual, double lengthMm)
        {
            var sb = new StringBuilder(n + " instance" + (n == 1 ? "" : "s"));
            if (n > 1)
            {
                sb.Append(", spacing " + Mm.F1(actual) + " mm");
                if (a.Spacing.HasValue && !a.Count.HasValue && Math.Abs(actual - a.Spacing.Value) > 0.05)
                    sb.Append(" (asked " + Mm.F1(a.Spacing.Value) + " mm; reduced so both ends of the " + Mm.F(lengthMm) + " mm line get one)");
            }
            if (a.Offset > 0) sb.Append(", first at " + Mm.F1(a.Offset) + " mm from the start");
            return sb.ToString();
        }

        /// <summary>
        /// Move a placed instance to elevation `want` (feet) through whichever offset parameter
        /// actually moves it: each candidate is tried and checked against the location, and put
        /// back when it does not help. Returns the line for the response.
        /// </summary>
        private static string FixZ(Document doc, FamilyInstance inst, double want)
        {
            if (!(inst.Location is LocationPoint lp)) return "z: ignored — this instance has no insertion point.";
            double actual = lp.Point.Z;
            if (Math.Abs(actual - want) < ZTolFeet) return null;

            var tried = new HashSet<int>();
            var visible = new HashSet<int>(inst.Parameters.Cast<Parameter>().Select(p => p.Id.IntegerValue));
            var candidates = new List<Parameter>();
            foreach (var bip in new[] { BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                                        BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM })
            {
                var p = inst.get_Parameter(bip);
                if (p != null) candidates.Add(p);
            }
            // Families with their own offset parameter ("Height Offset From Level", "Offset"…).
            candidates.AddRange(inst.Parameters.Cast<Parameter>().Where(p => p.Definition != null &&
                (p.Definition.Name.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 p.Definition.Name.IndexOf("elevation", StringComparison.OrdinalIgnoreCase) >= 0) &&
                p.Definition.Name.IndexOf("top", StringComparison.OrdinalIgnoreCase) < 0));

            // The one the modeller sees in Properties first: some families also carry a hidden
            // built-in elevation that moves the instance just as well but cannot be found by name.
            foreach (var p in candidates.OrderBy(p => visible.Contains(p.Id.IntegerValue) ? 0 : 1))
            {
                if (p.IsReadOnly || p.StorageType != StorageType.Double || !tried.Add(p.Id.IntegerValue)) continue;
                double old = p.AsDouble();
                try
                {
                    if (!p.Set(old + (want - actual))) continue;
                    doc.Regenerate();
                    double now = ((LocationPoint)inst.Location).Point.Z;
                    if (Math.Abs(now - want) < ZTolFeet)
                        return "z: " + Mm.F1(RevitUnitHelper.FeetToMm(want)) + " mm via '" + p.Definition.Name + "' = " +
                               Mm.F1(RevitUnitHelper.FeetToMm(p.AsDouble())) + " mm";
                    p.Set(old);
                    doc.Regenerate();
                }
                catch { try { p.Set(old); } catch { } }
            }
            return "WARN z: the instance stays at " + Mm.F1(RevitUnitHelper.FeetToMm(((LocationPoint)inst.Location).Point.Z)) +
                   " mm — none of its offset parameters moves it to " + Mm.F1(RevitUnitHelper.FeetToMm(want)) + " mm.";
        }

        private static string MissingParam(Document doc, FamilyInstance inst, string name)
        {
            var type = doc.GetElement(inst.GetTypeId());
            if (type != null && ParamWriter.Lookup(type, name) != null)
                return "'" + name + "' is a type parameter of '" + type.Name + "' — it changes every instance; use 'set " +
                       type.Id.IntegerValue + " \"" + name + "\" <value>' if that is what you want. Nothing was placed.";
            var names = ParamWriter.WritableNames(inst);
            return "No instance parameter '" + name + "' on " + inst.Symbol.Family.Name + ". Available: " +
                   string.Join(", ", names.Take(60)) + (names.Count > 60 ? " …" : "") + ". Nothing was placed.";
        }

        private static string SingleReport(Document doc, UIDocument uiDoc, FamilySymbol symbol, PlaceArgs a, Level level, bool useLevel,
            FamilyInstance instance, Spot spot, string zNote, List<string> setNotes, StructuralType structuralType, PhaseRequest phase)
        {
            var at = (instance.Location as LocationPoint)?.Point ?? spot.Point;
            var sb = new StringBuilder();
            sb.AppendLine("FAMILY PLACED");
            sb.AppendLine("=============");
            sb.AppendLine();
            sb.AppendLine("ID: " + instance.Id.IntegerValue);
            sb.AppendLine("Family: " + symbol.Family.Name);
            sb.AppendLine("Type: " + symbol.Name);
            sb.AppendLine("Location: (" + Mm.F1(a.X) + ", " + Mm.F1(a.Y) + ", " + Mm.F1(RevitUnitHelper.FeetToMm(at.Z)) + ") mm");
            if (Math.Abs(spot.RotationDeg) > 1e-9) sb.AppendLine("Rotation: " + Mm.F1(spot.RotationDeg) + "° (counter-clockwise from +X)");
            AppendCommon(sb, doc, uiDoc, a, level, useLevel, zNote, setNotes, structuralType, phase);
            return sb.ToString();
        }

        private static string AlongReport(Document doc, UIDocument uiDoc, FamilySymbol symbol, PlaceArgs a, Level level, bool useLevel,
            List<FamilyInstance> placed, List<Spot> spots, string lineNote, string spacingNote, string zNote, List<string> setNotes,
            StructuralType structuralType, PhaseRequest phase)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FAMILIES PLACED ALONG LINE");
            sb.AppendLine("==========================");
            sb.AppendLine();
            sb.AppendLine("Family: " + symbol.Family.Name);
            sb.AppendLine("Type: " + symbol.Name);
            sb.AppendLine("Along: " + lineNote);
            sb.AppendLine("Placed: " + spacingNote);
            sb.AppendLine("IDs: " + string.Join(",", placed.Select(p => p.Id.IntegerValue)));
            if (a.AutoRotate)
                sb.AppendLine("Rotation: along the line" + (Math.Abs(a.Rotate) > 1e-9 ? " " + (a.Rotate > 0 ? "+" : "") + Mm.F1(a.Rotate) + "°" : "") +
                              " → " + Mm.F1(spots[0].RotationDeg) + "°" + (spots.Count > 1 && Math.Abs(spots[spots.Count - 1].RotationDeg - spots[0].RotationDeg) > 0.05
                                  ? " … " + Mm.F1(spots[spots.Count - 1].RotationDeg) + "°" : "") + " (counter-clockwise from +X)");
            else if (Math.Abs(spots[0].RotationDeg) > 1e-9)
                sb.AppendLine("Rotation: " + Mm.F1(spots[0].RotationDeg) + "° (counter-clockwise from +X)");
            int show = Math.Min(spots.Count, 12);
            for (int i = 0; i < show; i++)
                sb.AppendLine("  " + placed[i].Id.IntegerValue + "  at " + Mm.F1(spots[i].AlongMm) + " mm  " + Pt(spots[i].Point));
            if (spots.Count > show) sb.AppendLine("  … " + (spots.Count - show) + " more");
            AppendCommon(sb, doc, uiDoc, a, level, useLevel, zNote, setNotes, structuralType, phase);
            sb.AppendLine("One undo step (Ctrl+Z removes all " + placed.Count + ").");
            return sb.ToString();
        }

        private static void AppendCommon(StringBuilder sb, Document doc, UIDocument uiDoc, PlaceArgs a, Level level, bool useLevel,
            string zNote, List<string> setNotes, StructuralType structuralType, PhaseRequest phase)
        {
            if (useLevel && level != null) sb.AppendLine("Level: " + level.Name + (a.Level != null ? "" : " (the active view's level; --level to choose)"));
            else if (a.Level != null && level != null) sb.AppendLine("Level: not level-based; z set to " + level.Name + "'s elevation");
            if (zNote != null) sb.AppendLine(zNote);
            foreach (var s in setNotes) sb.AppendLine("Set: " + s);
            if (structuralType != StructuralType.NonStructural)
                sb.AppendLine("Structural Type: " + structuralType);
            phase?.AppendNote(sb, doc, uiDoc.ActiveView);
        }

        private static string Pt(XYZ p)
            => "(" + Mm.F(RevitUnitHelper.FeetToMm(p.X)) + ", " + Mm.F(RevitUnitHelper.FeetToMm(p.Y)) + ")";
    }
}
