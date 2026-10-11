using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Search the office family library (the pack's libraryPaths) from a cached index, so
    /// "is there an office family for this?" is answered before anyone builds a new one.
    /// The index builds in the background; this command never waits on the network share.
    /// </summary>
    public class LibraryCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "library";
        public string Description => "Search the office family library (cached index of the pack's library folders); load a hit with loadfamily";
        public string Usage => "library <search terms> | library --rebuild";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var pack = StandardsContext.GetActivePack(doc);
            if (pack == null)
                return CommandResult.Error("NO_PACK", "No office standards pack applies to this document, so there is no office library.",
                    "Run 'standards' to see why. 'familytypes' lists what is already loaded.");
            if (pack.LibraryPaths.Count == 0)
                return CommandResult.Error("NO_LIBRARY", "Pack " + pack.Id + " declares no libraryPaths.", null);

            var q = (args ?? "").Trim();
            bool rebuild = q.Equals("--rebuild", StringComparison.OrdinalIgnoreCase);
            var idx = LibraryIndexService.Get(pack, rebuild);
            var building = LibraryIndexService.IsBuilding(pack);
            var err = LibraryIndexService.GetLastError(pack);

            if (rebuild)
                return CommandResult.Ok("Library index rebuild " + (building ? "running" : "requested") +
                                        " in the background for: " + string.Join("; ", pack.LibraryPaths) + "\n");

            if (idx == null)
                return CommandResult.Error("INDEX_NOT_READY",
                    "The office library index is not ready: " + LibraryIndexService.NotReadyReason(pack) + ".",
                    "Library folders: " + string.Join("; ", pack.LibraryPaths));

            if (q.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Give search terms.", "e.g. library paal, library damwand AZ");

            var hits = idx.Search(q, 20);
            var sb = new StringBuilder();
            sb.AppendLine("LIBRARY — " + hits.Count + " match(es) for '" + q + "' in " + idx.Entries.Count + " families" +
                          " (index " + idx.BuiltUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                          (building ? ", refreshing" : "") + (err != null ? "; last refresh failed: " + err : "") + ")");
            sb.AppendLine("=======");
            if (idx.UnreachableRoots.Count > 0)
                sb.AppendLine("(not reachable when indexed, so not searched: " + string.Join("; ", idx.UnreachableRoots) + ")");
            foreach (var h in hits)
            {
                sb.AppendLine(h.Name + "  [" + h.Prefix + "]  " + h.Folder);
                sb.AppendLine("    loadfamily \"" + h.Path + "\"");
            }
            if (hits.Count == 0)
                sb.AppendLine("No match. Try fewer or shorter terms; prefixes: " + string.Join(", ", pack.FamilyPrefixes));

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "count", hits.Count }, { "indexed", idx.Entries.Count },
                { "builtUtc", idx.BuiltUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "results", hits.Select(h => (object)new Dictionary<string, object>
                    { { "name", h.Name }, { "path", h.Path }, { "folder", h.Folder }, { "prefix", h.Prefix } }).ToList() }
            });
        }
    }
}
