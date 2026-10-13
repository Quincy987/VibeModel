using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Claude
{
    // Revit-free mirror of Autodesk.Revit.DB.FailureSeverity (named apart so files that import
    // both namespaces never see an ambiguous name).
    public enum RevitFailureSeverity { Warning, Error, DocumentCorruption }

    public enum RevitFailureAction { DeleteWarning, ResolveByDeletingAnnotations, RollBack }

    /// <summary>One failing element as Revit names it, read before any resolution deletes it.</summary>
    public sealed class FailingElementInfo
    {
        public long Id { get; set; }
        public string Category { get; set; }        // null when the element (or its category) is gone
        public int BuiltInCategory { get; set; }    // Category.Id.IntegerValue; 0 when unknown
        public bool ViewSpecific { get; set; }      // drawn in one view only (OwnerViewId set)
        public bool IsTag { get; set; }             // Category.IsTagCategory
    }

    /// <summary>One Revit failure message, translated out of the Revit API.</summary>
    public sealed class FailureInfo
    {
        public RevitFailureSeverity Severity { get; set; }
        public string Description { get; set; }
        public IReadOnlyList<FailingElementInfo> FailingElements { get; set; }
        public bool HasDeleteElementsResolution { get; set; }
    }

    /// <summary>
    /// What VibeModel does with a Revit failure while one of its commands runs, so Revit never
    /// shows its modal failure dialog (invisible when Revit is minimized or driven headless —
    /// it froze every following request). Warnings are dismissed; an error whose only failing
    /// elements are view-specific annotations that hang on a host (spot dimensions, dimensions,
    /// tags) is resolved by deleting them, which keeps the change; any other error rolls back.
    /// </summary>
    public static class RevitFailurePolicy
    {
        /// <summary>Failure passes per transaction before handling is considered looping.</summary>
        public const int MaxPasses = 10;

        public const string LoopReason = "failure handling looped (Revit kept posting failures after they were resolved)";

        // BuiltInCategory values (identical in Revit 2022–2025) of annotations that only
        // describe a host: deleting one never removes model content.
        private static readonly HashSet<int> HostedAnnotationCategories = new HashSet<int>
        {
            -2000260, // OST_Dimensions
            -2000263, // OST_SpotElevations
            -2000264, // OST_SpotCoordinates
            -2000265, // OST_SpotSlopes
            -2000970, // OST_MultiReferenceAnnotations
        };

        public static RevitFailureAction Decide(FailureInfo f, int pass)
        {
            if (pass > MaxPasses) return RevitFailureAction.RollBack;
            if (f == null) return RevitFailureAction.RollBack;
            if (f.Severity == RevitFailureSeverity.Warning) return RevitFailureAction.DeleteWarning;
            if (f.Severity == RevitFailureSeverity.Error
                && f.HasDeleteElementsResolution
                && f.FailingElements != null && f.FailingElements.Count > 0
                && f.FailingElements.All(IsDeletableAnnotation))
                return RevitFailureAction.ResolveByDeletingAnnotations;
            return RevitFailureAction.RollBack;
        }

        /// <summary>
        /// Strict whitelist: view-specific AND a dimension / spot dimension / multi-reference
        /// annotation / tag. Text notes, detail items, filled regions, model elements, groups,
        /// views and sheets are never deleted on the user's behalf.
        /// </summary>
        public static bool IsDeletableAnnotation(FailingElementInfo e)
        {
            if (e == null || !e.ViewSpecific) return false;
            if (HostedAnnotationCategories.Contains(e.BuiltInCategory)) return true;
            if (e.IsTag) return true;
            var name = e.Category?.Trim();
            return !string.IsNullOrEmpty(name) && name.EndsWith("Tags", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class WarningEntry
    {
        public string Message { get; set; }
        public int Count { get; set; }
    }

    public sealed class ResolvedEntry
    {
        public string Message { get; set; }
        public List<FailingElementInfo> Deleted { get; set; } = new List<FailingElementInfo>();
    }

    /// <summary>
    /// What Revit's failure processing did during one command: dismissed warnings, annotations
    /// deleted to keep the change, and the reasons of any rollback. Consumed by the response
    /// (text lines / JSON "revit" object) and from code (ids of what was deleted).
    /// </summary>
    public sealed class FailureReport
    {
        public const string RollbackErrorCode = "REVIT_FAILURE";
        public const string RollbackPrefix = "Revit rolled the change back: ";
        public const string RollbackHint = "Revit refused this change and nothing of it was kept. The message is Revit's own: fix its cause (move/resize the element, delete the dependent annotation first, or re-host it) and retry.";
        private const int MaxListedIds = 10;

        public List<WarningEntry> Warnings { get; } = new List<WarningEntry>();
        public List<ResolvedEntry> Resolved { get; } = new List<ResolvedEntry>();
        public List<string> RollbackReasons { get; } = new List<string>();

        /// <summary>The first rollback reason; null when nothing was rolled back.</summary>
        public string RollbackReason => RollbackReasons.Count > 0 ? RollbackReasons[0] : null;
        public bool RolledBack => RollbackReasons.Count > 0;
        public bool IsEmpty => Warnings.Count == 0 && Resolved.Count == 0 && RollbackReasons.Count == 0;

        public IEnumerable<long> DeletedIds => Resolved.SelectMany(r => r.Deleted).Select(d => d.Id).Distinct();

        public void AddWarning(string message)
        {
            message = Clean(message);
            var hit = Warnings.Find(w => w.Message == message);
            if (hit != null) hit.Count++;
            else Warnings.Add(new WarningEntry { Message = message, Count = 1 });
        }

        public void AddResolved(string message, IEnumerable<FailingElementInfo> deleted)
        {
            message = Clean(message);
            var list = deleted?.Where(d => d != null).ToList() ?? new List<FailingElementInfo>();
            var hit = Resolved.Find(r => r.Message == message);
            if (hit == null) Resolved.Add(new ResolvedEntry { Message = message, Deleted = list });
            else foreach (var d in list)
                if (!hit.Deleted.Any(x => x.Id == d.Id)) hit.Deleted.Add(d);
        }

        public void AddRollback(string reason)
        {
            reason = Clean(reason);
            if (!RollbackReasons.Contains(reason)) RollbackReasons.Add(reason);
        }

        public void Merge(FailureReport other)
        {
            if (other == null) return;
            foreach (var w in other.Warnings)
                for (int i = 0; i < w.Count; i++) AddWarning(w.Message);
            foreach (var r in other.Resolved) AddResolved(r.Message, r.Deleted);
            foreach (var reason in other.RollbackReasons) AddRollback(reason);
        }

        /// <summary>One "-- revit …" line per distinct warning / resolution / rollback reason.</summary>
        public IEnumerable<string> ToTextLines()
        {
            foreach (var w in Warnings)
                yield return "-- revit warning: " + w.Message + (w.Count > 1 ? " (" + w.Count + "×)" : "");
            foreach (var r in Resolved)
                yield return "-- revit resolved: " + r.Message + " → deleted " + DescribeDeleted(r.Deleted);
            foreach (var reason in RollbackReasons)
                yield return "-- revit rolled back: " + reason;
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "warnings", Warnings.Select(w => (object)new Dictionary<string, object>
                    { { "message", w.Message }, { "count", w.Count } }).ToList() },
                { "resolved", Resolved.Select(r => (object)new Dictionary<string, object>
                    {
                        { "message", r.Message },
                        { "deleted", r.Deleted.Select(d => (object)new Dictionary<string, object>
                            { { "id", d.Id }, { "category", d.Category } }).ToList() }
                    }).ToList() },
                { "rolledBack", RollbackReason }
            };
        }

        public static string RollbackErrorMessage(string reason)
            => RollbackPrefix + (string.IsNullOrWhiteSpace(reason) ? "Revit refused the change" : Clean(reason));

        /// <summary>True for an error message produced by <see cref="RollbackErrorMessage"/> (with or without "ERROR: ").</summary>
        public static bool IsRollbackError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var t = text.StartsWith("ERROR", StringComparison.Ordinal) ? text.Substring(text.IndexOf(':') + 1).TrimStart() : text;
            return t.StartsWith(RollbackPrefix, StringComparison.Ordinal);
        }

        private static string DescribeDeleted(List<FailingElementInfo> deleted)
        {
            if (deleted == null || deleted.Count == 0) return "the failing annotation";
            var parts = deleted.Take(MaxListedIds)
                .Select(d => d.Id + (string.IsNullOrEmpty(d.Category) ? "" : " (" + d.Category + ")"));
            var text = deleted.Count + (deleted.Count == 1 ? " annotation: " : " annotations: ") + string.Join(", ", parts);
            if (deleted.Count > MaxListedIds) text += " +" + (deleted.Count - MaxListedIds) + " more";
            return text;
        }

        // Revit descriptions can carry line breaks; a response line must stay one line.
        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(no description)";
            return string.Join(" ", s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0));
        }
    }
}
