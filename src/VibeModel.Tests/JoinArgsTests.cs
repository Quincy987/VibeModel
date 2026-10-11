using System.Collections.Generic;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Argument parsing, report rendering and failure-explanation text for the join/unjoin
    /// commands. All Revit-free — the live join/cut behaviour needs a running Revit.
    /// </summary>
    public class JoinArgsTests
    {
        // ---- join arg parsing ----

        [Fact]
        public void Join_TwoIds_ParseAsJoinMode()
        {
            var r = JoinArgs.ParseJoin("12345 67890");

            Assert.Null(r.Error);
            Assert.Equal(12345, r.IdA);
            Assert.Equal(67890, r.IdB);
            Assert.Equal(JoinMode.Join, r.Mode);
        }

        [Fact]
        public void Join_CommaSeparatedIds_Parse()
        {
            var r = JoinArgs.ParseJoin("12345,67890");

            Assert.Null(r.Error);
            Assert.Equal(12345, r.IdA);
            Assert.Equal(67890, r.IdB);
        }

        [Fact]
        public void Join_CutFlag_SetsCutMode()
        {
            var r = JoinArgs.ParseJoin("1 2 --cut");

            Assert.Null(r.Error);
            Assert.Equal(JoinMode.Cut, r.Mode);
        }

        [Fact]
        public void Join_SwitchFlag_SetsSwitchMode()
        {
            var r = JoinArgs.ParseJoin("1 2 --switch");

            Assert.Null(r.Error);
            Assert.Equal(JoinMode.Switch, r.Mode);
        }

        [Fact]
        public void Join_FlagBeforeIds_IsAccepted()
        {
            var r = JoinArgs.ParseJoin("--cut 7 8");

            Assert.Null(r.Error);
            Assert.Equal(7, r.IdA);
            Assert.Equal(8, r.IdB);
            Assert.Equal(JoinMode.Cut, r.Mode);
        }

        [Fact]
        public void Join_FlagCaseIsIgnored()
        {
            Assert.Equal(JoinMode.Cut, JoinArgs.ParseJoin("1 2 --CUT").Mode);
        }

        [Fact]
        public void Join_CutAndSwitchTogether_IsRejected()
        {
            var r = JoinArgs.ParseJoin("1 2 --cut --switch");

            Assert.Contains("mutually exclusive", r.Error);
        }

        [Fact]
        public void Join_UnknownFlag_IsRejected()
        {
            Assert.Contains("Unknown flag '--weld'", JoinArgs.ParseJoin("1 2 --weld").Error);
        }

        [Fact]
        public void Join_MissingSecondId_IsRejected()
        {
            Assert.Contains("Need two element IDs", JoinArgs.ParseJoin("12345").Error);
        }

        [Fact]
        public void Join_ThreeIds_IsRejected()
        {
            Assert.Contains("Expected exactly two element IDs", JoinArgs.ParseJoin("1 2 3").Error);
        }

        [Fact]
        public void Join_NonNumericId_IsRejected()
        {
            Assert.Contains("whole numbers", JoinArgs.ParseJoin("wall 2").Error);
        }

        [Fact]
        public void Join_SameIdTwice_IsRejected()
        {
            Assert.Contains("cannot be joined to itself", JoinArgs.ParseJoin("42 42").Error);
        }

        [Fact]
        public void Join_EmptyArgs_IsRejected()
        {
            Assert.NotNull(JoinArgs.ParseJoin("").Error);
            Assert.NotNull(JoinArgs.ParseJoin(null).Error);
        }

        // ---- unjoin arg parsing ----

        [Fact]
        public void Unjoin_TwoIds_Parse()
        {
            var r = JoinArgs.ParseUnjoin("100 200");

            Assert.Null(r.Error);
            Assert.Equal(100, r.IdA);
            Assert.Equal(200, r.IdB);
        }

        [Fact]
        public void Unjoin_RejectsFlags()
        {
            Assert.Contains("takes no flags", JoinArgs.ParseUnjoin("1 2 --cut").Error);
        }

        // ---- report rendering ----

        [Fact]
        public void Report_WithTitle_UnderlinesItAndListsBothElements()
        {
            var text = JoinReport.Build(new JoinReportModel
            {
                Title = "JOINED",
                ElementA = "1 - Walls",
                ElementB = "2 - Structural Foundations",
                Overlap = "bounding boxes intersect over 300 x 300 x 900 mm",
                ApiTried = "JoinGeometryUtils.JoinGeometry",
                Outcome = "Joined."
            });

            Assert.StartsWith("JOINED\n======", text.Replace("\r\n", "\n"));
            Assert.Contains("A: 1 - Walls", text);
            Assert.Contains("B: 2 - Structural Foundations", text);
            Assert.Contains("Overlap: bounding boxes intersect over 300 x 300 x 900 mm", text);
            Assert.Contains("API tried: JoinGeometryUtils.JoinGeometry", text);
            Assert.Contains("Result: Joined.", text);
        }

        [Fact]
        public void Report_WithoutTitle_OmitsTheUnderline()
        {
            var text = JoinReport.Build(new JoinReportModel { ElementA = "1 - Walls" });

            Assert.DoesNotContain("=", text);
            Assert.StartsWith("A: 1 - Walls", text);
        }

        [Fact]
        public void Report_RendersEachHintOnItsOwnLine()
        {
            var text = JoinReport.Build(new JoinReportModel
            {
                Title = "CUT REFUSED",
                Reason = "Revit refuses this pair.",
                Hints = new List<string> { "Use a void family.", "Or create Parts." }
            });

            Assert.Contains("Reason: Revit refuses this pair.", text);
            Assert.Contains("Hint: Use a void family.", text);
            Assert.Contains("Hint: Or create Parts.", text);
        }

        // ---- failure explanations ----

        [Theory]
        [InlineData("CutAlreadyExists", "already cut each other")]
        [InlineData("OppositeCutExists", "other way round")]
        [InlineData("CutNotAppropriateForElements", "refuses a solid-solid cut")]
        public void ExplainCutFailure_IsPlainLanguage(string reason, string expectedFragment)
        {
            var text = JoinDiagnostics.ExplainCutFailure(reason, "Structural Columns", "Walls");

            Assert.Contains(expectedFragment, text);
        }

        [Fact]
        public void ExplainCutFailure_NamesBothCategories()
        {
            var text = JoinDiagnostics.ExplainCutFailure("CutNotAppropriateForElements", "Structural Columns", "Walls");

            Assert.Contains("Structural Columns", text);
            Assert.Contains("Walls", text);
        }

        [Fact]
        public void ExplainCutFailure_UnknownReason_StillExplains()
        {
            var text = JoinDiagnostics.ExplainCutFailure("SomeFutureRevitReason", "A", "B");

            Assert.Contains("SomeFutureRevitReason", text);
            Assert.StartsWith("Revit refused the cut", text);
        }

        [Fact]
        public void CutHints_NotAppropriate_OffersVoidAndParts()
        {
            var hints = JoinDiagnostics.CutHints("CutNotAppropriateForElements", "Structural Framing", "Structural Foundations");

            Assert.Contains(hints, h => h.Contains("void"));
            Assert.Contains(hints, h => h.Contains("Parts"));
        }

        [Fact]
        public void CutHints_OppositeCutExists_TellsYouToUnjoinAndSwap()
        {
            var hints = JoinDiagnostics.CutHints("OppositeCutExists", "A", "B");

            Assert.Contains(hints, h => h.Contains("unjoin") && h.Contains("swapped"));
        }

        [Fact]
        public void JoinHints_NoOverlap_LeadsWithTheOverlapRule()
        {
            var hints = JoinDiagnostics.JoinHints(false, "Walls", "Structural Foundations");

            Assert.Contains("bounding boxes do not intersect", hints[0]);
            Assert.Contains(hints, h => h.Contains("Sloped elements"));
        }

        [Fact]
        public void JoinHints_Overlapping_SuggestsCutAndNamesCategories()
        {
            var hints = JoinDiagnostics.JoinHints(true, "Structural Framing", "Structural Foundations");

            Assert.Contains(hints, h => h.Contains("--cut"));
            Assert.Contains(hints, h => h.Contains("Structural Framing") && h.Contains("Structural Foundations"));
        }
    }
}
