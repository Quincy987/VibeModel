using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Finds every standards pack on this machine and decides which one applies to a document.
    ///
    /// Search order (first occurrence of a pack id wins):
    ///   1. the StandardsPackPath setting — a pack folder, or a folder of pack folders
    ///      (this is the knob that later points at the office share instead of a local copy);
    ///   2. %LOCALAPPDATA%\VibeModel\standards\*\pack.json (the local working copy);
    ///   3. &lt;add-in dll folder&gt;\standards\*\pack.json (anything shipped with the add-in).
    /// Every pack is loaded; the active one per document is the matching pack with the most
    /// specific fingerprint. Discovery is pure file IO and safe off the Revit thread.
    /// </summary>
    public static class StandardsPackRegistry
    {
        private static readonly object Sync = new object();
        private static List<DiscoveredPack> _discovered;

        public static string LocalStandardsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "standards");

        public static string AddinStandardsRoot
        {
            get
            {
                try
                {
                    var dir = Path.GetDirectoryName(typeof(StandardsPackRegistry).Assembly.Location);
                    return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "standards");
                }
                catch { return null; }
            }
        }

        /// <summary>Ordered (root, origin) pairs to search, per the documented order.</summary>
        public static List<KeyValuePair<string, string>> ResolveSearchRoots(
            string settingPath, string localRoot, string addinRoot)
        {
            var roots = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrWhiteSpace(settingPath))
                roots.Add(new KeyValuePair<string, string>(settingPath.Trim(), "setting StandardsPackPath"));
            if (!string.IsNullOrWhiteSpace(localRoot))
                roots.Add(new KeyValuePair<string, string>(localRoot, "local copy"));
            if (!string.IsNullOrWhiteSpace(addinRoot))
                roots.Add(new KeyValuePair<string, string>(addinRoot, "shipped with add-in"));
            return roots;
        }

        /// <summary>
        /// Load every pack under the given roots. A root may itself be a pack folder (has
        /// pack.json) or contain pack folders one level down. Duplicated ids: first wins, later
        /// copies are reported as shadowed so "why is my edit not picked up" is answerable.
        /// </summary>
        public static List<DiscoveredPack> Discover(IEnumerable<KeyValuePair<string, string>> roots)
        {
            var result = new List<DiscoveredPack>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                foreach (var folder in CandidateFolders(root.Key, result, root.Value))
                {
                    string norm;
                    try { norm = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
                    catch { norm = folder; }
                    if (!seenFolders.Add(norm)) continue;

                    var pack = StandardsPack.TryLoad(folder, out var error);
                    if (pack == null)
                    {
                        result.Add(new DiscoveredPack { Folder = folder, Error = error, Origin = root.Value });
                        continue;
                    }
                    if (!seenIds.Add(pack.Id))
                    {
                        result.Add(new DiscoveredPack
                        {
                            Folder = folder, Origin = root.Value,
                            Error = "shadowed: pack id '" + pack.Id + "' already loaded from an earlier search root"
                        });
                        continue;
                    }
                    result.Add(new DiscoveredPack { Folder = folder, Pack = pack, Origin = root.Value });
                }
            }
            return result;
        }

        private static IEnumerable<string> CandidateFolders(string root, List<DiscoveredPack> report, string origin)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(root))
                {
                    // Only the explicit setting is worth reporting; absent defaults are normal.
                    if (origin.StartsWith("setting", StringComparison.Ordinal))
                        report.Add(new DiscoveredPack { Folder = root, Origin = origin, Error = "folder not found or unreachable" });
                    return list;
                }
                if (File.Exists(Path.Combine(root, StandardsPack.PackFileName)))
                {
                    list.Add(root);
                    return list;
                }
                foreach (var sub in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(Path.Combine(sub, StandardsPack.PackFileName)))
                        list.Add(sub);
            }
            catch (Exception ex)
            {
                report.Add(new DiscoveredPack { Folder = root, Origin = origin, Error = ex.Message });
            }
            return list;
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

        public static List<DiscoveredPack> GetDiscovered()
        {
            lock (Sync)
            {
                if (_discovered == null)
                {
                    var roots = ResolveSearchRoots(SettingsManager.GetStandardsPackPath(),
                        LocalStandardsRoot, AddinStandardsRoot);
                    _discovered = Discover(roots);
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
                }
                return _discovered;
            }
        }

        public static List<StandardsPack> GetPacks() =>
            GetDiscovered().Where(d => d.Pack != null).Select(d => d.Pack).ToList();

        /// <summary>Forget everything; the next call rediscovers (after a setting change or --refresh).</summary>
        public static void Refresh()
        {
            lock (Sync) { _discovered = null; }
        }

        /// <summary>Per-pack cache folder for derived data (index, generated core, check log).</summary>
        public static string CacheDir(string packId) => Path.Combine(LocalStandardsRoot, "_cache", Sanitize(packId));

        /// <summary>The office standards folder: setting first, then the pack's own default.</summary>
        public static string ResolveStandardsFolder(StandardsPack pack)
        {
            var setting = SettingsManager.GetStandardsFolder();
            if (!string.IsNullOrWhiteSpace(setting)) return setting.Trim();
            return pack?.StandardsFolder;
        }

        private static void AttachRuntimePaths(StandardsPack pack)
        {
            pack.GeneratedCorePath = Path.Combine(CacheDir(pack.Id), "core.generated.md");
            pack.AdditionsPaths = new List<string> { Path.Combine(pack.Folder, "aanvullingen.md") };
            var folder = ResolveStandardsFolder(pack);
            if (!string.IsNullOrWhiteSpace(folder))
                pack.AdditionsPaths.Add(Path.Combine(folder, "aanvullingen.md"));
        }

        internal static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "pack";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
