using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Argument parsing for the source-data commands: curves (flags anywhere, bbox
    /// normalization, layer names with spaces), pcprobe and pcline (defaults + validation).
    /// </summary>
    public class SourceDataArgsTests
    {
        // ---- curves ----

        [Fact]
        public void Curves_MinimalArgs_Parse()
        {
            var r = SourceDataArgs.ParseCurves("123456 BGT-pand");

            Assert.Null(r.Error);
            Assert.Equal(123456, r.ImportId);
            Assert.Equal("BGT-pand", r.Layer);
            Assert.False(r.Closed);
            Assert.False(r.HasBbox);
            Assert.Equal(1, r.Page);
        }

        [Fact]
        public void Curves_LayerWithSpaces_IsJoined()
        {
            var r = SourceDataArgs.ParseCurves("42 My Layer Name --closed");

            Assert.Null(r.Error);
            Assert.Equal("My Layer Name", r.Layer);
            Assert.True(r.Closed);
        }

        [Fact]
        public void Curves_FlagsAnywhere_AreParsed()
        {
            var r = SourceDataArgs.ParseCurves("--closed 42 --page 3 pand");

            Assert.Null(r.Error);
            Assert.Equal(42, r.ImportId);
            Assert.Equal("pand", r.Layer);
            Assert.True(r.Closed);
            Assert.Equal(3, r.Page);
        }

        [Fact]
        public void Curves_Bbox_IsNormalizedMinMax()
        {
            var r = SourceDataArgs.ParseCurves("42 pand --bbox 5000 9000 1000 2000");

            Assert.Null(r.Error);
            Assert.True(r.HasBbox);
            Assert.Equal(1000, r.BboxMinX);
            Assert.Equal(2000, r.BboxMinY);
            Assert.Equal(5000, r.BboxMaxX);
            Assert.Equal(9000, r.BboxMaxY);
        }

        [Fact]
        public void Curves_BboxMissingNumbers_Errors()
        {
            var r = SourceDataArgs.ParseCurves("42 pand --bbox 1 2 3");
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Curves_MissingLayer_Errors()
        {
            var r = SourceDataArgs.ParseCurves("42");
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Curves_NonNumericId_Errors()
        {
            var r = SourceDataArgs.ParseCurves("abc pand");
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Curves_BadPage_Errors()
        {
            Assert.NotNull(SourceDataArgs.ParseCurves("42 pand --page 0").Error);
            Assert.NotNull(SourceDataArgs.ParseCurves("42 pand --page").Error);
        }

        [Fact]
        public void Curves_UnknownFlag_Errors()
        {
            var r = SourceDataArgs.ParseCurves("42 pand --frobnicate");
            Assert.NotNull(r.Error);
        }

        // ---- pcprobe ----

        [Fact]
        public void PcProbe_DefaultsRadius()
        {
            var r = SourceDataArgs.ParsePcProbe("77 1500.5 -2000");

            Assert.Null(r.Error);
            Assert.Equal(77, r.CloudId);
            Assert.Equal(1500.5, r.XMm);
            Assert.Equal(-2000, r.YMm);
            Assert.Equal(250, r.RadiusMm);
        }

        [Fact]
        public void PcProbe_ExplicitRadius_AndCommaSeparators()
        {
            var r = SourceDataArgs.ParsePcProbe("77 1500,2000,400");

            Assert.Null(r.Error);
            Assert.Equal(400, r.RadiusMm);
        }

        [Fact]
        public void PcProbe_MissingCoords_Errors()
        {
            Assert.NotNull(SourceDataArgs.ParsePcProbe("77 100").Error);
            Assert.NotNull(SourceDataArgs.ParsePcProbe("").Error);
            Assert.NotNull(SourceDataArgs.ParsePcProbe(null).Error);
        }

        [Fact]
        public void PcProbe_NegativeRadius_Errors()
        {
            Assert.NotNull(SourceDataArgs.ParsePcProbe("77 100 100 -50").Error);
        }

        // ---- pcline ----

        [Fact]
        public void PcLine_DefaultsStep()
        {
            var r = SourceDataArgs.ParsePcLine("9 0 0 10000 0");

            Assert.Null(r.Error);
            Assert.Equal(9, r.CloudId);
            Assert.Equal(10000, r.X2Mm);
            Assert.Equal(500, r.StepMm);
        }

        [Fact]
        public void PcLine_ExplicitStep()
        {
            var r = SourceDataArgs.ParsePcLine("9 0 0 10000 0 1000");

            Assert.Null(r.Error);
            Assert.Equal(1000, r.StepMm);
        }

        [Fact]
        public void PcLine_MissingCoords_Errors()
        {
            Assert.NotNull(SourceDataArgs.ParsePcLine("9 0 0 10000").Error);
        }

        [Fact]
        public void PcLine_ZeroStep_Errors()
        {
            Assert.NotNull(SourceDataArgs.ParsePcLine("9 0 0 10000 0 0").Error);
        }
    }
}
