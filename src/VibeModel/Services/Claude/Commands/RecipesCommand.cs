using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>The exec scripts that changed this model, saved in the project folder with who asked and why.</summary>
    public class RecipesCommand : IClaudeCommand, IStructuredCommand, IProjectMemoryCommand
    {
        private const int MaxListed = 50;

        public string Name => "recipes";
        public string Description => "The exec scripts that changed this model, saved in the project folder: list them, or print one to read, re-run or adapt it";
        public string Usage => "recipes [<number>|<part of the name>]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var error = ProjectMemorySupport.Locate(uiApp, out var doc, out var loc);
            if (error != null) return error;

            // A script saved a moment ago must be in what we read.
            ProjectRecipes.Flush(ProjectData.IoTimeout);

            var query = (args ?? "").Trim().Trim('"');
            var folders = ProjectRecipes.Folders(loc, doc.Title);
            string readError = null;
            var files = PathProbe.Run(() =>
            {
                try { return ProjectRecipes.List(folders); }
                catch (Exception ex) { readError = ex.Message; return null; }
            }, ProjectData.IoTimeout, null);
            if (files == null)
                return ProjectMemorySupport.Unavailable(loc, readError ?? "the folder did not answer within " +
                    ProjectData.IoTimeout.TotalSeconds + " s (network drive offline?)");

            return query.Length == 0 ? List(files, folders[0], loc.SourceLabel) : Show(files, query);
        }

        private static CommandResult List(List<string> files, string folder, string sourceLabel)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Recipes (" + files.Count + "): " + folder + "  " + sourceLabel);
            if (files.Count == 0) sb.AppendLine("(none yet: every exec that changes the model is saved here)");
            int first = Math.Max(0, files.Count - MaxListed);
            if (first > 0) sb.AppendLine("(" + Truncation.Line(files.Count - first, files.Count, "these are the newest") + ")");

            // Under one deadline: this runs on Revit's main thread and the folder may be a share.
            var shown = files.Skip(first).ToList();
            var headers = PathProbe.Run(() => shown.Select(ReadHeader).ToList(), ProjectData.IoTimeout, null);

            var rows = new List<object>();
            for (int i = first; i < files.Count; i++)
            {
                var name = Path.GetFileName(files[i]);
                var header = headers != null ? headers[i - first] : new RecipeHeader();
                sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture) + "  " + (header.Date ?? "?") + "  " + (header.User ?? "?") + "  " +
                              (header.Goal ?? "(no Doel line)") + "  [" + name + "]");
                rows.Add(new Dictionary<string, object>
                {
                    { "number", i + 1 }, { "file", name }, { "date", header.Date }, { "user", header.User }, { "goal", header.Goal }
                });
            }
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "folder", folder }, { "total", files.Count }, { "shown", rows.Count }, { "truncated", first > 0 }, { "recipes", rows }
            });
        }

        private static RecipeHeader ReadHeader(string path)
        {
            try { return ProjectRecipes.ParseHeader(File.ReadLines(path, Encoding.UTF8).Take(40).ToList()); }
            catch { return new RecipeHeader(); }
        }

        private static CommandResult Show(List<string> files, string query)
        {
            string file = null;
            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                if (number < 1 || number > files.Count)
                    return CommandResult.Error("BAD_ARGS", "No recipe " + query + " (there are " + files.Count + ").", "Run 'recipes' for the list.");
                file = files[number - 1];
            }
            else
            {
                var hits = files.Where(f => Path.GetFileName(f).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 0)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "No recipe matches '" + query + "'.", "Run 'recipes' for the list.");
                if (hits.Count > 1)
                    return CommandResult.Error("AMBIGUOUS", "'" + query + "' matches " + hits.Count + " recipes: " +
                        string.Join("; ", hits.Take(10).Select(Path.GetFileName)) + (hits.Count > 10 ? " …" : ""), "Use its number from 'recipes'.");
                file = hits[0];
            }

            var content = PathProbe.Run(() =>
            {
                try { return File.ReadAllText(file, Encoding.UTF8); }
                catch { return null; }
            }, ProjectData.IoTimeout, null);
            if (content == null)
                return CommandResult.Error("FOLDER_UNAVAILABLE", "Could not read " + Path.GetFileName(file) + ".", "Is the network drive reachable?");

            return CommandResult.Ok(content.TrimEnd() + "\n", new Dictionary<string, object>
            {
                { "file", Path.GetFileName(file) }, { "path", file }, { "content", content }
            });
        }
    }
}
