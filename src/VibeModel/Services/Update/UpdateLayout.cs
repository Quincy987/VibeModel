using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using VibeModel.Loader;

namespace VibeModel.Services.Update
{
    /// <summary>
    /// The folders an update lives in (plan 19): <c>%LOCALAPPDATA%\VibeModel\versions\Revit&lt;year&gt;\&lt;version&gt;\</c>
    /// (one per installed version), <c>current.txt</c> next to them (the version the loader starts next)
    /// and <c>%LOCALAPPDATA%\VibeModel\updates\</c> (downloads, the cached manifest). Roots are
    /// parameters so tests use a temp folder.
    /// </summary>
    public sealed class UpdateLayout
    {
        public string VersionsRoot { get; }
        public string DownloadDir { get; }

        public UpdateLayout(string versionsRoot, string downloadDir)
        {
            VersionsRoot = versionsRoot;
            DownloadDir = downloadDir;
        }

        /// <summary>The real folders for a Revit year (downloads are shared: their names carry the year).</summary>
        public static UpdateLayout For(string revitYear) => new UpdateLayout(
            LoaderPaths.VersionsRoot(revitYear), Path.Combine(LoaderPaths.DataRoot, "updates"));

        public string CurrentFile => Path.Combine(VersionsRoot, LoaderPaths.CurrentFile);

        public string FolderPath(string version) => Path.Combine(VersionsRoot, version);

        /// <summary>The folder name current.txt names (null when none / invalid).</summary>
        public string ReadCurrent() => LoaderPaths.ReadCurrent(VersionsRoot);

        /// <summary>Points the loader at <paramref name="folder"/> for the next Revit start (written atomically).</summary>
        public void WriteCurrent(string folder)
        {
            if (!LoaderPaths.IsPlainFolderName(folder))
                throw new ArgumentException("not a version folder name: " + folder);
            Directory.CreateDirectory(VersionsRoot);
            // A name of its own: another Revit (or install.cmd) may write current.txt at the same time.
            var tmp = CurrentFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, folder + Environment.NewLine);
            try
            {
                if (File.Exists(CurrentFile)) File.Replace(tmp, CurrentFile, null);
                else File.Move(tmp, CurrentFile);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        /// <summary>True when the version folder holds VibeModel.dll.</summary>
        public bool IsComplete(string folder) =>
            LoaderPaths.IsPlainFolderName(folder) && File.Exists(Path.Combine(VersionsRoot, folder, LoaderPaths.PayloadFile));

        /// <summary>
        /// The version folder an add-in at <paramref name="payloadPath"/> runs from — null when it does
        /// not run from <see cref="VersionsRoot"/> (a developer build in the add-ins folder).
        /// </summary>
        public string RunningFolder(string payloadPath)
        {
            if (string.IsNullOrEmpty(payloadPath)) return null;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(payloadPath));
                var parent = Path.GetDirectoryName(dir);
                if (parent == null) return null;
                return string.Equals(Normalize(parent), Normalize(VersionsRoot), StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(dir)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        /// <summary>Complete version folders, newest first.</summary>
        public List<KeyValuePair<string, Version>> Installed() => LoaderPaths.Installed(VersionsRoot);

        /// <summary>
        /// What "back" means: the newest installed version older than the one Revit starts next
        /// (current.txt, else the running one). Null when there is none.
        /// </summary>
        public string RollbackTarget(string running, string current)
        {
            var from = current ?? running;
            if (!LoaderPaths.TryParseFolderVersion(from, out var fromV)) return null;
            foreach (var kv in Installed())
                if (kv.Value < fromV) return kv.Key;
            return null;
        }

        /// <summary>
        /// Keeps the running version, the current one and one older version (the rollback target);
        /// removes other version folders and stale ".tmp" leftovers. A folder another Revit still
        /// uses cannot be renamed and is left alone. Returns the removed folder names.
        /// </summary>
        public List<string> Prune(string running, string current)
        {
            var removed = new List<string>();
            if (!Directory.Exists(VersionsRoot)) return removed;

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (running != null) keep.Add(running);
            if (current != null) keep.Add(current);
            Version oldestKept = null;
            foreach (var name in keep)
                if (LoaderPaths.TryParseFolderVersion(name, out var v) && (oldestKept == null || v < oldestKept))
                    oldestKept = v;
            if (oldestKept != null)
                foreach (var kv in Installed())
                    if (kv.Value < oldestKept) { keep.Add(kv.Key); break; }

            foreach (var dir in Directory.GetDirectories(VersionsRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.IndexOf(".removing-", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { Directory.Delete(dir, true); } catch { }
                    continue;
                }
                bool isVersion = LoaderPaths.TryParseFolderVersion(name, out _);
                // "<version>.tmp.<pid>": an unpack that never finished (a crashed Revit), after an hour.
                bool isStaleTmp = name.IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                  Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1);
                if ((!isVersion && !isStaleTmp) || keep.Contains(name)) continue;
                if (TryRemove(dir)) removed.Add(name);
            }
            return removed;
        }

        // Rename first: a folder whose DLLs another Revit has loaded cannot be renamed, so it is
        // never left half-deleted.
        private static bool TryRemove(string dir)
        {
            var doomed = dir + ".removing-" + DateTime.UtcNow.Ticks;
            try
            {
                Directory.Move(dir, doomed);
            }
            catch
            {
                return false;
            }
            try
            {
                Directory.Delete(doomed, true);
            }
            catch
            {
                // Leftover; the next prune does not see it as a version and it does no harm.
            }
            return true;
        }
    }

    /// <summary>Unpacks a zip only inside its target folder (no "..", no absolute paths, no drive letters).</summary>
    public static class ZipSafety
    {
        /// <summary>The full path an entry would land on, or null when it would escape <paramref name="targetDir"/>.</summary>
        public static string SafePath(string targetDir, string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;
            var name = entryName.Replace('/', '\\');
            if (name.StartsWith("\\", StringComparison.Ordinal) || name.IndexOf(':') >= 0) return null;
            foreach (var part in name.Split('\\'))
                if (part == "..") return null;
            try
            {
                var root = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
                var full = Path.GetFullPath(Path.Combine(root, name));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Extracts every entry; refuses the whole zip (nothing written) when one entry is unsafe.</summary>
        public static void ExtractTo(string zipPath, string targetDir)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                    if (SafePath(targetDir, entry.FullName) == null)
                        throw new InvalidDataException("the update zip contains an unsafe path: " + entry.FullName);

                Directory.CreateDirectory(targetDir);
                foreach (var entry in zip.Entries)
                {
                    var path = SafePath(targetDir, entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
            }
        }
    }
}
