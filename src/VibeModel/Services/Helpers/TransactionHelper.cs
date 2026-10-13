using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Wraps the Revit transaction pattern to eliminate boilerplate in commands.
    /// </summary>
    public static class TransactionHelper
    {
        /// <summary>
        /// Execute an action inside a Revit transaction. Returns null on success, error string on failure.
        /// Revit failures are handled by <see cref="VibeFailuresPreprocessor"/> so no modal failure
        /// dialog can block the main thread; a change Revit rolls back is returned as an error.
        /// </summary>
        public static string Execute(Document doc, string name, Action action)
        {
            // Every VibeModel transaction must carry the shared prefix — ModelChangeTracker
            // uses it to tell our edits from the user's. Callers already include it; this
            // guards future commands that might forget.
            if (name == null || !name.StartsWith(Claude.ModelChangeTracker.TransactionPrefix, StringComparison.Ordinal))
                name = Claude.ModelChangeTracker.TransactionPrefix + (name ?? "");

            using (var trans = new Transaction(doc, name))
            {
                var preprocessor = Attach(trans);

                trans.Start();
                try
                {
                    action();
                    var error = CommitOrReport(trans, preprocessor);
                    return error == null ? null : "ERROR: " + error;
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted() && !trans.HasEnded())
                        trans.RollBack();
                    return "ERROR: " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Gives a (not yet started) transaction VibeModel's failure handling. Also called from
        /// the code `exec` generates around a snippet.
        /// </summary>
        public static VibeFailuresPreprocessor Attach(Transaction trans)
        {
            var preprocessor = new VibeFailuresPreprocessor();
            var options = trans.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);
            options.SetClearAfterRollback(true);
            trans.SetFailureHandlingOptions(options);
            return preprocessor;
        }

        /// <summary>
        /// Commits and checks what Revit actually did: null when committed, else the error
        /// message (without "ERROR: ") — Revit's own reason when it rolled the change back.
        /// </summary>
        public static string CommitOrReport(Transaction trans, VibeFailuresPreprocessor preprocessor)
        {
            if (!trans.HasStarted() || trans.HasEnded()) return null;
            var status = trans.Commit();
            if (status == TransactionStatus.Committed) return null;
            if (status == TransactionStatus.RolledBack)
                return FailureReport.RollbackErrorMessage(preprocessor?.Report.RollbackReason);
            return "transaction ended with status " + status;
        }
    }

    /// <summary>
    /// Transaction-scoped failure handling: applies <see cref="RevitFailurePolicy"/> before
    /// Revit would show its failure dialog. <see cref="Report"/> holds what this transaction saw.
    /// </summary>
    public class VibeFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureReport Report { get; } = new FailureReport();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var result = RevitFailureHandler.Process(failuresAccessor, out var seen);
            Report.Merge(seen);
            return result;
        }
    }

    /// <summary>
    /// The old name. A Revit instance still running an earlier VibeModel compiles its exec
    /// wrapper against the DLL on disk, which references this type — so it stays, as an alias.
    /// </summary>
    [Obsolete("Use VibeFailuresPreprocessor (TransactionHelper.Attach).")]
    public class WarningSwallower : VibeFailuresPreprocessor
    {
    }

    /// <summary>
    /// The one place the failure policy is applied to Revit. Called by the transaction
    /// preprocessor and — for transactions an exec snippet opens itself — by the
    /// application-level FailuresProcessing event while a VibeModel command runs.
    /// Main thread only.
    /// </summary>
    public static class RevitFailureHandler
    {
        private static readonly FailureLoopGuard LoopGuard = new FailureLoopGuard();

        internal static void ResetPasses() => LoopGuard.Reset();

        public static FailureProcessingResult Process(FailuresAccessor fa, out FailureReport seen)
        {
            seen = new FailureReport();
            try
            {
                var messages = fa.GetFailureMessages();
                if (messages == null || messages.Count == 0)
                    return FailureProcessingResult.Continue;

                var doc = fa.GetDocument();
                var described = new List<KeyValuePair<FailureMessageAccessor, FailureInfo>>();
                foreach (FailureMessageAccessor msg in messages)
                    described.Add(new KeyValuePair<FailureMessageAccessor, FailureInfo>(msg, Describe(doc, msg)));
                int pass = LoopGuard.NextPass(described.Select(d => d.Value));

                // Decide everything first: when one failure forces a rollback nothing else is
                // applied or reported as kept — the whole transaction is undone anyway.
                var actions = described.ConvertAll(d => RevitFailurePolicy.Decide(d.Value, pass));
                if (actions.Contains(RevitFailureAction.RollBack))
                {
                    for (int i = 0; i < described.Count; i++)
                    {
                        if (actions[i] != RevitFailureAction.RollBack) continue;
                        var description = described[i].Value.Description;
                        seen.AddRollback(pass > RevitFailurePolicy.MaxPasses
                            ? RevitFailurePolicy.LoopReason + ": " + description
                            : description);
                    }
                    Record(seen);
                    ResetPasses();
                    try { fa.DeleteAllWarnings(); } catch { /* nothing left to delete */ }
                    ClearAfterRollback(fa);
                    Logger.Info("Revit failure → rollback of '" + SafeName(fa) + "': " + seen.RollbackReason);
                    return FailureProcessingResult.ProceedWithRollBack;
                }

                var resolved = new List<FailureInfo>();
                for (int i = 0; i < described.Count; i++)
                {
                    var msg = described[i].Key;
                    var info = described[i].Value;
                    if (actions[i] == RevitFailureAction.DeleteWarning)
                    {
                        fa.DeleteWarning(msg);
                        seen.AddWarning(info.Description);
                    }
                    else
                    {
                        // Revit's own "Delete Element(s)" — the button the dialog offers.
                        msg.SetCurrentResolutionType(FailureResolutionType.DeleteElements);
                        fa.ResolveFailure(msg);
                        seen.AddResolved(info.Description, info.FailingElements);
                        resolved.Add(info);
                    }
                }
                Record(seen);

                if (resolved.Count > 0)
                {
                    LoopGuard.Resolved(pass, resolved);
                    Logger.Info("Revit failure resolved by deleting annotations in '" + SafeName(fa) + "'");
                    return FailureProcessingResult.ProceedWithCommit;
                }
                ResetPasses();
                return FailureProcessingResult.Continue;
            }
            catch (Exception ex)
            {
                // Never fall through to Revit's dialog: an unhandled failure would block the main thread.
                Logger.Error("Revit failure handling failed — rolling back", ex);
                ResetPasses();
                seen = new FailureReport();
                seen.AddRollback("VibeModel could not process Revit's failure (" + ex.Message + ")");
                Record(seen);
                ClearAfterRollback(fa);
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }

        private static void Record(FailureReport seen)
        {
            if (!seen.IsEmpty) FailureCollector.Current?.Merge(seen);
        }

        private static FailureInfo Describe(Document doc, FailureMessageAccessor msg)
        {
            var elements = new List<FailingElementInfo>();
            ICollection<ElementId> ids = null;
            try { ids = msg.GetFailingElementIds(); } catch { /* no failing elements */ }
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    var info = new FailingElementInfo { Id = id.IntegerValue };
                    try
                    {
                        var e = doc?.GetElement(id);
                        if (e != null)
                        {
                            info.ViewSpecific = e.ViewSpecific;
                            var cat = e.Category;
                            if (cat != null)
                            {
                                info.Category = cat.Name;
                                info.BuiltInCategory = cat.Id.IntegerValue;
                                info.IsTag = cat.IsTagCategory;
                            }
                        }
                    }
                    catch { /* unreadable element → not whitelisted → rollback */ }
                    elements.Add(info);
                }
            }

            bool canDelete = false;
            try { canDelete = msg.HasResolutionOfType(FailureResolutionType.DeleteElements); } catch { }

            string description;
            try { description = msg.GetDescriptionText(); } catch { description = null; }

            return new FailureInfo
            {
                Severity = Map(msg.GetSeverity()),
                Description = description,
                FailingElements = elements,
                HasDeleteElementsResolution = canDelete
            };
        }

        private static RevitFailureSeverity Map(FailureSeverity s)
        {
            switch (s)
            {
                case FailureSeverity.Warning: return RevitFailureSeverity.Warning;
                case FailureSeverity.Error: return RevitFailureSeverity.Error;
                default: return RevitFailureSeverity.DocumentCorruption;
            }
        }

        // A snippet's own transaction has no ClearAfterRollback; without it the rolled-back
        // failures are re-posted to the UI afterwards.
        private static void ClearAfterRollback(FailuresAccessor fa)
        {
            try
            {
                var options = fa.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                fa.SetFailureHandlingOptions(options);
            }
            catch { /* best effort */ }
        }

        private static string SafeName(FailuresAccessor fa)
        {
            try { return fa.GetTransactionName(); } catch { return "?"; }
        }
    }

    /// <summary>
    /// Collects the <see cref="FailureReport"/> of the command running on the main thread
    /// (same idea as CreatedElementsRecorder). Outside <see cref="Collect{T}"/> failures are
    /// still handled, just not reported.
    /// </summary>
    public static class FailureCollector
    {
        /// <summary>The running command's report; null when no command is being collected.</summary>
        public static FailureReport Current { get; private set; }

        /// <summary>
        /// Runs one command with its own report; <paramref name="report"/> is what Revit's failure
        /// processing did. Nested calls are safe: the inner report is also merged into the outer one.
        /// </summary>
        public static T Collect<T>(Func<T> run, out FailureReport report)
        {
            var outer = Current;
            var mine = new FailureReport();
            Current = mine;
            if (outer == null) RevitFailureHandler.ResetPasses();
            try { return run(); }
            finally
            {
                Current = outer;
                outer?.Merge(mine);
                report = mine;
            }
        }
    }
}
