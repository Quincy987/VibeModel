using System.Linq;
using System.Text;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class HelpCommand : IClaudeCommand
    {
        private ClaudeCommandRegistry _registry;

        public string Name => "help";
        public string Description => "Show available commands";
        public string Usage => "help";

        public void SetRegistry(ClaudeCommandRegistry registry)
        {
            _registry = registry;
        }

        public string Execute(string args, UIApplication uiApp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("VIBEMODEL - AI Integration for Revit");
            sb.AppendLine("====================================");
            sb.AppendLine();

            if (_registry == null)
            {
                sb.AppendLine("(registry not available)");
                return sb.ToString();
            }

            var commands = _registry.GetCommands()
                .Values
                .OrderBy(c => c.Name)
                .ToList();

            int maxName = commands.Max(c => c.Usage.Length);

            sb.AppendLine("QUERY COMMANDS:");
            foreach (var cmd in commands.Where(c => !IsModificationCommand(c.Name)))
            {
                sb.AppendLine("  " + cmd.Usage.PadRight(maxName + 2) + " - " + cmd.Description);
            }

            sb.AppendLine();
            sb.AppendLine("MODIFICATION COMMANDS:");
            foreach (var cmd in commands.Where(c => IsModificationCommand(c.Name)))
            {
                sb.AppendLine("  " + cmd.Usage.PadRight(maxName + 2) + " - " + cmd.Description);
            }

            sb.AppendLine();
            sb.AppendLine("USAGE:");
            sb.AppendLine("  curl -s http://localhost:18884/selected");
            sb.AppendLine("  curl -s \"http://localhost:18884/set?args=123+Comments+Hello\"");
            sb.AppendLine("  curl -s http://localhost:18884/list?args=walls");

            return sb.ToString();
        }

        private static bool IsModificationCommand(string name)
        {
            switch (name)
            {
                case "wall":
                case "floor":
                case "delete":
                case "set":
                case "color":
                case "place":
                case "placeid":
                case "exec":
                    return true;
                default:
                    return false;
            }
        }
    }
}
