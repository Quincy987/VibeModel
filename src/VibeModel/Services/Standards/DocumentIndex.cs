using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Standards
{
    /// <summary>One office source document, split into chapters and cached by content hash.</summary>
    public sealed class IndexedDocument
    {
        public string File { get; set; }
        public string Name => Path.GetFileNameWithoutExtension(File ?? "");
        public string Hash { get; set; }
        public DateTime IndexedUtc { get; set; }
        public int PageCount { get; set; }
        public List<DocChapter> Chapters { get; set; } = new List<DocChapter>();

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            { "file", File }, { "hash", Hash },
            { "indexedUtc", IndexedUtc.ToString("o", CultureInfo.InvariantCulture) },
            { "pageCount", PageCount },
            { "chapters", Chapters.Select(c => (object)new Dictionary<string, object>
                {
                    { "number", c.Number }, { "title", c.Title },
                    { "pageFrom", c.PageFrom }, { "pageTo", c.PageTo }, { "text", c.Text }
                }).ToList() }
        };

        public static IndexedDocument FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var doc = new IndexedDocument
            {
                File = PackJson.GetString(d, "file"),
                Hash = PackJson.GetString(d, "hash"),
                PageCount = (int)(PackJson.GetDouble(d, "pageCount") ?? 0)
            };
            DateTime.TryParse(PackJson.GetString(d, "indexedUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t);
            doc.IndexedUtc = t;
            foreach (var c in PackJson.GetDictList(d, "chapters"))
                doc.Chapters.Add(new DocChapter
                {
                    Number = PackJson.GetString(c, "number"),
                    Title = PackJson.GetString(c, "title"),
                    PageFrom = (int)(PackJson.GetDouble(c, "pageFrom") ?? 0),
                    PageTo = (int)(PackJson.GetDouble(c, "pageTo") ?? 0),
                    Text = PackJson.GetString(c, "text") ?? ""
                });
            return doc;
        }
    }

    /// <summary>
    /// Plan 11 §4.0 — read the office's own source documents instead of a hand-typed copy.
    /// Lists the PDFs/.txt in the standards folder (top level only — the family library lives
    /// below it) plus the pack's declared sources, extracts text per page, splits it into
    /// chapters and caches &lt;hash&gt;.json under %LOCALAPPDATA%. A file whose hash is unchanged is
    /// never re-read; when the share is unreachable the cache is used as-is.
    ///
    /// PDF text comes from a small helper process (VibeModel.PdfText.exe, PdfPig) so the PDF
    /// library and its dependencies never load into Revit's AppDomain.
    /// </summary>
    public static class DocumentIndexService
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Task> Running = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(5);

        public static string IndexDir(StandardsPack pack) => Path.Combine(StandardsPackRegistry.CacheDir(pack.Id), "index");
        private static string ManifestPath(StandardsPack pack) => Path.Combine(IndexDir(pack), "manifest.json");

        public static string PdfToolPath
        {
            get
            {
                try
                {
                    var dir = Path.GetDirectoryName(typeof(DocumentIndexService).Assembly.Location) ?? "";
                    var candidates = new[]
                    {
                        Path.Combine(dir, "VibeModel.PdfText", "VibeModel.PdfText.exe"),
                        Path.Combine(dir, "VibeModel.PdfText.exe")
                    };
                    return candidates.FirstOrDefault(File.Exists);
                }
                catch { return null; }
            }
        }

        public static string GetStatus(StandardsPack pack)
        {
            lock (Sync)
            {
                if (pack == null) return "no pack";
                if (Running.TryGetValue(pack.Id, out var t) && !t.IsCompleted) return "indexing…";
                return Status.TryGetValue(pack.Id, out var s) ? s : "not indexed yet (run `standards --index`)";
            }
        }

        /// <summary>The cached documents (from the manifest) — works with the share offline.</summary>
        public static List<IndexedDocument> GetIndexed(StandardsPack pack)
        {
            var result = new List<IndexedDocument>();
            if (pack == null) return result;
            try
            {
                var manifest = LoadManifest(pack);
                foreach (var kv in manifest)
                {
                    var file = Path.Combine(IndexDir(pack), kv.Value + ".json");
                    if (!File.Exists(file)) continue;
                    var doc = IndexedDocument.FromJson(PackJson.Parse(File.ReadAllText(file, Encoding.UTF8)));
                    if (doc != null) result.Add(doc);
                }
            }
            catch (Exception ex) { Logger.Warn("Reading document index failed: " + ex.Message); }
            return result.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Start a background (re)index; returns false when one is already running.</summary>
        public static bool StartIndex(StandardsPack pack)
        {
            if (pack == null) return false;
            lock (Sync)
            {
                if (Running.TryGetValue(pack.Id, out var t) && !t.IsCompleted) return false;
                Running[pack.Id] = Task.Run(() => RunIndex(pack));
                return true;
            }
        }

        private static void RunIndex(StandardsPack pack)
        {
            try
            {
                var files = ListSourceFiles(pack, out var unreachable);
                Directory.CreateDirectory(IndexDir(pack));
                var manifest = LoadManifest(pack);
                int added = 0, cached = 0, failed = 0;
                var errors = new List<string>();

                foreach (var file in files)
                {
                    try
                    {
                        var hash = FileHash(file);
                        var target = Path.Combine(IndexDir(pack), hash + ".json");
                        if (File.Exists(target)) { manifest[file] = hash; cached++; continue; }

                        var pages = ExtractPages(file, out var err);
                        if (pages == null) { failed++; errors.Add(Path.GetFileName(file) + ": " + err); continue; }

                        var doc = new IndexedDocument
                        {
                            File = file, Hash = hash, IndexedUtc = DateTime.UtcNow,
                            PageCount = pages.Count, Chapters = ChapterSplitter.Split(pages)
                        };
                        File.WriteAllText(target, PackJson.Serializer.Serialize(doc.ToJson()), Encoding.UTF8);
                        manifest[file] = hash;
                        added++;
                    }
                    catch (Exception ex) { failed++; errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
                }

                SaveManifest(pack, manifest);
                var status = "indexed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                             ": " + added + " new, " + cached + " unchanged, " + failed + " failed" +
                             (unreachable.Count > 0
                                 ? "; " + unreachable.Count + " source(s) unreachable (e.g. " + string.Join(", ", unreachable.Take(2)) + ")"
                                 : "") +
                             (errors.Count > 0 ? "; errors: " + string.Join(" | ", errors.Take(3)) : "");
                lock (Sync) Status[pack.Id] = status;
                Logger.Info("Standards documents for " + pack.Id + ": " + status);
            }
            catch (Exception ex)
            {
                lock (Sync) Status[pack.Id] = "index failed: " + ex.Message;
                Logger.Warn("Standards document index failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The documents to index: *.pdf/*.txt at the TOP of the standards folder plus every
        /// source path the pack declares. Each probe runs with a timeout (network shares hang).
        /// </summary>
        internal static List<string> ListSourceFiles(StandardsPack pack, out List<string> unreachable)
        {
            unreachable = new List<string>();
            var files = new List<string>();
            var folder = StandardsPackRegistry.ResolveStandardsFolder(pack);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var listed = WithTimeout(() =>
                    Directory.Exists(folder)
                        ? Directory.GetFiles(folder).Where(IsIndexable).ToList()
                        : null);
                if (listed == null) unreachable.Add(folder);
                else files.AddRange(listed);
            }
            foreach (var src in pack.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Path) && IsIndexable(s.Path)))
            {
                if (files.Contains(src.Path, StringComparer.OrdinalIgnoreCase)) continue;
                var exists = WithTimeout(() => File.Exists(src.Path) ? "y" : null);
                if (exists == null) unreachable.Add(src.Path);
                else files.Add(src.Path);
            }
            return files;
        }

        private static bool IsIndexable(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".pdf" || ext == ".txt" || ext == ".md";
        }

        private static T WithTimeout<T>(Func<T> f) where T : class
        {
            try
            {
                var t = Task.Run(f);
                return t.Wait(ReachabilityTimeout) ? t.Result : null;
            }
            catch { return null; }
        }

        /// <summary>size + mtime + SHA-1 of the first MB: cheap, and changes when the file does.</summary>
        internal static string FileHash(string path)
        {
            var fi = new FileInfo(path);
            using (var sha = SHA1.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[Math.Min(fi.Length, 1024 * 1024)];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                var head = sha.ComputeHash(buf, 0, read);
                var seed = Encoding.UTF8.GetBytes(fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|");
                var all = sha.ComputeHash(seed.Concat(head).ToArray());
                return BitConverter.ToString(all, 0, 10).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Text per page. .txt/.md: pages split on form feeds. .pdf: the helper process.</summary>
        internal static List<string> ExtractPages(string file, out string error)
        {
            error = null;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".pdf")
                return File.ReadAllText(file, Encoding.UTF8).Split('\f').ToList();

            var tool = PdfToolPath;
            if (tool == null)
            {
                error = "PDF helper (VibeModel.PdfText.exe) not deployed — only .txt sources can be indexed";
                return null;
            }

            var outFile = Path.Combine(Path.GetTempPath(), "vibemodel-pdf-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = Quote(file) + " " + Quote(outFile),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    var stderr = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit((int)PerFileTimeout.TotalMilliseconds))
                    {
                        try { p.Kill(); } catch { }
                        error = "PDF extraction timed out";
                        return null;
                    }
                    if (p.ExitCode != 0 || !File.Exists(outFile))
                    {
                        error = "PDF extraction failed (exit " + p.ExitCode + "): " + Trim(stderr.Result);
                        return null;
                    }
                }
                var d = PackJson.Parse(File.ReadAllText(outFile, Encoding.UTF8));
                if (d == null) { error = "PDF helper wrote no JSON object"; return null; }
                return ReadPages(d);
            }
            finally
            {
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        // Pages may legitimately be empty strings (image-only pages) — keep them for page numbering.
        private static List<string> ReadPages(Dictionary<string, object> d)
        {
            var pages = new List<string>();
            if (d != null && d.TryGetValue("pages", out var v) && v is System.Collections.IEnumerable e)
                foreach (var item in e) pages.Add(item as string ?? "");
            return pages;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        private static string Trim(string s) => s == null ? "" : (s.Length > 300 ? s.Substring(0, 300) : s).Trim();

        private static Dictionary<string, string> LoadManifest(StandardsPack pack)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = ManifestPath(pack);
                if (File.Exists(path))
                    foreach (var kv in PackJson.Parse(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, object>())
                        if (kv.Value is string h) m[kv.Key] = h;
            }
            catch { /* corrupt manifest → rebuilt on next index */ }
            return m;
        }

        private static void SaveManifest(StandardsPack pack, Dictionary<string, string> manifest)
        {
            Directory.CreateDirectory(IndexDir(pack));
            File.WriteAllText(ManifestPath(pack),
                PackJson.Serializer.Serialize(manifest.ToDictionary(k => k.Key, k => (object)k.Value)), Encoding.UTF8);
        }

        /// <summary>Match a document by (case-insensitive) substring of its file name.</summary>
        public static IndexedDocument FindDocument(IEnumerable<IndexedDocument> docs, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            var list = docs.ToList();
            return list.FirstOrDefault(d => string.Equals(d.Name, query, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(d => d.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
