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
        /// Categories of the ids a command names AND of everything that goes with them
        /// (<c>GetDependentElements</c>: a floor's sketch, its spot elevations, a wall's hosted
        /// doors), read BEFORE it runs: a deleted element can no longer be read afterwards, so
        /// "deleted 2 (Structural Foundations 2)" needs them now. Parts that are never shown
        /// (<see cref="Hidden"/>) are recorded as hidden. At most <see cref="MaxPreRead"/> ids.
        /// </summary>
        public static Dictionary<int, string> PreRead(Document doc, IEnumerable<int> ids)
        {
            var map = new Dictionary<int, string>();
            if (doc == null || ids == null) return map;
            foreach (var id in ids)
            {
                if (map.Count >= MaxPreRead) break;
                // An element the command names always counts, even a part that is otherwise hidden.
                Read(doc, new ElementId(id), map, named: true);
                Element e;
                try { e = doc.GetElement(new ElementId(id)); } catch { e = null; }
                if (e == null) continue;
                IList<ElementId> dependents;
                try { dependents = e.GetDependentElements(null); } catch { dependents = null; }
                if (dependents == null) continue;
                foreach (var d in dependents)
                {
                    if (map.Count >= MaxPreRead) break;
                    Read(doc, d, map, named: false);
                }
            }
            return map;
        }

        private const int MaxPreRead = 5000;

        // The value that marks a pre-read id as never shown.
        private const string HiddenMark = "";

        private static void Read(Document doc, ElementId id, Dictionary<int, string> map, bool named)
        {
            if (map.ContainsKey(id.IntegerValue)) return;
            Element e;
            try { e = doc.GetElement(id); } catch { e = null; }
            if (e == null) return;
            map[id.IntegerValue] = !named && Hidden(e) ? HiddenMark : CategoryName(e) ?? e.GetType().Name;
        }

        /// <summary>The change set with categories (added/modified read live, deleted from <paramref name="preRead"/>).</summary>
        public static ChangeSetSummary Summarize(Document doc, RecordedChanges changes, IDictionary<int, string> preRead)
        {
            if (changes == null) return new ChangeSetSummary(null, null, null);
            // Hidden parts (internal elements, sketch lines, automatic sketch dimensions) are never
            // shown — measured: a new wall adds two plain Element objects, deleting a floor deletes
            // its sketch with 94 sketch lines. A deleted element whose category was not read
            // beforehand stays, as "other": an exec deletion must never read as "delete 0".
            Func<ElementId, ChangedElement> live = id =>
            {
                Element e;
                try { e = doc?.GetElement(id); } catch { e = null; }
                if (e == null) return new ChangedElement(id.IntegerValue, null);
                return Hidden(e) ? null : new ChangedElement(id.IntegerValue, CategoryName(e));
            };
            Func<ElementId, ChangedElement> gone = id =>
            {
                if (preRead != null && preRead.TryGetValue(id.IntegerValue, out var c))
                    return c == HiddenMark ? null : new ChangedElement(id.IntegerValue, c);
                return new ChangedElement(id.IntegerValue, null);
            };
            return new ChangeSetSummary(changes.Added.Select(live).Where(e => e != null),
                changes.Modified.Select(live).Where(e => e != null), changes.Deleted.Select(gone).Where(e => e != null));
        }

        /// <summary>
        /// Parts of an element nobody thinks of as an element: no category (sketch, sketch plane,
        /// internal objects; types excepted), sketch lines and automatic sketch dimensions.
        /// </summary>
        private static bool Hidden(Element e)
        {
            if (e is ElementType) return false;
            var cat = e.Category;
            if (cat == null) return true;
            int bic = cat.Id.IntegerValue;
            return bic == (int)BuiltInCategory.OST_SketchLines || bic == (int)BuiltInCategory.OST_WeakDims;
        }

        private static string CategoryName(Element e)
        {
            try
            {
                var cat = e.Category;
                if (cat != null) return cat.Name;
                return e is ElementType ? "Types" : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// What the window can isolate and zoom to: added and modified elements that still exist,
        /// minus views, types and hidden parts (a deletion modifies the view it was seen in).
        /// </summary>
        internal static List<ElementId> Showable(Document doc, RecordedChanges changes)
        {
            var list = new List<ElementId>();
            if (changes == null) return list;
            foreach (var id in changes.Added.Concat(changes.Modified))
            {
                Element e;
                try { e = doc.GetElement(id); } catch { e = null; }
                if (e == null || e is View || e is ElementType || Hidden(e)) continue;
                list.Add(id);
            }
            return list;
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

            var shown = Showable(doc, changes);
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
