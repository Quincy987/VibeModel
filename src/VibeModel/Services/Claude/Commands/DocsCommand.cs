using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 10: lists the project folder — the reference documents (bestek, memo's,
    /// DO-tekening, inspection reports) that belong to this model. Default: the folder the
    /// .rvt is in; Settings → Project folder overrides it per project. Read-only.
    /// </summary>
    public class DocsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "docs";
        public string Description => "Project folder: list this model's reference documents (default: the model's own folder; Settings → Project folder overrides). Read one with readdoc";
        public string Usage => "docs [subfolder]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var error = ResolveFolder(uiApp, out var folder);
            if (error != null) return error;

            var sub = (args ?? "").Trim().Trim('"');
            string subFull = null;
            if (sub.Length > 0)
            {
                if (!ProjectDocs.TryResolveInside(folder.Path, sub, out subFull, out var why))
                    return CommandResult.Error("OUTSIDE_PROJECT_FOLDER", why, "Give a subfolder name as listed by 'docs'.");
                if (ProjectDocs.CrossesReparsePoint(folder.Path, subFull))
                    return CommandResult.Error("OUTSIDE_PROJECT_FOLDER",
                        "'" + sub + "' is a link (junction/symlink) — links can lead outside the project folder and are not followed.");
            }

            var listing = ProjectDocs.TryList(folder.Path, subFull, out var listError);
            if (listing == null)
                return CommandResult.Error("FOLDER_UNAVAILABLE",
                    ProjectDocs.Header(folder) + (sub.Length > 0 ? " — subfolder " + sub : "") + ": " + listError,
                    "Check the folder, or pick another one in the chat pane → Settings → Project folder.");

            var text = ProjectDocs.BuildManifest(folder, listing, ProjectDocs.MaxManifestEntries,
                sub.Length > 0 ? sub : null) + "Use: readdoc <name>\n";

            return CommandResult.Ok(text, new Dictionary<string, object>
            {
                { "folder", folder.Path },
                { "source", SourceKey(folder.Source) },
                { "sourceLabel", folder.SourceLabel },
                { "subfolder", sub.Length > 0 ? sub : null },
                { "totalFiles", listing.TotalFiles },
                { "truncated", listing.TotalFiles > ProjectDocs.MaxManifestEntries },
                { "subfolders", listing.Subfolders.ToList() },
                { "files", listing.Entries.Take(ProjectDocs.MaxManifestEntries).Select(e => (object)new Dictionary<string, object>
                    {
                        { "name", e.RelativePath },
                        { "kind", e.Tag },
                        { "sizeBytes", e.SizeBytes }
                    }).ToList() }
            });
        }

        internal static string SourceKey(ProjectFolderSource source)
            => source == ProjectFolderSource.Configured ? "configured" : "modelFolder";

        /// <summary>The project folder of the active document, or the error that explains why there is none.</summary>
        internal static CommandResult ResolveFolder(UIApplication uiApp, out ProjectFolder folder)
        {
            folder = ProjectFolder.None;
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Error("NO_DOCUMENT", "No document is open.");

            folder = ProjectDocs.ResolveForDocument(doc.Title, doc.PathName);
            if (!folder.IsSet)
                return CommandResult.Error("NO_PROJECT_FOLDER",
                    "No project folder: this model has no folder on disk (not saved yet, or a cloud model) and none is configured.",
                    "Pick one in the chat pane → Settings → Project folder (Browse…).");
            return null;
        }
    }
}
