using Autodesk.Revit.UI;
using VibeModel.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ChatLogCommand : IClaudeCommand
    {
        public string Name => "chatlog";
        public string Description => "View recent chat messages from the VibeModel Chat panel";
        public string Usage => "chatlog [count]";

        public string Execute(string args, UIApplication uiApp)
        {
            int count = 20;
            if (!string.IsNullOrWhiteSpace(args))
            {
                int.TryParse(args.Trim(), out count);
                if (count <= 0) count = 20;
            }

            return ChatHistory.Format(count);
        }
    }
}
