using System;
using System.IO;
using System.Threading.Tasks;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// File checks with a deadline. Office standards point at network shares that can hang a
    /// plain File.Exists for minutes when the VPN is down — fatal on Revit's main thread.
    /// </summary>
    public static class PathProbe
    {
        public static readonly TimeSpan Default = TimeSpan.FromSeconds(3);

        /// <summary>True/false when the check finished in time; null when it timed out.</summary>
        public static bool? FileExists(string path, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var t = Task.Run(() => File.Exists(path));
                return t.Wait(timeout ?? Default) ? t.Result : (bool?)null;
            }
            catch { return false; }
        }
    }
}
