using System;
using System.Collections.Generic;
using System.IO;

namespace VibeModel.Infrastructure
{
    public static class Logger
    {
        private static string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeModel",
            "logs"
        );

        private static readonly bool IsDebugMode =
            Environment.GetEnvironmentVariable("VIBEMODEL_DEBUG") == "1";

        // The last warnings and errors of this session, for the Diagnose report.
        private const int RecentCapacity = 40;
        private static readonly LinkedList<string> RecentProblems = new LinkedList<string>();
        private static readonly object RecentLock = new object();

        /// <summary>%LOCALAPPDATA%\VibeModel\logs — one log_yyyy-MM-dd.txt per day.</summary>
        public static string LogDirectory => LogDir;

        /// <summary>Debug logging (VIBEMODEL_DEBUG=1): INFO lines are written too.</summary>
        public static bool DebugEnabled => IsDebugMode;

        public static void Info(string message) => Log("INFO", message);
        public static void Warn(string message) => Log("WARN", message);
        public static void Error(string message, Exception ex = null) =>
            Log("ERROR", ex != null ? message + "\n" + ex : message);

        /// <summary>Tests: write to this folder instead of the user's log folder.</summary>
        internal static void RedirectForTests(string directory) => LogDir = directory;

        /// <summary>This session's warnings and errors, oldest first (at most 40).</summary>
        public static IReadOnlyList<string> Recent()
        {
            lock (RecentLock) return new List<string>(RecentProblems);
        }

        private static void Log(string level, string message)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + message + "\n";
            var problem = level != "INFO";
            if (problem)
            {
                lock (RecentLock)
                {
                    RecentProblems.AddLast(line.TrimEnd());
                    while (RecentProblems.Count > RecentCapacity) RecentProblems.RemoveFirst();
                }
            }

            // Warnings and errors are always written (a user's Diagnose report and the support
            // log need them); INFO only with VIBEMODEL_DEBUG=1.
            if (!IsDebugMode && !problem) return;

            try
            {
                Directory.CreateDirectory(LogDir);
                var logFile = Path.Combine(LogDir, "log_" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                File.AppendAllText(logFile, line);
            }
            catch
            {
                // Logger must never throw
            }
        }
    }
}
