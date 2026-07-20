using System;
using System.Collections.Generic;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Counts committed transactions per document and classifies each as VibeModel-initiated
    /// (transaction name carries the "VibeModel: " prefix) or user-initiated (anything else,
    /// plus undo/redo). The counts feed the model-state stamp appended to every HTTP command
    /// response, so an AI client can tell "the model changed outside my control" without
    /// re-reading everything.
    ///
    /// Pure logic — no Revit API types — so it is unit-testable headlessly. The thin
    /// Revit-facing subscription lives in VibeModelApp (DocumentChanged / DocumentClosing).
    /// </summary>
    public static class ModelChangeTracker
    {
        /// <summary>
        /// Every transaction VibeModel opens must carry this name prefix — it is how the
        /// tracker tells our edits from the user's. TransactionHelper enforces it.
        /// </summary>
        public const string TransactionPrefix = "VibeModel: ";

        private sealed class DocState
        {
            public long TotalEdits;
            public long UserEditsSinceLastStamp;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DocState> Docs =
            new Dictionary<string, DocState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Stable per-document key: full path when saved, title otherwise.</summary>
        public static string DocKey(string pathName, string title)
        {
            return !string.IsNullOrEmpty(pathName) ? pathName : "untitled:" + (title ?? "");
        }

        /// <summary>A committed transaction: ours if ALL its names carry the prefix.</summary>
        public static void RecordCommit(string docKey, IEnumerable<string> transactionNames)
        {
            bool ours = IsVibeModelTransaction(transactionNames);
            lock (Sync)
            {
                var state = GetOrAdd(docKey);
                state.TotalEdits++;
                if (!ours)
                    state.UserEditsSinceLastStamp++;
            }
        }

        /// <summary>
        /// Undo/redo is always user-driven — even undoing a VibeModel edit changes the model
        /// outside VibeModel's control, so it counts as a user edit.
        /// </summary>
        public static void RecordUndoRedo(string docKey)
        {
            lock (Sync)
            {
                var state = GetOrAdd(docKey);
                state.TotalEdits++;
                state.UserEditsSinceLastStamp++;
            }
        }

        /// <summary>
        /// True only if there is at least one transaction name and every one starts with
        /// the VibeModel prefix. A mixed or empty set is treated as a user edit.
        /// </summary>
        public static bool IsVibeModelTransaction(IEnumerable<string> transactionNames)
        {
            if (transactionNames == null)
                return false;

            bool any = false;
            foreach (var name in transactionNames)
            {
                if (name == null || !name.StartsWith(TransactionPrefix, StringComparison.Ordinal))
                    return false;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Read the current counters for a stamp and reset the since-last-command user-edit
        /// counter, atomically. Only call when the stamp will actually be delivered — the
        /// reset means "the client has been told".
        /// </summary>
        public static ModelStamp TakeStamp(string docKey, string viewName, int selectedCount)
        {
            lock (Sync)
            {
                var state = GetOrAdd(docKey);
                var stamp = new ModelStamp(state.TotalEdits, state.UserEditsSinceLastStamp,
                    viewName, selectedCount);
                state.UserEditsSinceLastStamp = 0;
                return stamp;
            }
        }

        /// <summary>Drop per-document state when the document closes.</summary>
        public static void Forget(string docKey)
        {
            lock (Sync)
            {
                Docs.Remove(docKey);
            }
        }

        // Test seam: wipe all state between unit tests.
        internal static void ResetAll()
        {
            lock (Sync)
            {
                Docs.Clear();
            }
        }

        private static DocState GetOrAdd(string docKey)
        {
            var key = docKey ?? "";
            if (!Docs.TryGetValue(key, out var state))
            {
                state = new DocState();
                Docs[key] = state;
            }
            return state;
        }
    }

    /// <summary>
    /// An immutable model-state snapshot taken when a command response is built. Rendered as
    /// a trailing line in text mode and a "meta" object in JSON mode — both purely additive.
    /// </summary>
    public sealed class ModelStamp
    {
        public long TotalEdits { get; }
        public long UserEdits { get; }
        public string ViewName { get; }
        public int SelectedCount { get; }

        public ModelStamp(long totalEdits, long userEdits, string viewName, int selectedCount)
        {
            TotalEdits = totalEdits;
            UserEdits = userEdits;
            ViewName = viewName;
            SelectedCount = selectedCount;
        }

        /// <summary>
        /// "-- model #47 | view: {3D} | selected: 0", with an unmissable warning inserted
        /// when the user edited the model since the previous command.
        /// </summary>
        public string ToTextLine()
        {
            var line = "-- model #" + TotalEdits;
            if (UserEdits > 0)
            {
                line += " (" + UserEdits + " USER " + (UserEdits == 1 ? "edit" : "edits") +
                        " since last command - model changed outside VibeModel)";
            }
            line += " | view: " + (string.IsNullOrEmpty(ViewName) ? "unknown" : ViewName) +
                    " | selected: " + SelectedCount;
            return line;
        }

        /// <summary>Append the stamp as one trailing line, preserving the original text.</summary>
        public string AppendToText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return ToTextLine();
            return (text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n") + ToTextLine();
        }

        /// <summary>The additive JSON "meta" object.</summary>
        public Dictionary<string, object> ToMeta()
        {
            return new Dictionary<string, object>
            {
                { "edits", TotalEdits },
                { "userEditsSinceLastCommand", UserEdits },
                { "activeView", ViewName },
                { "selectedCount", SelectedCount }
            };
        }
    }
}
