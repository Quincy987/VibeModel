using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>The "-- changed" line, honest truncation and the user in the journal (v1.2.15).</summary>
    public class ChangeReportTests
    {
        private static ChangedElement El(long id, string category) => new ChangedElement(id, category);

        [Fact]
        public void ChangedLine_CountsPerCategory_InPastTense()
        {
            var s = new ChangeSetSummary(new[] { El(1, "Walls") }, new[] { El(2, "Walls"), El(3, "Walls"), El(4, "Views") }, null);
            Assert.Equal("-- changed: added 1 (Walls 1), modified 3 (Walls 2, Views 1), deleted 0", s.ChangedLine());
        }

        [Fact]
        public void ChangedLine_DeletedWithoutCategory_CountsAsOther()
        {
            var s = new ChangeSetSummary(null, null, new[] { El(1, "Floors"), El(2, null) });
            Assert.Equal("-- changed: added 0, modified 0, deleted 2 (Floors 1, other 1)", s.ChangedLine());
        }

        [Fact]
        public void ChangedLine_IsNull_WhenNothingChanged()
        {
            Assert.Null(new ChangeSetSummary(null, null, null).ChangedLine());
        }

        [Fact]
        public void ChangesJson_HasCountsPerCategory()
        {
            var json = new ChangeSetSummary(new[] { El(1, "Walls"), El(2, "Walls"), El(3, null) }, null, null).ChangesJson();
            var added = (Dictionary<string, object>)json["added"];
            Assert.Equal(3, added["count"]);
            var byCategory = (Dictionary<string, object>)added["byCategory"];
            Assert.Equal(2, byCategory["Walls"]);
            Assert.Equal(1, byCategory["other"]);
            Assert.Equal(3, ((List<object>)added["ids"]).Count);
            Assert.False(added.ContainsKey("idsTruncated"));
            Assert.Equal(0, ((Dictionary<string, object>)json["deleted"])["count"]);
        }

        [Fact]
        public void ChangesJson_CapsTheIds_NotTheCounts()
        {
            int n = ChangeSetSummary.MaxChangedIds + 25;
            var many = Enumerable.Range(1, n).Select(i => El(i, "Walls")).ToList();
            var modified = (Dictionary<string, object>)new ChangeSetSummary(null, many, null).ChangesJson()["modified"];
            Assert.Equal(n, modified["count"]);
            Assert.Equal(n, ((Dictionary<string, object>)modified["byCategory"])["Walls"]);
            Assert.Equal(ChangeSetSummary.MaxChangedIds, ((List<object>)modified["ids"]).Count);
            Assert.Equal(true, modified["idsTruncated"]);
        }

        [Fact]
        public void Truncation_GivesTheRealTotalAndSaysIncomplete()
        {
            Assert.Equal("showing 100 of 312 — incomplete; narrow with filter or --view",
                Truncation.Line(100, 312, "narrow with filter or --view"));
            Assert.Equal("showing 20 of 87 — incomplete", Truncation.Line(20, 87));
            Assert.Equal("showing 200 of 5000+ — incomplete", Truncation.Line(200, "5000+"));
        }

        [Fact]
        public void Journal_CommandLine_NamesTheUserAfterTheTimestamp()
        {
            var line = ProjectJournal.FormatCommandLine(new DateTime(2026, 11, 10, 14, 3, 12), "wall", "0 0 5000 0",
                new List<long> { 7 }, null, null, null, null, null, "jjansen");
            Assert.Equal("- 2026-11-10 14:03:12 · by jjansen · wall 0 0 5000 0 · created 7", line);
        }

        [Fact]
        public void Journal_TurnLine_NamesTheUser()
        {
            var line = ProjectJournal.FormatTurnLine(new DateTime(2026, 11, 10, 14, 3, 12),
                new ChatTurnRef { Id = "t1", Question = "vraag" }, null, false, "jjansen");
            Assert.StartsWith("- 2026-11-10 14:03:12 · by jjansen · chat t1", line);
        }

        [Fact]
        public void Journal_WithoutAUser_LeavesThePartOut()
        {
            var line = ProjectJournal.FormatCommandLine(new DateTime(2026, 11, 10, 14, 3, 12), "wall", null,
                new List<long> { 7 }, null, null, null);
            Assert.DoesNotContain("by ", line);
        }

        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void Rules_AskForTheChangeLine_OmissionsAndSchedules(string language)
        {
            var rules = ChatBackendBase.BuildBehaviourRules(language);
            Assert.Contains(ChatBackendBase.ChangeReportRule, rules);
            Assert.Contains(ChatBackendBase.OmissionsRule, rules);
            Assert.Contains(ChatBackendBase.QuantitiesRule, rules);
            // Both forms the backends see: text for the CLI, JSON for the API and local backends.
            Assert.Contains("-- changed:", ChatBackendBase.ChangeReportRule);
            Assert.Contains("`changes`", ChatBackendBase.ChangeReportRule);
        }
    }
}
