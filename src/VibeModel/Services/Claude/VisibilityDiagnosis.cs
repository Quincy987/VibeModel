using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Outcome of one visibility check. <see cref="CheckStatus.Blocked"/> means this check
    /// alone is enough to make the element invisible; <see cref="CheckStatus.Info"/> is a
    /// notable-but-not-fatal observation; <see cref="CheckStatus.Skipped"/> means the check
    /// does not apply (or the Revit API refused to answer) so it proves nothing.
    /// </summary>
    internal enum CheckStatus
    {
        Ok,
        Blocked,
        Info,
        Skipped
    }

    /// <summary>One rendered line of the whyhidden report.</summary>
    internal class VisibilityCheck
    {
        /// <summary>Ordinal shown to the user, e.g. "1a", "7c". Empty for editability notes.</summary>
        public string Label;

        /// <summary>Short name of what was checked, e.g. "Category visibility".</summary>
        public string Title;

        public CheckStatus Status;

        /// <summary>One-line verdict for this single check.</summary>
        public string Detail;

        /// <summary>Optional follow-up naming the exact Revit setting to change.</summary>
        public string Hint;
    }

    /// <summary>
    /// Revit-free container + renderer for the <c>whyhidden</c> report. The command fills this
    /// in from live Revit state; keeping the formatting here means the output contract is
    /// covered by the headless test suite.
    /// </summary>
    internal class VisibilityDiagnosis
    {
        /// <summary>Column width of the status prefix, sized to the longest ("BLOCKED:").</summary>
        private const int PrefixWidth = 9;

        public string ElementLine;
        public string ViewLine;

        /// <summary>
        /// What Revit's own view-scoped collector says about this element - the single most
        /// reliable "is it actually in this view" signal. Null when it could not be evaluated.
        /// </summary>
        public bool? InViewElementSet;

        public readonly List<VisibilityCheck> Checks = new List<VisibilityCheck>();
        public readonly List<VisibilityCheck> Editability = new List<VisibilityCheck>();

        public VisibilityCheck Add(string label, string title, CheckStatus status, string detail, string hint = null)
        {
            var check = new VisibilityCheck
            {
                Label = label,
                Title = title,
                Status = status,
                Detail = detail,
                Hint = hint
            };
            Checks.Add(check);
            return check;
        }

        public VisibilityCheck AddNote(string title, CheckStatus status, string detail, string hint = null)
        {
            var check = new VisibilityCheck
            {
                Label = "",
                Title = title,
                Status = status,
                Detail = detail,
                Hint = hint
            };
            Editability.Add(check);
            return check;
        }

        /// <summary>The first blocking check - the probable cause reported in the verdict.</summary>
        public VisibilityCheck FirstBlocked
        {
            get { return Checks.FirstOrDefault(c => c.Status == CheckStatus.Blocked); }
        }

        public int BlockedCount
        {
            get { return Checks.Count(c => c.Status == CheckStatus.Blocked); }
        }

        public static string Prefix(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Blocked: return "BLOCKED:".PadRight(PrefixWidth);
                case CheckStatus.Info: return "INFO".PadRight(PrefixWidth);
                case CheckStatus.Skipped: return "SKIPPED:".PadRight(PrefixWidth);
                default: return "OK".PadRight(PrefixWidth);
            }
        }

        public string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("WHY HIDDEN");
            sb.AppendLine("==========");
            if (!string.IsNullOrEmpty(ElementLine))
                sb.AppendLine("Element: " + ElementLine);
            if (!string.IsNullOrEmpty(ViewLine))
                sb.AppendLine("View:    " + ViewLine);
            sb.AppendLine();

            foreach (var check in Checks)
                RenderCheck(sb, check);

            if (InViewElementSet.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine(InViewElementSet.Value
                    ? "Ground truth: Revit's own view element set DOES contain this element."
                    : "Ground truth: Revit's own view element set does NOT contain this element.");
            }

            if (Editability.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("EDITABILITY");
                sb.AppendLine("-----------");
                foreach (var note in Editability)
                    RenderCheck(sb, note);
            }

            sb.AppendLine();
            sb.AppendLine(BuildVerdict());
            return sb.ToString();
        }

        private static void RenderCheck(StringBuilder sb, VisibilityCheck check)
        {
            var label = string.IsNullOrEmpty(check.Label) ? "" : check.Label + ". ";
            sb.AppendLine(Prefix(check.Status) + label + check.Title + " - " + check.Detail);
            if (!string.IsNullOrEmpty(check.Hint))
                sb.AppendLine(new string(' ', PrefixWidth) + "Fix: " + check.Hint);
        }

        /// <summary>
        /// One line naming the probable cause. The first BLOCKED check wins; when nothing blocks
        /// we still flag the case where Revit disagrees with every individual check, so the user
        /// is never told "all clear" about an element they genuinely cannot see.
        /// </summary>
        public string BuildVerdict()
        {
            var blocked = FirstBlocked;
            if (blocked != null)
            {
                var more = BlockedCount > 1 ? " (+" + (BlockedCount - 1) + " more blocker(s) above)" : "";
                return "Verdict: " + blocked.Title + " - " + blocked.Detail + more;
            }

            if (InViewElementSet.HasValue && !InViewElementSet.Value)
                return "Verdict: No single setting explains it, but Revit still excludes this element " +
                       "from the view - check linked-model visibility, a view template locking the settings, " +
                       "or geometry that is genuinely empty in this view.";

            return "Verdict: No visibility blocker found - this element should be drawn in this view.";
        }
    }

    /// <summary>
    /// Pure argument parsing for <c>whyhidden &lt;elementId&gt; [viewId]</c>.
    /// Revit-free so the headless test suite can cover it.
    /// </summary>
    internal class WhyHiddenArgs
    {
        public int ElementId;
        public int? ViewId;
        public string Error;

        private static readonly char[] Separators = { ' ', ',', '\t' };

        public static WhyHiddenArgs Parse(string args)
        {
            var r = new WhyHiddenArgs();
            var tokens = (args ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                r.Error = "No element ID given.";
                return r;
            }

            if (tokens.Length > 2)
            {
                r.Error = "Too many arguments - whyhidden takes one element ID and an optional view ID.";
                return r;
            }

            if (!int.TryParse(tokens[0], out int elemId) || elemId <= 0)
            {
                r.Error = "'" + tokens[0] + "' is not a valid element ID.";
                return r;
            }
            r.ElementId = elemId;

            if (tokens.Length == 2)
            {
                if (!int.TryParse(tokens[1], out int viewId) || viewId <= 0)
                {
                    r.Error = "'" + tokens[1] + "' is not a valid view ID.";
                    return r;
                }
                r.ViewId = viewId;
            }

            return r;
        }
    }
}
