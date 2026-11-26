using System;
using System.Globalization;
using System.IO;
using System.Threading;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// The Claude Code CLI's own folder per chat: <c>%LOCALAPPDATA%\VibeModel\scratch\&lt;session&gt;\</c>,
    /// handed to the CLI as <see cref="EnvVar"/> and an <c>--add-dir</c>. On a laptop without Git Bash
    /// the CLI shells out through PowerShell 5.1, which breaks code sent inline (embedded quotes are
    /// not escaped for curl.exe — exit 3, or a snippet silently missing its quotes), and inside its
    /// PowerShell <c>$env:TEMP</c> is a different subfolder per call (a colleague's export: four
    /// screenshots "File does not exist"). Snippets, batch bodies and images go here instead.
    /// Folders older than <see cref="MaxAge"/> are removed at startup, off Revit's main thread.
    /// </summary>
    public static class ScratchFolder
    {
        public const string EnvVar = "VIBEMODEL_SCRATCH";
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private static int _pruned;

        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "scratch");

        /// <summary>A new session key: sortable, unique per Revit process and chat ("20261126-101500-4711-1").</summary>
        public static string NewKey(DateTime nowLocal, int processId, int sequence) =>
            nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
            processId.ToString(CultureInfo.InvariantCulture) + "-" + sequence.ToString(CultureInfo.InvariantCulture);

        /// <summary>The folder of a session, created; null when it cannot be (the CLI then runs without one).</summary>
        public static string Ensure(string root, string key)
        {
            try
            {
                var dir = Path.Combine(root, key);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                Logger.Warn("Scratch folder not created: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Removes session folders under <paramref name="root"/> last written before
        /// <paramref name="nowUtc"/> − <see cref="MaxAge"/>, except <paramref name="keep"/>. Returns how
        /// many went. A folder in use (a file held open) stays until next time.
        /// </summary>
        public static int Prune(string root, DateTime nowUtc, string keep = null)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(root)) return 0;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (keep != null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(keep).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        if (nowUtc - LastWriteUtc(dir) < MaxAge) continue;
                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("Scratch folder kept (" + ex.Message + "): " + dir);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Scratch prune failed: " + ex.Message);
            }
            return removed;
        }

        /// <summary>Once per Revit session, on a pool thread: never on Revit's main thread.</summary>
        public static void PruneInBackgroundOnce()
        {
            if (Interlocked.Exchange(ref _pruned, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var n = Prune(Root, DateTime.UtcNow);
                if (n > 0) Logger.Info("Scratch: removed " + n + " folder(s) older than " + MaxAge.TotalDays + " days");
            });
        }

        // The newest write in the folder (a session that is still used keeps its folder).
        private static DateTime LastWriteUtc(string dir)
        {
            var latest = Directory.GetLastWriteTimeUtc(dir);
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > latest) latest = t;
            }
            return latest;
        }
    }
}
