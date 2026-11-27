using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Plan 23, release 3a — `filter any`, the zoomed `screenshot`, the terrain fact and the rule.</summary>
    public class FindAndLookTests
    {
        // ---------------------------------------------------------------- filter: argument split

        [Fact]
        public void Parse_SplitsCategoryParameterOperatorValue()
        {
            var q = FilterQuery.Parse(new List<string> { "any", "Type Name", "contains", "AZ 26" });
            Assert.Null(q.Error);
            Assert.True(FilterQuery.IsAny(q.Category));
            Assert.Equal("Type Name", q.Parameter);
            Assert.Equal("contains", q.Operator);
            Assert.Equal("AZ 26", q.Value);
        }

        [Fact]
        public void Parse_MultiWordNameWithoutQuotes_AndNoExpression()
        {
            var q = FilterQuery.Parse(new List<string> { "walls", "Base", "Offset", ">=", "-500" });
            Assert.Equal("Base Offset", q.Parameter);
            Assert.Equal(">=", q.Operator);
            Assert.Equal("-500", q.Value);

            var bare = FilterQuery.Parse(new List<string> { "walls" });
            Assert.False(bare.HasExpression);
            Assert.Equal("walls", bare.Category);
        }

        [Fact]
        public void Parse_RefusesAMissingNameOrValue_AsBefore()
        {
            Assert.Equal("No category specified.", FilterQuery.Parse(new List<string>()).Error);
            Assert.StartsWith("Missing parameter name before operator '='", FilterQuery.Parse(new List<string> { "any", "=", "x" }).Error);
            Assert.StartsWith("Missing value after operator '>'", FilterQuery.Parse(new List<string> { "any", "Length", ">" }).Error);
        }

        [Theory]
        [InlineData("any", true)]
        [InlineData(" ANY ", true)]
        [InlineData("anything", false)]
        [InlineData("walls", false)]
        public void IsAny_OnlyTheWholeWord(string category, bool expected)
            => Assert.Equal(expected, FilterQuery.IsAny(category));

        // ---------------------------------------------------------------- filter: the shared comparison

        [Fact]
        public void Text_IsCaseInsensitive_ContainsAndNotEqual()
        {
            Assert.True(FilterQuery.Text("AZ 26-700", "contains", "az 26"));
            Assert.True(FilterQuery.Text("K01", "=", "k01"));
            Assert.True(FilterQuery.Text(null, "!=", "x"));
            Assert.False(FilterQuery.Text("K01", ">", "K00"));
        }

        [Fact]
        public void Number_EqualsWithinHalfAUnit_UnparseableNeverMatches()
        {
            Assert.True(FilterQuery.Number(3000.4, "=", "3000"));
            Assert.False(FilterQuery.Number(3000.6, "=", "3000"));
            Assert.True(FilterQuery.Number(20001, ">", "20000"));
            Assert.True(FilterQuery.Number(-500, "<=", "-500"));
            Assert.False(FilterQuery.Number(5, "=", "abc"));
            Assert.False(FilterQuery.Number(5, "contains", "5"));
        }

        [Fact]
        public void Integer_ComparesNumbers_ElseTheShownValue()
        {
            Assert.True(FilterQuery.Integer(3, "3", ">=", "2"));
            Assert.True(FilterQuery.Integer(1, "Yes", "=", "yes"));
            Assert.True(FilterQuery.Integer(0, "No", "!=", "Yes"));
            Assert.False(FilterQuery.Integer(1, "Yes", ">", "No"));
        }

        // ---------------------------------------------------------------- filter: spelling and hints

        [Fact]
        public void Spelling_FindsRevitsCasing_ExactWins()
        {
            var known = new[] { "Comments", "Mark", "Type Name" };
            Assert.Equal("Comments", FilterQuery.Spelling("comments", known));
            Assert.Equal("Type Name", FilterQuery.Spelling("TYPE NAME", known));
            Assert.Equal("Mark", FilterQuery.Spelling("Mark", new[] { "MARK", "Mark" }));
            Assert.Null(FilterQuery.Spelling("Kommentaar", known));
        }

        [Fact]
        public void Closest_SuggestsNearNames()
        {
            var close = FilterQuery.Closest("Lenght", new[] { "Length", "Height", "Comments", "length" });
            Assert.Contains("Length", close);
            Assert.DoesNotContain("Comments", close);
            Assert.Equal(1, close.Count(c => c.ToLowerInvariant() == "length"));   // one spelling per name
        }

        [Fact]
        public void ByCount_LargestFirstThenByName()
        {
            var counts = FilterQuery.ByCount(new[] { "Walls", "Lines", "Walls", "Floors", "Lines", "Walls", null });
            Assert.Equal(new[] { "Walls", "Lines", "(no category)", "Floors" }, counts.Select(c => c.Key));
            Assert.Equal(new[] { 3, 2, 1, 1 }, counts.Select(c => c.Value));
        }

        // ---------------------------------------------------------------- screenshot --at / --box

        [Fact]
        public void Zoom_AtIdsOrSelected_WithMargin()
        {
            var a = ZoomArgs.Parse("--view 0001 --at 1561017,1561019 1561017 --margin 1000");
            Assert.Null(a.Error);
            Assert.True(a.Zoom);
            Assert.Equal("0001", a.View);
            Assert.Equal(new[] { 1561017, 1561019 }, a.AtIds);
            Assert.Equal(1000, a.Margin);

            var s = ZoomArgs.Parse("--at selected");
            Assert.True(s.AtSelected);
            Assert.Equal(ZoomArgs.DefaultMarginMm, s.Margin);
        }

        [Fact]
        public void Zoom_BoxIsNormalised_PixelsStayLenient()
        {
            var a = ZoomArgs.Parse("2000 --box 19952 1647 15952 -1353");
            Assert.Null(a.Error);
            Assert.Equal(new double[] { 15952, -1353, 19952, 1647 }, a.Box);
            Assert.Equal(2000, a.Pixels);
            Assert.Equal(ZoomArgs.MaxPixels, ZoomArgs.Parse("99999").Pixels);
            Assert.Equal(ZoomArgs.MinPixels, ZoomArgs.Parse("10").Pixels);
            Assert.False(ZoomArgs.Parse("--view Bovenaanzicht").Zoom);
        }

        [Theory]
        [InlineData("--at 1 --box 0 0 1 1", "Pick one")]
        [InlineData("--box 0 0 10", "needs 4 number(s)")]
        [InlineData("--box 0 0 0 100", "no area")]
        [InlineData("--box 0 0 10 10 --margin 5", "--margin goes with --at")]
        [InlineData("--at 12 --margin -1", "cannot be negative")]
        [InlineData("--at abc", "not an element id")]
        [InlineData("--at", "--at needs element ids")]
        [InlineData("--zoom 2", "Unknown flag")]
        public void Zoom_RefusesConflictsWithTheReason(string args, string expected)
            => Assert.Contains(expected, ZoomArgs.Parse(args).Error);

        [Fact]
        public void PickViewport_TheSmallestThatHoldsTheWholeBox()
        {
            var vps = new List<MmRect> { new MmRect(0, 0, 1189, 841), new MmRect(19, 334, 253, 452), new MmRect(20, 215, 217, 311) };
            Assert.Equal(1, ZoomArgs.PickViewport(vps, new double[] { 60, 360, 140, 420 }, out _));
            Assert.Equal(1, ZoomArgs.PickViewport(vps, new double[] { 19.6, 334, 253.4, 452 }, out _));   // edge tolerance
        }

        [Fact]
        public void PickViewport_SaysWhyNoneFits()
        {
            var vps = new List<MmRect> { new MmRect(19, 334, 253, 452), new MmRect(20, 215, 217, 311) };
            Assert.Equal(-1, ZoomArgs.PickViewport(vps, new double[] { 600, 600, 700, 700 }, out var none));
            Assert.Equal("the box lies on no viewport of this sheet", none);
            Assert.Equal(-1, ZoomArgs.PickViewport(vps, new double[] { 200, 300, 260, 340 }, out var two));
            Assert.Equal("the box spans 2 viewports", two);
            Assert.Equal(-1, ZoomArgs.PickViewport(vps, new double[] { 200, 400, 300, 420 }, out var one));
            Assert.Equal("the box spans 1 viewport but sticks out of it", one);
        }

        // ---------------------------------------------------------------- terrain fact and the rule

        [Fact]
        public void TerrainGap_AnswersTopographyAndToposolid()
        {
            var gap = KnownApiGaps.All.Single(g => g.Name == "terrain");
            Assert.Contains("TopographySurface", gap.TypeKeys);
            Assert.Contains("Toposolid", gap.TypeKeys);
            Assert.Contains("TopographySurface.Create(doc, points, facets)", string.Join(" ", gap.Works));
            Assert.Contains("300 m²", string.Join(" ", gap.Works));
            Assert.Contains("concave terrain", KnownApiGaps.Summary);
        }

        [Fact]
        public void Rules_FindByValueAndLookCloselyWithTheCommands()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl");
            Assert.Contains("- " + ChatBackendBase.FindAndLookRule, rules);
            Assert.Contains("`filter any <param> <op> <value>`", ChatBackendBase.FindAndLookRule);
            Assert.Contains("`screenshot --at <ids>`", ChatBackendBase.FindAndLookRule);
            Assert.Contains("Never make a temporary view", ChatBackendBase.FindAndLookRule);
        }

        [Fact]
        public void TemporaryWork_NestsAndCloses()
        {
            Assert.False(TemporaryWork.Active);
            using (TemporaryWork.Begin())
            {
                Assert.True(TemporaryWork.Active);
                var inner = TemporaryWork.Begin();
                inner.Dispose();
                inner.Dispose();   // twice is harmless
                Assert.True(TemporaryWork.Active);
            }
            Assert.False(TemporaryWork.Active);
        }
    }
}
