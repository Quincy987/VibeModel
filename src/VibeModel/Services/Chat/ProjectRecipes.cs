using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>What the header of a saved script says.</summary>
    public sealed class RecipeHeader
    {
        public string Date { get; set; }
        public string User { get; set; }
        public string Goal { get; set; }
    }

    /// <summary>
    /// Every exec snippet that changed the model is kept in full in the project folder, with who
    /// asked for it and what it did, so a colleague can read, re-run or change it without the AI.
    /// Writing never blocks Revit and a script is never lost: when the project folder does not
    /// answer it goes to the local folder of the project.
    /// </summary>
    public static class ProjectRecipes
    {
        public const string FolderName = "recipes";
        private const int MaxSlugChars = 40;

        // --- Pure --------------------------------------------------------------------------

        /// <summary>The first 40 characters of the chat question, safe as a file name; "exec" without one.</summary>
        public static string Slug(string question)
        {
            var kept = Regex.Replace(question ?? "", @"[^\p{L}\p{Nd} _-]", " ");
            kept = Regex.Replace(kept, @"\s+", " ").Trim();
            if (kept.Length > MaxSlugChars) kept = kept.Substring(0, MaxSlugChars).TrimEnd();
            return kept.Length == 0 ? "exec" : kept;
        }

        /// <summary>"2026-11-09 140312 maak een muur.cs"; <paramref name="sequence"/> above 1 adds " (2)".</summary>
        public static string FileName(DateTime local, string question, int sequence = 1)
            => local.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + " " + Slug(question) +
               (sequence > 1 ? " (" + sequence.ToString(CultureInfo.InvariantCulture) + ")" : "") + ".cs";

        /// <summary>The header block, then the snippet exactly as it ran.</summary>
        public static string Content(DateTime local, string user, string question, string changedLine, string snippet)
        {
            var sb = new StringBuilder();
            sb.Append("// VibeModel recept\r\n");
            sb.Append("// Datum: ").Append(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(user)) sb.Append("// Door: ").Append(user.Trim()).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(question)) sb.Append("// Vraag: ").Append(OneLine(question)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(changedLine)) sb.Append("// ").Append(changedLine.Trim()).Append("\r\n");
            sb.Append("// Opnieuw uitvoeren: plak dit in de chat met \"voer dit script uit\"\r\n\r\n");
            sb.Append(snippet ?? "");
            return sb.ToString();
        }

        private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        private static readonly Regex GoalLine = new Regex(@"(?://|/\*)\s*Doel:\s*(.*?)\s*(?:\*/.*)?$", RegexOptions.Compiled);

        /// <summary>Date, user and the snippet's "Doel:" comment from the first lines of a saved script.</summary>
        public static RecipeHeader ParseHeader(IEnumerable<string> lines)
        {
            var header = new RecipeHeader();
            foreach (var line in (lines ?? Enumerable.Empty<string>()).Take(40))
            {
                var l = line.Trim();
                if (header.Date == null && l.StartsWith("// Datum:", StringComparison.Ordinal)) header.Date = l.Substring(9).Trim();
                else if (header.User == null && l.StartsWith("// Door:", StringComparison.Ordinal)) header.User = l.Substring(8).Trim();
                else if (header.Goal == null)
                {
                    var m = GoalLine.Match(l);
                    if (m.Success) header.Goal = m.Groups[1].Value;
                }
            }
            return header;
        }

        /// <summary>
        /// True when the whole snippet is one "//" line: a "\n" written inside a quoted one-line
        /// argument is not a newline, so the comment swallowed the code behind it.
        /// </summary>
        public static bool IsOnlyLineComment(string snippet)
        {
            var s = (snippet ?? "").Trim();
            return s.StartsWith("//", StringComparison.Ordinal) && s.IndexOf('\n') < 0;
        }

        // --- File names per second (main thread) --------------------------------------------

        private static string _lastSecond;
        private static int _sameSecond;

        /// <summary>A file name that differs from the previous one also within the same second.</summary>
        public static string NextFileName(DateTime local, string question)
        {
            var second = local.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            _sameSecond = second == _lastSecond ? _sameSecond + 1 : 1;
            _lastSecond = second;
            return FileName(local, question, _sameSecond);
        }

        // --- Writing ------------------------------------------------------------------------

        private static int _inFlight;

        /// <summary>Queues the script for the document's recipes folder. Cheap: safe on Revit's main thread.</summary>
        public static void Save(string documentTitle, string documentPathName, string fileName, string content)
        {
            if (!ProjectData.Enabled) return;
            Interlocked.Increment(ref _inFlight);
            Task.Run(() =>
            {
                try { Write(documentTitle, documentPathName, fileName, content); }
                catch (Exception ex) { Logger.Warn("Recipe not saved: " + ex.Message); }
                finally { Interlocked.Decrement(ref _inFlight); }
            });
        }

        /// <summary>Waits until every queued script has been written, or the timeout passed.</summary>
        public static bool Flush(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _inFlight) > 0)
            {
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(25);
            }
            return true;
        }

        private static void Write(string title, string pathName, string fileName, string content)
        {
            ProjectDataLocation location = null;
            try { location = ProjectData.LocateForDocument(title, pathName); }
            catch (Exception ex) { Logger.Warn("Recipe: the project folder could not be resolved: " + ex.Message); }

            if (location != null && !location.IsFallback)
            {
                var folder = Path.Combine(location.Directory, FolderName);
                var task = Task.Run(() => WriteFile(folder, fileName, content));
                try
                {
                    if (task.Wait(ProjectData.IoTimeout)) return;
                    Logger.Warn("Recipe: the project folder did not answer, saved locally instead: " + folder);
                }
                catch (AggregateException ex)
                {
                    Logger.Warn("Recipe: not written to the project folder, saved locally instead: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }

            WriteFile(LocalFolder(title), fileName, content);
        }

        private static string LocalFolder(string title)
            => Path.Combine(ProjectData.Locate(null, string.IsNullOrWhiteSpace(title) ? "untitled" : title).Directory, FolderName);

        internal static void WriteFile(string folder, string fileName, string content)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ").cs");
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        // --- Reading ------------------------------------------------------------------------

        /// <summary>The recipes folders of a project: its own and, when different, the local one.</summary>
        public static List<string> Folders(ProjectDataLocation location, string title)
        {
            var folders = new List<string> { Path.Combine(location.Directory, FolderName) };
            var localFolder = LocalFolder(title);
            if (!folders.Contains(localFolder, StringComparer.OrdinalIgnoreCase)) folders.Add(localFolder);
            return folders;
        }

        /// <summary>Every saved script, oldest first (the name starts with the time).</summary>
        public static List<string> List(IEnumerable<string> folders)
            => folders.Where(Directory.Exists)
                .SelectMany(f => Directory.GetFiles(f, "*.cs"))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
