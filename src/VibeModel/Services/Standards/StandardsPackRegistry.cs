using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Standards
{
    /// <summary>A pack the registry found, or a folder it could not use (with the reason).</summary>
    public sealed class DiscoveredPack
    {
        public string Folder { get; set; }
        public StandardsPack Pack { get; set; }
        public string Error { get; set; }
        public string Origin { get; set; }
    }

    /// <summary>One place to look for packs, in search order.</summary>
    public sealed class SearchRoot
    {
        public string Path { get; set; }
        public string Origin { get; set; }
        /// <summary>Report a missing folder (true only for a folder the user configured).</summary>
        public bool ReportMissing { get; set; }
    }

    /// <summary>
    /// Finds every standards pack on this machine and decides which one applies to a document.
    ///
    /// Search order (first occurrence of a pack id wins):
    ///   1. the StandardsPackPath setting — a pack folder, or a folder of pack folders. A local
    ///      path is read directly; a NETWORK path (UNC or mapped drive) is never read on Revit's
    ///      main thread: a background sync copies its packs to %LOCALAPPDATA%\VibeModel\standards\
    ///      _mirror\ and discovery reads that mirror;
    ///   2. %LOCALAPPDATA%\VibeModel\standards\*\pack.json (the local working copy);
    ///   3. &lt;add-in dll folder&gt;\standards\*\pack.json (anything shipped with the add-in).
    /// Every pack is loaded; the active one per document is the matching pack with the most
    /// specific fingerprint.
    /// </summary>
    public static class StandardsPackRegistry
    {
        public const string MirrorSourceFile = "_source.txt";
        public static readonly TimeSpan ShareListTimeout = TimeSpan.FromSeconds(20);
        private const long MaxMirroredFileBytes = 5 * 1024 * 1024;

        private static readonly object Sync = new object();
        private static List<DiscoveredPack> _discovered;
        private static int _generation;
        private static Task _shareSync;

        /// <summary>Bumped whenever the pack set may have changed; consumers compare it to invalidate.</summary>
        public static int Generation => Volatile.Read(ref _generation);

        /// <summary>Outcome of the last share sync (null when the setting is not a network path).</summary>
        public static string ShareSyncStatus { get; private set; }

        public static string LocalStandardsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "standards");

        public static string MirrorRoot => Path.Combine(LocalStandardsRoot, "_mirror");

        public static string AddinStandardsRoot
        {
            get
            {
                try
                {
                    // The add-in folder the user knows: Revit's add-ins folder, where the loader lives
                    // (plan 19). VibeModel.dll itself runs from a version folder that every update replaces.
                    var anchor = Update.LoaderInfo.Path ?? typeof(StandardsPackRegistry).Assembly.Location;
                    var dir = Path.GetDirectoryName(anchor);
                    return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "standards");
                }
                catch { return null; }
            }
        }

        /// <summary>Ordered roots to search, per the documented order.</summary>
        public static List<SearchRoot> ResolveSearchRoots(string settingPath, string localRoot, string addinRoot,
            string settingOrigin = "setting StandardsPackPath")
        {
            var roots = new List<SearchRoot>();
            if (!string.IsNullOrWhiteSpace(settingPath))
                roots.Add(new SearchRoot { Path = settingPath.Trim(), Origin = settingOrigin, ReportMissing = true });
            if (!string.IsNullOrWhiteSpace(localRoot))
                roots.Add(new SearchRoot { Path = localRoot, Origin = "local copy" });
            if (!string.IsNullOrWhiteSpace(addinRoot))
                roots.Add(new SearchRoot { Path = addinRoot, Origin = "shipped with add-in" });
            return roots;
        }

        /// <summary>
        /// Load every pack under the given roots. A root may itself be a pack folder (has
        /// pack.json) or contain pack folders one level down. Duplicated ids: first wins, later
        /// copies are reported as shadowed so "why is my edit not picked up" is answerable.
        /// </summary>
        public static List<DiscoveredPack> Discover(IEnumerable<SearchRoot> roots)
        {
            var result = new List<DiscoveredPack>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                List<string> folders;
                try
                {
                    folders = FindPackFolders(root.Path);
                    if (folders == null)
                    {
                        if (root.ReportMissing)
                            result.Add(new DiscoveredPack { Folder = root.Path, Origin = root.Origin, Error = "folder not found" });
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    result.Add(new DiscoveredPack { Folder = root.Path, Origin = root.Origin, Error = ex.Message });
                    continue;
                }

                foreach (var folder in folders)
                {
                    string norm;
                    try { norm = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
                    catch { norm = folder; }
                    if (!seenFolders.Add(norm)) continue;

                    var pack = StandardsPack.TryLoad(folder, out var error);
                    if (pack == null)
                    {
                        result.Add(new DiscoveredPack { Folder = folder, Error = error, Origin = root.Origin });
                        continue;
                    }
                    if (!seenIds.Add(pack.Id))
                    {
                        result.Add(new DiscoveredPack
                        {
                            Folder = folder, Origin = root.Origin,
                            Error = "shadowed: pack id '" + pack.Id + "' already loaded from an earlier search root"
                        });
                        continue;
                    }
                    result.Add(new DiscoveredPack { Folder = folder, Pack = pack, Origin = root.Origin });
                }
            }
            return result;
        }

        /// <summary>Pack folders under a root (the root itself, or its direct subfolders); null when the root is missing.</summary>
        internal static List<string> FindPackFolders(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            if (File.Exists(Path.Combine(root, StandardsPack.PackFileName))) return new List<string> { root };
            return Directory.GetDirectories(root)
                .Where(sub => File.Exists(Path.Combine(sub, StandardsPack.PackFileName)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Pick the pack for a document: among packs whose fingerprint matches, the one with
        /// the highest score (most specific) wins; ties go to search order. Null = no pack.
        /// </summary>
        public static StandardsPack PickActive(IEnumerable<StandardsPack> packs, TemplateFacts facts,
            out FingerprintResult match, List<string> rejections = null)
        {
            match = null;
            StandardsPack best = null;
            foreach (var p in packs ?? Enumerable.Empty<StandardsPack>())
            {
                var r = FingerprintMatcher.Match(p.Fingerprint, facts);
                if (!r.Matched)
                {
                    rejections?.Add(p.Id + ": missing " + string.Join("; ", r.Missing));
                    continue;
                }
                if (best == null || r.Max > match.Max)
                {
                    best = p;
                    match = r;
                }
            }
            return best;
        }

        // --- Process-wide cache (setting + default roots) ----------------------------------

        /// <summary>
        /// The discovered packs. Reads LOCAL folders only (a network setting is served from its
        /// mirror), so it is safe on Revit's main thread.
        /// </summary>
        public static List<DiscoveredPack> GetDiscovered()
        {
            lock (Sync)
            {
                if (_discovered != null) return _discovered;

                var setting = SettingsManager.GetStandardsPackPath()?.Trim();
                var settingRoot = setting;
                var settingOrigin = "setting StandardsPackPath";
                var report = new List<DiscoveredPack>();
                if (PathProbe.IsNetworkPath(setting))
                {
                    settingRoot = MirrorMatches(setting) ? MirrorRoot : null;
                    settingOrigin = "setting StandardsPackPath (local mirror of " + setting + ")";
                    if (settingRoot == null)
                        report.Add(new DiscoveredPack
                        {
                            Folder = setting, Origin = settingOrigin,
                            Error = "network share not mirrored yet" + (ShareSyncStatus != null ? " (" + ShareSyncStatus + ")" : "") +
                                    " — it is copied in the background; run 'standards --refresh' to retry"
                        });
                }

                _discovered = report.Concat(Discover(ResolveSearchRoots(settingRoot, LocalStandardsRoot, AddinStandardsRoot, settingOrigin))).ToList();
                foreach (var d in _discovered)
                {
                    if (d.Pack != null)
                    {
                        AttachRuntimePaths(d.Pack);
                        Logger.Info("Standards pack loaded: " + d.Pack.Id + " " + d.Pack.Version + " (" + d.Origin + ": " + d.Folder + ")");
                    }
                    else
                        Logger.Info("Standards pack skipped: " + d.Folder + " — " + d.Error);
                }
                return _discovered;
            }
        }

        public static List<StandardsPack> GetPacks() =>
            GetDiscovered().Where(d => d.Pack != null).Select(d => d.Pack).ToList();

        /// <summary>Forget everything; the next call rediscovers (after a setting change, a sync or --refresh).</summary>
        public static void Refresh()
        {
            lock (Sync) { _discovered = null; }
            Interlocked.Increment(ref _generation);
        }

        // --- Network share mirror ------------------------------------------------------------

        private static bool MirrorMatches(string share)
        {
            try
            {
                var source = File.ReadAllText(Path.Combine(MirrorRoot, MirrorSourceFile)).Trim();
                return string.Equals(source.TrimEnd('\\', '/'), share.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Start a background copy of the network StandardsPackPath into the local mirror.
        /// No-op (false) when the setting is empty or local. On success the registry refreshes.
        /// </summary>
        public static bool StartShareSync()
        {
            var setting = SettingsManager.GetStandardsPackPath()?.Trim();
            if (!PathProbe.IsNetworkPath(setting)) return false;
            lock (Sync)
            {
                if (_shareSync == null || _shareSync.IsCompleted)
                    _shareSync = Task.Run(() => SyncShareNow(setting));
            }
            return true;
        }

        /// <summary>Synchronous sync (background callers only). Never throws.</summary>
        public static void SyncShareNow(string share)
        {
            var status = SyncShare(share, MirrorRoot);
            ShareSyncStatus = status;
            Logger.Info("Standards share sync (" + share + "): " + status);
            Refresh();
        }

        /// <summary>
        /// Copy every pack folder under <paramref name="share"/> into <paramref name="mirrorRoot"/>
        /// (staged, then swapped in, so readers never see a half copy). An unreachable share
        /// leaves the previous mirror untouched. Returns a one-line status.
        /// </summary>
        internal static string SyncShare(string share, string mirrorRoot)
        {
            var folders = PathProbe.Run(() => FindPackFolders(share), ShareListTimeout, null);
            if (folders == null)
                return "share unreachable at " + DateTime.Now.ToString("HH:mm") + " — using the previous mirror";

            var staging = mirrorRoot + ".staging";
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                int files = 0;
                foreach (var folder in folders)
                {
                    var name = new DirectoryInfo(folder).Name;
                    files += CopyTree(folder, Path.Combine(staging, name));
                }
                File.WriteAllText(Path.Combine(staging, MirrorSourceFile), share);

                if (Directory.Exists(mirrorRoot)) Directory.Delete(mirrorRoot, true);
                Directory.Move(staging, mirrorRoot);
                return "synced " + folders.Count + " pack(s), " + files + " file(s) at " + DateTime.Now.ToString("HH:mm");
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                return "sync failed: " + ex.Message;
            }
        }

        private static int CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (var f in Directory.GetFiles(from))
            {
                if (new FileInfo(f).Length > MaxMirroredFileBytes) continue; // packs are text; skip stray binaries
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
                n++;
            }
            foreach (var d in Directory.GetDirectories(from))
                n += CopyTree(d, Path.Combine(to, new DirectoryInfo(d).Name));
            return n;
        }

        // --- Per-pack local paths -------------------------------------------------------------

        /// <summary>Per-pack cache folder for derived data (index, generated core, check log, additions copy).</summary>
        public static string CacheDir(string packId) => Path.Combine(LocalStandardsRoot, "_cache", Sanitize(packId));

        /// <summary>The office standards folder: setting first, then the pack's own default.</summary>
        public static string ResolveStandardsFolder(StandardsPack pack)
        {
            var setting = SettingsManager.GetStandardsFolder();
            if (!string.IsNullOrWhiteSpace(setting)) return setting.Trim();
            return pack?.StandardsFolder;
        }

        /// <summary>Local copy of the additions file found in the standards folder (refreshed by the document index run).</summary>
        public static string CachedAdditionsPath(StandardsPack pack) => Path.Combine(CacheDir(pack.Id), pack.AdditionsFile);

        // Only LOCAL paths: the core is rendered on Revit's main thread.
        private static void AttachRuntimePaths(StandardsPack pack)
        {
            pack.GeneratedCorePath = Path.Combine(CacheDir(pack.Id), "core.generated.md");
            pack.AdditionsPaths = new List<string>
            {
                Path.Combine(pack.Folder, pack.AdditionsFile),
                CachedAdditionsPath(pack)
            };
        }

        internal static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "pack";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
