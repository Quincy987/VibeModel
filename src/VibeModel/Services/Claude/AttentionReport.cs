using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Phase 2 (pure): the shared drawing work a run changed that the user did not necessarily ask
    /// for — a sheet renamed or renumbered, a view template taken off or swapped, a filter added to or
    /// taken off views, a filter element itself changed. Named in ONE line per action
    /// (<c>-- attention: …</c>), never asked about (the user's decision: no window in auto, an overview).
    /// The Revit side (<see cref="AttentionGuard"/>) reads the facts before and after; this compares
    /// values for the ids that existed before, so it needs no list of commands.
    /// </summary>
    public static class AttentionReport
    {
        public sealed class SheetFact
        {
            public string Number;
            public string Name;
        }

        public sealed class ViewFact
        {
            public string Name;
            public bool IsTemplate;
            /// <summary>The view template applied (0 = none).</summary>
            public long TemplateId;
            /// <summary>The filters applied, sorted.</summary>
            public List<long> FilterIds = new List<long>();
        }

        /// <summary>What a document's sheets, views and filters looked like at one moment.</summary>
        public sealed class Snapshot
        {
            public Dictionary<long, SheetFact> Sheets = new Dictionary<long, SheetFact>();
            public Dictionary<long, ViewFact> Views = new Dictionary<long, ViewFact>();
            /// <summary>Filter elements: id → name.</summary>
            public Dictionary<long, string> Filters = new Dictionary<long, string>();
            /// <summary>Views whose template/filters were not read (the cap): only their ids, so a run that touches one says so.</summary>
            public HashSet<long> UnreadViews = new HashSet<long>();

            /// <summary>
            /// True when the run touched anything this snapshot describes (a sheet, view or filter it
            /// modified or deleted): only then is the document read again. A changed sheet name, template or
            /// filter list always marks its sheet or view modified; a filter's rules mark the filter.
            /// </summary>
            public bool Touches(IEnumerable<long> modifiedOrDeleted)
                => (modifiedOrDeleted ?? Enumerable.Empty<long>()).Any(id =>
                    Sheets.ContainsKey(id) || Views.ContainsKey(id) || Filters.ContainsKey(id) || UnreadViews.Contains(id));

            /// <summary>How many views the run touched that were not read (they are named as not checked).</summary>
            public int UnreadTouched(IEnumerable<long> modifiedOrDeleted)
                => (modifiedOrDeleted ?? Enumerable.Empty<long>()).Distinct().Count(UnreadViews.Contains);
        }

        public sealed class Item
        {
            public string Kind;   // sheet | template | filter
            public string Text;
        }

        /// <summary>Views whose template and filters are read at most (the rest only by id, named when the run changed them).</summary>
        public const int MaxViews = 3000;

        private const int MaxNamed = 3;
        private const int MaxItems = 6;

        /// <summary>
        /// What changed between <paramref name="before"/> and <paramref name="after"/>, for the ids that
        /// existed before. <paramref name="modifiedIds"/> = the run's modified elements (a filter
        /// element's rules are not compared by value: Revit marks it modified when they change).
        /// </summary>
        public static List<Item> Compare(Snapshot before, Snapshot after, ICollection<long> modifiedIds)
        {
            var items = new List<Item>();
            if (before == null || after == null) return items;
            var modified = new HashSet<long>(modifiedIds ?? new long[0]);

            foreach (var kv in before.Sheets)
            {
                var old = kv.Value;
                if (!after.Sheets.TryGetValue(kv.Key, out var now))
                {
                    items.Add(new Item { Kind = "sheet", Text = "sheet " + old.Number + " '" + old.Name + "' deleted" });
                    continue;
                }
                if (old.Number != now.Number && old.Name != now.Name)
                    items.Add(new Item { Kind = "sheet", Text = "sheet " + old.Number + " '" + old.Name + "' is now " + now.Number + " '" + now.Name + "'" });
                else if (old.Number != now.Number)
                    items.Add(new Item { Kind = "sheet", Text = "sheet " + old.Number + " renumbered to " + now.Number + " ('" + now.Name + "')" });
                else if (old.Name != now.Name)
                    items.Add(new Item { Kind = "sheet", Text = "sheet " + old.Number + " renamed '" + old.Name + "' → '" + now.Name + "'" });
            }

            // View templates taken off / swapped, grouped by the old template.
            var removedFrom = new Dictionary<long, List<string>>();
            var swapped = new List<string>();
            var filtersAdded = new Dictionary<long, List<string>>();
            var filtersRemoved = new Dictionary<long, List<string>>();
            foreach (var kv in before.Views)
            {
                if (!after.Views.TryGetValue(kv.Key, out var now)) continue;   // a deleted view is in the change line
                var old = kv.Value;
                if (old.TemplateId != now.TemplateId)
                {
                    if (old.TemplateId > 0 && now.TemplateId <= 0) Add(removedFrom, old.TemplateId, old.Name);
                    else if (old.TemplateId > 0)
                        swapped.Add("'" + old.Name + "' " + TemplateName(before, old.TemplateId) + " → " + TemplateName(after, now.TemplateId));
                    else
                        swapped.Add("'" + old.Name + "' got view template " + TemplateName(after, now.TemplateId));
                }
                // A view whose template changed shows the new template's filters: the template line says it all.
                if (old.TemplateId != now.TemplateId) continue;
                foreach (var f in now.FilterIds.Except(old.FilterIds)) Add(filtersAdded, f, old.Name);
                foreach (var f in old.FilterIds.Except(now.FilterIds)) Add(filtersRemoved, f, old.Name);
            }
            foreach (var kv in removedFrom)
                items.Add(new Item { Kind = "template", Text = "view template " + TemplateName(before, kv.Key) + " removed from " + Views(kv.Value) });
            if (swapped.Count > 0)
                items.Add(new Item { Kind = "template", Text = "view template changed on " + Count(swapped.Count, "view") + " (" + Names(swapped, quoted: false) + ")" });
            foreach (var kv in filtersAdded)
                items.Add(new Item { Kind = "filter", Text = "filter " + FilterName(before, after, kv.Key) + " added to " + Views(kv.Value) });
            foreach (var kv in filtersRemoved)
                items.Add(new Item { Kind = "filter", Text = "filter " + FilterName(before, after, kv.Key) + " removed from " + Views(kv.Value) });

            foreach (var kv in before.Filters)
            {
                if (!after.Filters.TryGetValue(kv.Key, out var name))
                    items.Add(new Item { Kind = "filter", Text = "filter '" + kv.Value + "' deleted" });
                else if (name != kv.Value)
                    items.Add(new Item { Kind = "filter", Text = "filter '" + kv.Value + "' renamed to '" + name + "'" });
                else if (modified.Contains(kv.Key))
                    items.Add(new Item { Kind = "filter", Text = "filter '" + kv.Value + "' changed" });
            }
            return items;
        }

        /// <summary>
        /// "-- attention: sheet 0001 renamed 'A' → 'B'; view template 'X' removed from 3 views ('L1', 'L2', 'L3')";
        /// null when nothing changed and nothing was left unchecked. <paramref name="notRead"/> = views the run changed that the cap left unread.
        /// </summary>
        public static string Line(IList<Item> items, int notRead = 0, bool preview = false)
        {
            var parts = (items ?? new List<Item>()).Select(i => i.Text).ToList();
            if (parts.Count == 0 && notRead <= 0) return null;
            var shown = parts.Take(MaxItems).ToList();
            if (parts.Count > MaxItems) shown.Add("+" + (parts.Count - MaxItems).ToString(CultureInfo.InvariantCulture) + " more");
            if (notRead > 0)
                shown.Add(Count(notRead, "changed view") + " not checked for template/filter changes (more than " +
                          MaxViews.ToString(CultureInfo.InvariantCulture) + " views in the model)");
            return (preview ? "-- preview: attention: " : "-- attention: ") + string.Join("; ", shown);
        }

        /// <summary>The additive JSON <c>attention</c> array: <c>[{kind, text}]</c>.</summary>
        public static List<object> Json(IList<Item> items, int notRead = 0)
        {
            var list = (items ?? new List<Item>()).Select(i => (object)new Dictionary<string, object> { { "kind", i.Kind }, { "text", i.Text } }).ToList();
            if (notRead > 0) list.Add(new Dictionary<string, object> { { "kind", "notChecked" }, { "views", notRead } });
            return list;
        }

        private static void Add(Dictionary<long, List<string>> map, long key, string view)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            list.Add(view);
        }

        private static string TemplateName(Snapshot s, long id)
            => id > 0 && s.Views.TryGetValue(id, out var v) ? "'" + v.Name + "'" : id > 0 ? "#" + id.ToString(CultureInfo.InvariantCulture) : "none";

        private static string FilterName(Snapshot before, Snapshot after, long id)
            => before.Filters.TryGetValue(id, out var n) || after.Filters.TryGetValue(id, out n) ? "'" + n + "'" : "#" + id.ToString(CultureInfo.InvariantCulture);

        private static string Views(List<string> names) => Count(names.Count, "view") + " (" + Names(names, quoted: true) + ")";

        private static string Names(List<string> names, bool quoted)
        {
            var shown = string.Join(", ", names.Take(MaxNamed).Select(n => quoted ? "'" + n + "'" : n));
            return names.Count > MaxNamed ? shown + " +" + (names.Count - MaxNamed).ToString(CultureInfo.InvariantCulture) + " more" : shown;
        }

        private static string Count(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
    }
}
