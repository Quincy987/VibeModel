using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The Revit-free half of the <c>whyhidden</c> command: argument parsing and the report
    /// format the user actually reads (status prefixes, fix hints, verdict selection).
    /// The individual checks themselves need live Revit API objects and are verified in-app.
    /// </summary>
    public class WhyHiddenTests
    {
        // ---- argument parsing -------------------------------------------------

        [Fact]
        public void Parse_ElementIdOnly_LeavesViewUnset()
        {
            var r = WhyHiddenArgs.Parse("348122");

            Assert.Null(r.Error);
            Assert.Equal(348122, r.ElementId);
            Assert.False(r.ViewId.HasValue);
        }

        [Fact]
        public void Parse_ElementAndViewId()
        {
            var r = WhyHiddenArgs.Parse("348122 51203");

            Assert.Null(r.Error);
            Assert.Equal(348122, r.ElementId);
            Assert.Equal(51203, r.ViewId);
        }

        [Fact]
        public void Parse_CommaSeparated_IsAccepted()
        {
            // The HTTP layer percent-decodes "%2C" into a comma, so ids can arrive comma-joined.
            var r = WhyHiddenArgs.Parse("100,200");

            Assert.Null(r.Error);
            Assert.Equal(100, r.ElementId);
            Assert.Equal(200, r.ViewId);
        }

        [Fact]
        public void Parse_ExtraWhitespace_IsIgnored()
        {
            var r = WhyHiddenArgs.Parse("   42    99   ");

            Assert.Null(r.Error);
            Assert.Equal(42, r.ElementId);
            Assert.Equal(99, r.ViewId);
        }

        [Fact]
        public void Parse_Empty_IsAnError()
        {
            Assert.Contains("No element ID", WhyHiddenArgs.Parse("").Error);
            Assert.Contains("No element ID", WhyHiddenArgs.Parse(null).Error);
            Assert.Contains("No element ID", WhyHiddenArgs.Parse("   ").Error);
        }

        [Fact]
        public void Parse_NonNumericElementId_IsAnError()
        {
            var r = WhyHiddenArgs.Parse("walls");

            Assert.Contains("not a valid element ID", r.Error);
            Assert.Contains("walls", r.Error);
        }

        [Fact]
        public void Parse_NonNumericViewId_IsAnError()
        {
            var r = WhyHiddenArgs.Parse("42 kelder");

            Assert.Contains("not a valid view ID", r.Error);
            Assert.Contains("kelder", r.Error);
        }

        [Fact]
        public void Parse_NonPositiveIds_AreRejected()
        {
            Assert.NotNull(WhyHiddenArgs.Parse("0").Error);
            Assert.NotNull(WhyHiddenArgs.Parse("-5").Error);
            Assert.NotNull(WhyHiddenArgs.Parse("42 0").Error);
        }

        [Fact]
        public void Parse_TooManyArguments_IsAnError()
        {
            var r = WhyHiddenArgs.Parse("1 2 3");

            Assert.Contains("Too many arguments", r.Error);
        }

        // ---- report rendering ---------------------------------------------------

        [Fact]
        public void Prefixes_AreColumnAligned()
        {
            // All four prefixes pad to the same width so the checks line up in a terminal.
            var ok = VisibilityDiagnosis.Prefix(CheckStatus.Ok);
            var blocked = VisibilityDiagnosis.Prefix(CheckStatus.Blocked);
            var info = VisibilityDiagnosis.Prefix(CheckStatus.Info);
            var skipped = VisibilityDiagnosis.Prefix(CheckStatus.Skipped);

            Assert.Equal(blocked.Length, ok.Length);
            Assert.Equal(blocked.Length, info.Length);
            Assert.Equal(blocked.Length, skipped.Length);
            Assert.StartsWith("OK", ok);
            Assert.StartsWith("BLOCKED:", blocked);
        }

        [Fact]
        public void Render_LabelsAndDetailsAppearOnOneLinePerCheck()
        {
            var d = new VisibilityDiagnosis { ElementLine = "123 Walls", ViewLine = "9 Level 1" };
            d.Add("1a", "Element exists", CheckStatus.Ok, "found in the document");
            d.Add("1c", "Category visibility", CheckStatus.Blocked, "'Walls' is switched OFF in this view");

            var text = d.Render();

            Assert.Contains("Element: 123 Walls", text);
            Assert.Contains("View:    9 Level 1", text);
            Assert.Contains("1a. Element exists - found in the document", text);
            Assert.Contains("BLOCKED: 1c. Category visibility - 'Walls' is switched OFF in this view", text);
        }

        [Fact]
        public void Render_HintBecomesAnIndentedFixLine()
        {
            var d = new VisibilityDiagnosis();
            d.Add("2a", "Hidden in view", CheckStatus.Blocked, "hidden individually", "Reveal Hidden Elements (RH).");

            var text = d.Render();

            Assert.Contains("         Fix: Reveal Hidden Elements (RH).", text);
        }

        [Fact]
        public void Render_OmitsHintLineWhenNoneGiven()
        {
            var d = new VisibilityDiagnosis();
            d.Add("2a", "Hidden in view", CheckStatus.Ok, "no per-element hide set");

            Assert.DoesNotContain("Fix:", d.Render());
        }

        [Fact]
        public void Verdict_NamesTheFirstBlockingCheck()
        {
            var d = new VisibilityDiagnosis();
            d.Add("1a", "Element exists", CheckStatus.Ok, "found");
            d.Add("2a", "Hidden in view", CheckStatus.Blocked, "hidden individually in this view");
            d.Add("5", "View range", CheckStatus.Blocked, "above the view range top");

            var verdict = d.BuildVerdict();

            Assert.StartsWith("Verdict: Hidden in view - hidden individually in this view", verdict);
            Assert.Contains("+1 more blocker(s)", verdict);
        }

        [Fact]
        public void Verdict_SingleBlocker_HasNoExtraCount()
        {
            var d = new VisibilityDiagnosis();
            d.Add("5", "View range", CheckStatus.Blocked, "above the view range top");

            Assert.DoesNotContain("more blocker", d.BuildVerdict());
        }

        [Fact]
        public void Verdict_InfoAndSkippedNeverCountAsBlockers()
        {
            var d = new VisibilityDiagnosis { InViewElementSet = true };
            d.Add("6a", "Associated level", CheckStatus.Info, "different level");
            d.Add("8b", "Section box", CheckStatus.Skipped, "not a 3D view");

            Assert.Contains("No visibility blocker found", d.BuildVerdict());
        }

        [Fact]
        public void Verdict_FlagsDisagreementWhenRevitStillExcludesTheElement()
        {
            // Nothing individually blocks, yet Revit's own view element set omits it -
            // the report must not claim "all clear".
            var d = new VisibilityDiagnosis { InViewElementSet = false };
            d.Add("1a", "Element exists", CheckStatus.Ok, "found");

            var verdict = d.BuildVerdict();

            Assert.Contains("No single setting explains it", verdict);
            Assert.DoesNotContain("No visibility blocker found", verdict);
        }

        [Fact]
        public void Render_GroundTruthLineReflectsTheCollectorResult()
        {
            var present = new VisibilityDiagnosis { InViewElementSet = true };
            var absent = new VisibilityDiagnosis { InViewElementSet = false };
            var unknown = new VisibilityDiagnosis();

            Assert.Contains("Ground truth: Revit's own view element set DOES contain", present.Render());
            Assert.Contains("Ground truth: Revit's own view element set does NOT contain", absent.Render());
            Assert.DoesNotContain("Ground truth", unknown.Render());
        }

        [Fact]
        public void Render_EditabilitySectionOnlyAppearsWhenThereAreNotes()
        {
            var without = new VisibilityDiagnosis();
            without.Add("1a", "Element exists", CheckStatus.Ok, "found");
            Assert.DoesNotContain("EDITABILITY", without.Render());

            var with = new VisibilityDiagnosis();
            with.Add("1a", "Element exists", CheckStatus.Ok, "found");
            with.AddNote("Pinned", CheckStatus.Info, "element is pinned", "Press UP to unpin.");

            var text = with.Render();
            Assert.Contains("EDITABILITY", text);
            Assert.Contains("INFO     Pinned - element is pinned", text);
            Assert.Contains("Fix: Press UP to unpin.", text);
        }

        [Fact]
        public void Render_EditabilityNotesNeverAffectTheVerdict()
        {
            var d = new VisibilityDiagnosis { InViewElementSet = true };
            d.Add("1a", "Element exists", CheckStatus.Ok, "found");
            d.AddNote("Group membership", CheckStatus.Info, "inside group 'Kozijn A'");

            Assert.Contains("No visibility blocker found", d.Render());
        }

        [Fact]
        public void Render_VerdictIsTheLastLine()
        {
            var d = new VisibilityDiagnosis { InViewElementSet = false };
            d.Add("1c", "Category visibility", CheckStatus.Blocked, "'Walls' is switched OFF in this view");
            d.AddNote("Pinned", CheckStatus.Info, "element is pinned");

            var lines = d.Render().TrimEnd().Replace("\r\n", "\n").Split('\n');

            Assert.StartsWith("Verdict:", lines[lines.Length - 1]);
        }
    }
}
