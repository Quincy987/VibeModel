using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// File-system access with a deadline. Office standards point at network shares that can
    /// hang a plain File.Exists for minutes when the VPN is down — fatal on Revit's main thread,
    /// and a thread-pool leak in the background. One helper for both.
    /// </summary>
    public static class PathProbe
    {
        public static readonly TimeSpan Default = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Run <paramref name="probe"/> with a deadline. Returns its result, or
        /// <paramref name="onTimeout"/> when it did not finish in time or threw.
        /// </summary>
        public static T Run<T>(Func<T> probe, TimeSpan timeout, T onTimeout)
        {
            try
            {
                // A dedicated thread, not the pool: when the pool is saturated (the background indexers,
                // a test run) a queued probe can wait longer than its deadline before it even starts, and a
                // local folder is then reported as "did not answer within 3 s" (seen on the CI runner).
                var t = Task.Factory.StartNew(probe, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return t.Wait(timeout) ? t.Result : onTimeout;
            }
            catch { return onTimeout; }
        }

        /// <summary>True/false when the check finished in time; null when it timed out.</summary>
        public static bool? FileExists(string path, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsLocalDrive(path)) return File.Exists(path);
            return Run<bool?>(() => File.Exists(path), timeout ?? Default, null);
        }

        /// <summary>True/false when the check finished in time; null when it timed out.</summary>
        public static bool? DirectoryExists(string path, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsLocalDrive(path)) return Directory.Exists(path);
            return Run<bool?>(() => Directory.Exists(path), timeout ?? Default, null);
        }

        /// <summary>
        /// UNC paths and mapped network drives. Determined without touching the network
        /// (GetDriveType is local), so it is safe on the main thread.
        /// </summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var p = path.Trim();
            if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return true;
            try
            {
                var root = Path.GetPathRoot(p);
                if (root != null && root.Length >= 2 && root[1] == ':')
                    return new DriveInfo(root.Substring(0, 1)).DriveType == DriveType.Network;
            }
            catch { /* malformed path: treat as local, the read will report it */ }
            return false;
        }

        /// <summary>
        /// A fixed or removable drive of this machine: it cannot hang like a network share, and a
        /// task on a busy thread pool could lose the deadline to a file that is simply there.
        /// </summary>
        public static bool IsLocalDrive(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsNetworkPath(path)) return false;
            try
            {
                var root = Path.GetPathRoot(path.Trim());
                if (root == null || root.Length < 2 || root[1] != ':') return false;
                var type = new DriveInfo(root.Substring(0, 1)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable;
            }
            catch { return false; }
        }

        /// <summary>"K:\", "\\server\share\" — the unit that is reachable or not as a whole.</summary>
        public static string ShareRoot(string path)
        {
            try { return Path.GetPathRoot(path?.Trim() ?? ""); }
            catch { return null; }
        }
    }
}
