using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Collects the ids of elements added while a VibeModel command (or batch) runs, fed by the
    /// DocumentChanged subscription. The post-command check then looks at exactly what the
    /// command created; the project journal also gets what it modified and deleted.
    /// Main-thread only (DocumentChanged and commands both run there).
    /// </summary>
    public static class CreatedElementsRecorder
    {
        private static List<ElementId> _ids;
        private static List<ElementId> _modified;
        private static List<ElementId> _deleted;

        public static bool IsArmed => _ids != null;

        /// <summary>How many ids were recorded so far (0 when not armed) — exec compares before/after (plan 17).</summary>
        public static int RecordedCount => _ids == null ? 0 : _ids.Count + _modified.Count + _deleted.Count;

        public static void Arm()
        {
            _ids = new List<ElementId>();
            _modified = new List<ElementId>();
            _deleted = new List<ElementId>();
        }

        /// <summary>Stop recording and return what was added (empty when never armed).</summary>
        public static List<ElementId> Disarm() => DisarmChanges().Added;

        /// <summary>Stop recording and return everything that changed (empty when never armed).</summary>
        public static RecordedChanges DisarmChanges()
        {
            var changes = new RecordedChanges(_ids, _modified, _deleted);
            _ids = _modified = _deleted = null;
            return changes;
        }

        public static void Record(ICollection<ElementId> added) => Record(added, null, null);

        public static void Record(ICollection<ElementId> added, ICollection<ElementId> modified, ICollection<ElementId> deleted)
        {
            if (_ids == null) return;
            if (added != null) _ids.AddRange(added);
            if (modified != null) _modified.AddRange(modified);
            if (deleted != null) _deleted.AddRange(deleted);
        }
    }

    /// <summary>
    /// Every element a VibeModel command created in this Revit session, per document — so
    /// `spot`, `dimension`, `tag` and `whatdepends` can warn that an annotation sits on an
    /// element VibeModel may replace. Fed from DocumentChanged while a command runs. Capped at
    /// 50 000 ids per document (the oldest drop out). Main-thread only.
    /// </summary>
    public static class VibeModelCreated
    {
        private const int Cap = 50000;
        private static readonly Dictionary<string, InsertionOrderedIdSet> ByDoc = new Dictionary<string, InsertionOrderedIdSet>();

        public static void Record(string docKey, ICollection<ElementId> added)
        {
            if (string.IsNullOrEmpty(docKey) || added == null || added.Count == 0) return;
            if (!ByDoc.TryGetValue(docKey, out var set)) ByDoc[docKey] = set = new InsertionOrderedIdSet();
            foreach (var id in added) if (id != null) set.Add(id.IntegerValue);
            set.Trim(Cap);
        }

        /// <summary>
        /// A run that was rolled back (preview, read-only, discarded — plan 15) created nothing:
        /// its ids are dropped so a later element that gets the same id is not taken for ours.
        /// </summary>
        public static void Forget(string docKey, IEnumerable<ElementId> ids)
        {
            if (string.IsNullOrEmpty(docKey) || ids == null || !ByDoc.TryGetValue(docKey, out var set)) return;
            foreach (var id in ids) if (id != null) set.Remove(id.IntegerValue);
        }

        public static bool Contains(Document doc, ElementId id)
        {
            if (doc == null || id == null) return false;
            var key = Services.Claude.ModelChangeTracker.DocKey(doc.PathName, doc.Title);
            return ByDoc.TryGetValue(key, out var set) && set.Contains(id.IntegerValue);
        }
    }

    // Insertion-ordered set so the cap drops the oldest ids first. Each id remembers the sequence
    // number of its latest insertion: a removed id leaves a stale queue entry behind, and when
    // Revit hands the same id out again (it reuses ids after a rollback) only the newest entry
    // may evict it.
    internal sealed class InsertionOrderedIdSet
    {
        private readonly Dictionary<int, long> _live = new Dictionary<int, long>();
        private readonly Queue<KeyValuePair<int, long>> _order = new Queue<KeyValuePair<int, long>>();
        private long _sequence;

        public void Add(int v)
        {
            if (_live.ContainsKey(v)) return;
            _live[v] = ++_sequence;
            _order.Enqueue(new KeyValuePair<int, long>(v, _sequence));
        }

        public bool Contains(int v) => _live.ContainsKey(v);

        public void Remove(int v) => _live.Remove(v);

        public void Trim(int cap)
        {
            while (_live.Count > cap && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                if (_live.TryGetValue(oldest.Key, out var seq) && seq == oldest.Value) _live.Remove(oldest.Key);
            }
            // Stale entries of removed ids never outnumber the live ones for long.
            if (_order.Count > 2 * cap + 1024) Compact();
        }

        private void Compact()
        {
            var keep = _order.Where(e => _live.TryGetValue(e.Key, out var seq) && seq == e.Value).ToList();
            _order.Clear();
            foreach (var e in keep) _order.Enqueue(e);
        }
    }

    /// <summary>What one command / batch changed: distinct ids, an id in exactly one group.</summary>
    public sealed class RecordedChanges
    {
        public List<ElementId> Added { get; }
        public List<ElementId> Modified { get; }
        public List<ElementId> Deleted { get; }

        public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0;

        public RecordedChanges(IEnumerable<ElementId> added, IEnumerable<ElementId> modified, IEnumerable<ElementId> deleted)
        {
            Added = Distinct(added).ToList();
            Deleted = Distinct(deleted).ToList();
            var addedValues = new HashSet<int>(Added.Select(i => i.IntegerValue));
            var deletedValues = new HashSet<int>(Deleted.Select(i => i.IntegerValue));
            // Created then edited in the same batch is "created"; edited then deleted is "deleted".
            Added.RemoveAll(i => deletedValues.Contains(i.IntegerValue));
            Modified = Distinct(modified)
                .Where(i => !addedValues.Contains(i.IntegerValue) && !deletedValues.Contains(i.IntegerValue)).ToList();
        }

        private static IEnumerable<ElementId> Distinct(IEnumerable<ElementId> ids)
        {
            var seen = new HashSet<int>();
            foreach (var id in ids ?? Enumerable.Empty<ElementId>())
                if (id != null && seen.Add(id.IntegerValue)) yield return id;
        }

        public static List<long> Values(IEnumerable<ElementId> ids) => ids.Select(i => (long)i.IntegerValue).ToList();
    }
}
