using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 08 (project part): reads the project journal back — every modifying command / batch
    /// (with the ids it created, modified, deleted and the chat question behind it) and every
    /// chat turn with the sources its answer cited.
    /// </summary>
    public class JournalCommand : IClaudeCommand, IStructuredCommand, IProjectMemoryCommand
    {
        public string Name => "journal";
        public string Description => "Project journal: what VibeModel changed in this model and why (command, ids, chat question, cited sources). Default: the last 20 lines";
        public string Usage => "journal [today|yesterday|<yyyy-mm-dd>|--last <n>]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var error = ProjectMemorySupport.Locate(uiApp, out _, out var loc);
            if (error != null) return error;

            var query = ProjectJournal.ParseQuery(args, DateTime.Today, out var queryError);
            if (query == null) return CommandResult.Error("BAD_ARGS", queryError);

            // Lines queued a moment ago (the wall just made) must be in what we read.
            ProjectJournal.Flush(ProjectData.IoTimeout);

            string readError = null;
            var lines = PathProbe.Run(() =>
            {
                try
                {
                    var all = new List<string>();
                    if (query.Date != null)
                        foreach (var f in ProjectJournal.RotatedCandidates(loc.Directory, query.Date))
                            all.AddRange(File.ReadAllLines(f, Encoding.UTF8));
                    var current = Path.Combine(loc.Directory, ProjectJournal.FileName);
                    if (File.Exists(current)) all.AddRange(File.ReadAllLines(current, Encoding.UTF8));
                    return ProjectJournal.Filter(all, query);
                }
                catch (Exception ex) { readError = ex.Message; return null; }
            }, ProjectData.IoTimeout, null);
            if (lines == null)
                return ProjectMemorySupport.Unavailable(loc, readError ?? "the folder did not answer within " +
                    ProjectData.IoTimeout.TotalSeconds + " s (network drive offline?)");

            int hidden = Math.Max(0, lines.Count - ProjectJournal.MaxLinesShown);
            var shown = lines.Skip(hidden).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Journal (" + query.Label + "): " + Path.Combine(loc.Directory, ProjectJournal.FileName) + "  " + loc.SourceLabel);
            if (shown.Count == 0) sb.AppendLine("(no entries" + (query.Date != null ? " on " + query.Date : "") + ")");
            if (hidden > 0) sb.AppendLine("(+" + hidden + " earlier lines — narrow it with --last <n>)");
            foreach (var l in shown) sb.AppendLine(l);
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "file", Path.Combine(loc.Directory, ProjectJournal.FileName) },
                { "query", query.Label },
                { "lines", shown.Cast<object>().ToList() },
                { "omitted", hidden }
            });
        }
    }
}
