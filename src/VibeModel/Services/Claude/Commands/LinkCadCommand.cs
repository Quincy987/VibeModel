using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Link (never import) a DWG/DGN underlay with the office's link settings: off-axis
    /// correction off, orient-to-view off, positioned by shared coordinates (or centre-to-centre
    /// for the very first link), pinned afterwards. Every choice is reported so the modeller
    /// can see and override it.
    /// </summary>
    public class LinkCadCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "linkcad";
        public string Description => "Link a DWG/DGN underlay with the office link settings (link not import, shared coordinates, pinned)";
        public string Usage => "linkcad \"<path.dwg|.dgn>\" [--first] [--origin] [--no-pin] [--force] [--dgnview <model view name>]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var raw = (args ?? "").Trim();

            bool first = LinkArgs.TakeFlag(ref raw, "--first");
            bool origin = LinkArgs.TakeFlag(ref raw, "--origin");
            bool noPin = LinkArgs.TakeFlag(ref raw, "--no-pin");
            bool force = LinkArgs.TakeFlag(ref raw, "--force");
            // Revit links a DGN through one of its model views; "Default" is the usual one.
            var dgnView = LinkArgs.TakeValue(ref raw, "--dgnview") ?? "Default";
            var path = raw.Trim().Trim('"');
            if (path.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Usage: " + Usage, null);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".dwg" && ext != ".dgn")
                return CommandResult.Error("BAD_ARGS", "Only .dwg and .dgn are supported: " + path, null);

            var missing = LinkArgs.ProbeFile(path);
            if (missing != null) return missing;

            var view = uidoc.ActiveView;
            if (view == null || view is ViewSheet || view.IsTemplate)
                return CommandResult.Error("BAD_VIEW", "Activate a plan or 3D view to link into (the active view is a sheet/template).", null);

            var pack = StandardsContext.GetActivePack(doc);
            var opts = pack?.LinkCad ?? new LinkCadDefaults();
            int existingLinks = TemplateFactsCollector.CountCad(doc).Links;
            bool isFirst = first || existingLinks == 0;

            // A DGN is only placed correctly by shared coordinates, so it waits until a base map
            // is linked AND coordinates have been acquired from it.
            var coords = TemplateFactsCollector.ReadCoordinates(doc);
            if (ext == ".dgn" && !force && (existingLinks == 0 || !coords.SharedCoordinates))
                return CommandResult.Error("REFUSED",
                    "Not linking a DGN " + (existingLinks == 0 ? "as the first underlay" : "before shared coordinates are set up") + ": " +
                    (opts.RefuseFirstDgnReason ?? "a DGN does not carry reliable coordinates; positioning would go wrong."),
                    "Link the base map (DWG) first and acquire shared coordinates from it, then link the DGN (by shared coordinates). --force overrides.");

            // Placement: shared coordinates once they exist; the very first underlay has none to
            // follow, so it goes centre-to-centre (or origin-to-origin on request) and is then
            // moved — never rotated — and used to acquire coordinates.
            ImportPlacement placement;
            string placementWhy;
            if (origin) { placement = ImportPlacement.Origin; placementWhy = "origin-to-origin (--origin)"; }
            else if (coords.SharedCoordinates) { placement = ImportPlacement.Shared; placementWhy = "Auto – By Shared Coordinates (coordinates set up: " + CoordinatesSetUpText(coords) + ")"; }
            else if (!isFirst) { placement = ImportPlacement.Shared; placementWhy = "Auto – By Shared Coordinates (" + existingLinks + " underlay(s) already linked)"; }
            else { placement = ImportPlacement.Centered; placementWhy = "centre-to-centre (first underlay — no shared coordinates yet)"; }

            ElementId linkId = ElementId.InvalidElementId;
            bool pinned = false, linked = false;
            var error = TransactionHelper.Execute(doc, "VibeModel: Link CAD", () =>
            {
                if (ext == ".dwg")
                {
                    var o = new DWGImportOptions
                    {
                        AutoCorrectAlmostVHLines = opts.CorrectOffAxisLines,
                        OrientToView = opts.OrientToView,
                        Placement = placement,
                        ThisViewOnly = false
                    };
                    linked = doc.Link(path, o, view, out linkId);
                }
                else
                {
                    var o = new DGNImportOptions
                    {
                        DGNModelViewName = dgnView,
                        AutoCorrectAlmostVHLines = opts.CorrectOffAxisLines,
                        OrientToView = opts.OrientToView,
                        Placement = placement,
                        ThisViewOnly = false
                    };
                    linked = doc.Link(path, o, view, out linkId);
                }

                // Revit reports an unreadable file by returning false rather than throwing.
                if (!linked || linkId == null || linkId == ElementId.InvalidElementId)
                    throw new Exception("Revit could not read " + Path.GetFileName(path) + " as a " + ext.TrimStart('.').ToUpperInvariant() +
                                        (ext == ".dgn" ? " (check the file and the model view name: --dgnview <name>)" : " (check the file)"));

                var inst = doc.GetElement(linkId);
                if (inst != null && opts.PinAfterLink && !noPin)
                {
                    inst.Pinned = true;
                    pinned = true;
                }
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "LINK_FAILED",
                    placement == ImportPlacement.Shared ? "If the file has no usable coordinates, retry with --first (centre-to-centre)." : null);

            var sb = new StringBuilder();
            sb.AppendLine("CAD LINKED");
            sb.AppendLine("==========");
            sb.AppendLine("ID: " + linkId.IntegerValue);
            sb.AppendLine("File: " + path);
            sb.AppendLine("Linked, not imported. Settings used:");
            sb.AppendLine("  correct lines slightly off axis: " + (opts.CorrectOffAxisLines ? "on" : "off"));
            sb.AppendLine("  orient to view: " + (opts.OrientToView ? "on" : "off"));
            sb.AppendLine("  positioning: " + placementWhy);
            if (ext == ".dgn") sb.AppendLine("  DGN model view: " + dgnView + " (--dgnview <name> to change)");
            sb.AppendLine("  pinned: " + (pinned ? "yes" : "no" + (noPin ? " (--no-pin)" : "")));
            if (placement == ImportPlacement.Centered)
                sb.AppendLine("Next: move it into place (do not rotate — Project North stays True North), then acquire shared coordinates from it and pin the base point" +
                              (pack != null ? " — see 'standards coordinates'." : "."));

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "created", new List<object> { linkId.IntegerValue } },
                { "placement", placement.ToString() }, { "pinned", pinned },
                { "autoCorrectAlmostVHLines", opts.CorrectOffAxisLines }, { "orientToView", opts.OrientToView }
            });
        }

        /// <summary>The "coordinates are set up" criterion in words: what the base point reads.</summary>
        internal static string CoordinatesSetUpText(CoordinateFacts c)
        {
            if (Math.Abs(c.BaseNorthMm) < 0.5 && Math.Abs(c.BaseEastMm) < 0.5)
                return "shared coordinates acquired (site '" + c.SiteName + "')";
            return "base point N/S " + Math.Round(c.BaseNorthMm).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   ", E/W " + Math.Round(c.BaseEastMm).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mm ≠ 0";
        }
    }
}
