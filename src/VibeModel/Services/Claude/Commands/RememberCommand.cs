using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 08 (project part): saves one dated fact for this project ("doorsnedes krijgen fase
    /// 2. Bouwfase") to memory.md; the same fact (case, spacing, quotes, trailing punctuation
    /// ignored) is not saved twice. Every chat turn sees the facts in its PROJECT MEMORY block.
    /// Not a modification: it writes no Revit data, so no transaction and no undo step.
    /// </summary>
    public class RememberCommand : IClaudeCommand, IStructuredCommand, IProjectMemoryCommand
    {
        public string Name => "remember";
        public string Description => "Project memory: save one project decision/preference/correction (one line, the user's words) so later chats know it";
        public string Usage => "remember <fact>";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var error = ProjectMemorySupport.Locate(uiApp, out var doc, out var loc);
            if (error != null) return error;
            if (ProjectMemory.CleanFact(args).Length == 0)
                return CommandResult.Error("BAD_ARGS", "Nothing to remember.", "remember <fact>, e.g. remember doorsnedes krijgen fase 2. Bouwfase");

            var r = ProjectMemory.Remember(loc.Directory, doc.Title, args, DateTime.Now, Strings.Current, out var writeError);
            if (r == null) return ProjectMemorySupport.Unavailable(loc, writeError);

            var entry = r.Entries[r.Index];
            var text = (r.Duplicate ? "Already remembered as #" : "Remembered #") + (r.Index + 1) + ": " + entry.Display + "\n" +
                       "(" + ProjectMemory.PathIn(loc.Directory) + "  " + loc.SourceLabel + ")\n";
            return CommandResult.Ok(text, new Dictionary<string, object>
            {
                { "n", r.Index + 1 },
                { "date", entry.Date },
                { "text", entry.Text },
                { "duplicate", r.Duplicate },
                { "file", ProjectMemory.PathIn(loc.Directory) }
            });
        }
    }
}
