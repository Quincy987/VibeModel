using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Phase 2: takes back VibeModel's LAST kept change in the active document by posting Revit's own
    /// Undo — the only public way to undo. The posted command runs after this request and undoes
    /// whatever is on top of Revit's undo list, so the ledger (<see cref="UndoLedger"/>) only allows it
    /// while nothing else happened since that change; it never undoes the user's work. The next
    /// response's stamp confirms it (<c>undone: …</c>). Not a transaction, never inside a group.
    /// </summary>
    public class UndoCommand : IClaudeCommand, IStructuredCommand, IJournaledOutcome
    {
        public string Name => "undo";
        public string Description => "Take back VibeModel's last change in this document (one Revit undo step; refused when anything else changed since). 'undo --status' lists what it could take back";
        public string Usage => "undo [--status]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Error("NO_DOCUMENT", "No document open", "Open a Revit project before running commands.");
            var key = ModelChangeTracker.DocKey(doc.PathName, doc.Title);
            long edits = ModelChangeTracker.GetTotalEdits(key);
            var check = UndoLedger.Decide(key, doc.Title, edits, DateTime.UtcNow);

            var a = (args ?? "").Trim();
            if (a.Equals("--status", StringComparison.OrdinalIgnoreCase) || a.Equals("status", StringComparison.OrdinalIgnoreCase))
                return Status(key, check);
            if (a.Length > 0)
                return CommandResult.Error("INVALID_ARGS", "Unknown argument '" + a + "'.", "Usage: " + Usage);

            if (RunContext.InBatch)
                return CommandResult.Error(UndoLedger.RefusedCode, "undo cannot run inside a batch: the batch is one undo step itself.",
                    "call undo on its own, after the batch.");
            if (!check.Ok)
                return CommandResult.Error(UndoLedger.RefusedCode, "Nothing was undone: " + check.Reason, check.Hint);

            var run = check.Run;
            if (RunContext.Current != null && RunContext.Current.Preview)
                return CommandResult.Ok("Preview: undo would take back '" + run.UndoName + "' (" + run.Summary + "). Nothing was undone.",
                    Data(run, posted: false));

            RevitCommandId id;
            try { id = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo); }
            catch (Exception ex) { return CommandResult.Error(UndoLedger.RefusedCode, "Nothing was undone: Revit's Undo command was not found (" + ex.Message + ").", null); }
            bool canPost;
            try { canPost = id != null && uiApp.CanPostCommand(id); }
            catch { canPost = false; }
            if (!canPost)
                return CommandResult.Error(UndoLedger.RefusedCode,
                    "Nothing was undone: Revit cannot run its Undo now (an edit mode such as a sketch, or a dialog, is open).",
                    "the user finishes or cancels what is open in Revit; then call undo again.");
            try { uiApp.PostCommand(id); }
            catch (Exception ex)
            {
                return CommandResult.Error(UndoLedger.RefusedCode, "Nothing was undone: Revit refused to post its Undo (" + ex.Message + ").",
                    "the user presses Ctrl+Z in Revit.");
            }
            UndoLedger.MarkPosted(run, edits, DateTime.UtcNow);

            var text = "Undo posted: Revit takes back '" + run.UndoName + "' (" + run.Summary + ") right after this answer." +
                       "\nThe next response's stamp confirms it (`undone: " + run.UndoName + "`); the user can bring it back with Redo (Ctrl+Y).";
            return CommandResult.Ok(text, Data(run, posted: true));
        }

        private static Dictionary<string, object> Data(UndoLedger.Run run, bool posted) => new Dictionary<string, object>
        {
            { "posted", posted },
            { "undoName", run.UndoName },
            { "change", run.Summary },
            { "time", run.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) }
        };

        private static CommandResult Status(string key, UndoLedger.Check check)
        {
            var runs = UndoLedger.List(key);
            var sb = new StringBuilder();
            sb.AppendLine(check.Ok
                ? "undo would take back: '" + check.Run.UndoName + "' (" + check.Run.Summary + ")"
                : "undo now: refused - " + check.Reason);
            if (runs.Count == 0) sb.AppendLine("No VibeModel change kept in this document this session.");
            else sb.AppendLine("VibeModel's kept changes in this document, newest first" + (runs.Count > 10 ? " (10 of " + runs.Count + ")" : "") + ":");
            var list = new List<object>();
            int n = 0;
            foreach (var r in runs)
            {
                if (n++ >= 10) break;
                sb.AppendLine("  " + r.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + r.UndoName +
                              (r.Undoable ? "" : "  (several undo steps: Ctrl+Z only)") + "  - " + r.Summary);
                list.Add(new Dictionary<string, object>
                {
                    { "time", r.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
                    { "undoName", r.UndoName }, { "change", r.Summary }, { "undoable", r.Undoable }
                });
            }
            sb.AppendLine("Only the newest can be taken back, and only while nothing else changed the model since.");
            var data = new Dictionary<string, object>
            {
                { "canUndo", check.Ok },
                { "reason", check.Ok ? null : check.Reason },
                { "runs", list }
            };
            return CommandResult.Ok(sb.ToString().TrimEnd(), data);
        }

        /// <summary>The journal line's outcome: only a posted undo is journaled.</summary>
        public string JournalOutcome(CommandResult result)
        {
            if (result?.Data is Dictionary<string, object> d && d.TryGetValue("posted", out var p) && p is bool posted && posted &&
                d.TryGetValue("undoName", out var name))
                return "took back '" + name + "'";
            return null;
        }
    }
}
