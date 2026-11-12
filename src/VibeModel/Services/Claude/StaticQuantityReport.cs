using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// The `-- quantities:` line (interview Ruben Wolbers, 2026-11-12): a run created a project parameter
    /// and filled it with numbers — a quantity as a fixed value that does not follow the model. Only
    /// parameters NEW in the run count, so every value on them was written by this run (no pre-read, no
    /// false alarm from elements Revit merely regenerated). Pure wording; the Revit side is
    /// <see cref="StaticQuantityCheck"/>. Unit-tested.
    /// </summary>
    internal static class StaticQuantityReport
    {
        public sealed class Entry
        {
            public string Parameter;
            /// <summary>Elements (or types) that got a non-zero value in the run.</summary>
            public int Elements;
            /// <summary>0 = every changed element was read; else the read cap it stopped at (there may be more).</summary>
            public int StoppedAfter;
            public bool AtLeast => StoppedAfter > 0;
            /// <summary>Length, Area, Volume or Number.</summary>
            public string Spec;
        }

        private const string Advice = "they do not follow model changes — use a schedule field, a takeoff or a family formula for quantities that must stay current";

        /// <summary>One line, or null when nothing was filled.</summary>
        public static string Line(IList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return null;
            if (entries.Count == 1)
            {
                var e = entries[0];
                return "-- quantities: parameter '" + e.Parameter + "' (" + Describe(e.Spec) + "new this run) was filled with fixed numbers on " +
                       Count(e) + "; " + Advice;
            }
            return "-- quantities: new parameters " + string.Join(", ", entries.Select(e => "'" + e.Parameter + "' (" + Describe(e.Spec) + Count(e) + ")")) +
                   " were filled with fixed numbers this run; " + Advice;
        }

        /// <summary>The additive JSON array (<c>quantities</c>), or null when nothing was filled.</summary>
        public static List<object> Json(IList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return null;
            return entries.Select(e => (object)new Dictionary<string, object>
            {
                { "parameter", e.Parameter },
                { "newThisRun", true },
                { "elements", e.Elements },
                { "atLeast", e.AtLeast },
                { "spec", e.Spec },
            }).ToList();
        }

        private static string Describe(string spec) => string.IsNullOrEmpty(spec) ? "" : spec + ", ";

        private static string Count(Entry e)
            => (e.AtLeast ? "at least " : "") + e.Elements + (e.Elements == 1 ? " element" : " elements") +
               (e.AtLeast ? " (stopped reading after " + e.StoppedAfter + " changed elements)" : "");
    }
}
