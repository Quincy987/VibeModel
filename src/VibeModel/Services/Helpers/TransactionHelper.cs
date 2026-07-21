using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Wraps the Revit transaction pattern to eliminate boilerplate in commands.
    /// </summary>
    public static class TransactionHelper
    {
        /// <summary>
        /// Execute an action inside a Revit transaction. Returns null on success, error string on failure.
        /// Warnings are auto-dismissed to prevent modal dialogs from blocking the main thread.
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
                var options = trans.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new WarningSwallower());
                options.SetClearAfterRollback(true);
                trans.SetFailureHandlingOptions(options);

                trans.Start();
                try
                {
                    action();
                    trans.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted())
                        trans.RollBack();
                    return "ERROR: " + ex.Message;
                }
            }
        }
    }

    /// <summary>
    /// Auto-dismisses warnings during transaction commit to prevent modal dialogs.
    /// Actual errors are left for normal Revit failure handling (rollback).
    /// </summary>
    public class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();

            foreach (FailureMessageAccessor failure in failures)
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }

            return FailureProcessingResult.Continue;
        }
    }
}
