using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// The Revit side of plan 15 (the decisions are <see cref="ChangeApprovalPolicy"/>): reading a
    /// run's change set with categories, showing it to the user (isolate + the approval window)
    /// and the bookkeeping after a run group was rolled back. Main thread only — every call
    /// happens inside <see cref="RevitCommandHandler.Execute"/>, while the run's TransactionGroup
    /// is still open.
    /// </summary>
    internal static class ApprovalRun
    {
        /// <summary>
        /// Categories of the ids a command names, read BEFORE it runs: a deleted element can no
        /// longer be read afterwards, so "deleted 2 (Structural Foundations 2)" needs them now.
        /// </summary>
        public static Dictionary<int, string> PreRead(Document doc, IEnumerable<int> ids)
        {
            var map = new Dictionary<int, string>();
            if (doc == null || ids == null) return map;
            foreach (var id in ids)
            {
                if (map.ContainsKey(id)) continue;
                var name = CategoryName(doc, new ElementId(id));
                if (name != null) map[id] = name;
            }
            return map;
        }

        /// <summary>The change set with categories (added/modified read live, deleted from <paramref name="preRead"/>).</summary>
        public static ChangeSetSummary Summarize(Document doc, RecordedChanges changes, IDictionary<int, string> preRead)
        {
            if (changes == null) return new ChangeSetSummary(null, null, null);
            Func<ElementId, ChangedElement> live = id => new ChangedElement(id.IntegerValue, CategoryName(doc, id));
            Func<ElementId, ChangedElement> gone = id =>
                new ChangedElement(id.IntegerValue, preRead != null && preRead.TryGetValue(id.IntegerValue, out var c) ? c : null);
            return new ChangeSetSummary(changes.Added.Select(live), changes.Modified.Select(live), changes.Deleted.Select(gone));
        }

        private static string CategoryName(Document doc, ElementId id)
        {
            try
            {
                var e = doc?.GetElement(id);
                if (e == null) return null;
                var cat = e.Category;
                if (cat != null) return cat.Name;
                return e is ElementType ? "Types" : e.GetType().Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The edit counter before a run, so a rolled-back run can put it back.</summary>
        public static long EditsMark(Document doc)
        {
            if (doc == null) return 0;
            return ModelChangeTracker.GetTotalEdits(ModelChangeTracker.DocKey(doc.PathName, doc.Title));
        }

        /// <summary>
        /// A run group was rolled back: nothing it created exists. Its ids leave the "created by
        /// VibeModel" set and the edit counter returns to where it was before the run.
        /// </summary>
        public static void AfterRollback(Document doc, RecordedChanges changes, long editsMark)
        {
            if (doc == null) return;
            try
            {
                var key = ModelChangeTracker.DocKey(doc.PathName, doc.Title);
                if (changes != null) VibeModelCreated.Forget(key, changes.Added);
                ModelChangeTracker.RewindTo(key, editsMark);
            }
            catch (Exception ex)
            {
                Logger.Warn("Approval: bookkeeping after a rollback failed — " + ex.Message);
            }
        }

        /// <summary>What the user answered, and the selection to restore once the run group is closed.</summary>
        public sealed class PromptResult
        {
            public ApprovalDecision Decision { get; set; } = ApprovalDecision.Discarded;
            public bool DontAskAgain { get; set; }
            public ICollection<ElementId> PreviousSelection { get; set; }
        }

        /// <summary>
        /// Shows the change to the user and waits for Keep / Discard: the added and modified
        /// elements the active view can show are isolated temporarily and selected — inside a
        /// nested TransactionGroup that is rolled back afterwards, so the view returns exactly to
        /// its previous state (including a temporary isolate the user had) on both answers — then
        /// the modal approval window. Anything that goes wrong here is a Discard: an unapproved
        /// change is never kept by accident.
        /// </summary>
        public static PromptResult Ask(UIApplication app, Document doc, ChangeSetSummary summary, RecordedChanges changes,
            string commandText, string question, IList<string> contextLines, bool commandFailed)
        {
            var result = new PromptResult();
            var uidoc = app.ActiveUIDocument;
            try { result.PreviousSelection = uidoc?.Selection.GetElementIds(); }
            catch { result.PreviousSelection = null; }

            var shown = (changes?.Added ?? new List<ElementId>()).Concat(changes?.Modified ?? new List<ElementId>())
                .Where(id => doc.GetElement(id) != null).ToList();
            View view = null;
            try { view = uidoc?.ActiveView; } catch { view = null; }
            var visible = VisibleIn(doc, view, shown);

            TransactionGroup isolate = null;
            long mark = EditsMark(doc);
            try
            {
                if (visible.Count > 0)
                {
                    isolate = new TransactionGroup(doc, ModelChangeTracker.TransactionPrefix + "approval isolate");
                    isolate.Start();
                    var error = TransactionHelper.Execute(doc, ModelChangeTracker.TransactionPrefix + "approval isolate",
                        () => view.IsolateElementsTemporary(visible));
                    if (error != null)
                    {
                        Logger.Warn("Approval: isolate failed — " + error);
                        visible = new List<ElementId>();
                    }
                    try
                    {
                        uidoc.Selection.SetElementIds(visible);
                        uidoc.RefreshActiveView();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Approval: could not show the change — " + ex.Message);
                    }
                }

                var request = new UI.ApprovalRequest
                {
                    Question = question,
                    CommandText = commandText,
                    Summary = summary,
                    ContextLines = contextLines ?? new List<string>(),
                    IsolatedCount = visible.Count,
                    ShowableCount = shown.Count,
                    CommandFailed = commandFailed,
                    Zoom = shown.Count == 0 ? (Action)null : () =>
                    {
                        try { uidoc.ShowElements(shown); }
                        catch (Exception ex) { Logger.Warn("Approval: zoom failed — " + ex.Message); }
                    }
                };
                var answer = UI.ApprovalWindow.Ask(app.MainWindowHandle, request);
                result.Decision = answer.Decision;
                result.DontAskAgain = answer.DontAskAgain;
            }
            catch (Exception ex)
            {
                Logger.Error("Approval window failed — the change is discarded", ex);
                result.Decision = ApprovalDecision.Discarded;
            }
            finally
            {
                if (isolate != null)
                {
                    try { if (isolate.HasStarted() && !isolate.HasEnded()) isolate.RollBack(); }
                    catch (Exception ex) { Logger.Warn("Approval: undoing the isolate failed — " + ex.Message); }
                    finally { isolate.Dispose(); }
                    // The isolate's own commit was counted; the rollback undid it.
                    ModelChangeTracker.RewindTo(ModelChangeTracker.DocKey(doc.PathName, doc.Title), mark);
                }
            }
            return result;
        }

        /// <summary>
        /// The ids the view can isolate: none on a sheet, schedule or legend (no temporary view
        /// modes there), else the ones Revit lists as visible-able in that view.
        /// </summary>
        private static List<ElementId> VisibleIn(Document doc, View view, List<ElementId> ids)
        {
            var list = new List<ElementId>();
            if (view == null || ids.Count == 0) return list;
            try
            {
                if (!view.CanUseTemporaryVisibilityModes()) return list;
                var inView = new HashSet<int>(new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType().ToElementIds().Select(i => i.IntegerValue));
                foreach (var id in ids)
                    if (inView.Contains(id.IntegerValue)) list.Add(id);
            }
            catch (Exception ex)
            {
                Logger.Warn("Approval: could not read the view's elements — " + ex.Message);
                list.Clear();
            }
            return list;
        }

        /// <summary>Restores the selection the user had before the prompt (ids that no longer exist are skipped).</summary>
        public static void RestoreSelection(UIApplication app, ICollection<ElementId> previous)
        {
            if (previous == null) return;
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return;
                uidoc.Selection.SetElementIds(previous.Where(id => doc.GetElement(id) != null).ToList());
            }
            catch (Exception ex)
            {
                Logger.Warn("Approval: could not restore the selection — " + ex.Message);
            }
        }
    }
}
