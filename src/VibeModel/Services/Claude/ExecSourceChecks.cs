using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Phase 2 (pure): what an exec snippet's own source says before it runs, decided on the code
    /// with its comments and string literals stripped (<see cref="ForbiddenActions.StripCommentsAndStrings"/>).
    /// <list type="bullet">
    /// <item>A snippet that posts Revit's Undo is refused: the posted command runs after the request, on
    /// whatever is on top of Revit's undo list (also the user's own work). The <c>undo</c> command does it safely.</item>
    /// <item>A snippet with its own dry-run switch gets a hint: measured in a colleague's export, a
    /// "<c>bool CREATE = false</c>" trial skipped the delete, so it never showed the 256 dimensions the real
    /// run took with it. A trial is <c>?preview=1</c> on the real script.</item>
    /// </list>
    /// </summary>
    public static class ExecSourceChecks
    {
        public const string PostsUndoCode = "EXEC_POSTS_UNDO";

        public const string PostsUndoMessage =
            "Refused: the snippet posts Revit's Undo command. It runs after this request, on whatever is on top of Revit's undo list (also the user's own work).";

        public const string PostsUndoHint =
            "use the undo command: it takes back VibeModel's last change only, and refuses when anything else happened since.";

        private static readonly Regex PostableUndo = new Regex(@"PostableCommand\s*\.\s*(Undo|Redo)\b", RegexOptions.Compiled);

        /// <summary>True when the snippet's code (not a comment or a string) names <c>PostableCommand.Undo</c> or <c>.Redo</c>.</summary>
        public static bool PostsUndo(string code)
            => !string.IsNullOrEmpty(code) && PostableUndo.IsMatch(ForbiddenActions.StripCommentsAndStrings(code));

        // bool CREATE = false; const bool DryRun = true; var apply = false; bool dry_run = true …
        private static readonly Regex Switch = new Regex(
            @"\b(?:bool|var|const\s+bool|static\s+bool)\s+(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>true|false)\b",
            RegexOptions.Compiled);

        // Names that mean "only pretend" (value true) and names that mean "really do it" (value false).
        private static readonly Regex PretendName = new Regex(@"^(dry_?run|dryrun|test_?only|simulate|simulation|preview|trial|what_?if|fake)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DoName = new Regex(@"^(create|apply|commit|execute|do_?it|really|write|live|for_?real|delete|modify)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public const string DryRunHint =
            "a switch inside the script ({0}) hides what the real run does: a trial that skips a step never shows what that step deletes. Run the real script with ?preview=1 instead (it reports what it would add, modify and delete, also annotations, and keeps nothing).";

        /// <summary>
        /// "CREATE = false" when the snippet declares a dry-run switch set to its pretend value; null
        /// otherwise. A switch set to its real value (CREATE = true) is just a constant.
        /// </summary>
        public static string DryRunSwitch(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            foreach (Match m in Switch.Matches(ForbiddenActions.StripCommentsAndStrings(code)))
            {
                var name = m.Groups["name"].Value;
                bool value = m.Groups["value"].Value == "true";
                if (PretendName.IsMatch(name) && value) return name + " = true";
                if (DoName.IsMatch(name) && !value) return name + " = false";
            }
            return null;
        }
    }
}
