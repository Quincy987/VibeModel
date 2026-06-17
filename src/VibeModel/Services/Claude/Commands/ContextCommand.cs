using System;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Combined turn-context query. Composes /info + /activeview + /selected into a
    /// single response so chat backends gather model context in ONE round-trip across
    /// the ExternalEvent boundary instead of three serial calls. Lowers time-to-first-token.
    /// The output format is byte-equivalent to what the backends used to assemble client-side.
    /// </summary>
    public class ContextCommand : IClaudeCommand
    {
        public string Name => "context";
        public string Description => "Combined model context (info + active view + selection) in one call";
        public string Usage => "context";

        // Reuse the existing query commands so there is one source of truth per section.
        private readonly InfoCommand _info = new InfoCommand();
        private readonly ActiveViewCommand _activeView = new ActiveViewCommand();
        private readonly SelectedCommand _selected = new SelectedCommand();

        public string Execute(string args, UIApplication uiApp)
        {
            var sb = new StringBuilder();

            var info = SafeRun(_info, args, uiApp, "/info");
            if (!string.IsNullOrWhiteSpace(info))
            {
                sb.AppendLine("CURRENT REVIT CONTEXT:");
                sb.AppendLine(info.Trim());
            }

            var view = SafeRun(_activeView, args, uiApp, "/activeview");
            if (!string.IsNullOrWhiteSpace(view))
                sb.AppendLine("Active view: " + view.Trim());

            var selected = SafeRun(_selected, args, uiApp, "/selected");
            if (!string.IsNullOrWhiteSpace(selected))
                sb.AppendLine("Selection: " + selected.Trim());

            return sb.ToString().Trim();
        }

        // Per-section isolation: one failing section must not blank the others.
        private static string SafeRun(IClaudeCommand cmd, string args, UIApplication uiApp, string label)
        {
            try
            {
                return cmd.Execute(args, uiApp);
            }
            catch (Exception ex)
            {
                Logger.Info("context: " + label + " failed — " + ex.Message);
                return null;
            }
        }
    }
}
