using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using VibeModel.Infrastructure;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Chat
{
    public enum ProjectFolderSource
    {
        None,
        ModelFolder,
        Configured
    }

    /// <summary>The project folder in force for a document, and where that choice came from.</summary>
    public sealed class ProjectFolder
    {
        public string Path { get; set; }
        public ProjectFolderSource Source { get; set; }

        public bool IsSet => Source != ProjectFolderSource.None && !string.IsNullOrEmpty(Path);

        /// <summary>"(map van het model)" / "(ingesteld)" — every output naming the folder carries it.</summary>
        public string SourceLabel => ProjectDocs.LabelFor(Source);

        public static readonly ProjectFolder None = new ProjectFolder { Source = ProjectFolderSource.None };
    }

    /// <summary>One file in the project folder manifest.</summary>
    public sealed class ProjectDocEntry
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public AttachmentKind Kind { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>[pdf] / [image] / [text] for the classified kinds, else the extension ([docx]).</summary>
        public string Tag
        {
            get
            {
                switch (Kind)
                {
                    case AttachmentKind.Image: return "image";
                    case AttachmentKind.Pdf: return "pdf";
                    case AttachmentKind.Text: return "text";
                }
                var ext = System.IO.Path.GetExtension(RelativePath ?? "").TrimStart('.').ToLowerInvariant();
                return ext.Length == 0 ? "file" : ext;
            }
        }
    }

    /// <summary>A one-level listing of a project folder (or one of its subfolders).</summary>
    public sealed class ProjectDocListing
    {
        public List<ProjectDocEntry> Entries { get; } = new List<ProjectDocEntry>();
        public List<string> Subfolders { get; } = new List<string>();
        public int TotalFiles => Entries.Count;
    }

    /// <summary>
    /// Plan 10: the per-project reference folder (bestek, memo's, DO-tekening, inspection
    /// reports). Shared by the docs / readdoc commands and the chat backends' per-turn context,
    /// so the manifest the model sees and the one a human lists can never drift. Read-only:
    /// nothing here writes inside the folder. Resolution, the root boundary check, name lookup
    /// and the manifest are pure and covered headlessly.
    /// </summary>
    public static class ProjectDocs
    {
        public const int MaxManifestEntries = 200;
        // The always-on per-turn cost: names only, half the command's cap.
        public const int MaxPromptEntries = 100;
        // A runaway folder (thousands of scans) must not stall a turn: stop enumerating here.
        internal const int MaxEnumeratedFiles = 5000;

        public const string ModelFolderLabel = "(map van het model)";
        public const string ConfiguredLabel = "(ingesteld)";

        /// <summary>A model folder on a share can hang a listing; the listing is abandoned after this.</summary>
        public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(3);

        /// <summary>How long readdoc waits for PDF text before handing back the path (Revit's thread waits).</summary>
        public static readonly TimeSpan PdfWait = TimeSpan.FromSeconds(12);

        public static string LabelFor(ProjectFolderSource source)
        {
            switch (source)
            {
                case ProjectFolderSource.Configured: return ConfiguredLabel;
                case ProjectFolderSource.ModelFolder: return ModelFolderLabel;
                default: return "(niet ingesteld)";
            }
        }

        /// <summary>
        /// The settings key for a document: the same sanitized title the attachment store uses,
        /// so attachments and folder context agree on project identity.
        /// </summary>
        public static string ProjectKey(string documentTitle) => AttachmentStore.SanitizeName(documentTitle);

        /// <summary>
        /// Pure: a configured folder wins; otherwise the folder the .rvt lives in; a document
        /// without a local path (unsaved, cloud model) has no default.
        /// </summary>
        public static ProjectFolder Resolve(string configuredPath, string documentPathName)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return new ProjectFolder { Path = configuredPath.Trim(), Source = ProjectFolderSource.Configured };

            var modelFolder = ModelFolderOf(documentPathName);
            return modelFolder == null
                ? ProjectFolder.None
                : new ProjectFolder { Path = modelFolder, Source = ProjectFolderSource.ModelFolder };
        }

        /// <summary>Settings + document → the folder in force (the live wiring around <see cref="Resolve"/>).</summary>
        public static ProjectFolder ResolveForDocument(string documentTitle, string documentPathName)
        {
            if (string.IsNullOrWhiteSpace(documentTitle) && string.IsNullOrWhiteSpace(documentPathName))
                return ProjectFolder.None;
            return Resolve(SettingsManager.GetProjectContextFolder(ProjectKey(documentTitle)), documentPathName);
        }

        /// <summary>The directory of a local / UNC model path; null for unsaved and cloud models.</summary>
        internal static string ModelFolderOf(string documentPathName)
        {
            if (string.IsNullOrWhiteSpace(documentPathName)) return null;
            var p = documentPathName.Trim();
            // Only drive-letter and UNC paths have a folder on disk. Cloud models report things
            // like "BIM 360://Project/model.rvt" or "Autodesk Docs://...".
            bool driveRooted = p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            bool unc = p.StartsWith(@"\\", StringComparison.Ordinal);
            if (!driveRooted && !unc) return null;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(p);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch
            {
                return null;
            }
        }

        // --- Root boundary ---------------------------------------------------------------

        /// <summary>
        /// True when <paramref name="fullPath"/> lies strictly inside <paramref name="root"/>.
        /// Separator-suffixed and case-insensitive, so a sibling like "docs-x" never matches "docs".
        /// Both paths must already be absolute. Shared with <see cref="AttachmentStore"/>.
        /// </summary>
        internal static bool IsUnderRoot(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return false;
            var sep = System.IO.Path.DirectorySeparatorChar.ToString();
            var rootPrefix = root.EndsWith(sep, StringComparison.Ordinal) ? root : root + sep;
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Pure: resolves a user-supplied relative name against the project root, or explains
        /// why not. Rejects absolute / drive-relative / UNC paths, any ".." segment, alternate
        /// data streams, and anything whose normalized form leaves the root.
        /// </summary>
        public static bool TryResolveInside(string root, string relativeName, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(root)) { error = "No project folder."; return false; }

            var name = (relativeName ?? "").Trim().Trim('"').Replace('/', '\\');
            if (name.Length == 0) { error = "No file name given."; return false; }
            if (name.StartsWith(@"\", StringComparison.Ordinal) || name.IndexOf(':') >= 0)
            {
                error = "Only names inside the project folder are allowed (got an absolute path).";
                return false;
            }
            if (name.Split('\\').Any(seg => seg.Trim().Trim('.').Length == 0 && seg.Contains("..")))
            {
                error = "'..' is not allowed — names are relative to the project folder.";
                return false;
            }

            string rootFull;
            try
            {
                rootFull = System.IO.Path.GetFullPath(root.Trim()).TrimEnd('\\');
                fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, name));
            }
            catch (Exception ex)
            {
                fullPath = null;
                error = "Not a valid file name: " + ex.Message;
                return false;
            }

            if (!IsUnderRoot(rootFull, fullPath))
            {
                fullPath = null;
                error = "That name points outside the project folder.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when a junction / symlink sits anywhere between the root and the target: the
        /// resolved path may then lie outside the root even though the name does not.
        /// </summary>
        internal static bool CrossesReparsePoint(string root, string fullPath)
        {
            try
            {
                var rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\');
                var current = fullPath;
                while (!string.IsNullOrEmpty(current) && IsUnderRoot(rootFull, current))
                {
                    if ((File.Exists(current) || Directory.Exists(current)) &&
                        (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return true;
                    current = System.IO.Path.GetDirectoryName(current);
                }
                return false;
            }
            catch
            {
                return true; // can't prove it's safe: refuse
            }
        }

        // --- Listing ---------------------------------------------------------------------

        /// <summary>Revit's own files: models, families, templates, backups, worksharing logs.</summary>
        internal static bool IsSkippedFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            if (fileName.StartsWith("~$", StringComparison.Ordinal)) return true; // Office lock files
            if (fileName.StartsWith(".", StringComparison.Ordinal)) return true;
            switch (System.IO.Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".rvt":   // also the numbered backups (model.0001.rvt)
                case ".rfa":
                case ".rte":
                case ".rft":
                case ".slog":
                case ".tmp":
                    return true;
            }
            return false;
        }

        /// <summary>Folders Revit creates next to a model ("&lt;model&gt;_backup", point-cloud "… Support") and VibeModel's own.</summary>
        internal static bool IsSkippedFolder(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return true;
            if (folderName.StartsWith(".", StringComparison.Ordinal)) return true;
            // VibeModel's own project data (memory.md, journal.md) is not a reference document.
            if (folderName.Equals(ProjectData.SubfolderName, StringComparison.OrdinalIgnoreCase)) return true;
            return folderName.EndsWith("_backup", StringComparison.OrdinalIgnoreCase) ||
                   folderName.EndsWith("Support", StringComparison.OrdinalIgnoreCase) ||
                   folderName.Equals("Revit_temp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Files in <paramref name="folder"/> plus those of its direct subfolders, relative to
        /// <paramref name="root"/> (the folder itself or one of its subfolders). Direct file-system
        /// access — callers on Revit's thread go through <see cref="TryList"/>.
        /// </summary>
        internal static ProjectDocListing List(string root, string folder = null)
        {
            var listing = new ProjectDocListing();
            var rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\');
            var start = folder == null ? rootFull : System.IO.Path.GetFullPath(folder).TrimEnd('\\');

            AddFiles(listing, rootFull, start);
            foreach (var sub in SafeDirectories(start).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(sub);
                if (IsSkippedFolder(name) || IsReparsePoint(sub)) continue;
                listing.Subfolders.Add(Relative(rootFull, sub));
                if (listing.Entries.Count >= MaxEnumeratedFiles) continue;
                AddFiles(listing, rootFull, sub);
            }
            return listing;
        }

        /// <summary><see cref="List"/> with a deadline; null when the folder is missing, unreachable or too slow.</summary>
        public static ProjectDocListing TryList(string root, string folder, out string error)
        {
            string err = null;
            var result = PathProbe.Run(() =>
            {
                if (!Directory.Exists(folder ?? root)) { err = "missing"; return null; }
                return List(root, folder);
            }, ListTimeout, null);
            error = result != null ? null
                : err == "missing" ? "the folder does not exist (moved or renamed?)"
                : "the folder did not answer within " + ListTimeout.TotalSeconds + " s (network drive offline?)";
            return result;
        }

        private static void AddFiles(ProjectDocListing listing, string rootFull, string folder)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder).ToList(); }
            catch { return; }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (listing.Entries.Count >= MaxEnumeratedFiles) return;
                var name = System.IO.Path.GetFileName(f);
                if (IsSkippedFile(name)) continue;
                long size = 0;
                try
                {
                    var info = new FileInfo(f);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    size = info.Length;
                }
                catch { /* unreadable metadata: still list it */ }
                listing.Entries.Add(new ProjectDocEntry
                {
                    RelativePath = Relative(rootFull, f),
                    FullPath = f,
                    Kind = ChatAttachment.ClassifyExtension(System.IO.Path.GetExtension(f)),
                    SizeBytes = size
                });
            }
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try { return Directory.EnumerateDirectories(folder).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        internal static string Relative(string rootFull, string full)
        {
            return IsUnderRoot(rootFull, full) ? full.Substring(rootFull.Length + 1) : full;
        }

        // --- Manifest (shared by `docs` and the per-turn prompt) -------------------------

        public static string Header(ProjectFolder folder)
        {
            return "Project folder: " + folder.Path + "  " + folder.SourceLabel;
        }

        /// <summary>
        /// The manifest text: header, counts, one line per file (name, kind tag, size), capped
        /// at <paramref name="maxEntries"/> with a count of what was left out.
        /// </summary>
        public static string BuildManifest(ProjectFolder folder, ProjectDocListing listing, int maxEntries,
            string subfolder = null)
        {
            var sb = new StringBuilder();
            sb.Append(Header(folder));
            if (!string.IsNullOrEmpty(subfolder)) sb.Append("  — subfolder " + subfolder);
            sb.AppendLine();

            var files = listing.Entries;
            var more = listing.TotalFiles >= MaxEnumeratedFiles ? "+" : "";
            sb.AppendLine("(" + files.Count + more + " file" + (files.Count == 1 ? "" : "s") + ", " +
                          listing.Subfolders.Count + " subfolder" + (listing.Subfolders.Count == 1 ? "" : "s") + ")");
            if (files.Count == 0)
            {
                sb.AppendLine("  (no documents — Revit model files and backup folders are not listed)");
                return sb.ToString();
            }

            var shown = files.Take(maxEntries).ToList();
            int width = Math.Min(48, shown.Max(e => e.RelativePath.Length));
            int tagWidth = shown.Max(e => e.Tag.Length) + 2;
            foreach (var e in shown)
            {
                sb.Append("  ").Append(e.RelativePath.PadRight(width)).Append("  ");
                sb.Append(("[" + e.Tag + "]").PadRight(tagWidth)).Append("  ");
                sb.AppendLine(ChatAttachment.FormatSize(e.SizeBytes));
            }
            if (files.Count > shown.Count)
                sb.AppendLine("  … and " + (files.Count - shown.Count) + more + " more — list one subfolder with: docs <subfolder>" +
                              (listing.Subfolders.Count > 0 ? " (" + string.Join(", ", listing.Subfolders.Take(10)) + ")" : ""));
            return sb.ToString();
        }

        /// <summary>
        /// The bounded block each chat turn carries when a folder resolves. Empty when there is
        /// no folder; a folder that cannot be listed still gets named (with the reason) so the
        /// model does not invent its contents.
        /// </summary>
        public static string BuildPromptBlock(ProjectFolder folder, ProjectDocListing listing, string listError,
            bool nativeRead)
        {
            if (folder == null || !folder.IsSet) return "";
            var sb = new StringBuilder();
            sb.AppendLine("PROJECT FOLDER (" + (folder.Source == ProjectFolderSource.Configured
                ? "chosen by the user in Settings"
                : "default: the folder the open model is in") + "):");
            if (listing == null)
            {
                sb.AppendLine(Header(folder));
                sb.AppendLine("  (could not be listed: " + (listError ?? "unknown error") + ")");
            }
            else
            {
                sb.Append(BuildManifest(folder, listing, MaxPromptEntries));
            }
            sb.Append("These are the project's reference documents (read-only). Use readdoc <name> when one is relevant");
            sb.Append(nativeRead ? " — or Read the file directly, the folder is readable to you" : "");
            sb.AppendLine("; don't guess at their contents.");
            return sb.ToString();
        }

        /// <summary>The prompt block for the document the chat is about (worker-thread safe, deadline-bound).</summary>
        public static string BuildPromptBlockForActiveDocument(bool nativeRead, out ProjectFolder folder)
        {
            folder = ProjectFolder.None;
            try
            {
                folder = ResolveForDocument(ActiveDocumentTracker.Title, ActiveDocumentTracker.PathName);
                if (!folder.IsSet) return "";
                var listing = TryList(folder.Path, null, out var err);
                return BuildPromptBlock(folder, listing, err, nativeRead);
            }
            catch (Exception ex)
            {
                Logger.Warn("Project folder context unavailable: " + ex.Message);
                return "";
            }
        }

        // --- Name resolution -------------------------------------------------------------

        /// <summary>
        /// Case-insensitive lookup: exact relative path, then exact file name, then prefix of
        /// either. Returns the single hit; <paramref name="matches"/> holds every candidate when
        /// the name is ambiguous (or empty when nothing matches).
        /// </summary>
        public static ProjectDocEntry Find(IEnumerable<ProjectDocEntry> entries, string query, out List<ProjectDocEntry> matches)
        {
            var all = entries.ToList();
            var q = (query ?? "").Trim().Trim('"').Replace('/', '\\');
            matches = new List<ProjectDocEntry>();
            if (q.Length == 0) return null;

            var cmp = StringComparison.OrdinalIgnoreCase;
            Func<ProjectDocEntry, string> fileName = e => System.IO.Path.GetFileName(e.RelativePath);
            var stages = new Func<ProjectDocEntry, bool>[]
            {
                e => e.RelativePath.Equals(q, cmp),
                e => fileName(e).Equals(q, cmp),
                e => e.RelativePath.StartsWith(q, cmp) || fileName(e).StartsWith(q, cmp),
            };
            foreach (var stage in stages)
            {
                var hits = all.Where(stage).ToList();
                if (hits.Count == 1) { matches = hits; return hits[0]; }
                if (hits.Count > 1) { matches = hits; return null; }
            }
            return null;
        }

        // --- Content ---------------------------------------------------------------------

        /// <summary>Text capped at the attachment limit; the notice matches the attachment one.</summary>
        internal static string CapText(string content, string fullPath, out bool truncated)
        {
            content = content ?? "";
            truncated = content.Length > AttachmentFormatting.MaxInlineTextChars;
            if (!truncated) return content;
            return content.Substring(0, AttachmentFormatting.MaxInlineTextChars) + "\n" +
                   AttachmentFormatting.TruncationNotice(fullPath);
        }

        /// <summary>Word text: paragraphs of word/document.xml (tabs and breaks kept). Null when it is not a .docx.</summary>
        internal static string ReadDocxText(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var part = zip.GetEntry("word/document.xml");
                if (part == null) return null;
                XDocument xml;
                using (var s = part.Open()) xml = XDocument.Load(s);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                var sb = new StringBuilder();
                foreach (var p in xml.Descendants(w + "p"))
                {
                    foreach (var node in p.Descendants())
                    {
                        if (node.Name == w + "t") sb.Append(node.Value);
                        else if (node.Name == w + "tab") sb.Append('\t');
                        else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
                    }
                    sb.Append('\n');
                }
                return sb.ToString();
            }
        }

        // PDF text, keyed by the file's content hash so an edited PDF is re-extracted.
        private static readonly object PdfSync = new object();
        private static readonly Dictionary<string, Task<List<string>>> PdfRunning =
            new Dictionary<string, Task<List<string>>>(StringComparer.OrdinalIgnoreCase);

        internal static string PdfCacheDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeModel", "projectdocs", "_cache");

        public enum PdfTextState { Ready, Running, NoHelper, Failed }

        /// <summary>
        /// PDF text through the plan-11 helper (VibeModel.PdfText.exe), cached. Waits at most
        /// <paramref name="wait"/>; a slower extraction keeps running in the background and the
        /// next call returns its cached result.
        /// </summary>
        public static List<string> ReadPdfPages(string path, TimeSpan wait, out PdfTextState state, out string error)
        {
            error = null;
            if (DocumentIndexService.PdfToolPath == null)
            {
                state = PdfTextState.NoHelper;
                return null;
            }

            string key;
            try { key = DocumentIndexService.FileHash(path); }
            catch (Exception ex) { state = PdfTextState.Failed; error = ex.Message; return null; }
            var cacheFile = System.IO.Path.Combine(PdfCacheDir, key + ".json");

            try
            {
                if (File.Exists(cacheFile))
                {
                    var cached = PackJson.Parse(File.ReadAllText(cacheFile, Encoding.UTF8));
                    if (cached != null && cached.TryGetValue("pages", out var v) && v is System.Collections.IEnumerable e)
                    {
                        state = PdfTextState.Ready;
                        return e.Cast<object>().Select(o => o as string ?? "").ToList();
                    }
                }
            }
            catch { /* corrupt cache: re-extract */ }

            Task<List<string>> task;
            lock (PdfSync)
            {
                if (!PdfRunning.TryGetValue(key, out task))
                {
                    task = Task.Run(() =>
                    {
                        try
                        {
                            var pages = DocumentIndexService.ExtractPages(path, out var err);
                            if (pages == null) throw new InvalidOperationException(err ?? "PDF extraction failed");
                            try
                            {
                                Directory.CreateDirectory(PdfCacheDir);
                                var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                                    .Serialize(new Dictionary<string, object> { { "file", path }, { "pages", pages } });
                                File.WriteAllText(cacheFile, json, Encoding.UTF8);
                            }
                            catch (Exception ex) { Logger.Warn("PDF text cache write failed: " + ex.Message); }
                            return pages;
                        }
                        finally
                        {
                            lock (PdfSync) PdfRunning.Remove(key);
                        }
                    });
                    PdfRunning[key] = task;
                }
            }

            try
            {
                if (!task.Wait(wait))
                {
                    state = PdfTextState.Running;
                    return null;
                }
                state = PdfTextState.Ready;
                return task.Result;
            }
            catch (AggregateException ex)
            {
                state = PdfTextState.Failed;
                error = ex.InnerException?.Message ?? ex.Message;
                return null;
            }
        }
    }
}
