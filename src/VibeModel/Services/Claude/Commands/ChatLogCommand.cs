using System;
using System.Globalization;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;
using VibeModel.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ChatLogCommand : IClaudeCommand
    {
        public const int DefaultExportCount = 20;

        public string Name => "chatlog";
        public string Description =>
            "View recent chat messages from the VibeModel Chat panel; --export writes the saved chats " +
            "(questions, answers, the commands per turn, tokens) to a markdown file or zip under %LOCALAPPDATA%\\VibeModel\\exports";
        public string Usage => "chatlog [count] | chatlog --export [n|all]";

        public string Execute(string args, UIApplication uiApp)
        {
            var text = (args ?? "").Trim();
            if (text.StartsWith("--export", StringComparison.OrdinalIgnoreCase))
                return Export(text.Substring("--export".Length).Trim());

            int count = 20;
            if (!string.IsNullOrWhiteSpace(text))
            {
                int.TryParse(text, out count);
                if (count <= 0) count = 20;
            }

            return ChatHistory.Format(count);
        }

        /// <summary>"" → the last 20 chats, "5" → 5, "all" → every saved chat.</summary>
        public static bool TryParseExportCount(string arg, out int max)
        {
            max = DefaultExportCount;
            var a = (arg ?? "").Trim();
            if (a.Length == 0) return true;
            if (a.Equals("all", StringComparison.OrdinalIgnoreCase) || a.Equals("alles", StringComparison.OrdinalIgnoreCase))
            {
                max = 0;
                return true;
            }
            return int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out max) && max > 0;
        }

        private static string Export(string arg)
        {
            int max;
            if (!TryParseExportCount(arg, out max))
                return "ERROR: chatlog --export takes a number of chats or 'all' (got '" + arg + "').\n" +
                       "Hint: chatlog --export 5 | chatlog --export all";

            // The document in the Settings language (like the buttons); this answer stays English like every command.
            var result = ChatExportService.Export(max, Strings.Current);
            return Describe(result);
        }

        /// <summary>The command's answer for an export result (pure, English like every command).</summary>
        public static string Describe(ChatExportResult result)
        {
            if (result == null || result.Error != null)
                return "ERROR: chat export failed: " + (result?.Error ?? "unknown") + "\n" +
                       "Hint: the chats are in %LOCALAPPDATA%\\VibeModel\\chats\\ (one JSON file per chat).";
            var st = result.Stats;
            var sb = new StringBuilder();
            sb.Append("Exported ").Append(st.Chats).Append(st.Chats == 1 ? " chat" : " chats")
              .Append(" (").Append(st.Turns).Append(" turns, ").Append(st.Commands).Append(" commands, ")
              .Append(st.Failed).Append(" failed) to ").Append(result.IsZip ? "a zip" : "one markdown file")
              .Append(" in ").Append(((int)result.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).AppendLine(" ms:");
            sb.AppendLine(result.Path);
            if (st.ChatsFromLog > 0)
                sb.Append(st.ChatsFromLog).Append(st.ChatsFromLog == 1 ? " older chat takes its" : " older chats take their")
                  .AppendLine(" commands from the debug log (indicative).");
            if (st.ChatsWithoutRecord > 0)
                sb.Append(st.ChatsWithoutRecord).Append(st.ChatsWithoutRecord == 1 ? " chat has" : " chats have")
                  .AppendLine(" no commands recorded (older than the per-turn record, and debug logging was off).");
            sb.Append("No server token or API key is in the export; questions, answers and paths are.");
            return sb.ToString();
        }
    }
}
