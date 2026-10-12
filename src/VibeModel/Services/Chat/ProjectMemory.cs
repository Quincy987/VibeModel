using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VibeModel.Infrastructure;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Chat
{
    /// <summary>Where a project's memory.md and journal.md live, and why there.</summary>
    public sealed class ProjectDataLocation
    {
        /// <summary>The directory holding memory.md / journal.md.</summary>
        public string Directory { get; set; }

        /// <summary>The project folder it sits in (None when the local fallback is used).</summary>
        public ProjectFolder Folder { get; set; }

        /// <summary>True when the model has no project folder: %LOCALAPPDATA%\VibeModel\projects\&lt;title&gt;.</summary>
        public bool IsFallback { get; set; }

        /// <summary>"&lt;dir&gt; (map van het model)" / "(ingesteld)" / "(lokaal …)" — every output names it.</summary>
        public string Label => Directory + "  " + SourceLabel;

        public string SourceLabel => IsFallback ? ProjectData.FallbackLabel : Folder.SourceLabel;
    }

    /// <summary>
    /// Plan 08 (project part): the per-project data folder shared by the memory store and the
    /// journal — "VibeModel\" inside the project folder of plan 10, or a local folder keyed by
    /// the document title when the model has none (unsaved / cloud model).
    /// </summary>
    public static class ProjectData
    {
        public const string SubfolderName = "VibeModel";
        public const string FallbackLabel = "(lokaal - het model heeft geen projectmap)";

        /// <summary>Deadline for every read/write of the project data folder (network shares).</summary>
        public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);

        internal static string FallbackRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "projects");

        /// <summary>Tests pin the switch without touching the user's settings.json.</summary>
        internal static bool? EnabledOverride;

        /// <summary>The Settings switch (default on).</summary>
        public static bool Enabled
        {
            get
            {
                if (EnabledOverride.HasValue) return EnabledOverride.Value;
                try { return SettingsManager.GetProjectMemoryEnabled(); }
                catch { return false; }
            }
        }

        /// <summary>Pure: the project folder's VibeModel\ subfolder, else the local fallback; null without a document.</summary>
        public static ProjectDataLocation Locate(ProjectFolder folder, string documentTitle, string fallbackRoot = null)
        {
            if (folder != null && folder.IsSet)
                return new ProjectDataLocation
                {
                    Directory = Path.Combine(folder.Path, SubfolderName),
                    Folder = folder
                };
            if (string.IsNullOrWhiteSpace(documentTitle)) return null;
            return new ProjectDataLocation
            {
                Directory = Path.Combine(fallbackRoot ?? FallbackRoot, ProjectDocs.ProjectKey(documentTitle)),
                Folder = ProjectFolder.None,
                IsFallback = true
            };
        }

        /// <summary>Settings + document → the data folder (may probe a configured folder, ≤ 3 s).</summary>
        public static ProjectDataLocation LocateForDocument(string documentTitle, string documentPathName)
        {
            if (string.IsNullOrWhiteSpace(documentTitle) && string.IsNullOrWhiteSpace(documentPathName))
                return null;
            return Locate(ProjectDocs.ResolveForDocument(documentTitle, documentPathName), documentTitle);
        }
    }

    /// <summary>One remembered fact: "- 2026-10-12 — deksteen = NLCS Prefab Beton hoek".</summary>
    public sealed class MemoryEntry
    {
        /// <summary>yyyy-MM-dd; null for a hand-written line without a date.</summary>
        public string Date { get; set; }
        public string Text { get; set; }

        public string Display => (Date != null ? Date + " — " : "") + Text;
    }

    /// <summary>
    /// Plan 08 (project part): short dated facts per project in memory.md — decisions,
    /// preferences, corrections the chat must still know next time. Plain markdown the user may
    /// edit by hand. Parsing, de-duplication, forget and the bounded prompt block are pure and
    /// covered headlessly; file access always runs with a deadline (network project folders).
    /// </summary>
    public static class ProjectMemory
    {
        public const string FileName = "memory.md";

        /// <summary>The whole PROJECT MEMORY block stays under this many UTF-8 bytes.</summary>
        public const int MaxPromptBytes = 2048;

        /// <summary>A fact is one line; anything longer is cut (memory is not a document store).</summary>
        public const int MaxFactChars = 300;

        internal const string PromptRule =
            "When the user states a project decision, preference or correction that will matter next time, save it with `remember` (one line, their words) and say so in one short line; read PROJECT MEMORY before answering 'last time' questions. Use `forget <n>` when they withdraw a remembered fact.";

        private static readonly object FileLock = new object();

        private static readonly Regex FactLine =
            new Regex(@"^\s*[-*]\s+(?:(\d{4}-\d{2}-\d{2})\s*(?:—|–|-|:)\s*)?(.*\S)\s*$", RegexOptions.Compiled);

        // --- Pure -------------------------------------------------------------------------

        /// <summary>Every bullet line of memory.md, oldest first. Headings and prose are not facts.</summary>
        public static List<MemoryEntry> Parse(string text)
        {
            var list = new List<MemoryEntry>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                var m = FactLine.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;
                var fact = m.Groups[2].Value.Trim();
                if (fact.Length == 0) continue;
                list.Add(new MemoryEntry { Date = m.Groups[1].Success ? m.Groups[1].Value : null, Text = fact });
            }
            return list;
        }

        /// <summary>One line, trimmed, outer quotes removed, capped. Empty when nothing is left.</summary>
        public static string CleanFact(string text)
        {
            var s = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (s.Length >= 2 && (s[0] == '"' && s[s.Length - 1] == '"' || s[0] == '\'' && s[s.Length - 1] == '\'' ||
                                  s[0] == '“' && s[s.Length - 1] == '”'))
                s = s.Substring(1, s.Length - 2).Trim();
            if (s.Length > MaxFactChars) s = s.Substring(0, MaxFactChars - 1).TrimEnd() + "…";
            return s;
        }

        /// <summary>Comparison key: case, whitespace, quotes and trailing punctuation do not make a fact new.</summary>
        public static string Normalize(string text)
        {
            var s = CleanFact(text).ToLowerInvariant();
            s = Regex.Replace(s, "[\"'“”‘’`]", "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.TrimEnd('.', '!', ';', ',', ':', ' ');
        }

        /// <summary>0-based index of an existing fact with the same normalized text, or -1.</summary>
        public static int IndexOfDuplicate(IList<MemoryEntry> entries, string text)
        {
            var key = Normalize(text);
            if (key.Length == 0) return -1;
            for (int i = 0; i < entries.Count; i++)
                if (Normalize(entries[i].Text) == key) return i;
            return -1;
        }

        public static string FormatLine(MemoryEntry e) => "- " + e.Display;

        /// <summary>The whole memory.md: a short header, then one bullet per fact.</summary>
        public static string Render(IEnumerable<MemoryEntry> entries, string documentTitle, string language)
        {
            var sb = new StringBuilder();
            sb.Append(Header(documentTitle, language));
            foreach (var e in entries) sb.Append(FormatLine(e)).Append("\r\n");
            return sb.ToString();
        }

        internal static string Header(string documentTitle, string language)
        {
            bool en = Strings.IsEnglish(language);
            var title = string.IsNullOrWhiteSpace(documentTitle) ? "" : " — " + documentTitle.Trim();
            return (en ? "# Project memory" : "# Projectgeheugen") + title + "\r\n\r\n" +
                   (en
                       ? "Facts VibeModel keeps for this project (remember / forget in the chat). One fact per line; you may edit this file."
                       : "Feiten die VibeModel voor dit project onthoudt (remember / forget in de chat). Eén feit per regel; je mag dit bestand zelf bewerken.") +
                   "\r\n\r\n";
        }

        /// <summary>
        /// Which facts <paramref name="arg"/> means: "3" (number from <c>memory</c>, 1 = oldest),
        /// the exact (normalized) text, or a text fragment that matches exactly one fact.
        /// Returns 0-based indexes; empty with <paramref name="error"/> set when nothing or too
        /// much matches.
        /// </summary>
        public static List<int> FindForget(IList<MemoryEntry> entries, string arg, out string error)
        {
            error = null;
            var result = new List<int>();
            var a = (arg ?? "").Trim().Trim('"').Trim();
            if (a.Length == 0) { error = "Say which fact: forget <number> or forget <text> (see memory)."; return result; }
            if (entries.Count == 0) { error = "Project memory is empty."; return result; }

            if (Regex.IsMatch(a, @"^#?\d+$"))
            {
                int n = int.Parse(a.TrimStart('#'));
                if (n < 1 || n > entries.Count) { error = "There is no fact #" + n + " (1–" + entries.Count + ")."; return result; }
                result.Add(n - 1);
                return result;
            }

            int exact = IndexOfDuplicate(entries, a);
            if (exact >= 0) { result.Add(exact); return result; }

            var key = Normalize(a);
            for (int i = 0; i < entries.Count; i++)
                if (Normalize(entries[i].Text).Contains(key)) result.Add(i);
            if (result.Count == 0) error = "No remembered fact matches '" + a + "'.";
            else if (result.Count > 1)
            {
                error = "'" + a + "' matches " + result.Count + " facts — give the number: " +
                        string.Join("; ", result.Select(i => "#" + (i + 1) + " " + entries[i].Text));
                result.Clear();
            }
            return result;
        }

        /// <summary>Numbered listing, oldest first (the numbers <c>forget</c> takes).</summary>
        public static string FormatList(IList<MemoryEntry> entries)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
                sb.Append(i + 1).Append(". ").AppendLine(entries[i].Display);
            return sb.ToString();
        }

        /// <summary>
        /// The per-turn PROJECT MEMORY block: where the file is, the newest facts that fit (newest
        /// last, numbered as in <c>memory</c>), a "+N older" line when cut, and the save rule.
        /// Never more than <paramref name="maxBytes"/> UTF-8 bytes.
        /// </summary>
        public static string BuildPromptBlock(IList<MemoryEntry> entries, ProjectDataLocation location, string loadError,
            int maxBytes = MaxPromptBytes)
        {
            entries = entries ?? new List<MemoryEntry>();
            var where = location == null ? FileName
                : PathIn(location.Directory) + " " + location.SourceLabel;
            var head = "PROJECT MEMORY (facts saved for this project with `remember`, newest last; file: " + where + "):\n";
            var tail = PromptRule + "\n";

            var lines = new List<string>();
            if (loadError != null) lines.Add("(could not be read: " + loadError + ")");
            else if (entries.Count == 0) lines.Add("(nothing saved yet)");

            int budget = maxBytes - Utf8(head) - Utf8(tail);
            int used = lines.Sum(l => Utf8(l) + 1);
            var facts = new List<string>();
            int omitted = 0;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var line = (i + 1) + ". " + entries[i].Display;
                // Reserve room for the "+N older" line as soon as anything is left out.
                int reserve = i > 0 ? Utf8(OlderLine(i)) + 1 : 0;
                if (used + Utf8(line) + 1 + reserve > budget)
                {
                    omitted = i + 1;
                    break;
                }
                facts.Insert(0, line);
                used += Utf8(line) + 1;
            }

            var sb = new StringBuilder(head);
            foreach (var l in lines) sb.Append(l).Append('\n');
            if (omitted > 0) sb.Append(OlderLine(omitted)).Append('\n');
            foreach (var f in facts) sb.Append(f).Append('\n');
            sb.Append(tail);
            return sb.ToString();
        }

        private static string OlderLine(int n) => "(+" + n + " older — run memory to see all)";

        private static int Utf8(string s) => Encoding.UTF8.GetByteCount(s ?? "");

        // --- File access (deadline-bound) -------------------------------------------------

        internal static string PathIn(string directory) => Path.Combine(directory, FileName);

        /// <summary>The facts in <paramref name="directory"/>; empty when there is no file yet; null + error when it cannot be read in time.</summary>
        public static List<MemoryEntry> Load(string directory, out string error)
        {
            string err = null;
            var result = PathProbe.Run(() =>
            {
                try
                {
                    lock (FileLock)
                    {
                        var p = PathIn(directory);
                        return File.Exists(p) ? Parse(File.ReadAllText(p, Encoding.UTF8)) : new List<MemoryEntry>();
                    }
                }
                catch (Exception ex) { err = ex.Message; return null; }
            }, ProjectData.IoTimeout, null);
            error = result != null ? null
                : err ?? "the folder did not answer within " + ProjectData.IoTimeout.TotalSeconds + " s (network drive offline?)";
            return result;
        }

        /// <summary>Outcome of <see cref="Remember"/>.</summary>
        public sealed class RememberResult
        {
            public List<MemoryEntry> Entries { get; set; }
            public int Index { get; set; }
            public bool Duplicate { get; set; }
        }

        /// <summary>Appends a dated fact unless the same fact (normalized) is already there.</summary>
        public static RememberResult Remember(string directory, string documentTitle, string text, DateTime now,
            string language, out string error)
        {
            var fact = CleanFact(text);
            if (fact.Length == 0) { error = "Nothing to remember: give the fact as text."; return null; }
            return Write(directory, out error, entries =>
            {
                int dup = IndexOfDuplicate(entries, fact);
                if (dup >= 0) return new RememberResult { Entries = entries, Index = dup, Duplicate = true };
                var entry = new MemoryEntry { Date = now.ToString("yyyy-MM-dd"), Text = fact };
                entries.Add(entry);
                var p = PathIn(directory);
                if (!File.Exists(p))
                    File.WriteAllText(p, Render(entries, documentTitle, language), new UTF8Encoding(false));
                else
                    AppendLine(p, FormatLine(entry));
                return new RememberResult { Entries = entries, Index = entries.Count - 1 };
            });
        }

        /// <summary>
        /// Removes the facts <paramref name="arg"/> names (see <see cref="FindForget"/>) and rewrites
        /// the file; returns the removed ones. Null with <paramref name="error"/> when nothing
        /// matched, or — <paramref name="folderFailed"/> — when the file could not be read/written.
        /// </summary>
        public static List<MemoryEntry> Forget(string directory, string documentTitle, string arg, string language,
            out string error, out bool folderFailed)
        {
            string findError = null;
            var removed = Write(directory, out error, entries =>
            {
                var idx = FindForget(entries, arg, out findError);
                if (idx.Count == 0) return null;
                var gone = idx.Select(i => entries[i]).ToList();
                foreach (var i in idx.OrderByDescending(i => i)) entries.RemoveAt(i);
                File.WriteAllText(PathIn(directory), Render(entries, documentTitle, language), new UTF8Encoding(false));
                return gone;
            });
            folderFailed = error != null;
            if (!folderFailed && removed == null) error = findError;
            return removed;
        }

        /// <summary>Empties memory.md (the header stays, so the file still explains itself). Returns the dropped facts.</summary>
        public static List<MemoryEntry> Clear(string directory, string documentTitle, string language, out string error)
        {
            return Write(directory, out error, entries =>
            {
                File.WriteAllText(PathIn(directory), Render(new MemoryEntry[0], documentTitle, language), new UTF8Encoding(false));
                return entries;
            });
        }

        // Read-modify-write under the in-process lock, with the folder deadline.
        private static T Write<T>(string directory, out string error, Func<List<MemoryEntry>, T> change) where T : class
        {
            string err = null;
            bool done = false;
            var result = PathProbe.Run(() =>
            {
                try
                {
                    lock (FileLock)
                    {
                        System.IO.Directory.CreateDirectory(directory);
                        var p = PathIn(directory);
                        var entries = File.Exists(p) ? Parse(File.ReadAllText(p, Encoding.UTF8)) : new List<MemoryEntry>();
                        var r = change(entries);
                        done = true;
                        return r;
                    }
                }
                catch (Exception ex) { err = ex.Message; return null; }
            }, ProjectData.IoTimeout, null);
            error = err ?? (result == null && !done
                ? "the folder did not answer within " + ProjectData.IoTimeout.TotalSeconds + " s (network drive offline?)"
                : null);
            return result;
        }

        // Appends one line, adding the missing line break of a hand-edited file first.
        internal static void AppendLine(string path, string line)
        {
            bool needsBreak = false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length > 0)
                {
                    fs.Seek(-1, SeekOrigin.End);
                    needsBreak = fs.ReadByte() != '\n';
                }
            }
            File.AppendAllText(path, (needsBreak ? "\r\n" : "") + line + "\r\n", new UTF8Encoding(false));
        }

        /// <summary>
        /// The PROJECT MEMORY block for the document the chat is about (worker thread, deadline-bound).
        /// Empty when the setting is off or no document is open — the prompt is then exactly as before.
        /// </summary>
        public static string BuildPromptBlockForActiveDocument()
        {
            try
            {
                if (!ProjectData.Enabled) return "";
                var loc = ProjectData.LocateForDocument(ActiveDocumentTracker.Title, ActiveDocumentTracker.PathName);
                if (loc == null) return "";
                var entries = Load(loc.Directory, out var err);
                return BuildPromptBlock(entries, loc, err);
            }
            catch (Exception ex)
            {
                Logger.Warn("Project memory unavailable: " + ex.Message);
                return "";
            }
        }
    }
}
