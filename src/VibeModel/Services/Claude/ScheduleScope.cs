using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    // Feedback 2026-11-12 (a modeller): the chat modelled the bottom rebar on request, but the
    // reinforcement overview it had made before kept showing only the top rebar — and nothing in
    // any response said so. `schedule` now states its scope (how many elements of its category it
    // shows and why the rest is outside), and a modifying command that adds elements a schedule or a
    // sheet view of this session leaves out says so in one line. Pure wording — unit-tested; the Revit
    // side is DerivedCoverage.cs.

    /// <summary>What a schedule shows of its category, and why the rest is outside (`Scope:` line).</summary>
    internal sealed class ScheduleScopeSummary
    {
        /// <summary>Elements the schedule lists (Revit's own view-scoped collector on the schedule).</summary>
        public int InSchedule;
        /// <summary>Data rows of the table as read back (a grand-total row included) — not the element count when grouped.</summary>
        public int Rows;
        /// <summary>Instances of the category in this document (links not counted; an area schedule: its area scheme).</summary>
        public int TotalInCategory;
        public string Category;
        /// <summary>The area scheme an area schedule lists, else null.</summary>
        public string AreaScheme;
        /// <summary>Outside elements the schedule's phase / phase filter hides, and those in a non-primary design option.</summary>
        public int OutByPhase, OutByDesignOption;
        /// <summary>The schedule's phase and phase filter names, for the phase part of the line.</summary>
        public string PhaseName, PhaseFilterName;
        /// <summary>"Partition = 'Boven'", "Length > 12000 mm" — Revit's filters, never re-evaluated by us.</summary>
        public List<string> FilterDescriptions = new List<string>();
        /// <summary>The schedule is set to "Filter by sheet" (Revit 2023+).</summary>
        public bool FilteredBySheet;
        public bool IncludesLinks;
        /// <summary>The schedule has an embedded schedule: only the parent category is counted.</summary>
        public bool Embedded;
        /// <summary>Above the reason cap: counted, but the outside elements were not classified.</summary>
        public bool ReasonsSkipped;
        /// <summary>Not computed at all: "multi-category schedule", "key schedule", "material takeoff", "category unknown".</summary>
        public string SkippedReason;

        public const int ReasonCap = 20000;

        public int Outside => Math.Max(0, TotalInCategory - InSchedule);

        /// <summary>
        /// Outside elements that neither the phase nor a design option explains. What is left can only
        /// be put down to the schedule's filters or "filter by sheet" as a whole — never per element.
        /// </summary>
        public int Rest => ReasonsSkipped ? Outside : Math.Max(0, Outside - OutByPhase - OutByDesignOption);

        /// <summary>"filtersOrSheet" | "filters" | "sheet" | "unknown" — what the rest can be put down to.</summary>
        public string RestCause =>
            FilterDescriptions.Count > 0 && FilteredBySheet ? "filtersOrSheet"
            : FilterDescriptions.Count > 0 ? "filters"
            : FilteredBySheet ? "sheet"
            : "unknown";

        public string ScopeLine()
        {
            if (SkippedReason != null) return "Scope: not computed (" + SkippedReason + ").";
            var cat = (string.IsNullOrEmpty(Category) ? "the category" : Category) +
                      (string.IsNullOrEmpty(AreaScheme) ? "" : " in area scheme '" + AreaScheme + "'");
            string rows = " (" + Count(Rows, "row") + ")";
            string tail = (IncludesLinks ? "; linked elements shown but not counted" : "") +
                          (Embedded ? "; embedded schedule: only the parent category counted" : "") + ".";
            if (Outside == 0)
                return "Scope: shows all " + Count(InSchedule, "element") + " of " + cat + rows + tail;

            var line = "Scope: shows " + ScopeText.N(InSchedule) + " of " + ScopeText.N(TotalInCategory) + " elements of " + cat + rows + "; outside: ";
            if (ReasonsSkipped)
                return line + ScopeText.N(Outside) + " (reasons skipped above " + ScopeText.N(ReasonCap) + " elements)" + Filters() + tail;

            var parts = new List<string>();
            if (OutByPhase > 0)
            {
                var detail = new List<string>();
                if (!string.IsNullOrEmpty(PhaseName)) detail.Add("schedule phase '" + PhaseName + "'");
                if (!string.IsNullOrEmpty(PhaseFilterName)) detail.Add("filter '" + PhaseFilterName + "'");
                parts.Add(ScopeText.N(OutByPhase) + " phase" + (detail.Count > 0 ? " (" + string.Join(", ", detail) + ")" : ""));
            }
            if (OutByDesignOption > 0) parts.Add(ScopeText.N(OutByDesignOption) + " design option (not primary)");
            if (Rest > 0) parts.Add(ScopeText.N(Rest) + RestText());
            return line + string.Join(", ", parts) + Filters() + tail;
        }

        private string RestText()
        {
            switch (RestCause)
            {
                case "filtersOrSheet": return " by the schedule filter(s) or filter by sheet";
                case "filters": return " by the schedule filter(s)";
                case "sheet": return " not on the sheet's views (filtered by sheet)";
                default: return " not explained by phase or design option";
            }
        }

        private string Filters() =>
            FilterDescriptions.Count == 0 ? "" : " — schedule filter(s): " + string.Join("; ", FilterDescriptions);

        private static string Count(int n, string noun) => ScopeText.N(n) + " " + noun + (n == 1 ? "" : "s");

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            if (SkippedReason != null)
            {
                d["skipped"] = SkippedReason;
                return d;
            }
            d["inSchedule"] = InSchedule;
            d["rows"] = Rows;
            d["totalInCategory"] = TotalInCategory;
            if (AreaScheme != null) d["areaScheme"] = AreaScheme;
            var outside = new Dictionary<string, object> { { "total", Outside } };
            if (!ReasonsSkipped)
            {
                outside["phase"] = OutByPhase;
                outside["designOption"] = OutByDesignOption;
            }
            outside["rest"] = Rest;
            outside["restCause"] = ReasonsSkipped ? "skipped" : RestCause;
            d["outside"] = outside;
            d["filters"] = FilterDescriptions.Cast<object>().ToList();
            d["filteredBySheet"] = FilteredBySheet;
            d["includesLinks"] = IncludesLinks;
            if (Embedded) d["embedded"] = true;
            return d;
        }
    }

    /// <summary>New elements a schedule of their category does not list.</summary>
    internal sealed class ScheduleMiss
    {
        public string Schedule;
        public int Id;
        public string Category;
        public int Missing;
        public List<long> Ids = new List<long>();
    }

    /// <summary>New elements a view VibeModel put on a sheet this session does not show.</summary>
    internal sealed class ViewMiss
    {
        public const string Obscured = "obscured", NotVisible = "notVisible";
        public string View;
        public int Id;
        /// <summary>"0003 - Wapening" (the sheet label), or null.</summary>
        public string Sheet;
        public string Category;
        public int Missing;
        /// <summary><see cref="Obscured"/> (rebar hidden inside its host) or <see cref="NotVisible"/>.</summary>
        public string Cause;
        public List<long> Ids = new List<long>();
    }

    /// <summary>The `-- schedules:` / `-- views:` lines after a modifying command (and their JSON).</summary>
    internal sealed class CoverageReport
    {
        public const int MaxShown = 5, MaxIds = 20;
        public List<ScheduleMiss> Schedules = new List<ScheduleMiss>();
        public List<ViewMiss> Views = new List<ViewMiss>();
        /// <summary>Schedules / views over the count cap or the time budget, not checked.</summary>
        public int SchedulesNotChecked, ViewsNotChecked;

        /// <summary>Nothing to say: no misses, and nothing left unchecked by the caps.</summary>
        public bool IsEmpty => Schedules.Count == 0 && Views.Count == 0 && SchedulesNotChecked == 0 && ViewsNotChecked == 0;

        /// <summary>
        /// "-- schedules: 12 new Structural Rebar not in 'Wapeningsstaat' (schedule 4711 --rows for why); …",
        /// null when every new element is in every checked schedule.
        /// </summary>
        public string SchedulesLine()
        {
            if (Schedules.Count == 0 && SchedulesNotChecked == 0) return null;
            if (Schedules.Count == 0)
                return "-- schedules: every checked schedule lists the new elements; " + ScopeText.N(SchedulesNotChecked) +
                       " more schedules of these categories not checked";
            var parts = Schedules.Take(MaxShown).Select(s =>
                ScopeText.N(s.Missing) + " new " + s.Category + " not in '" + s.Schedule + "' (schedule " + ScopeText.N(s.Id) + " --rows for why)").ToList();
            if (Schedules.Count > MaxShown) parts.Add("+" + ScopeText.N(Schedules.Count - MaxShown) + " more schedules");
            if (SchedulesNotChecked > 0) parts.Add(ScopeText.N(SchedulesNotChecked) + " more schedules not checked");
            return "-- schedules: " + string.Join("; ", parts);
        }

        /// <summary>
        /// "-- views: 12 new Structural Rebar hidden inside their host in 'Wapening boven' (sheet 0003 - Wapening), where the other
        /// bars are unobscured — exec: rebar.SetUnobscuredInView(view, true)"; null when nothing is missing.
        /// </summary>
        public string ViewsLine()
        {
            if (Views.Count == 0 && ViewsNotChecked == 0) return null;
            if (Views.Count == 0)
                return "-- views: no checked view leaves the new elements out; " + ScopeText.N(ViewsNotChecked) + " more views not checked";
            var parts = Views.Take(MaxShown).Select(v =>
            {
                var where = "'" + v.View + "'" + (string.IsNullOrEmpty(v.Sheet) ? "" : " (sheet " + v.Sheet + ")");
                if (v.Cause == ViewMiss.Obscured)
                    return ScopeText.N(v.Missing) + " new " + v.Category + " hidden inside their host in " + where +
                           ", where the other bars are unobscured — exec: rebar.SetUnobscuredInView(view, true)";
                var first = v.Ids.Count > 0 ? v.Ids[0].ToString(CultureInfo.InvariantCulture) : "<id>";
                return ScopeText.N(v.Missing) + " new " + v.Category + " not visible in " + where + " — whyhidden " + first + " " + ScopeText.N(v.Id);
            }).ToList();
            if (Views.Count > MaxShown) parts.Add("+" + ScopeText.N(Views.Count - MaxShown) + " more");
            if (ViewsNotChecked > 0) parts.Add(ScopeText.N(ViewsNotChecked) + " more views not checked");
            return "-- views: " + string.Join("; ", parts);
        }

        public List<object> SchedulesJson() => Schedules.Select(s => (object)new Dictionary<string, object>
        {
            { "schedule", s.Schedule }, { "id", s.Id }, { "category", s.Category }, { "missing", s.Missing },
            { "ids", s.Ids.Take(MaxIds).Cast<object>().ToList() }
        }).ToList();

        public List<object> ViewsJson() => Views.Select(v => (object)new Dictionary<string, object>
        {
            { "view", v.View }, { "id", v.Id }, { "sheet", v.Sheet }, { "category", v.Category }, { "missing", v.Missing },
            { "cause", v.Cause }, { "ids", v.Ids.Take(MaxIds).Cast<object>().ToList() }
        }).ToList();

        /// <summary>The text lines (before the stamp), in the order schedules, views.</summary>
        public IEnumerable<string> TextLines()
        {
            var s = SchedulesLine();
            if (s != null) yield return s;
            var v = ViewsLine();
            if (v != null) yield return v;
        }

        /// <summary>The additive JSON keys "schedules" / "views" on a response envelope.</summary>
        public void AddJson(IDictionary<string, object> envelope)
        {
            if (Schedules.Count > 0) envelope["schedules"] = SchedulesJson();
            if (Views.Count > 0) envelope["views"] = ViewsJson();
            if (SchedulesNotChecked > 0) envelope["schedulesNotChecked"] = SchedulesNotChecked;
            if (ViewsNotChecked > 0) envelope["viewsNotChecked"] = ViewsNotChecked;
        }
    }

    /// <summary>Axis-aligned box overlap for the `-- views:` noise guard (pure).</summary>
    internal static class CoverageGeometry
    {
        /// <summary>
        /// True when box A, shrunk by <paramref name="shrink"/> on every side, still overlaps box B, in
        /// as many axes as the arrays have (the view's drawing plane, or 3 for a 3D view). A box that only
        /// touches B — an element just beyond the edge of what the view draws — does not count.
        /// </summary>
        public static bool Overlaps(double[] aMin, double[] aMax, double[] bMin, double[] bMax, double shrink)
        {
            for (int i = 0; i < aMin.Length; i++)
            {
                double lo = aMin[i] + shrink, hi = aMax[i] - shrink;
                if (lo > hi) lo = hi = (aMin[i] + aMax[i]) / 2; // thinner than twice the shrink: its middle
                if (hi < bMin[i] || lo > bMax[i]) return false;
            }
            return true;
        }
    }

    /// <summary>Wording of Revit's schedule filters for the scope line (pure parts).</summary>
    internal static class ScheduleFilterText
    {
        /// <summary>"Partition = 'Boven'", "Length > 12000 mm", "Comments has a value".</summary>
        public static string Describe(string field, string filterType, string value)
        {
            var op = Op(filterType);
            if (filterType == "HasValue" || filterType == "HasNoValue" || filterType == "HasParameter")
                return field + " " + op;
            return field + " " + op + (string.IsNullOrEmpty(value) ? " ''" : " " + value);
        }

        /// <summary>Revit's ScheduleFilterType name → a readable operator.</summary>
        public static string Op(string filterType)
        {
            switch (filterType)
            {
                case "Equal": return "=";
                case "NotEqual": return "!=";
                case "GreaterThan": return ">";
                case "GreaterThanOrEqual": return ">=";
                case "LessThan": return "<";
                case "LessThanOrEqual": return "<=";
                case "Contains": return "contains";
                case "NotContains": return "does not contain";
                case "BeginsWith": return "begins with";
                case "NotBeginsWith": return "does not begin with";
                case "EndsWith": return "ends with";
                case "NotEndsWith": return "does not end with";
                case "HasValue": return "has a value";
                case "HasNoValue": return "has no value";
                case "HasParameter": return "has the parameter";
                case "IsAssociatedWithGlobalParameter": return "is associated with global parameter";
                case "IsNotAssociatedWithGlobalParameter": return "is not associated with global parameter";
                default: return string.IsNullOrEmpty(filterType) ? "?" : filterType;
            }
        }
    }

    internal static class ScopeText
    {
        public static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    }
}
