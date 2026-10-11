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
        public string Usage => "linkcad \"<path.dwg|.dgn>\" [--first] [--origin] [--no-pin] [--force]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var raw = (args ?? "").Trim();

            bool first = TakeFlag(ref raw, "--first");
            bool origin = TakeFlag(ref raw, "--origin");
            bool noPin = TakeFlag(ref raw, "--no-pin");
            bool force = TakeFlag(ref raw, "--force");
            var path = raw.Trim().Trim('"');
            if (path.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Usage: " + Usage, null);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".dwg" && ext != ".dgn")
                return CommandResult.Error("BAD_ARGS", "Only .dwg and .dgn are supported: " + path, null);

            var exists = PathProbe.FileExists(path);
            if (exists == null)
                return CommandResult.Error("SHARE_UNREACHABLE", "The location did not answer within 3 s: " + path, "Is the network drive reachable?");
            if (exists == false)
                return CommandResult.Error("FILE_NOT_FOUND", "File not found: " + path, null);

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
            else if (!isFirst || coords.SharedCoordinates) { placement = ImportPlacement.Shared; placementWhy = "Auto – By Shared Coordinates"; }
            else { placement = ImportPlacement.Centered; placementWhy = "centre-to-centre (first underlay — no shared coordinates yet)"; }

            ElementId linkId = ElementId.InvalidElementId;
            bool pinned = false;
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
                    doc.Link(path, o, view, out linkId);
                }
                else
                {
                    var o = new DGNImportOptions
                    {
                        AutoCorrectAlmostVHLines = opts.CorrectOffAxisLines,
                        OrientToView = opts.OrientToView,
                        Placement = placement,
                        ThisViewOnly = false
                    };
                    doc.Link(path, o, view, out linkId);
                }

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
            sb.AppendLine("  pinned: " + (pinned ? "yes" : "no" + (noPin ? " (--no-pin)" : "")));
            if (placement == ImportPlacement.Centered)
                sb.AppendLine("Next: move it into place (do not rotate), then acquire shared coordinates from it and pin the base point" +
                              (pack != null ? " — see 'standards coordinates'." : "."));

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "created", new List<object> { linkId.IntegerValue } },
                { "placement", placement.ToString() }, { "pinned", pinned },
                { "autoCorrectAlmostVHLines", opts.CorrectOffAxisLines }, { "orientToView", opts.OrientToView }
            });
        }

        private static bool TakeFlag(ref string s, string flag)
        {
            var i = s.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            s = (s.Substring(0, i) + s.Substring(i + flag.Length)).Trim();
            return true;
        }
    }
}
