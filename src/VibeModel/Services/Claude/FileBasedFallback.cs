using System;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// File-based polling fallback if HTTP server cannot start.
    /// Uses claim-by-rename pattern to avoid TOCTOU races.
    /// </summary>
    public class FileBasedFallback
    {
        private static readonly string BaseDir = @"C:\RevitClaudeLink";
        private static readonly string CommandFile = Path.Combine(BaseDir, "command.txt");
        private static readonly string OutputFile = Path.Combine(BaseDir, "output.txt");
        private DateTime _lastCommandTime = DateTime.MinValue;
        private bool _initialized;

        private readonly ClaudeCommandRegistry _registry;

        public FileBasedFallback(ClaudeCommandRegistry registry)
        {
            _registry = registry;
        }

        public void CheckAndExecute(UIApplication uiApp)
        {
            try
            {
                if (!_initialized)
                {
                    if (!Directory.Exists(BaseDir))
                        Directory.CreateDirectory(BaseDir);
                    _initialized = true;
                }

                if (!File.Exists(CommandFile))
                    return;

                var lastWriteTime = File.GetLastWriteTime(CommandFile);
                if (lastWriteTime <= _lastCommandTime)
                    return;

                _lastCommandTime = lastWriteTime;

                var commandText = File.ReadAllText(CommandFile).Trim();
                if (string.IsNullOrEmpty(commandText))
                    return;

                // Parse command
                var parts = commandText.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts.Length > 0 ? parts[0].ToLower() : "";
                var args = parts.Length > 1 ? parts[1] : "";

                Logger.Info("[Fallback] Executing: " + commandText);

                var output = _registry.Execute(cmd, args, uiApp);

                WriteOutput(commandText, output);
            }
            catch (Exception ex)
            {
                Logger.Error("File-based fallback error", ex);
                try
                {
                    WriteOutput("(error)", "ERROR: " + ex.Message);
                }
                catch { }
            }
        }

        private static void WriteOutput(string command, string content)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== REVIT OUTPUT ===");
            sb.AppendLine("Time: " + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine("Status: " + (content.StartsWith("ERROR:") ? "ERROR" : "OK"));
            sb.AppendLine("Command: " + command);
            sb.AppendLine();
            sb.AppendLine(content);
            sb.AppendLine();
            sb.AppendLine("=== END ===");

            File.WriteAllText(OutputFile, sb.ToString());
        }
    }
}
