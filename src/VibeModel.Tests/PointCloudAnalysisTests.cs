using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Ground estimation and profile smoothing: ground = lowest DENSE band (clutter above
    /// and scan noise below are ignored), and profile spikes from cars/occlusion are
    /// rejected and interpolated.
    /// </summary>
    public class PointCloudAnalysisTests
    {
        // A street scan column: dense band at ~1800mm (street), a car roof blob at ~3200mm.
        private static List<double> StreetWithCar()
        {
            var zs = new List<double>();
            for (int i = 0; i < 500; i++) zs.Add(1800 + (i % 5) * 10);   // dense street band 1800-1840
            for (int i = 0; i < 120; i++) zs.Add(3200 + (i % 4) * 12);   // car roof blob
            return zs;
        }

        [Fact]
        public void Ground_IsTheStreetBand_NotTheCar()
        {
            var est = PointCloudAnalysis.EstimateGround(StreetWithCar());

            Assert.NotNull(est.GroundZMm);
            Assert.InRange(est.GroundZMm.Value, 1790, 1860);
        }

        [Fact]
        public void StrayNoiseBelowGround_DoesNotPullGroundDown()
        {
            var zs = StreetWithCar();
            zs.Add(-2500); // single reflection artifact far below street
            zs.Add(-2450);

            var est = PointCloudAnalysis.EstimateGround(zs);

            Assert.NotNull(est.GroundZMm);
            Assert.InRange(est.GroundZMm.Value, 1790, 1860);
            Assert.Equal(-2500, est.MinZMm); // raw range still reports the noise
        }

        [Fact]
        public void WallRisingFromStreet_GroundStaysAtTheBase()
        {
            // Quay-wall scenario: street band at 1800-1840 plus a facade rising
            // contiguously to 6000 — one merged cluster. Ground must stay at the base,
            // not drift to the middle of the wall face.
            var zs = new List<double>();
            for (int i = 0; i < 400; i++) zs.Add(1800 + (i % 5) * 10);
            for (double z = 1850; z < 6000; z += 2.5) zs.Add(z); // ~1660 wall points

            var est = PointCloudAnalysis.EstimateGround(zs);

            Assert.NotNull(est.GroundZMm);
            Assert.InRange(est.GroundZMm.Value, 1790, 1950);
        }

        [Fact]
        public void TooFewPoints_GivesNoGround()
        {
            var zs = new List<double> { 1800, 1810, 1820 }; // below the 30-point density floor

            var est = PointCloudAnalysis.EstimateGround(zs);

            Assert.NotNull(est);
            Assert.Null(est.GroundZMm);
            Assert.Equal(3, est.Count);
        }

        [Fact]
        public void EmptyInput_ReturnsNull()
        {
            Assert.Null(PointCloudAnalysis.EstimateGround(new List<double>()));
            Assert.Null(PointCloudAnalysis.EstimateGround(null));
        }

        [Fact]
        public void Histogram_OnlyContainsOccupiedBins()
        {
            var est = PointCloudAnalysis.EstimateGround(StreetWithCar());

            Assert.All(est.Bins, b => Assert.True(b.Count > 0));
            // Street band and car blob are far apart -> no contiguous coverage between them.
            Assert.True(est.Bins.Count < 20);
        }

        [Fact]
        public void Profile_SpikeFromParkedCar_IsRejectedAndInterpolated()
        {
            // Smooth street ~1800->1840, one station reads the car roof at 3300.
            var grounds = new List<double?> { 1800, 1810, 3300, 1830, 1840 };

            var profile = PointCloudAnalysis.SmoothProfile(grounds, 250);

            Assert.Equal("ok", profile[1].Flag);
            Assert.Equal("interp", profile[2].Flag);
            Assert.InRange(profile[2].ZMm.Value, 1810, 1830);
            Assert.Equal("ok", profile[3].Flag);
        }

        [Fact]
        public void Profile_MissingStation_IsInterpolated()
        {
            var grounds = new List<double?> { 1800, null, 1900 };

            var profile = PointCloudAnalysis.SmoothProfile(grounds, 250);

            Assert.Equal("interp", profile[1].Flag);
            Assert.Equal(1850, profile[1].ZMm.Value, 1);
        }

        [Fact]
        public void Profile_MissingEnds_ClampToNearestGoodValue()
        {
            var grounds = new List<double?> { null, 1800, 1820, null };

            var profile = PointCloudAnalysis.SmoothProfile(grounds, 250);

            Assert.Equal(1800, profile[0].ZMm.Value, 1);
            Assert.Equal("interp", profile[0].Flag);
            Assert.Equal(1820, profile[3].ZMm.Value, 1);
        }

        [Fact]
        public void Profile_GenuineSlope_IsNotRejected()
        {
            // A real ramp: consistent 200mm steps must all stay "ok".
            var grounds = new List<double?> { 1000, 1200, 1400, 1600, 1800 };

            var profile = PointCloudAnalysis.SmoothProfile(grounds, 250);

            Assert.All(profile, p => Assert.Equal("ok", p.Flag));
        }

        [Fact]
        public void Profile_AllMissing_IsAllNodata()
        {
            var profile = PointCloudAnalysis.SmoothProfile(new List<double?> { null, null }, 250);

            Assert.All(profile, p => Assert.Equal("nodata", p.Flag));
            Assert.All(profile, p => Assert.Null(p.ZMm));
        }

        [Fact]
        public void BuildStations_CoversLineIncludingEndpoint()
        {
            var stations = PointCloudAnalysis.BuildStations(0, 0, 10250, 0, 500, 100, out string error);

            Assert.Null(error);
            Assert.Equal(0, stations[0].Dist);
            Assert.Equal(10250, stations[stations.Count - 1].Dist, 1);
            Assert.Equal(0, stations[0].X, 1);
            Assert.Equal(10250, stations[stations.Count - 1].X, 1);
        }

        [Fact]
        public void BuildStations_TooManyStations_ErrorsWithSuggestedStep()
        {
            var stations = PointCloudAnalysis.BuildStations(0, 0, 100000, 0, 100, 100, out string error);

            Assert.Null(stations);
            Assert.Contains("Increase the step", error);
        }

        [Fact]
        public void BuildStations_DegenerateLine_Errors()
        {
            var stations = PointCloudAnalysis.BuildStations(500, 500, 500, 500, 100, 100, out string error);

            Assert.Null(stations);
            Assert.Contains("degenerate", error);
        }
    }
}
