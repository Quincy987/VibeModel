using System.Collections.Generic;
using VibeModel.Markdown;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Feedback 2026-11-12: the schedule's scope line, the `-- schedules:` / `-- views:` lines after a
    /// modifying command and the rule that makes the chat act on them.
    /// </summary>
    public class ScheduleScopeTests
    {
        private static ScheduleScopeSummary Scope(int inSchedule, int total, int rows) =>
            new ScheduleScopeSummary { InSchedule = inSchedule, TotalInCategory = total, Rows = rows, Category = "Structural Rebar" };

        [Fact]
        public void ScopeLine_AllIn()
        {
            Assert.Equal("Scope: shows all 22 elements of Structural Rebar (22 rows).", Scope(22, 22, 22).ScopeLine());
        }

        [Fact]
        public void ScopeLine_SingularCounts()
        {
            Assert.Equal("Scope: shows all 1 element of Structural Rebar (1 row).", Scope(1, 1, 1).ScopeLine());
        }

        [Fact]
        public void ScopeLine_NamesEveryReasonAndTheFilters()
        {
            var s = Scope(14, 22, 14);
            s.OutByPhase = 5;
            s.PhaseName = "Nieuw";
            s.PhaseFilterName = "Show Complete";
            s.OutByDesignOption = 2;
            s.FilterDescriptions.Add("Partition = 'Boven'");
            Assert.Equal("Scope: shows 14 of 22 elements of Structural Rebar (14 rows); outside: 5 phase (schedule phase 'Nieuw', " +
                         "filter 'Show Complete'), 2 design option (not primary), 1 by the schedule filter(s) — schedule filter(s): Partition = 'Boven'.",
                s.ScopeLine());
            Assert.Equal("filters", s.RestCause);
        }

        [Fact]
        public void ScopeLine_RestWithoutFiltersOrSheet_IsUnexplained()
        {
            var s = Scope(3, 4, 3);
            Assert.EndsWith("outside: 1 not explained by phase or design option.", s.ScopeLine());
            Assert.Equal("unknown", s.RestCause);
        }

        [Fact]
        public void ScopeLine_EmptySchedule_ExplainsItself()
        {
            var s = Scope(0, 6, 0);
            s.OutByPhase = 6;
            s.PhaseName = "Bestaand";
            Assert.Equal("Scope: shows 0 of 6 elements of Structural Rebar (0 rows); outside: 6 phase (schedule phase 'Bestaand').", s.ScopeLine());
        }

        [Fact]
        public void ScopeLine_GroupedRowsDifferFromElements()
        {
            Assert.Equal("Scope: shows all 40 elements of Structural Rebar (3 rows).", Scope(40, 40, 3).ScopeLine());
        }

        [Fact]
        public void ScopeLine_SheetFilterOnly_LinksAndEmbedded()
        {
            var s = Scope(2, 5, 2);
            s.FilteredBySheet = true;
            s.IncludesLinks = true;
            s.Embedded = true;
            Assert.Equal("Scope: shows 2 of 5 elements of Structural Rebar (2 rows); outside: 3 not on the sheet's views (filtered by sheet)" +
                         "; linked elements shown but not counted; embedded schedule: only the parent category counted.", s.ScopeLine());
        }

        [Fact]
        public void ScopeLine_FiltersAndSheet_NeverClaimsOne()
        {
            // The rest is not re-evaluated, so with both set neither is named as the cause.
            var s = Scope(2, 5, 2);
            s.FilteredBySheet = true;
            s.FilterDescriptions.Add("Mark = 'K1'");
            Assert.Contains("3 by the schedule filter(s) or filter by sheet", s.ScopeLine());
            Assert.Equal("filtersOrSheet", s.RestCause);
        }

        [Fact]
        public void ScopeLine_PhaseAndRest_AddUpToTheOutsideCount()
        {
            var s = Scope(10, 13, 10);
            s.OutByPhase = 1;
            Assert.EndsWith("outside: 1 phase, 2 not explained by phase or design option.", s.ScopeLine());
        }

        [Fact]
        public void Rest_NeverNegative_WhenReasonsOutnumberTheGap()
        {
            var s = Scope(10, 12, 10);
            s.OutByPhase = 5;
            Assert.Equal(0, s.Rest);
            Assert.DoesNotContain("not explained", s.ScopeLine());
        }

        [Fact]
        public void ScopeLine_AreaSchedule_NamesItsScheme()
        {
            var s = new ScheduleScopeSummary { InSchedule = 4, TotalInCategory = 4, Rows = 4, Category = "Areas", AreaScheme = "Gross Building" };
            Assert.Equal("Scope: shows all 4 elements of Areas in area scheme 'Gross Building' (4 rows).", s.ScopeLine());
            Assert.Equal("Gross Building", s.ToJson()["areaScheme"]);
        }

        [Fact]
        public void ScopeLine_ReasonsSkipped_AboveTheCap()
        {
            var s = Scope(5, 30005, 5);
            s.ReasonsSkipped = true;
            Assert.Equal("Scope: shows 5 of 30005 elements of Structural Rebar (5 rows); outside: 30000 (reasons skipped above 20000 elements).", s.ScopeLine());
            var outside = (Dictionary<string, object>)s.ToJson()["outside"];
            Assert.Equal("skipped", outside["restCause"]);
            Assert.False(outside.ContainsKey("phase"));
        }

        [Theory]
        [InlineData("multi-category schedule")]
        [InlineData("key schedule")]
        [InlineData("material takeoff")]
        public void ScopeLine_SkippedKinds(string reason)
        {
            var s = new ScheduleScopeSummary { SkippedReason = reason };
            Assert.Equal("Scope: not computed (" + reason + ").", s.ScopeLine());
            Assert.Equal(reason, s.ToJson()["skipped"]);
        }

        [Fact]
        public void ScopeJson_HasCountsPerReason()
        {
            var s = Scope(14, 22, 12);
            s.OutByPhase = 5;
            s.OutByDesignOption = 2;
            s.FilterDescriptions.Add("Length > 12000 mm");
            var json = s.ToJson();
            Assert.Equal(14, json["inSchedule"]);
            Assert.Equal(12, json["rows"]);
            Assert.Equal(22, json["totalInCategory"]);
            var outside = (Dictionary<string, object>)json["outside"];
            Assert.Equal(8, outside["total"]);
            Assert.Equal(5, outside["phase"]);
            Assert.Equal(2, outside["designOption"]);
            Assert.Equal(1, outside["rest"]);
            Assert.Equal("filters", outside["restCause"]);
            Assert.Single((List<object>)json["filters"]);
            Assert.Equal(false, json["filteredBySheet"]);
            Assert.Equal(false, json["includesLinks"]);
            Assert.False(json.ContainsKey("areaScheme"));
        }

        // ------------------------------------------------------------------ view-plane overlap (the -- views: noise guard)

        [Fact]
        public void Overlaps_InsideAndAcross()
        {
            var bMin = new[] { 0.0, 0.0 };
            var bMax = new[] { 10.0, 10.0 };
            Assert.True(CoverageGeometry.Overlaps(new[] { 2.0, 2.0 }, new[] { 3.0, 3.0 }, bMin, bMax, 0.1));
            Assert.True(CoverageGeometry.Overlaps(new[] { -5.0, 4.0 }, new[] { 5.0, 6.0 }, bMin, bMax, 0.1));
        }

        [Fact]
        public void Overlaps_OnlyTouchingOrBeyond_DoesNotCount()
        {
            Assert.False(CoverageGeometry.Overlaps(new[] { 10.0, 0.0 }, new[] { 20.0, 10.0 }, new[] { 0.0, 0.0 }, new[] { 10.0, 10.0 }, 0.05));
            Assert.False(CoverageGeometry.Overlaps(new[] { 30.0, 0.0 }, new[] { 40.0, 10.0 }, new[] { 0.0, 0.0 }, new[] { 10.0, 10.0 }, 0.05));
        }

        [Fact]
        public void Overlaps_AThinElementKeepsItsMiddle()
        {
            // A bar thinner than twice the shrink: its middle decides.
            Assert.True(CoverageGeometry.Overlaps(new[] { 5.0, 4.999 }, new[] { 6.0, 5.001 }, new[] { 0.0, 0.0 }, new[] { 10.0, 10.0 }, 0.01));
            Assert.False(CoverageGeometry.Overlaps(new[] { 5.0, 10.0 }, new[] { 6.0, 10.002 }, new[] { 0.0, 0.0 }, new[] { 10.0, 10.0 }, 0.01));
        }

        [Fact]
        public void Overlaps_ThreeAxes_For3DViews()
        {
            Assert.False(CoverageGeometry.Overlaps(new[] { 1.0, 1.0, 20.0 }, new[] { 2.0, 2.0, 30.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 10.0, 10.0 }, 0.01));
            Assert.True(CoverageGeometry.Overlaps(new[] { 1.0, 1.0, 5.0 }, new[] { 2.0, 2.0, 6.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 10.0, 10.0 }, 0.01));
        }

        [Theory]
        [InlineData("Partition", "Equal", "'Boven'", "Partition = 'Boven'")]
        [InlineData("Length", "GreaterThan", "12000 mm", "Length > 12000 mm")]
        [InlineData("Comments", "HasValue", null, "Comments has a value")]
        [InlineData("Comments", "HasParameter", null, "Comments has the parameter")]
        [InlineData("Mark", "NotBeginsWith", "'K'", "Mark does not begin with 'K'")]
        [InlineData("Mark", "Equal", null, "Mark = ''")]
        public void FilterText_IsReadable(string field, string type, string value, string expected)
        {
            Assert.Equal(expected, ScheduleFilterText.Describe(field, type, value));
        }

        // ------------------------------------------------------------------ coverage lines

        private static ScheduleMiss SMiss(string name, int id, int missing) =>
            new ScheduleMiss { Schedule = name, Id = id, Category = "Structural Rebar", Missing = missing, Ids = new List<long> { 1001, 1002 } };

        [Fact]
        public void CoverageLines_AreNull_WhenNothingIsMissing()
        {
            var r = new CoverageReport();
            Assert.True(r.IsEmpty);
            Assert.Null(r.SchedulesLine());
            Assert.Null(r.ViewsLine());
            Assert.Empty(r.TextLines());
            var envelope = new Dictionary<string, object>();
            r.AddJson(envelope);
            Assert.Empty(envelope);
        }

        [Fact]
        public void SchedulesLine_NamesTheScheduleAndTheReadBack()
        {
            var r = new CoverageReport();
            r.Schedules.Add(SMiss("Wapeningsstaat", 4711, 12));
            Assert.Equal("-- schedules: 12 new Structural Rebar not in 'Wapeningsstaat' (schedule 4711 --rows for why)", r.SchedulesLine());
        }

        [Fact]
        public void SchedulesLine_CapsAndCountsTheRest()
        {
            var r = new CoverageReport { SchedulesNotChecked = 4 };
            for (int i = 0; i < 7; i++) r.Schedules.Add(SMiss("S" + i, 100 + i, 1));
            var line = r.SchedulesLine();
            Assert.Contains("'S4'", line);
            Assert.DoesNotContain("'S5'", line);
            Assert.EndsWith("; +2 more schedules; 4 more schedules not checked", line);
        }

        [Fact]
        public void NotChecked_IsSaid_AlsoWithoutAMiss()
        {
            // A cap that left schedules or views unchecked is never silent.
            var r = new CoverageReport { SchedulesNotChecked = 7, ViewsNotChecked = 2 };
            Assert.False(r.IsEmpty);
            Assert.Equal("-- schedules: every checked schedule lists the new elements; 7 more schedules of these categories not checked", r.SchedulesLine());
            Assert.Equal("-- views: every checked view shows the new elements; 2 more views not checked", r.ViewsLine());
            var envelope = new Dictionary<string, object>();
            r.AddJson(envelope);
            Assert.Equal(7, envelope["schedulesNotChecked"]);
            Assert.Equal(2, envelope["viewsNotChecked"]);
            Assert.False(envelope.ContainsKey("schedules"));
        }

        [Fact]
        public void ViewsLine_CapsFindings()
        {
            var r = new CoverageReport();
            for (int i = 0; i < 7; i++)
                r.Views.Add(new ViewMiss { View = "V" + i, Id = i, Category = "Walls", Missing = 1, Cause = ViewMiss.NotVisible, Ids = new List<long> { 9 } });
            Assert.EndsWith("; +2 more", r.ViewsLine());
        }

        [Fact]
        public void ViewsLine_ObscuredRebar_GivesTheExecFix()
        {
            var r = new CoverageReport();
            r.Views.Add(new ViewMiss { View = "Wapening boven", Id = 501, Sheet = "0003 - Wapening", Category = "Structural Rebar", Missing = 12, Cause = ViewMiss.Obscured });
            Assert.Equal("-- views: 12 new Structural Rebar hidden inside their host in 'Wapening boven' (sheet 0003 - Wapening), " +
                         "where the other bars are unobscured — exec: rebar.SetUnobscuredInView(view, true)", r.ViewsLine());
        }

        [Fact]
        public void ViewsLine_NotVisible_PointsAtWhyHidden()
        {
            var r = new CoverageReport();
            r.Views.Add(new ViewMiss { View = "Plattegrond", Id = 77, Category = "Walls", Missing = 2, Cause = ViewMiss.NotVisible, Ids = new List<long> { 9001, 9002 } });
            Assert.Equal("-- views: 2 new Walls not visible in 'Plattegrond' — whyhidden 9001 77", r.ViewsLine());
        }

        [Fact]
        public void CoverageJson_ListsMissesWithCappedIds()
        {
            var r = new CoverageReport();
            var miss = SMiss("Wapeningsstaat", 4711, 30);
            miss.Ids = new List<long>();
            for (int i = 0; i < 30; i++) miss.Ids.Add(i);
            r.Schedules.Add(miss);
            r.Views.Add(new ViewMiss { View = "V", Id = 5, Category = "Structural Rebar", Missing = 1, Cause = ViewMiss.Obscured, Ids = new List<long> { 3 } });
            var envelope = new Dictionary<string, object>();
            r.AddJson(envelope);
            var s = (Dictionary<string, object>)((List<object>)envelope["schedules"])[0];
            Assert.Equal("Wapeningsstaat", s["schedule"]);
            Assert.Equal(30, s["missing"]);
            Assert.Equal(CoverageReport.MaxIds, ((List<object>)s["ids"]).Count);
            var v = (Dictionary<string, object>)((List<object>)envelope["views"])[0];
            Assert.Equal("obscured", v["cause"]);
            Assert.Equal(2, new List<string>(r.TextLines()).Count);
        }

        // ------------------------------------------------------------------ rule + source popup

        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void Rules_ActOnTheCoverageLines(string language)
        {
            var rules = ChatBackendBase.BuildBehaviourRules(language);
            Assert.Contains(ChatBackendBase.DerivedCoverageRule, rules);
            Assert.Contains("-- schedules:", ChatBackendBase.DerivedCoverageRule);
            Assert.Contains("-- views:", ChatBackendBase.DerivedCoverageRule);
            Assert.Contains("SetUnobscuredInView", ChatBackendBase.DerivedCoverageRule);
        }

        [Fact]
        public void SourceText_DropsEveryPostCommandLine_InAnyOrder()
        {
            var response = "Doorsnede 1-1\nTekst\n-- check: 1 WARN (x) → run 'check' for fixes\n-- schedules: 2 new Walls not in 'W'\n" +
                           "-- views: 1 new Walls not visible in 'P'\n-- changed: added 2 (Walls 2), modified 0, deleted 0\n-- model #4 | view: P | selected: 0\n";
            Assert.Equal("Doorsnede 1-1\nTekst", CitationSourceText.Clean(response));
        }
    }
}
