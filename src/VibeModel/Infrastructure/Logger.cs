using System;
using System.IO;

namespace VibeModel.Infrastructure
{
    public static class Logger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeModel",
            "logs"
        );

        private static readonly bool IsDebugMode =
            Environment.GetEnvironmentVariable("VIBEMODEL_DEBUG") == "1";

        public static void Info(string message) => Log("INFO", message);
        public static void Warn(string message) => Log("WARN", message);
        public static void Error(string message, Exception ex = null) =>
            Log("ERROR", ex != null ? message + "\n" + ex : message);

        private static void Log(string level, string message)
        {
            if (!IsDebugMode) return;

            try
            {
                Directory.CreateDirectory(LogDir);
                var logFile = Path.Combine(LogDir, "log_" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + message + "\n";
                File.AppendAllText(logFile, line);
            }
            catch
            {
                // Logger must never throw
            }
        }
    }
}
