using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 08 (project part): removes a remembered fact — by its number in <c>memory</c>, its
    /// text, or a fragment that matches exactly one fact.
    /// </summary>
    public class ForgetCommand : IClaudeCommand, IStructuredCommand, IProjectMemoryCommand
    {
        public string Name => "forget";
        public string Description => "Project memory: remove a remembered fact by its number (see memory) or its text";
        public string Usage => "forget <n|text>";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var error = ProjectMemorySupport.Locate(uiApp, out var doc, out var loc);
            if (error != null) return error;

            var removed = ProjectMemory.Forget(loc.Directory, doc.Title, args, Strings.Current, out var forgetError, out var folderFailed);
            if (removed == null)
                return folderFailed
                    ? ProjectMemorySupport.Unavailable(loc, forgetError)
                    : CommandResult.Error("NOT_FOUND", forgetError ?? "Nothing removed.", "Run 'memory' for the numbered list.");

            var text = "Forgotten: " + string.Join("; ", removed.Select(e => e.Text)) + "\n";
            return CommandResult.Ok(text, new Dictionary<string, object>
            {
                { "removed", removed.Select(e => (object)e.Text).ToList() },
                { "file", ProjectMemory.PathIn(loc.Directory) }
            });
        }
    }
}
