using System.Linq;
using VibeModel.Services.Standards;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>The angle arithmetic behind project-north-true, reference-point-true-north and links-by-shared-coordinates.</summary>
    public class NorthAnglesTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(360, 0)]
        [InlineData(359.99, -0.01)]
        [InlineData(-370, -10)]
        [InlineData(180, 180)]
        [InlineData(-180, 180)]
        [InlineData(540, 180)]
        public void Normalize_FoldsIntoHalfOpenRange(double input, double expected)
        {
            Assert.Equal(expected, NorthAngles.Normalize(input), 6);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(0.005, true)]
        [InlineData(-0.005, true)]
        [InlineData(359.995, true)]
        [InlineData(0.02, false)]
        [InlineData(12.5, false)]
        [InlineData(double.NaN, false)]
        public void IsZero_WithinTolerance(double deg, bool expected)
        {
            Assert.Equal(expected, NorthAngles.IsZero(deg, 0.01));
        }

        [Fact]
        public void PlanAngle_CountsCounterClockwiseFromEast()
        {
            Assert.Equal(0, NorthAngles.PlanAngle(1, 0), 6);
            Assert.Equal(90, NorthAngles.PlanAngle(0, 1), 6);
            Assert.Equal(-45, NorthAngles.PlanAngle(1, -1), 6);
        }

        [Fact]
        public void Format_NeverPrintsMinusZero()
        {
            Assert.Equal("0", NorthAngles.Format(-0.00001));
            Assert.Equal("-0.01", NorthAngles.Format(359.99));
            Assert.Equal("12.5", NorthAngles.Format(12.5));
        }

        [Fact]
        public void NewOrientationKinds_AreKnown_AndElementKindsAreASubset()
        {
            foreach (var k in new[] { "project-north-true", "reference-point-true-north", "links-by-shared-coordinates" })
                Assert.Contains(k, ModelChecks.KnownKinds);
            Assert.All(ModelChecks.ElementKinds, k => Assert.Contains(k, ModelChecks.KnownKinds));
            // Model-level: judged once per audit, never after a single command.
            Assert.DoesNotContain("project-north-true", ModelChecks.ElementKinds);
            Assert.Contains("links-by-shared-coordinates", ModelChecks.ElementKinds);
            Assert.Equal(ModelChecks.KnownKinds.Length, ModelChecks.KnownKinds.Distinct().Count());
        }
    }
}
