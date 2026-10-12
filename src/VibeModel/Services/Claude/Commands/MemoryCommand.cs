using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 08 (project part): lists the facts remembered for this project (memory.md in the
    /// project folder's VibeModel\ subfolder), numbered oldest first; <c>memory --clear</c>
    /// empties it.
    /// </summary>
    public class MemoryCommand : IClaudeCommand, IStructuredCommand, IProjectMemoryCommand
    {
        public string Name => "memory";
        public string Description => "Project memory: list the facts remembered for this project (numbered; forget <n> removes one). --clear empties it";
        public string Usage => "memory [--clear]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var error = ProjectMemorySupport.Locate(uiApp, out var doc, out var loc);
            if (error != null) return error;

            var a = (args ?? "").Trim();
            if (a.Equals("--clear", System.StringComparison.OrdinalIgnoreCase) || a.Equals("clear", System.StringComparison.OrdinalIgnoreCase))
            {
                var dropped = ProjectMemory.Clear(loc.Directory, doc.Title, Strings.Current, out var clearError);
                if (dropped == null)
                    return ProjectMemorySupport.Unavailable(loc, clearError);
                return CommandResult.Ok("Project memory cleared (" + dropped.Count + " fact" + (dropped.Count == 1 ? "" : "s") +
                                        " removed): " + ProjectMemory.PathIn(loc.Directory) + "  " + loc.SourceLabel + "\n",
                    new Dictionary<string, object> { { "file", ProjectMemory.PathIn(loc.Directory) }, { "removed", dropped.Count } });
            }
            if (a.Length > 0)
                return CommandResult.Error("BAD_ARGS", "Unknown option '" + a + "'.",
                    "Use 'memory' to list, 'memory --clear' to empty, 'remember <text>' / 'forget <n|text>' to change.");

            var entries = ProjectMemory.Load(loc.Directory, out var loadError);
            if (entries == null) return ProjectMemorySupport.Unavailable(loc, loadError);

            var text = "Project memory: " + ProjectMemory.PathIn(loc.Directory) + "  " + loc.SourceLabel + "\n" +
                       (entries.Count == 0
                           ? "(nothing remembered yet — remember <text> saves a fact)\n"
                           : ProjectMemory.FormatList(entries) + "forget <n> removes one.\n");
            return CommandResult.Ok(text, new Dictionary<string, object>
            {
                { "file", ProjectMemory.PathIn(loc.Directory) },
                { "source", loc.IsFallback ? "local" : DocsCommand.SourceKey(loc.Folder.Source) },
                { "facts", entries.Select((e, i) => (object)new Dictionary<string, object>
                    {
                        { "n", i + 1 }, { "date", e.Date }, { "text", e.Text }
                    }).ToList() }
            });
        }
    }

    /// <summary>Shared by the memory / journal commands: the document and its data folder, or why not.</summary>
    internal static class ProjectMemorySupport
    {
        internal static CommandResult Locate(UIApplication uiApp, out Document doc, out ProjectDataLocation location)
        {
            location = null;
            doc = uiApp.ActiveUIDocument?.Document;
            if (!ProjectData.Enabled)
                return CommandResult.Error("MEMORY_OFF", "Project memory and journal are switched off.",
                    "Switch them on in the chat pane → Settings → \"" + Strings.Get(Strings.SettingProjectMemory) + "\".");
            if (doc == null)
                return CommandResult.Error("NO_DOCUMENT", "No document is open.");
            location = ProjectData.LocateForDocument(doc.Title, doc.PathName);
            if (location == null)
                return CommandResult.Error("NO_PROJECT_FOLDER", "This document has no title or folder to keep project data for.");
            return null;
        }

        internal static CommandResult Unavailable(ProjectDataLocation loc, string why)
            => CommandResult.Error("FOLDER_UNAVAILABLE", loc.Label + ": " + why,
                "Check the folder, or pick another one in the chat pane → Settings → Project folder.");
    }
}
