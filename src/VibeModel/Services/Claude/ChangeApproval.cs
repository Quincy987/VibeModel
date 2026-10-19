using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Settings → "Wijzigingen door de AI" (plan 15). <see cref="Auto"/> is today's behaviour and
    /// the default: every change is kept and is one Ctrl+Z.
    /// </summary>
    public enum ChangeApprovalMode
    {
        /// <summary>Never ask (default) — responses byte-identical to before plan 15.</summary>
        Auto,
        /// <summary>Delete/edit commands that changed something wait for Keep / Discard in Revit.</summary>
        Ask,
        /// <summary>No change is kept: a modifying command that changed something is rolled back and refused.</summary>
        ReadOnly
    }

    /// <summary>Where the policy in force came from (Diagnose names it).</summary>
    public enum ChangeApprovalSource { Default, Settings, Environment, Managed }

    /// <summary>What happens to a run's TransactionGroup once the command has run.</summary>
    public enum RunDecision
    {
        /// <summary>Changes kept: Assimilate (one undo entry), as before plan 15.</summary>
        Keep,
        /// <summary>Isolate the changes, show the approval window; the user keeps or discards.</summary>
        Prompt,
        /// <summary>?preview=1: report what would change, roll everything back.</summary>
        Preview,
        /// <summary>Policy readonly and the command changed something: roll back, READ_ONLY.</summary>
        ReadOnly,
        /// <summary>?readonly=1 and the snippet/batch changed something: roll back, QUERY_MODIFIED_MODEL.</summary>
        QueryModified,
        /// <summary>No change recorded: roll the empty group back (no empty undo entry).</summary>
        Nothing
    }

    /// <summary>The user's answer in the approval window.</summary>
    public enum ApprovalDecision { Kept, Discarded }

    /// <summary>
    /// The pure part of plan 15: which policy is in force, which commands it guards, and what a
    /// finished run does with its TransactionGroup. The Revit side (the group, isolate, the
    /// window) is in <see cref="RevitCommandHandler"/>; nothing here touches the Revit API.
    /// </summary>
    public static class ChangeApprovalPolicy
    {
        /// <summary>managed.json / settings.json key (same name in both).</summary>
        public const string SettingKey = "ChangeApproval";

        /// <summary>Environment variable between managed.json and settings.json (build.ps1 -Approval sets it).</summary>
        public const string EnvVar = "VIBEMODEL_APPROVAL";

        /// <summary>How long a client waits for the user's Keep / Discard before APPROVAL_PENDING.</summary>
        public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(10);

        private static volatile bool _dontAskThisSession;

        /// <summary>
        /// "Deze Revit-sessie niet meer vragen": the window's checkbox. A plain static, so it ends
        /// with the Revit process; Settings shows it and can clear it.
        /// </summary>
        public static bool DontAskThisSession
        {
            get => _dontAskThisSession;
            set => _dontAskThisSession = value;
        }

        public static string Name(ChangeApprovalMode mode)
        {
            switch (mode)
            {
                case ChangeApprovalMode.Ask: return "ask";
                case ChangeApprovalMode.ReadOnly: return "readonly";
                default: return "auto";
            }
        }

        /// <summary>auto | ask | readonly (case-insensitive; "read-only" and "never"/"always" tolerated).</summary>
        public static bool TryParse(string value, out ChangeApprovalMode mode)
        {
            mode = ChangeApprovalMode.Auto;
            var v = (value ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            switch (v)
            {
                case "auto":
                case "never":
                case "off":
                    mode = ChangeApprovalMode.Auto; return true;
                case "ask":
                case "always":
                case "on":
                    mode = ChangeApprovalMode.Ask; return true;
                case "readonly":
                    mode = ChangeApprovalMode.ReadOnly; return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// First valid value wins: managed.json → environment → settings.json → auto. An invalid
        /// value is skipped (falls through), never an error.
        /// </summary>
        public static ChangeApprovalMode Resolve(string managed, string environment, string settings, out ChangeApprovalSource source)
        {
            if (TryParse(managed, out var m)) { source = ChangeApprovalSource.Managed; return m; }
            if (TryParse(environment, out var e)) { source = ChangeApprovalSource.Environment; return e; }
            if (TryParse(settings, out var s)) { source = ChangeApprovalSource.Settings; return s; }
            source = ChangeApprovalSource.Default;
            return ChangeApprovalMode.Auto;
        }

        /// <summary>The configured policy with "don't ask again this session" applied (ask → auto).</summary>
        public static ChangeApprovalMode Effective(ChangeApprovalMode configured, bool dontAskThisSession)
            => configured == ChangeApprovalMode.Ask && dontAskThisSession ? ChangeApprovalMode.Auto : configured;

        /// <summary>The policy in force right now (reads managed.json, the environment and settings.json).</summary>
        public static ChangeApprovalMode EffectiveMode
            => Effective(Infrastructure.SettingsManager.GetChangeApproval(out _), DontAskThisSession);

        /// <summary>"ask (settings)", "auto (default)", "readonly (VIBEMODEL_APPROVAL)", "ask (managed.json)".</summary>
        public static string Describe(ChangeApprovalMode configured, ChangeApprovalSource source, bool dontAskThisSession)
        {
            string from;
            switch (source)
            {
                case ChangeApprovalSource.Managed: from = "managed.json"; break;
                case ChangeApprovalSource.Environment: from = EnvVar; break;
                case ChangeApprovalSource.Settings: from = "settings"; break;
                default: from = "default"; break;
            }
            var text = Name(configured) + " (" + from + ")";
            if (configured == ChangeApprovalMode.Ask && dontAskThisSession)
                text += " - not asking for the rest of this Revit session";
            return text;
        }

        /// <summary>The commands that ask under <see cref="ChangeApprovalMode.Ask"/>: they delete or edit existing elements.</summary>
        public static bool IsGuarded(string command) => AnnotationScope.Applies(command);

        public static bool IsGuardedBatch(IEnumerable<string> commands) => AnnotationScope.AppliesToAny(commands);

        /// <summary>
        /// True when the run needs a TransactionGroup it can roll back or show first. False under
        /// <see cref="ChangeApprovalMode.Auto"/> without flags: the run is exactly as before plan 15.
        /// A non-modifying command never needs one (it changes nothing).
        /// </summary>
        public static bool NeedsRunGroup(ChangeApprovalMode mode, bool guarded, bool modifying, bool preview, bool readOnlyFlag)
        {
            if (!modifying) return false;
            if (preview || readOnlyFlag) return true;
            if (mode == ChangeApprovalMode.ReadOnly) return true;
            return mode == ChangeApprovalMode.Ask && guarded;
        }

        /// <summary>
        /// What a finished run does with its group. Called only for a run that has one
        /// (<see cref="NeedsRunGroup"/>). Success does not matter: a failed exec may still have
        /// committed transactions, and the user (or the preview) should see those too.
        /// </summary>
        public static RunDecision Decide(ChangeApprovalMode mode, bool guarded, bool preview, bool readOnlyFlag, bool changesEmpty)
        {
            if (preview) return RunDecision.Preview;
            if (changesEmpty) return RunDecision.Nothing;
            if (readOnlyFlag) return RunDecision.QueryModified;
            if (mode == ChangeApprovalMode.ReadOnly) return RunDecision.ReadOnly;
            if (mode == ChangeApprovalMode.Ask && guarded) return RunDecision.Prompt;
            return RunDecision.Keep;
        }

        /// <summary>True when the client may have to wait for the user's decision.</summary>
        public static bool MayWaitForUser(ChangeApprovalMode mode, bool guarded, bool modifying, bool preview)
            => mode == ChangeApprovalMode.Ask && guarded && modifying && !preview;

        /// <summary>The wait for a request: the usual one, or <see cref="ApprovalTimeout"/> when the user may be asked.</summary>
        public static TimeSpan TimeoutFor(TimeSpan usual, bool mayWaitForUser)
            => mayWaitForUser && ApprovalTimeout > usual ? ApprovalTimeout : usual;

        private const int MaxUndoNameChars = 60;

        /// <summary>"VibeModel: delete 1730959 1730980" — the undo entry of a kept single command, ≤ 60 characters.</summary>
        public static string UndoName(string command, string args)
        {
            var text = ModelChangeTracker.TransactionPrefix + (command ?? "").Trim();
            var a = string.Join(" ", (args ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            if (a.Length > 0) text += " " + a;
            if (text.Length > MaxUndoNameChars) text = text.Substring(0, MaxUndoNameChars - 1).TrimEnd() + "…";
            return text;
        }

        // --- Response texts (model-facing, English like every other response) -------------

        public const string DiscardedCode = "DISCARDED";
        public const string DiscardedMessage = "Discarded by the user.";
        public const string DiscardedHint = "do not retry this change; ask the user what they want instead.";

        public const string ReadOnlyCode = "READ_ONLY";
        public const string ReadOnlyMessage = "VibeModel is set to read-only; nothing was changed.";
        public const string ReadOnlyHint = "Settings → Wijzigingen door de AI.";

        public const string QueryModifiedCode = "QUERY_MODIFIED_MODEL";
        public const string QueryModifiedHint = "a read-only exec must not modify; drop ?readonly=1 to apply.";

        public const string PendingCode = "APPROVAL_PENDING";
        public const string PendingMessage = "still waiting for the user's decision in Revit.";
        public const string PendingHint = "call 'approvals' to learn the outcome; never re-run the change.";

        public const string RunGroupCode = "RUN_GROUP_FAILED";

        /// <summary>The message of <see cref="RunDecision.QueryModified"/>.</summary>
        public static string QueryModifiedMessage(ChangeSetSummary changes, bool batch)
            => (batch ? "the batch" : "the snippet") + " changed the model (" + changes.PastCounts() + ") and was rolled back.";

        /// <summary>"-- approval: kept by the user (#7)".</summary>
        public static string KeptLine(int id) => "-- approval: kept by the user (#" + id.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>The error text a discarded run returns (text mode).</summary>
        public static string DiscardedText() => "ERROR: " + DiscardedMessage + "\nHint: " + DiscardedHint;

        /// <summary>
        /// The JSON "approval" object: <c>{id, decision:"kept"|"discarded", by:"user"}</c>.
        /// </summary>
        public static Dictionary<string, object> ApprovalJson(int id, ApprovalDecision decision)
            => new Dictionary<string, object>
            {
                { "id", id },
                { "decision", decision == ApprovalDecision.Kept ? "kept" : "discarded" },
                { "by", "user" }
            };
    }

    /// <summary>One element in a run's change set; the category is null when it can no longer be read (deleted).</summary>
    public sealed class ChangedElement
    {
        public long Id { get; }
        public string Category { get; }

        public ChangedElement(long id, string category)
        {
            Id = id;
            Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        }
    }

    /// <summary>
    /// What a run added, modified and deleted, with categories — the preview line, the approval
    /// window's counts and the JSON <c>preview</c> object. Revit's regeneration inflates
    /// "modified" (a new wall touches its joined neighbours and their dimensions), which is why
    /// it is shown by category.
    /// </summary>
    public sealed class ChangeSetSummary
    {
        public IReadOnlyList<ChangedElement> Added { get; }
        public IReadOnlyList<ChangedElement> Modified { get; }
        public IReadOnlyList<ChangedElement> Deleted { get; }

        public ChangeSetSummary(IEnumerable<ChangedElement> added, IEnumerable<ChangedElement> modified, IEnumerable<ChangedElement> deleted)
        {
            Added = (added ?? Enumerable.Empty<ChangedElement>()).ToList();
            Modified = (modified ?? Enumerable.Empty<ChangedElement>()).ToList();
            Deleted = (deleted ?? Enumerable.Empty<ChangedElement>()).ToList();
        }

        public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0;

        /// <summary>"Walls 2, Floors 1" — most first, then by name; elements without a category as "other".</summary>
        public static string ByCategory(IReadOnlyList<ChangedElement> elements, string unknown = "other")
        {
            if (elements == null || elements.Count == 0) return "";
            return string.Join(", ", elements
                .GroupBy(e => e.Category ?? unknown, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture)));
        }

        private static string Part(string verb, IReadOnlyList<ChangedElement> elements)
        {
            var text = verb + " " + elements.Count.ToString(CultureInfo.InvariantCulture);
            return elements.Count == 0 ? text : text + " (" + ByCategory(elements) + ")";
        }

        /// <summary>"add 2 (Walls 2), modify 1 (Floors 1), delete 0".</summary>
        public string Counts() => Part("add", Added) + ", " + Part("modify", Modified) + ", " + Part("delete", Deleted);

        /// <summary>"added 1, modified 3, deleted 0".</summary>
        public string PastCounts()
            => "added " + Added.Count.ToString(CultureInfo.InvariantCulture) +
               ", modified " + Modified.Count.ToString(CultureInfo.InvariantCulture) +
               ", deleted " + Deleted.Count.ToString(CultureInfo.InvariantCulture);

        /// <summary>"-- preview: nothing was changed. Would add 2 (Walls 2), modify 1 (Floors 1), delete 0. Ids are provisional."</summary>
        public string PreviewLine()
        {
            var counts = Counts();
            return "-- preview: nothing was changed. Would " + counts + "." +
                   (Added.Count > 0 ? " Ids are provisional." : "");
        }

        /// <summary>The additive JSON <c>preview</c> object.</summary>
        public Dictionary<string, object> ToJson(bool rolledBack)
        {
            return new Dictionary<string, object>
            {
                { "added", ToJson(Added) },
                { "modified", ToJson(Modified) },
                { "deleted", ToJson(Deleted) },
                { "rolledBack", rolledBack }
            };
        }

        private static List<object> ToJson(IReadOnlyList<ChangedElement> elements)
        {
            var list = new List<object>();
            foreach (var e in elements)
            {
                var d = new Dictionary<string, object> { { "id", e.Id } };
                if (e.Category != null) d["category"] = e.Category;
                list.Add(d);
            }
            return list;
        }
    }

    /// <summary>
    /// This Revit session's approval decisions (the <c>approvals</c> command), newest last. A
    /// decision the client never received (its request timed out with APPROVAL_PENDING) is shown
    /// once in the next model-state stamp. Thread-safe: the HTTP thread marks a timed-out
    /// request's decision undelivered, the main thread writes and reads.
    /// </summary>
    public static class ApprovalLog
    {
        private const int Cap = 200;
        private static readonly object Sync = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static int _nextId = 1;

        public sealed class Entry
        {
            public int Id { get; internal set; }
            public DateTime Time { get; internal set; }
            public string Text { get; internal set; }
            public ApprovalDecision Decision { get; internal set; }
            public string Counts { get; internal set; }
            public bool Delivered { get; internal set; }
        }

        /// <summary>Logs a decision; <paramref name="delivered"/> = the client is still waiting for it.</summary>
        public static Entry Add(DateTime local, string text, ApprovalDecision decision, string counts, bool delivered)
        {
            lock (Sync)
            {
                var e = new Entry
                {
                    Id = _nextId++,
                    Time = local,
                    Text = text ?? "",
                    Decision = decision,
                    Counts = counts ?? "",
                    Delivered = delivered
                };
                Entries.Add(e);
                if (Entries.Count > Cap) Entries.RemoveRange(0, Entries.Count - Cap);
                return e;
            }
        }

        /// <summary>The client stopped waiting before the decision reached it.</summary>
        public static void MarkUndelivered(int id)
        {
            lock (Sync)
            {
                foreach (var e in Entries)
                    if (e.Id == id) e.Delivered = false;
            }
        }

        /// <summary>
        /// "approval #8: discarded" (several joined with ", ") for decisions no client received yet,
        /// marking them delivered; null when there are none. The stamp calls it.
        /// </summary>
        public static string TakeUndelivered()
        {
            lock (Sync)
            {
                var pending = Entries.Where(e => !e.Delivered).ToList();
                if (pending.Count == 0) return null;
                foreach (var e in pending) e.Delivered = true;
                return string.Join(", ", pending.Select(e =>
                    "approval #" + e.Id.ToString(CultureInfo.InvariantCulture) + ": " + DecisionWord(e.Decision)));
            }
        }

        public static IReadOnlyList<Entry> List()
        {
            lock (Sync) return Entries.ToList();
        }

        public static string DecisionWord(ApprovalDecision d) => d == ApprovalDecision.Kept ? "kept" : "discarded";

        /// <summary>"#7 14:03:12 delete 1730959 1730980 → kept by the user (deleted 2)".</summary>
        public static string FormatLine(Entry e)
        {
            var sb = new StringBuilder();
            sb.Append('#').Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append(' ');
            sb.Append(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append(e.Text).Append(" → ").Append(DecisionWord(e.Decision)).Append(" by the user");
            if (!string.IsNullOrEmpty(e.Counts)) sb.Append(" (").Append(e.Counts).Append(')');
            return sb.ToString();
        }

        /// <summary>"deleted 2" / "added 1, modified 3" — the non-zero counts for the log line.</summary>
        public static string ShortCounts(ChangeSetSummary s)
        {
            var parts = new List<string>();
            if (s.Added.Count > 0) parts.Add("added " + s.Added.Count.ToString(CultureInfo.InvariantCulture));
            if (s.Modified.Count > 0) parts.Add("modified " + s.Modified.Count.ToString(CultureInfo.InvariantCulture));
            if (s.Deleted.Count > 0) parts.Add("deleted " + s.Deleted.Count.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }

        /// <summary>Tests: start empty, ids from 1.</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                Entries.Clear();
                _nextId = 1;
            }
        }
    }
}
