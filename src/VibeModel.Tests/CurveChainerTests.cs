using System.Collections.Generic;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Loop assembly from unordered DWG segments: closed loops are detected regardless of
    /// segment order/direction, endpoints snap within tolerance, and chains that cannot
    /// close are reported as open.
    /// </summary>
    public class CurveChainerTests
    {
        private static List<(double X, double Y)> Seg(double x1, double y1, double x2, double y2)
            => new List<(double X, double Y)> { (x1, y1), (x2, y2) };

        [Fact]
        public void ClosedSquare_FromUnorderedSegments_BecomesOneLoop()
        {
            // 4 segments of a 5000x5000 square, deliberately out of order and one reversed.
            var chains = new List<List<(double X, double Y)>>
            {
                Seg(5000, 0, 5000, 5000),
                Seg(0, 0, 5000, 0),
                Seg(0, 5000, 0, 0),
                Seg(5000, 5000, 0, 5000),
            };

            var r = CurveChainer.ChainLoops(chains, 1.0);

            Assert.Single(r.Loops);
            Assert.Empty(r.OpenChains);
            Assert.Equal(4, r.Loops[0].Count); // closing point removed
        }

        [Fact]
        public void ReversedSegment_StillCloses()
        {
            var chains = new List<List<(double X, double Y)>>
            {
                Seg(0, 0, 1000, 0),
                Seg(1000, 1000, 1000, 0),   // reversed relative to walk direction
                Seg(1000, 1000, 0, 1000),
                Seg(0, 1000, 0, 0),
            };

            var r = CurveChainer.ChainLoops(chains, 1.0);

            Assert.Single(r.Loops);
            Assert.Empty(r.OpenChains);
        }

        [Fact]
        public void EndpointsWithinSnapTolerance_AreJoined()
        {
            // Sloppy linework: gaps of 0.9 mm at each junction still close.
            var chains = new List<List<(double X, double Y)>>
            {
                Seg(0, 0, 1000, 0),
                Seg(1000.9, 0.5, 1000, 1000),
                Seg(1000.4, 1000.9, 0, 1000),
                Seg(-0.9, 1000.4, 0.3, 0.8),
            };

            var r = CurveChainer.ChainLoops(chains, 1.0);

            Assert.Single(r.Loops);
            Assert.Empty(r.OpenChains);
        }

        [Fact]
        public void GapBeyondTolerance_StaysOpen()
        {
            // U-shape with a 5 mm gap: must NOT be forced closed.
            var chains = new List<List<(double X, double Y)>>
            {
                Seg(0, 0, 1000, 0),
                Seg(1000, 0, 1000, 1000),
                Seg(1000, 1000, 5, 1000), // 5 mm short of x=0... and nothing bridges back
            };

            var r = CurveChainer.ChainLoops(chains, 1.0);

            Assert.Empty(r.Loops);
            Assert.Single(r.OpenChains); // merged into one open chain
        }

        [Fact]
        public void AlreadyClosedPolyline_IsALoop_WithoutDuplicateClosingPoint()
        {
            var closed = new List<(double X, double Y)>
            {
                (0, 0), (2000, 0), (2000, 2000), (0, 2000), (0, 0)
            };

            var r = CurveChainer.ChainLoops(new List<List<(double X, double Y)>> { closed }, 1.0);

            Assert.Single(r.Loops);
            Assert.Equal(4, r.Loops[0].Count);
        }

        [Fact]
        public void TwoSeparateSquares_BecomeTwoLoops()
        {
            var chains = new List<List<(double X, double Y)>>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 1000, 1000),
                Seg(1000, 1000, 0, 1000), Seg(0, 1000, 0, 0),
                Seg(9000, 0, 9500, 0), Seg(9500, 0, 9500, 500),
                Seg(9500, 500, 9000, 500), Seg(9000, 500, 9000, 0),
            };

            var r = CurveChainer.ChainLoops(chains, 1.0);

            Assert.Equal(2, r.Loops.Count);
            Assert.Empty(r.OpenChains);
        }

        [Fact]
        public void DegenerateAndNullChains_AreIgnored()
        {
            var chains = new List<List<(double X, double Y)>>
            {
                null,
                new List<(double X, double Y)>(),
                new List<(double X, double Y)> { (5, 5) },     // single point
                new List<(double X, double Y)> { (7, 7), (7, 7) }, // zero-length
            };

            var r = CurveChainer.ChainLoops(chains, 1.0);

            Assert.Empty(r.Loops);
            Assert.Empty(r.OpenChains);
        }
    }
}
