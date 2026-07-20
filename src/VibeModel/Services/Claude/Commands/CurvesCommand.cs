using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Extracts actual curve coordinates (mm, model space) from a CAD import, filtered by
    /// layer. With --closed, chains segments into closed loops whose output lines are
    /// paste-ready for the 'floor' command.
    /// </summary>
    public class CurvesCommand : IClaudeCommand, IStructuredCommand
    {
        private const int CurvesPerPage = 200;
        private const int LoopsPerPage = 100;
        private const double SnapTolMm = 1.0;

        public string Name => "curves";
        public string Description => "Extract curve coordinates from a CAD import layer (mm)";
        public string Usage => "curves <importId> <layer> [--bbox x1 y1 x2 y2] [--closed] [--page N]";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parsed = SourceDataArgs.ParseCurves(args);
            if (parsed.Error != null)
                return CommandResult.Error("BAD_ARGS", parsed.Error, Usage);

            var element = doc.GetElement(new ElementId(parsed.ImportId));
            if (element == null)
                return CommandResult.Error("NOT_FOUND",
                    "No element with ID " + parsed.ImportId + ".",
                    "Run 'imports' to list CAD imports and their IDs.");
            if (!(element is ImportInstance inst))
                return CommandResult.Error("NOT_AN_IMPORT",
                    "Element " + parsed.ImportId + " is not a CAD import (" + element.GetType().Name + ").",
                    "Run 'imports' to list CAD imports and their IDs.");

            var allChains = DwgGeometryHelper.GetChains(inst, doc);

            // Layer resolution: exact (case-insensitive) first, then substring.
            var layerNames = allChains.Select(c => c.Layer).Distinct().ToList();
            var exact = layerNames.Where(l => l.Equals(parsed.Layer, StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> matched;
            if (exact.Count > 0)
            {
                matched = exact;
            }
            else
            {
                var contains = layerNames
                    .Where(l => l.IndexOf(parsed.Layer, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (contains.Count == 0)
                    return CommandResult.Error("UNKNOWN_LAYER",
                        "No layer matching '" + parsed.Layer + "' in this import.",
                        "Run 'layers " + parsed.ImportId + "' to list layer names.");
                if (contains.Count > 1)
                    return CommandResult.Error("AMBIGUOUS_LAYER",
                        "'" + parsed.Layer + "' matches " + contains.Count + " layers: " +
                        string.Join(", ", contains.Take(10)) + (contains.Count > 10 ? ", ..." : "") + ".",
                        "Use a more specific layer name.");
                matched = contains;
            }
            string layerName = matched[0];

            var chains = allChains.Where(c => c.Layer == layerName).ToList();

            if (parsed.HasBbox)
            {
                chains = chains.Where(c => c.PointsMm.Any(p =>
                    p.X >= parsed.BboxMinX && p.X <= parsed.BboxMaxX &&
                    p.Y >= parsed.BboxMinY && p.Y <= parsed.BboxMaxY)).ToList();
            }

            var name = inst.Category?.Name ?? inst.Name;
            var sb = new StringBuilder();
            sb.AppendLine("CURVES: " + layerName + " (import " + parsed.ImportId + ", " + name + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();

            var data = new Dictionary<string, object>
            {
                { "importId", parsed.ImportId },
                { "layer", layerName },
                { "closed", parsed.Closed }
            };

            if (chains.Count == 0)
            {
                sb.AppendLine(parsed.HasBbox
                    ? "No curves on this layer inside the given bbox."
                    : "No curves on this layer.");
                data["total"] = 0;
                return CommandResult.Ok(sb.ToString(), data);
            }

            if (parsed.Closed)
            {
                var tupleChains = chains
                    .Select(c => c.PointsMm.Select(p => (p.X, p.Y)).ToList())
                    .ToList();
                var chained = CurveChainer.ChainLoops(tupleChains, SnapTolMm);

                var page = Paginate(chained.Loops.Count, LoopsPerPage, parsed.Page, out int from, out int to);
                if (page == null)
                    return PageError(chained.Loops.Count, LoopsPerPage, parsed.Page, "loops");

                sb.AppendLine("Closed loops: " + chained.Loops.Count +
                              "  (open chains: " + chained.OpenChains.Count + ")");
                sb.AppendLine();
                var loopData = new List<object>();
                for (int i = from; i < to; i++)
                {
                    var loop = chained.Loops[i];
                    sb.AppendLine("Loop " + (i + 1) + " (" + loop.Count + " pts): " + FormatChain2D(loop));
                    loopData.Add(loop.Select(p => new[] { Round1(p.X), Round1(p.Y) }).ToList());
                }
                if (chained.OpenChains.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Open chains not shown; run without --closed to inspect them.");
                }
                sb.AppendLine();
                sb.AppendLine("Tip: each loop line is paste-ready for 'floor <points>'.");
                AppendPageFooter(sb, parsed.Page, page.Value, chained.Loops.Count, LoopsPerPage);

                data["total"] = chained.Loops.Count;
                data["openChains"] = chained.OpenChains.Count;
                data["page"] = parsed.Page;
                data["pages"] = page.Value;
                data["loops"] = loopData;
            }
            else
            {
                var page = Paginate(chains.Count, CurvesPerPage, parsed.Page, out int from, out int to);
                if (page == null)
                    return PageError(chains.Count, CurvesPerPage, parsed.Page, "curves");

                sb.AppendLine("Curves: " + chains.Count);
                sb.AppendLine();
                var curveData = new List<object>();
                for (int i = from; i < to; i++)
                {
                    var pts = chains[i].PointsMm.Select(p => (p.X, p.Y)).ToList();
                    sb.AppendLine("Curve " + (i + 1) + " (" + pts.Count + " pts): " + FormatChain2D(pts));
                    curveData.Add(pts.Select(p => new[] { Round1(p.Item1), Round1(p.Item2) }).ToList());
                }
                sb.AppendLine();
                AppendPageFooter(sb, parsed.Page, page.Value, chains.Count, CurvesPerPage);

                data["total"] = chains.Count;
                data["page"] = parsed.Page;
                data["pages"] = page.Value;
                data["curves"] = curveData;
            }

            return CommandResult.Ok(sb.ToString(), data);
        }

        private static int? Paginate(int total, int perPage, int page, out int from, out int to)
        {
            int pages = Math.Max(1, (total + perPage - 1) / perPage);
            from = (page - 1) * perPage;
            to = Math.Min(total, from + perPage);
            if (page > pages) { from = to = 0; return null; }
            return pages;
        }

        private static CommandResult PageError(int total, int perPage, int page, string unit)
        {
            int pages = Math.Max(1, (total + perPage - 1) / perPage);
            return CommandResult.Error("BAD_PAGE",
                "Page " + page + " does not exist (" + total + " " + unit + " = " + pages + " pages).",
                "Use --page 1.." + pages + ".");
        }

        private static void AppendPageFooter(StringBuilder sb, int page, int pages, int total, int perPage)
        {
            if (pages > 1)
                sb.AppendLine("Page " + page + "/" + pages + " (" + total + " total, " + perPage +
                              " per page) — use --page <n> for more.");
        }

        private static string FormatChain2D(List<(double X, double Y)> pts)
        {
            return string.Join(" ", pts.Select(p =>
                p.X.ToString("F1", CultureInfo.InvariantCulture) + "," +
                p.Y.ToString("F1", CultureInfo.InvariantCulture)));
        }

        private static double Round1(double v) => Math.Round(v, 1);
    }
}
