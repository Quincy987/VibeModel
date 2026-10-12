using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Link another Revit model or an IFC by shared coordinates, pinned afterwards — the office
    /// rule for every file after the first base map. Without shared coordinates in the host there
    /// is nothing to place it by, so it refuses (explaining why) unless told otherwise.
    /// </summary>
    public class LinkRvtCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "linkrvt";
        public string Description => "Link a Revit model (.rvt) or IFC by shared coordinates, pinned (the office rule once coordinates are set up)";
        public string Usage => "linkrvt \"<path.rvt|.ifc>\" [--origin] [--no-pin] [--force]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var raw = (args ?? "").Trim();

            bool origin = LinkArgs.TakeFlag(ref raw, "--origin");
            bool noPin = LinkArgs.TakeFlag(ref raw, "--no-pin");
            bool force = LinkArgs.TakeFlag(ref raw, "--force");
            var path = raw.Trim().Trim('"');
            if (path.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Usage: " + Usage, null);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".rvt" && ext != ".ifc")
                return CommandResult.Error("BAD_ARGS", "Only .rvt and .ifc are supported: " + path,
                    ext == ".dwg" || ext == ".dgn" ? "CAD underlays go through 'linkcad'." : null);

            var missing = LinkArgs.ProbeFile(path);
            if (missing != null) return missing;
            if (!string.IsNullOrEmpty(doc.PathName) && SamePath(doc.PathName, path))
                return CommandResult.Error("BAD_ARGS", "That is the open model itself; a model cannot link itself.", null);

            var coords = TemplateFactsCollector.ReadCoordinates(doc);
            ImportPlacement placement;
            string placementWhy;
            if (origin) { placement = ImportPlacement.Origin; placementWhy = "origin-to-origin (--origin)"; }
            else if (coords.SharedCoordinates)
            {
                placement = ImportPlacement.Shared;
                placementWhy = LinkArgs.SharedPlacementWhy(coords);
            }
            else if (force) { placement = ImportPlacement.Origin; placementWhy = "origin-to-origin (--force; this model has no shared coordinates yet)"; }
            else
                return CommandResult.Error("REFUSED",
                    "Not linking " + Path.GetFileName(path) + ": this model has no shared coordinates yet, so there is nothing to place it by.",
                    "Link the base map (DWG) first with 'linkcad' and acquire shared coordinates from it, then link this file by shared coordinates. --origin / --force links origin-to-origin instead.");

            // The pack's link settings are shared with linkcad; only pin-after-link applies here.
            var pack = StandardsContext.GetActivePack(doc);
            var opts = pack?.LinkCad ?? new LinkCadDefaults();

            // IFC: Revit links the IFC through a converted copy (<file>.ifc.RVT next to it). The
            // conversion opens a document, which cannot happen inside a transaction, so it runs first.
            string ifcRvt = null;
            bool converted = false;
            if (ext == ".ifc")
            {
                ifcRvt = path + ".RVT";
                bool haveCopy = PathProbe.FileExists(ifcRvt) == true;
                // A copy older than the IFC is a previous version of the model: convert again.
                bool stale = haveCopy && File.GetLastWriteTimeUtc(ifcRvt) < File.GetLastWriteTimeUtc(path);
                if (!haveCopy || stale)
                {
                    var convError = ConvertIfc(uiApp, path, ifcRvt, overwrite: stale);
                    if (convError != null)
                        return CommandResult.Error("LINK_FAILED", "Revit could not convert " + Path.GetFileName(path) + " for linking: " + convError,
                            stale
                                ? "The old copy " + Path.GetFileName(ifcRvt) + " may be in use: if this IFC is already linked, Manage Links → Reload From… updates it instead."
                                : "Link it once through Insert → Link IFC in Revit (that writes " + Path.GetFileName(ifcRvt) + "), then run linkrvt again.");
                    converted = true;
                }
            }

            ElementId typeId = ElementId.InvalidElementId, instId = ElementId.InvalidElementId;
            bool pinned = false, reusedType = false;
            var error = TransactionHelper.Execute(doc, "VibeModel: Link " + (ext == ".ifc" ? "IFC" : "Revit model"), () =>
            {
                if (ext == ".rvt")
                {
                    typeId = FindLinkType(doc, path);
                    reusedType = typeId != ElementId.InvalidElementId;
                    if (!reusedType)
                    {
                        var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                        var res = RevitLinkType.Create(doc, mp, new RevitLinkOptions(false));
                        typeId = res?.ElementId ?? ElementId.InvalidElementId;
                        if (typeId == ElementId.InvalidElementId)
                            throw new Exception("Revit could not load " + Path.GetFileName(path) + " as a link" + LoadResultText(res));
                    }
                }
                else
                {
                    var res = RevitLinkType.CreateFromIFC(doc, path, ifcRvt, false, new RevitLinkOptions(false));
                    typeId = res?.ElementId ?? ElementId.InvalidElementId;
                    if (typeId == ElementId.InvalidElementId)
                        throw new Exception("Revit could not load " + Path.GetFileName(path) + " as an IFC link" + LoadResultText(res));
                }

                var inst = RevitLinkInstance.Create(doc, typeId, placement);
                if (inst == null) throw new Exception("Revit did not place the link instance.");
                instId = inst.Id;
                if (opts.PinAfterLink && !noPin)
                {
                    inst.Pinned = true;
                    pinned = true;
                }
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "LINK_FAILED",
                    placement == ImportPlacement.Shared
                        ? "Revit only places by shared coordinates when both models took them from the same source. Otherwise: link with --origin, move it into place, then Manage → Coordinates → Publish Coordinates on the link (or have its author acquire coordinates from the same base map)."
                        : null);

            // Read back where it landed, so the report states it rather than assuming it.
            string landed = null;
            if (placement == ImportPlacement.Shared && doc.GetElement(instId) is RevitLinkInstance placed)
            {
                var check = SharedPlacement.Check(doc, placed, NorthAngles.DefaultToleranceMm, NorthAngles.DefaultToleranceDeg);
                landed = check.Verdict == SharedPlacement.Verdict.Matches ? "at its shared-coordinates position (verified)"
                    : check.Verdict == SharedPlacement.Verdict.Differs ? "NOT at its shared-coordinates position: " + check.Why
                    : "position not verifiable (" + check.Why + ")";
            }

            var sb = new StringBuilder();
            sb.AppendLine(ext == ".ifc" ? "IFC LINKED" : "REVIT MODEL LINKED");
            sb.AppendLine("==================");
            sb.AppendLine("ID: " + instId.IntegerValue + " (link type " + typeId.IntegerValue + (reusedType ? ", already loaded — new instance" : "") + ")");
            sb.AppendLine("File: " + path);
            if (ifcRvt != null)
                sb.AppendLine("  IFC via " + Path.GetFileName(ifcRvt) + (converted ? " (converted now — the IFC was new or newer than its copy — cached next to the IFC)" : " (existing converted copy, up to date)"));
            sb.AppendLine("  positioning: " + placementWhy);
            if (landed != null) sb.AppendLine("  landed: " + landed);
            sb.AppendLine("  pinned: " + (pinned ? "yes" : "no" + (noPin ? " (--no-pin)" : "")));

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "created", new List<object> { instId.IntegerValue } },
                { "linkTypeId", typeId.IntegerValue }, { "placement", placement.ToString() }, { "pinned", pinned },
                { "ifcConverted", converted }, { "landed", landed }
            });
        }

        /// <summary>Convert an IFC into the .ifc.RVT copy Revit links through; null on success, else why not.</summary>
        private static string ConvertIfc(UIApplication uiApp, string ifcPath, string rvtPath, bool overwrite)
        {
            Document ifcDoc = null;
            // "Did the importer write the copy itself?" is judged by the file changing, not by
            // comparing clocks — a network share's clock need not match this machine's.
            bool existedBefore = File.Exists(rvtPath);
            var before = existedBefore ? File.GetLastWriteTimeUtc(rvtPath) : DateTime.MinValue;
            try
            {
                var o = new IFCImportOptions { Intent = IFCImportIntent.Reference, Action = IFCImportAction.Link };
                ifcDoc = uiApp.Application.OpenIFCDocument(ifcPath, o);
                if (ifcDoc == null) return "the IFC importer returned nothing";
                // The importer may already have written the copy itself; otherwise save it there.
                bool written = File.Exists(rvtPath) && (!existedBefore || File.GetLastWriteTimeUtc(rvtPath) != before);
                if (!written)
                    ifcDoc.SaveAs(rvtPath, new SaveAsOptions { OverwriteExistingFile = overwrite });
                return File.Exists(rvtPath) ? null : "the converted copy was not written to " + rvtPath;
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                try { ifcDoc?.Close(false); } catch { }
            }
        }

        private static ElementId FindLinkType(Document doc, string path)
        {
            foreach (RevitLinkType t in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)))
            {
                try
                {
                    var r = t.GetExternalFileReference();
                    var p = r != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath()) : null;
                    if (p != null && SamePath(p, path)) return t.Id;
                }
                catch { }
            }
            return ElementId.InvalidElementId;
        }

        private static string LoadResultText(LinkLoadResult res) =>
            res == null ? "." : " (" + res.LoadResult + ").";

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
