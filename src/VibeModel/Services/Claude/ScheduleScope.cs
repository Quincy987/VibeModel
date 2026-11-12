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
        /// <summary>Body rows of the table — differs from InSchedule when grouped (not itemized).</summary>
        public int Rows;
        /// <summary>Instances of the category in this document (links not counted).</summary>
        public int TotalInCategory;
        public string Category;
        public int OutByPhase, OutByDesignOption, OutBySheetFilter, OutUnexplained;
        /// <summary>The schedule's phase and phase filter names, for the phase part of the line.</summary>
        public string PhaseName, PhaseFilterName;
        /// <summary>"Partition = Boven", "Length > 12000 mm" — Revit's filters, never re-evaluated by us.</summary>
        public List<string> FilterDescriptions = new List<string>();
        public bool IncludesLinks;
        /// <summary>Embedded schedule: only the parent category is counted.</summary>
        public bool Embedded;
        /// <summary>Above the reason cap: counted, but the outside elements were not classified.</summary>
        public bool ReasonsSkipped;
        /// <summary>Not computed at all: "multi-category schedule", "key schedule", "material takeoff", "category unknown".</summary>
        public string SkippedReason;

        public const int ReasonCap = 20000;

        public int Outside => Math.Max(0, TotalInCategory - InSchedule);

        public string ScopeLine()
        {
            if (SkippedReason != null) return "Scope: not computed (" + SkippedReason + ").";
            var cat = string.IsNullOrEmpty(Category) ? "the category" : Category;
            string rows = " (" + Count(Rows, "row") + ")";
            string links = IncludesLinks ? "; linked elements shown but not counted" : "";
            string embedded = Embedded ? "; embedded schedule: only the parent category counted" : "";
            if (Outside == 0)
                return "Scope: shows all " + Count(InSchedule, "element") + " of " + cat + rows + links + embedded + ".";

            var line = "Scope: shows " + InSchedule.ToString(CultureInfo.InvariantCulture) + " of " +
                       TotalInCategory.ToString(CultureInfo.InvariantCulture) + " elements of " + cat + rows +
                       "; outside: ";
            if (ReasonsSkipped)
                return line + Outside.ToString(CultureInfo.InvariantCulture) + " (reasons skipped above " +
                       ReasonCap.ToString(CultureInfo.InvariantCulture) + " elements)" + Filters() + links + embedded + ".";

            var parts = new List<string>();
            if (OutByPhase > 0)
            {
                var detail = new List<string>();
                if (!string.IsNullOrEmpty(PhaseName)) detail.Add("schedule phase '" + PhaseName + "'");
                if (!string.IsNullOrEmpty(PhaseFilterName)) detail.Add("filter '" + PhaseFilterName + "'");
                parts.Add(OutByPhase.ToString(CultureInfo.InvariantCulture) + " phase" + (detail.Count > 0 ? " (" + string.Join(", ", detail) + ")" : ""));
            }
            if (OutByDesignOption > 0) parts.Add(OutByDesignOption.ToString(CultureInfo.InvariantCulture) + " design option (not primary)");
            if (OutBySheetFilter > 0) parts.Add(OutBySheetFilter.ToString(CultureInfo.InvariantCulture) + " not on the sheet's views (filtered by sheet)");
            if (OutUnexplained > 0)
                parts.Add(OutUnexplained.ToString(CultureInfo.InvariantCulture) +
                          (FilterDescriptions.Count > 0 ? " by the schedule filter(s)" : " not explained by phase/option/sheet"));
            // Counted outside but every per-element test passed it: the residual still has to show.
            int named = OutByPhase + OutByDesignOption + OutBySheetFilter + OutUnexplained;
            if (named < Outside) parts.Add((Outside - named).ToString(CultureInfo.InvariantCulture) + " not explained");
            return line + string.Join(", ", parts) + Filters() + links + embedded + ".";
        }

        private string Filters() =>
            FilterDescriptions.Count == 0 ? "" : " — schedule filter(s): " + string.Join("; ", FilterDescriptions);

        private static string Count(int n, string noun) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

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
            d["outside"] = new Dictionary<string, object>
            {
                { "total", Outside },
                { "phase", OutByPhase },
                { "designOption", OutByDesignOption },
                { "sheet", OutBySheetFilter },
                { "unexplained", Math.Max(0, Outside - OutByPhase - OutByDesignOption - OutBySheetFilter) }
            };
            d["filters"] = FilterDescriptions.Cast<object>().ToList();
            d["includesLinks"] = IncludesLinks;
            if (Embedded) d["embedded"] = true;
            if (ReasonsSkipped) d["reasonsSkipped"] = true;
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
        /// <summary>Schedules / views of the touched categories that were over the cap and not checked.</summary>
        public int SchedulesNotChecked, ViewsNotChecked;

        public bool IsEmpty => Schedules.Count == 0 && Views.Count == 0;

        /// <summary>
        /// "-- schedules: 12 new Structural Rebar not in 'Wapeningsstaat' (schedule 'Wapeningsstaat' --rows for why); …",
        /// null when every new element is in every checked schedule.
        /// </summary>
        public string SchedulesLine()
        {
            if (Schedules.Count == 0) return null;
            var parts = Schedules.Take(MaxShown).Select(s =>
                N(s.Missing) + " new " + s.Category + " not in '" + s.Schedule + "' (schedule " + s.Id.ToString(CultureInfo.InvariantCulture) + " --rows for why)").ToList();
            if (Schedules.Count > MaxShown) parts.Add("+" + N(Schedules.Count - MaxShown) + " more schedules");
            if (SchedulesNotChecked > 0) parts.Add(N(SchedulesNotChecked) + " more schedules not checked");
            return "-- schedules: " + string.Join("; ", parts);
        }

        /// <summary>
        /// "-- views: 12 new Structural Rebar hidden inside their host in 'Wapening boven' (sheet 0003) — exec: rebar.SetUnobscuredInView(view, true)",
        /// null when nothing is missing.
        /// </summary>
        public string ViewsLine()
        {
            if (Views.Count == 0) return null;
            var parts = Views.Take(MaxShown).Select(v =>
            {
                var where = "'" + v.View + "'" + (string.IsNullOrEmpty(v.Sheet) ? "" : " (sheet " + v.Sheet + ")");
                if (v.Cause == ViewMiss.Obscured)
                    return N(v.Missing) + " new " + v.Category + " hidden inside their host in " + where +
                           ", where the other bars are unobscured — exec: rebar.SetUnobscuredInView(view, true)";
                var first = v.Ids.Count > 0 ? v.Ids[0].ToString(CultureInfo.InvariantCulture) : "<id>";
                return N(v.Missing) + " new " + v.Category + " not visible in " + where +
                       " — whyhidden " + first + " " + v.Id.ToString(CultureInfo.InvariantCulture);
            }).ToList();
            if (Views.Count > MaxShown) parts.Add("+" + N(Views.Count - MaxShown) + " more views");
            if (ViewsNotChecked > 0) parts.Add(N(ViewsNotChecked) + " more views not checked");
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

        /// <summary>Adds the text lines (before the stamp) — in the order schedules, views.</summary>
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
        }

        private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Wording of Revit's schedule filters for the scope line (pure parts).</summary>
    internal static class ScheduleFilterText
    {
        /// <summary>"Partition = Boven", "Length > 12000 mm", "Comments has a value".</summary>
        public static string Describe(string field, string filterType, string value)
        {
            var op = Op(filterType);
            if (filterType == "HasValue" || filterType == "HasNoValue")
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
}
