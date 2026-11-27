using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Phase 2 (pure): VibeModel's kept runs per document, newest last, so the <c>undo</c> command can
    /// take back exactly the last one — and refuse whenever anything else happened since. Revit offers
    /// one public way to undo: posting its own Undo command, which runs after the request and undoes
    /// whatever is on top of Revit's undo list. So the ledger only allows it while the model's edit
    /// counter (<see cref="ModelChangeTracker"/>) still stands where the run left it: then the top of
    /// Revit's list IS that run.
    /// <para>
    /// The posted Undo is confirmed when DocumentChanged reports the undo
    /// (<see cref="ObserveUndoRedo"/>); the next model-state stamp says so (<see cref="TakeReport"/>).
    /// Main thread only, apart from the lock (the stamp is built there too).
    /// </para>
    /// </summary>
    public static class UndoLedger
    {
        /// <summary>Runs kept per document (older ones drop off).</summary>
        public const int Cap = 50;

        /// <summary>A posted Undo not seen after this long did not happen (the stamp says so).</summary>
        public static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(20);

        public sealed class Run
        {
            public string DocKey { get; internal set; }
            public string Title { get; internal set; }
            /// <summary>The entry in Revit's undo list ("VibeModel: exec …").</summary>
            public string UndoName { get; internal set; }
            /// <summary>"exec // Doel: … — added 1 (Levels 1), modified 1 (Views 1), deleted 0".</summary>
            public string Summary { get; internal set; }
            public long EditsBefore { get; internal set; }
            public long EditsAfter { get; internal set; }
            public DateTime Time { get; internal set; }
            public string TurnId { get; internal set; }
            /// <summary>False when the run may have left several undo entries (an exec that saves runs without a group).</summary>
            public bool Undoable { get; internal set; }
            public string NotUndoableReason { get; internal set; }
            /// <summary>Transaction names committed during the run: an undo naming any of them is this run.</summary>
            internal HashSet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>The answer to "may the last run be taken back now?".</summary>
        public sealed class Check
        {
            public bool Ok { get; internal set; }
            public Run Run { get; internal set; }
            public string Reason { get; internal set; }
            public string Hint { get; internal set; }
        }

        private sealed class Pending
        {
            public Run Run;
            public long EditsAtPost;
            public DateTime PostedUtc;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, List<Run>> Stacks = new Dictionary<string, List<Run>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Reports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CurrentNames = new HashSet<string>(StringComparer.Ordinal);
        private static Pending _pending;
        private static Run _lastAnywhere;

        // ------------------------------------------------------------------ recording

        /// <summary>A modifying run starts: the transaction names it commits are collected from here on.</summary>
        public static void BeginRun()
        {
            lock (Sync) CurrentNames.Clear();
        }

        /// <summary>DocumentChanged while a command runs: the names of a committed transaction.</summary>
        public static void NoteCommit(IEnumerable<string> names)
        {
            if (names == null) return;
            lock (Sync)
                foreach (var n in names)
                    if (!string.IsNullOrEmpty(n)) CurrentNames.Add(n);
        }

        /// <summary>
        /// A kept run. <paramref name="undoName"/> is the entry Revit shows (a group's name), else the
        /// last committed transaction's. Returns the recorded run.
        /// </summary>
        public static Run Record(string docKey, string title, string undoName, string summary, long editsBefore, long editsAfter,
            DateTime localTime, string turnId, bool undoable, string notUndoableReason = null)
        {
            lock (Sync)
            {
                var run = new Run
                {
                    DocKey = docKey ?? "",
                    Title = title ?? "",
                    UndoName = string.IsNullOrWhiteSpace(undoName) ? CurrentNames.LastOrDefault() ?? "" : undoName,
                    Summary = summary ?? "",
                    EditsBefore = editsBefore,
                    EditsAfter = editsAfter,
                    Time = localTime,
                    TurnId = turnId,
                    Undoable = undoable,
                    NotUndoableReason = undoable ? null : notUndoableReason
                };
                foreach (var n in CurrentNames) run.Names.Add(n);
                if (!string.IsNullOrEmpty(run.UndoName)) run.Names.Add(run.UndoName);
                CurrentNames.Clear();
                var stack = StackOf(run.DocKey);
                stack.Add(run);
                if (stack.Count > Cap) stack.RemoveRange(0, stack.Count - Cap);
                _lastAnywhere = run;
                return run;
            }
        }

        // ------------------------------------------------------------------ deciding

        public const string RefusedCode = "UNDO_REFUSED";

        /// <summary>
        /// May VibeModel's last run in <paramref name="docKey"/> be taken back now? Refuses — naming
        /// why — when there is none, when the model was edited since (by the user, Revit or another
        /// client: the top of Revit's undo list is not that run any more), when the run may be several
        /// undo entries, or while an earlier undo is still on its way.
        /// </summary>
        public static Check Decide(string docKey, string title, long currentEdits, DateTime utcNow)
        {
            lock (Sync)
            {
                ExpirePending(utcNow);
                if (_pending != null)
                    return Refuse(_pending.Run, "the previous undo (of '" + _pending.Run.UndoName + "') has not happened yet.",
                        "wait for the next response's stamp (`undone: …`), then call undo again.");
                Stacks.TryGetValue(docKey ?? "", out var stack);
                var top = stack != null && stack.Count > 0 ? stack[stack.Count - 1] : null;
                if (top == null)
                {
                    if (_lastAnywhere != null && !string.Equals(_lastAnywhere.DocKey, docKey, StringComparison.OrdinalIgnoreCase))
                        return Refuse(null, "VibeModel's last change was made in another document ('" + _lastAnywhere.Title + "'), not in the active one ('" + title + "').",
                            "make that document active in Revit, then call undo again; or the user presses Ctrl+Z there.");
                    return Refuse(null, "VibeModel has made no change in this document this Revit session that it could take back.",
                        "the user presses Ctrl+Z in Revit to undo their own work.");
                }
                if (!top.Undoable)
                    return Refuse(top, "the last change ('" + top.Summary + "') " + (top.NotUndoableReason ?? "may be several undo steps") + ", so one Undo would not take all of it back.",
                        "the user takes it back with Ctrl+Z in Revit (Revit's undo list shows each step).");
                if (currentEdits != top.EditsAfter)
                {
                    long since = currentEdits - top.EditsAfter;
                    return Refuse(top, "the model changed after VibeModel's last change ('" + top.Summary + "')" +
                        (since > 0 ? " — " + since.ToString(CultureInfo.InvariantCulture) + " " + (since == 1 ? "edit" : "edits") + " since (the user's work, Revit's own Undo/Redo or another client)" : "") +
                        ", so Revit's Undo would take back something else.",
                        "the user decides: Ctrl+Z in Revit shows what each step is. Never undo the user's own work.");
                }
                return new Check { Ok = true, Run = top };
            }
        }

        private static Check Refuse(Run run, string reason, string hint) => new Check { Ok = false, Run = run, Reason = reason, Hint = hint };

        /// <summary>The undo was posted (it runs after the request): wait for DocumentChanged to confirm it.</summary>
        public static void MarkPosted(Run run, long currentEdits, DateTime utcNow)
        {
            lock (Sync) _pending = new Pending { Run = run, EditsAtPost = currentEdits, PostedUtc = utcNow };
        }

        /// <summary>True while a posted undo for this document has not been seen yet: that undo is VibeModel's, not the user's.</summary>
        public static bool IsPending(string docKey)
        {
            lock (Sync) return _pending != null && string.Equals(_pending.Run.DocKey, docKey ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ observing

        /// <summary>
        /// DocumentChanged reported an undo or redo in <paramref name="docKey"/> (after the edit counter
        /// moved to <paramref name="totalEditsAfter"/>). The posted undo is confirmed when it undid one of
        /// the run's transactions; the run below then becomes the top — its counter moved with the undo,
        /// so it is set to where the model stands now, unless something else happened between the two runs.
        /// </summary>
        public static void ObserveUndoRedo(string docKey, IEnumerable<string> names, long totalEditsAfter, bool redo)
        {
            lock (Sync)
            {
                if (_pending == null || !string.Equals(_pending.Run.DocKey, docKey ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    // The user's own Ctrl+Z took back VibeModel's last run (measured: Revit's Undo button). It is
                    // gone from the model, so it leaves the ledger too; the run below becomes the top as above.
                    if (!redo) DropIfTopUndone(docKey, names, totalEditsAfter);
                    return;
                }
                var p = _pending;
                _pending = null;
                var list = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
                // The posted undo is the first change after it was posted, and it names the run's transaction.
                bool matches = !redo && totalEditsAfter == p.EditsAtPost + 1 &&
                               (list.Count == 0 || list.Any(n => p.Run.Names.Contains(n)));
                if (!matches)
                {
                    Reports[p.Run.DocKey] = "undo of '" + p.Run.UndoName + "' did not happen (Revit " + (redo ? "redid" : "undid") + " '" +
                                            (list.Count > 0 ? string.Join("', '", list) : "?") + "')";
                    return;
                }
                var stack = StackOf(p.Run.DocKey);
                int index = stack.IndexOf(p.Run);
                if (index >= 0)
                {
                    stack.RemoveAt(index);
                    var below = index - 1 >= 0 && index - 1 < stack.Count ? stack[index - 1] : null;
                    if (below != null && below.EditsAfter == p.Run.EditsBefore) below.EditsAfter = totalEditsAfter;
                }
                if (ReferenceEquals(_lastAnywhere, p.Run)) _lastAnywhere = stack.Count > 0 ? stack[stack.Count - 1] : null;
                Reports[p.Run.DocKey] = "undone: " + p.Run.UndoName;
            }
        }

        private static void DropIfTopUndone(string docKey, IEnumerable<string> names, long totalEditsAfter)
        {
            if (!Stacks.TryGetValue(docKey ?? "", out var stack) || stack.Count == 0) return;
            var top = stack[stack.Count - 1];
            var list = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (totalEditsAfter != top.EditsAfter + 1 || list.Count == 0 || !list.Any(n => top.Names.Contains(n))) return;
            stack.RemoveAt(stack.Count - 1);
            var below = stack.Count > 0 ? stack[stack.Count - 1] : null;
            if (below != null && below.EditsAfter == top.EditsBefore) below.EditsAfter = totalEditsAfter;
            if (ReferenceEquals(_lastAnywhere, top)) _lastAnywhere = below;
        }

        /// <summary>
        /// For the next model-state stamp of this document: "undone: VibeModel: exec …" once the posted
        /// undo was seen, "undo of '…' did not happen" when it was not seen in time; null otherwise.
        /// Shown once.
        /// </summary>
        public static string TakeReport(string docKey, DateTime utcNow)
        {
            lock (Sync)
            {
                ExpirePending(utcNow);
                if (Reports.TryGetValue(docKey ?? "", out var report))
                {
                    Reports.Remove(docKey ?? "");
                    return report;
                }
                return null;
            }
        }

        private static void ExpirePending(DateTime utcNow)
        {
            if (_pending == null || utcNow - _pending.PostedUtc < PendingTimeout) return;
            Reports[_pending.Run.DocKey] = "undo of '" + _pending.Run.UndoName + "' did not happen (Revit did not run the posted Undo)";
            _pending = null;
        }

        // ------------------------------------------------------------------ reading

        /// <summary>The document's kept runs, newest first.</summary>
        public static IReadOnlyList<Run> List(string docKey)
        {
            lock (Sync)
                return Stacks.TryGetValue(docKey ?? "", out var stack) ? Enumerable.Reverse(stack).ToList() : new List<Run>();
        }

        private static List<Run> StackOf(string docKey)
        {
            if (!Stacks.TryGetValue(docKey ?? "", out var stack))
            {
                stack = new List<Run>();
                Stacks[docKey ?? ""] = stack;
            }
            return stack;
        }

        /// <summary>"exec // Doel: … (one line, ≤ 80 chars) — added 1 (Levels 1), modified 0, deleted 0".</summary>
        public static string SummaryOf(string command, string args, string changedLine)
        {
            var a = string.Join(" ", (args ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var text = ((command ?? "") + (a.Length > 0 ? " " + a : "")).Trim();
            if (text.Length > 80) text = text.Substring(0, 79).TrimEnd() + "…";
            var changed = changedLine ?? "";
            if (changed.StartsWith("-- changed: ", StringComparison.Ordinal)) changed = changed.Substring("-- changed: ".Length);
            return changed.Length > 0 ? text + " — " + changed : text;
        }

        /// <summary>Tests: start empty.</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                Stacks.Clear();
                Reports.Clear();
                CurrentNames.Clear();
                _pending = null;
                _lastAnywhere = null;
            }
        }
    }
}
