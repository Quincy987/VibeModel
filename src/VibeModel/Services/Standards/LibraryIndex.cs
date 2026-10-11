using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Standards
{
    public sealed class LibraryEntry
    {
        public string Name { get; set; }     // file name without .rfa
        public string Path { get; set; }     // full path
        public string Folder { get; set; }   // folder relative to its root, for display/search
        public string Prefix { get; set; }   // matched family prefix class, or "other"
    }

    /// <summary>
    /// Index of the office family library (*.rfa under the pack's libraryPaths). Built OFF the
    /// Revit thread — the library usually lives on a slow network share — and cached to disk
    /// so lookups are instant and keep working when the share is unreachable (off-site, VPN).
    /// </summary>
    public sealed class LibraryIndex
    {
        public DateTime BuiltUtc { get; set; }
        public List<string> Roots { get; set; } = new List<string>();
        public List<string> UnreachableRoots { get; set; } = new List<string>();
        public List<LibraryEntry> Entries { get; set; } = new List<LibraryEntry>();

        /// <summary>Scan the roots recursively. Unreadable folders are skipped, not fatal.</summary>
        public static LibraryIndex Build(IEnumerable<string> roots, IList<string> prefixes, CancellationToken ct)
        {
            var idx = new LibraryIndex { BuiltUtc = DateTime.UtcNow };
            var ordered = (prefixes ?? new List<string>()).OrderByDescending(p => p.Length).ToList();
            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                ct.ThrowIfCancellationRequested();
                idx.Roots.Add(root);
                if (!Directory.Exists(root)) { idx.UnreachableRoots.Add(root); continue; }

                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = stack.Pop();
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir)) stack.Push(sub);
                        foreach (var file in Directory.GetFiles(dir, "*.rfa"))
                        {
                            var name = Path.GetFileNameWithoutExtension(file);
                            // Revit backup copies (Family.0001.rfa) are noise.
                            if (IsBackupName(name)) continue;
                            idx.Entries.Add(new LibraryEntry
                            {
                                Name = name,
                                Path = file,
                                Folder = RelativeFolder(root, dir),
                                Prefix = ordered.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? "other"
                            });
                        }
                    }
                    catch (Exception) { /* access denied / vanished folder: skip */ }
                }
            }
            return idx;
        }

        internal static bool IsBackupName(string name)
        {
            // "<name>.0001" — four digits after the last dot
            int dot = name.LastIndexOf('.');
            if (dot < 0 || name.Length - dot - 1 != 4) return false;
            for (int i = dot + 1; i < name.Length; i++) if (!char.IsDigit(name[i])) return false;
            return true;
        }

        private static string RelativeFolder(string root, string dir)
        {
            try
            {
                var r = Path.GetFullPath(root).TrimEnd('\\', '/');
                var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
                return d.Length > r.Length ? d.Substring(r.Length).TrimStart('\\', '/') : "";
            }
            catch { return dir; }
        }

        /// <summary>
        /// Every whitespace-separated term must occur in the name or the folder. Name hits rank
        /// above folder-only hits; shorter names first (closest match).
        /// </summary>
        public List<LibraryEntry> Search(string query, int max = 15)
        {
            var terms = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant()).ToList();
            if (terms.Count == 0) return new List<LibraryEntry>();
            return Entries
                .Select(e => new { e, name = e.Name.ToLowerInvariant(), folder = (e.Folder ?? "").ToLowerInvariant() })
                .Where(x => terms.All(t => x.name.Contains(t) || x.folder.Contains(t)))
                .OrderByDescending(x => terms.Count(t => x.name.Contains(t)))
                .ThenBy(x => x.e.Name.Length)
                .ThenBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max).Select(x => x.e).ToList();
        }

        // --- Persistence ----------------------------------------------------------------

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var obj = new Dictionary<string, object>
            {
                { "builtUtc", BuiltUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "roots", Roots },
                { "unreachable", UnreachableRoots },
                { "entries", Entries.Select(e => (object)new Dictionary<string, object>
                    { { "n", e.Name }, { "p", e.Path }, { "f", e.Folder }, { "x", e.Prefix } }).ToList() }
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, PackJson.Serializer.Serialize(obj), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static LibraryIndex Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var d = PackJson.Parse(File.ReadAllText(path, Encoding.UTF8));
                var idx = new LibraryIndex();
                DateTime.TryParse(PackJson.GetString(d, "builtUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var built);
                idx.BuiltUtc = built;
                idx.Roots = PackJson.GetStrings(d, "roots");
                idx.UnreachableRoots = PackJson.GetStrings(d, "unreachable");
                foreach (var e in PackJson.GetDictList(d, "entries"))
                    idx.Entries.Add(new LibraryEntry
                    {
                        Name = PackJson.GetString(e, "n"),
                        Path = PackJson.GetString(e, "p"),
                        Folder = PackJson.GetString(e, "f"),
                        Prefix = PackJson.GetString(e, "x")
                    });
                return idx;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Owns the background build of one library index per pack. Commands only ever read the
    /// last finished index; a stale or missing index triggers a rebuild on a worker thread.
    /// </summary>
    public static class LibraryIndexService
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, LibraryIndex> Loaded = new Dictionary<string, LibraryIndex>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task> Building = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> LastError = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
        public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

        public static string IndexPath(StandardsPack pack) =>
            Path.Combine(StandardsPackRegistry.CacheDir(pack.Id), "library-index.json");

        /// <summary>The current index (possibly stale) or null; kicks off a rebuild when needed.</summary>
        public static LibraryIndex Get(StandardsPack pack, bool forceRebuild = false)
        {
            if (pack == null) return null;
            LibraryIndex idx;
            lock (Sync)
            {
                if (!Loaded.TryGetValue(pack.Id, out idx))
                {
                    idx = LibraryIndex.Load(IndexPath(pack));
                    if (idx != null) Loaded[pack.Id] = idx;
                }
            }
            if (forceRebuild || idx == null || DateTime.UtcNow - idx.BuiltUtc > MaxAge)
                StartBuild(pack);
            return idx;
        }

        public static bool IsBuilding(StandardsPack pack)
        {
            lock (Sync) return pack != null && Building.TryGetValue(pack.Id, out var t) && !t.IsCompleted;
        }

        public static string GetLastError(StandardsPack pack)
        {
            lock (Sync) return pack != null && LastError.TryGetValue(pack.Id, out var e) ? e : null;
        }

        public static void StartBuild(StandardsPack pack)
        {
            if (pack == null || pack.LibraryPaths.Count == 0) return;
            lock (Sync)
            {
                if (Building.TryGetValue(pack.Id, out var running) && !running.IsCompleted) return;
                var cts = new CancellationTokenSource(BuildTimeout);
                var roots = pack.LibraryPaths.ToList();
                var prefixes = pack.FamilyPrefixes.ToList();
                var path = IndexPath(pack);
                var id = pack.Id;
                Building[id] = Task.Run(() =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var idx = LibraryIndex.Build(roots, prefixes, cts.Token);
                        // A scan that reached nothing must not overwrite a good cached index.
                        if (idx.Entries.Count == 0 && idx.UnreachableRoots.Count == idx.Roots.Count)
                        {
                            lock (Sync) LastError[id] = "library unreachable: " + string.Join("; ", idx.UnreachableRoots);
                            Logger.Warn("Library index for " + id + ": roots unreachable, keeping cached index");
                            return;
                        }
                        idx.Save(path);
                        lock (Sync) { Loaded[id] = idx; LastError.Remove(id); }
                        Logger.Info("Library index for " + id + ": " + idx.Entries.Count + " families in " + sw.ElapsedMilliseconds + " ms");
                    }
                    catch (OperationCanceledException)
                    {
                        lock (Sync) LastError[id] = "library scan timed out after " + BuildTimeout.TotalMinutes + " min";
                    }
                    catch (Exception ex)
                    {
                        lock (Sync) LastError[id] = ex.Message;
                        Logger.Warn("Library index build failed for " + id + ": " + ex.Message);
                    }
                    finally { cts.Dispose(); }
                });
            }
        }
    }
}
