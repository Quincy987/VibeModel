using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Read-only (plan 15): this Revit session's Keep / Discard decisions in the approval window,
    /// oldest first — how a client whose request timed out (APPROVAL_PENDING) learns the outcome.
    /// </summary>
    public class ApprovalsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "approvals";
        public string Description => "This session's Keep/Discard decisions on AI changes (change approval), oldest first";
        public string Usage => "approvals";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var configured = SettingsManager.GetChangeApproval(out var source);
            var policy = "Change approval: " + ChangeApprovalPolicy.Describe(configured, source, ChangeApprovalPolicy.DontAskThisSession);
            var entries = ApprovalLog.List();

            var sb = new StringBuilder();
            sb.AppendLine(policy);
            if (entries.Count == 0)
                sb.AppendLine("No decisions this session.");
            var list = new List<object>();
            foreach (var e in entries)
            {
                sb.AppendLine(ApprovalLog.FormatLine(e));
                list.Add(new Dictionary<string, object>
                {
                    { "id", e.Id },
                    { "time", e.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
                    { "command", e.Text },
                    { "decision", ApprovalLog.DecisionWord(e.Decision) },
                    { "counts", e.Counts }
                });
            }

            var data = new Dictionary<string, object>
            {
                { "policy", ChangeApprovalPolicy.Name(configured) },
                { "source", source.ToString().ToLowerInvariant() },
                { "dontAskThisSession", ChangeApprovalPolicy.DontAskThisSession },
                { "decisions", list }
            };
            return CommandResult.Ok(sb.ToString().TrimEnd(), data);
        }
    }
}
