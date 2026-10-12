using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// 2D drafting in a view (sheets 2): a filled region from a closed polygon, or a detail
    /// component (detail item family) at a point. Coordinates are in the view's own frame —
    /// plan: project x,y mm; section/elevation: x along the view, y elevation; sheet: paper mm
    /// (the convention of `text`). Read back with `list regions --view`.
    /// </summary>
    public class RegionCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "region";
        public string Description => "Filled region in a view from --points x,y x,y x,y ... [--type <filled region type>], or a detail item " +
                                     "--detail \"Family : Type\" --at x y [--rotate deg]; view frame coordinates like 'text'";
        public string Usage => RegionArgs.Usage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = RegionArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            var view = SheetHelper.FindView(doc, a.View);
            if (view == null) return CommandResult.Error("ELEMENT_NOT_FOUND", "No view '" + a.View + "'.", "Run 'views'.");
            if (view is View3D || view is ViewSchedule || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' (" + view.ViewType + ") cannot hold 2D detail elements.",
                    "Use a plan, section, elevation, detail/drafting view or a sheet.");
            return a.Points != null ? Filled(doc, view, a) : Detail(doc, view, a);
        }

        private static CommandResult Filled(Document doc, View view, RegionArgs a)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            if (types.Count == 0) return CommandResult.Error("TYPE_NOT_LOADED", "The document has no filled region types.", null);
            FilledRegionType type;
            if (a.Type != null)
            {
                type = types.FirstOrDefault(t => t.Name.Equals(a.Type, StringComparison.OrdinalIgnoreCase));
                if (type == null)
                {
                    var hits = types.Where(t => t.Name.IndexOf(a.Type, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (hits.Count != 1)
                        return CommandResult.Error("ELEMENT_NOT_FOUND", (hits.Count > 1 ? "'" + a.Type + "' matches several filled region types" : "No filled region type '" + a.Type + "'") +
                            ". Types: " + string.Join(", ", (hits.Count > 1 ? hits : types).Select(t => "'" + t.Name + "'").Take(40)) + ".", "Give the exact type name.");
                    type = hits[0];
                }
            }
            else
            {
                type = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType)) as FilledRegionType ?? types[0];
            }

            string convention = null;
            var pts = a.Points.Select(p => SheetHelper.ViewPoint(doc, view, p[0], p[1], out convention)).ToList();
            FilledRegion region = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Filled Region", () =>
            {
                var loop = new CurveLoop();
                for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                region = FilledRegion.Create(doc, type.Id, view.Id, new List<CurveLoop> { loop });
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR", "The outline must be closed, not cross itself, and lie in the view's plane.");

            double areaM2 = Math.Abs(Polygon.Area(a.Points)) / 1e6;
            var sb = new StringBuilder();
            sb.AppendLine("FILLED REGION CREATED");
            sb.AppendLine("=====================");
            sb.AppendLine("ID: " + region.Id.IntegerValue);
            sb.AppendLine("Type: " + type.Name + (a.Type == null ? "  (default; --type <name> to choose)" : ""));
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ")");
            sb.AppendLine("Outline: " + a.Points.Count + " corners, " + areaM2.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m², " + convention);
            sb.AppendLine("Next: 'list regions --view' with the view active; 'screenshot --view " + view.Id.IntegerValue + "' to look at it.");
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "created", new List<object> { region.Id.IntegerValue } },
                { "type", type.Name }, { "view", view.Id.IntegerValue }, { "corners", a.Points.Count }, { "areaM2", Math.Round(areaM2, 3) }
            });
        }

        private static CommandResult Detail(Document doc, View view, RegionArgs a)
        {
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_DetailComponents)
                .Cast<FamilySymbol>().ToList();
            if (symbols.Count == 0)
                return CommandResult.Error("TYPE_NOT_LOADED", "No detail item families are loaded.", "Load one with 'library <search>' + 'loadfamily'.");
            var symbol = FindSymbol(symbols, a.Detail, out var candidates);
            if (symbol == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", (candidates.Count > 1 ? "'" + a.Detail + "' matches several detail items" : "No detail item '" + a.Detail + "'") +
                    ". Loaded: " + string.Join(", ", (candidates.Count > 1 ? candidates : symbols).Take(40).Select(Label)) + ".",
                    "Give it as \"Family : Type\".");
            var placement = symbol.Family.FamilyPlacementType;
            if (placement == FamilyPlacementType.CurveBasedDetail || placement == FamilyPlacementType.CurveDrivenStructural)
                return CommandResult.Error("BAD_ARGS", Label(symbol) + " is line-based (drawn along a line), not placed at a point.", "Place line-based detail items with 'exec' (NewFamilyInstance(Line, symbol, view)).");

            var at = SheetHelper.ViewPoint(doc, view, a.At[0], a.At[1], out var convention);
            FamilyInstance fi = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Detail Component", () =>
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                fi = doc.Create.NewFamilyInstance(at, symbol, view);
                if (Math.Abs(a.Rotate) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(at, at + view.ViewDirection), a.Rotate * Math.PI / 180);
            });
            if (error != null) return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("DETAIL COMPONENT PLACED");
            sb.AppendLine("=======================");
            sb.AppendLine("ID: " + fi.Id.IntegerValue);
            sb.AppendLine("Type: " + Label(symbol));
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ")");
            sb.AppendLine("At: " + Mm.F(a.At[0]) + ", " + Mm.F(a.At[1]) + " (" + convention + ")" + (Math.Abs(a.Rotate) > 1e-9 ? ", rotated " + a.Rotate + "°" : ""));
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "created", new List<object> { fi.Id.IntegerValue } }, { "type", Label(symbol) }, { "view", view.Id.IntegerValue }, { "rotateDeg", a.Rotate }
            });
        }

        private static string Label(FamilySymbol s) => s.FamilyName + " : " + s.Name;

        /// <summary>"Family : Type" (any spacing around the colon), else a unique type or family name.</summary>
        private static FamilySymbol FindSymbol(List<FamilySymbol> symbols, string q, out List<FamilySymbol> candidates)
        {
            candidates = new List<FamilySymbol>();
            q = q.Trim();
            int colon = q.IndexOf(':');
            if (colon > 0)
            {
                string fam = q.Substring(0, colon).Trim(), typ = q.Substring(colon + 1).Trim();
                var exact = symbols.FirstOrDefault(s => s.FamilyName.Equals(fam, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(typ, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            candidates = symbols.Where(s => s.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0) candidates = symbols.Where(s => s.FamilyName.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0)
                candidates = symbols.Where(s => Label(s).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }
}
