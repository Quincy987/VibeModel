using Autodesk.Revit.UI;
using VibeModel.Services.Diagnostics;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// The support report (same as the chat pane's Diagnose button): versions, signature, backend
    /// and key source (masked), where Claude Code was looked for, proxy, API reachability, recent
    /// warnings/errors and the settings. Read-only; its network checks share a 6 s deadline.
    /// </summary>
    public class DiagnoseCommand : IClaudeCommand
    {
        public string Name => "diagnose";
        public string Description => "Support report: versions, connection to Claude, proxy, recent errors (secrets masked)";
        public string Usage => "diagnose";

        public string Execute(string args, UIApplication uiApp)
        {
            if (DiagnoseReport.RevitVersion == null && uiApp != null)
                DiagnoseReport.RevitVersion = uiApp.Application.VersionNumber;
            return DiagnoseReport.Build();
        }
    }
}
