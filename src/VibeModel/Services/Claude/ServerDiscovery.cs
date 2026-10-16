using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Publishes where a live VibeModel server is listening so external clients
    /// (terminal Claude Code sessions, bots) can discover the right port instead of
    /// assuming 18884. One JSON file per port under %LOCALAPPDATA%\VibeModel\servers\
    /// — per-port filenames so multiple Revit instances never clobber each other.
    /// Readers detect stale files by checking whether the recorded PID is still alive.
    /// Best-effort on both write and delete: the server must never fail because of this.
    /// </summary>
    internal static class ServerDiscovery
    {
        internal static string GetServersDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel",
                "servers");
        }

        internal static string GetFilePath(int port)
        {
            return Path.Combine(GetServersDirectory(), port + ".json");
        }

        /// <summary>
        /// {port, pid, startedUtc[, token]}. The token is the one requests must carry (absent when
        /// the server is open); the folder is under the user's own profile, so only this Windows
        /// user (and administrators) can read it — the same people who could read Revit's memory.
        /// </summary>
        internal static string BuildJson(int port, int pid, DateTime startedUtc, string token = null)
        {
            var payload = new Dictionary<string, object>
            {
                { "port", port },
                { "pid", pid },
                { "startedUtc", startedUtc.ToString("o") }
            };
            if (!string.IsNullOrEmpty(token))
                payload["token"] = token;
            return new JavaScriptSerializer().Serialize(payload);
        }

        public static void Write(int port, string token = null)
        {
            try
            {
                Directory.CreateDirectory(GetServersDirectory());
                var json = BuildJson(
                    port,
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    DateTime.UtcNow,
                    token);
                File.WriteAllText(GetFilePath(port), json);
                Logger.Info("Server discovery file written: " + GetFilePath(port));
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to write server discovery file: " + ex.Message);
            }
        }

        public static void Delete(int port)
        {
            try
            {
                var path = GetFilePath(port);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort: a leftover file is detected as stale via its dead PID.
            }
        }
    }
}
