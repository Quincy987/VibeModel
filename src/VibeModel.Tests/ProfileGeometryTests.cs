using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 12 profile maths: polygon rules, area / centroid, volumes over a length and an arc,
    /// the L-wall polygon, family naming, the update decision and path chaining.
    /// </summary>
    public class ProfileGeometryTests
    {
        private static List<P2> Pts(params double[] uv)
        {
            var l = new List<P2>();
            for (int i = 0; i < uv.Length; i += 2) l.Add(new P2(uv[i], uv[i + 1]));
            return l;
        }

        // The probed L: stem 600 x 4000 (total), foot 2500 x 800 → 3.92 m².
        private static readonly List<P2> ProbeL = Pts(0, 0, 2500, 0, 2500, 800, 600, 800, 600, 4000, 0, 4000);

        // ---------------- area / centroid ----------------

        [Fact]
        public void Area_OfTheProbedL_Is392()
        {
            var p = ProfileGeometry.Normalize(ProbeL, out var err);
            Assert.Null(err);
            Assert.Equal(3.92, p.AreaM2, 6);
            Assert.Equal(2500, p.Width, 6);
            Assert.Equal(4000, p.Height, 6);
            Assert.Equal("3.92 m², 2500 x 4000 mm", p.Describe());
        }

        [Fact]
        public void Centroid_OfTheL_IsTheAreaWeightedMeanOfItsRectangles()
        {
            // foot 2500x800 at (1250, 400), stem 600x3200 at (300, 2400)
            double a1 = 2500 * 800, a2 = 600 * 3200;
            double cu = (a1 * 1250 + a2 * 300) / (a1 + a2), cv = (a1 * 400 + a2 * 2400) / (a1 + a2);
            var p = ProfileGeometry.Normalize(ProbeL, out _);
            Assert.Equal(cu, p.CentroidU, 6);
            Assert.Equal(cv, p.CentroidV, 6);
        }

        [Fact]
        public void Centroid_OfARectangle_IsItsMiddle()
        {
            ProfileGeometry.Centroid(Pts(0, 0, 400, 0, 400, 200, 0, 200), out var cu, out var cv);
            Assert.Equal(200, cu, 9);
            Assert.Equal(100, cv, 9);
        }

        [Fact]
        public void Volume_OverALength()
        {
            Assert.Equal(39.20, ProfileGeometry.VolumeM3(3.92e6, 10000), 9);   // the probe, 10 m
            Assert.Equal(44.80, ProfileGeometry.VolumeM3(6.4e6, 7000), 9);     // 800x5000 voet 3200x1000 over 7 m
        }

        [Fact]
        public void Volume_OnAnArc_UsesTheCentroidRadius()
        {
            // The probe's quarter arc, r = 10 m, turning left (from -90° to 0° counter-clockwise):
            // the L's centroid lies left of the path → radius r − cu. Revit reported 56.74 m³.
            var p = ProfileGeometry.Normalize(ProbeL, out _);
            double v = ProfileGeometry.VolumeOnArcM3(p, 10000, Math.PI / 2, true);
            Assert.Equal(p.AreaMm2 * (10000 - p.CentroidU) * Math.PI / 2 / 1e9, v, 9);
            Assert.InRange(v, 56.6, 56.9);
            Assert.True(ProfileGeometry.VolumeOnArcM3(p, 10000, Math.PI / 2, false) > v);
        }

        // ---------------- polygon rules ----------------

        [Fact]
        public void Normalize_DropsTheRepeatedClosingPoint()
        {
            var p = ProfileGeometry.Normalize(Pts(0, 0, 500, 0, 500, 280, 0, 280, 0, 0), out var err);
            Assert.Null(err);
            Assert.Equal(4, p.Points.Count);
        }

        [Fact]
        public void Normalize_DropsConsecutiveDuplicatesAndCollinearPoints()
        {
            var p = ProfileGeometry.Normalize(Pts(0, 0, 250, 0, 500, 0, 500, 0, 500, 280, 0, 280), out var err);
            Assert.Null(err);
            Assert.Equal(4, p.Points.Count);
            Assert.DoesNotContain(p.Points, q => q.U == 250 && q.V == 0);
        }

        [Fact]
        public void Normalize_TurnsClockwiseRound()
        {
            var p = ProfileGeometry.Normalize(Pts(0, 0, 0, 280, 500, 280, 500, 0), out var err);
            Assert.Null(err);
            Assert.True(p.WasClockwise);
            Assert.True(ProfileGeometry.SignedArea(p.Points) > 0);
            Assert.Equal(0.14, p.AreaM2, 9);
        }

        [Fact]
        public void Normalize_RefusesFewerThanThreePoints()
        {
            Assert.Null(ProfileGeometry.Normalize(Pts(0, 0, 500, 0), out var err));
            Assert.Contains("at least 3", err);
            Assert.Null(ProfileGeometry.Normalize(Pts(0, 0, 0, 0.2, 500, 0), out err));
        }

        [Fact]
        public void Normalize_RefusesZeroArea()
        {
            Assert.Null(ProfileGeometry.Normalize(Pts(0, 0, 500, 0, 1000, 0.1, 200, 0.05), out var err));
            Assert.NotNull(err);
        }

        [Fact]
        public void Normalize_RefusesASelfIntersectingOutline_NamingTheSegments()
        {
            // A bow tie: segment 1 (0,0)→(500,280) crosses segment 3 (500,0)→(0,280).
            Assert.Null(ProfileGeometry.Normalize(Pts(0, 0, 500, 280, 500, 0, 0, 280), out var err));
            Assert.Contains("crosses itself", err);
            Assert.Contains("segment 1", err);
            Assert.Contains("segment 3", err);
        }

        [Fact]
        public void ChamferedCapstone_HandComputedArea()
        {
            // profile Deksteen --points 0,0 500,0 500,250 470,280 0,280 → 500x280 minus a 30x30 corner / 2.
            var p = ProfileGeometry.Normalize(Pts(0, 0, 500, 0, 500, 250, 470, 280, 0, 280), out var err);
            Assert.Null(err);
            Assert.Equal((500 * 280 - 30 * 30 / 2.0) / 1e6, p.AreaM2, 9);
        }

        [Fact]
        public void Mirror_PutsTheProfileOnTheOtherSide_KeepingItCounterClockwise()
        {
            var m = ProfileGeometry.Mirror(ProbeL);
            Assert.All(m, q => Assert.True(q.U <= 0));
            Assert.True(ProfileGeometry.SignedArea(m) > 0);
            Assert.Equal(3.92, ProfileGeometry.Normalize(m, out _).AreaM2, 6);
        }

        // ---------------- points text ----------------

        [Theory]
        [InlineData("0,0 500,0 500,250 470,280 0,280")]
        [InlineData("0 0 500 0 500 250 470 280 0 280")]
        [InlineData("0,0; 500,0; 500,250; 470,280; 0,280")]
        public void ParsePoints_ReadsPairs(string text)
        {
            var pts = ProfileGeometry.ParsePoints(text, out var err);
            Assert.Null(err);
            Assert.Equal(5, pts.Count);
            Assert.Equal(470, pts[3].U);
            Assert.Equal(280, pts[3].V);
        }

        [Fact]
        public void ParsePoints_RoundTripsThroughFormat()
        {
            var text = ProfileGeometry.FormatPoints(Pts(-1200.4, 850, 0, 0, 2500, 800));
            Assert.Equal("-1200,850 0,0 2500,800", text);
            Assert.Equal(3, ProfileGeometry.ParsePoints(text, out _).Count);
        }

        [Theory]
        [InlineData("0,0 500,x", "500,x")]
        [InlineData("0 0 500", "odd number")]
        public void ParsePoints_NamesTheProblem(string text, string expected)
        {
            Assert.Null(ProfileGeometry.ParsePoints(text, out var err));
            Assert.Contains(expected, err);
        }

        // ---------------- the L ----------------

        [Fact]
        public void LwallPolygon_WithoutToe_IsTheProbedL()
        {
            var s = new LwallSizes { Stem = 600, Height = 4000, Foot = 2500, FootThickness = 800 };
            Assert.Null(ProfileGeometry.CheckLwall(s));
            var p = ProfileGeometry.Normalize(ProfileGeometry.LwallPolygon(s), out var err);
            Assert.Null(err);
            Assert.Equal(6, p.Points.Count);
            Assert.Equal(3.92, p.AreaM2, 9);
            Assert.Equal(0, p.MinU);
            Assert.Equal(2500, p.MaxU);
        }

        [Fact]
        public void LwallPolygon_WithToe_SticksOutInFront()
        {
            var s = new LwallSizes { Stem = 600, Height = 4000, Foot = 3000, FootThickness = 800, Toe = 500 };
            var p = ProfileGeometry.Normalize(ProfileGeometry.LwallPolygon(s), out _);
            Assert.Equal(8, p.Points.Count);
            Assert.Equal(-500, p.MinU);
            Assert.Equal(2500, p.MaxU);
            Assert.Equal((3000 * 800 + 600 * 3200) / 1e6, p.AreaM2, 9);
            Assert.Equal(2500, s.Heel);
        }

        [Fact]
        public void LwallPolygon_SlopedFoot_HandComputedArea()
        {
            // Foot 2500 wide: 800 at the stem face falling to 500 at the back; the part under the
            // stem (0..600) is 800 thick, then a trapezoid 600..2500 from 800 to 500.
            var s = new LwallSizes { Stem = 600, Height = 4000, Foot = 2500, FootThickness = 800, Slope = 500 };
            Assert.Null(ProfileGeometry.CheckLwall(s));
            var p = ProfileGeometry.Normalize(ProfileGeometry.LwallPolygon(s), out _);
            double expected = 600 * 4000 + (2500 - 600) * (800 + 500) / 2.0;
            Assert.Equal(expected / 1e6, p.AreaM2, 9);
        }

        [Theory]
        [InlineData(600, 4000, 500, 800, 0, null, "too narrow")]
        [InlineData(600, 4000, 1000, 800, 500, null, "plus the toe")]
        [InlineData(600, 800, 2500, 800, 0, null, "thinner")]
        [InlineData(0, 4000, 2500, 800, 0, null, "positive")]
        [InlineData(600, 4000, 2500, 800, -1, null, "negative")]
        [InlineData(600, 4000, 2500, 800, 0, 900.0, "--slope")]
        public void CheckLwall_RefusesImpossibleSizes(double stem, double h, double foot, double ft, double toe, double? slope, string expected)
        {
            var err = ProfileGeometry.CheckLwall(new LwallSizes { Stem = stem, Height = h, Foot = foot, FootThickness = ft, Toe = toe, Slope = slope });
            Assert.NotNull(err);
            Assert.Contains(expected, err);
        }

        // ---------------- naming ----------------

        [Theory]
        [InlineData("Kade: L {A}", "Kade- L -A-")]
        [InlineData("  a\\b|c  ", "a-b-c")]
        [InlineData("Deksteen 500x280", "Deksteen 500x280")]
        public void Sanitize_ReplacesRevitsForbiddenCharacters(string input, string expected)
        {
            Assert.Equal(expected, FamilyNaming.Sanitize(input));
        }

        [Fact]
        public void FamilyName_AddsThePrefixOnce()
        {
            Assert.Equal("VM_Deksteen", FamilyNaming.FamilyName("VM_", "Deksteen"));
            Assert.Equal("VM_Deksteen", FamilyNaming.FamilyName("VM_", "VM_Deksteen"));
            Assert.Equal("Deksteen", FamilyNaming.FamilyName("", "Deksteen"));
        }

        [Fact]
        public void FamilyName_IsFileSafe_TypeNamesKeepTheSlash()
        {
            // A family name is also the .rfa's file name; a type name is not.
            Assert.Equal("VM_L-wand Beton C30-37", FamilyNaming.FamilyName("VM_", "L-wand Beton C30/37"));
            Assert.Equal("Beton C30/37", FamilyNaming.Sanitize("Beton C30/37"));
        }

        [Fact]
        public void LwallTypeName_DefaultPattern()
        {
            var s = new LwallSizes { Stem = 600, Height = 4000, Foot = 2500, FootThickness = 800 };
            Assert.Equal("600x4000 voet 2500x800", FamilyNaming.LwallTypeName(null, s));
            s.Toe = 500;
            Assert.Equal("600x4000 voet 2500x800 teen 500", FamilyNaming.LwallTypeName(null, s));
            s.Slope = 500;
            Assert.Equal("600x4000 voet 2500x800 teen 500 schuin 500", FamilyNaming.LwallTypeName(null, s));
        }

        [Fact]
        public void LwallTypeName_OfficePattern_ToeShownOnlyOnce()
        {
            var s = new LwallSizes { Stem = 600, Height = 4000, Foot = 2500, FootThickness = 800, Toe = 300 };
            Assert.Equal("L 600-4000 / 2500-800 t300", FamilyNaming.LwallTypeName("L {stem}-{height} / {foot}-{footThickness} t{toe}", s));
        }

        [Fact]
        public void Hash_IsStableAndShort()
        {
            Assert.Equal(FamilyNaming.Hash("a", "b"), FamilyNaming.Hash("a", "b"));
            Assert.NotEqual(FamilyNaming.Hash("a", "b"), FamilyNaming.Hash("ab", ""));
            Assert.Equal(12, FamilyNaming.Hash("x").Length);
        }

        [Fact]
        public void Stamp_RoundTrips()
        {
            var st = FamilyNaming.Stamp("spec123", "struct456");
            Assert.True(FamilyNaming.TryParseStamp(st, out var v, out var spec, out var structure));
            Assert.Equal(FamilyNaming.GeneratorVersion, v);
            Assert.Equal("spec123", spec);
            Assert.Equal("struct456", structure);
            Assert.False(FamilyNaming.TryParseStamp("hand made", out _, out _, out _));
            Assert.False(FamilyNaming.TryParseStamp(null, out _, out _, out _));
        }

        // ---------------- update decision ----------------

        private static readonly string Ours = FamilyNaming.Stamp("specA", "structA");

        [Fact]
        public void Decide_NewName_Creates()
            => Assert.Equal(FamilyNaming.Action.Create, FamilyNaming.Decide(false, null, "specA", null, false));

        [Fact]
        public void Decide_SameSpec_Reuses_WithoutOpeningTheFamily()
        {
            Assert.Equal(FamilyNaming.Action.Reuse, FamilyNaming.Decide(true, Ours, "specA", null, false));
            Assert.False(FamilyNaming.NeedsStructureCheck(true, Ours, "specA", false));
        }

        [Fact]
        public void Decide_NewSpec_Unedited_Regenerates()
        {
            Assert.True(FamilyNaming.NeedsStructureCheck(true, Ours, "specB", false));
            Assert.Equal(FamilyNaming.Action.Regenerate, FamilyNaming.Decide(true, Ours, "specB", "structA", false));
        }

        [Fact]
        public void Decide_NewSpec_EditedInRevit_Refuses_UnlessReplace()
        {
            Assert.Equal(FamilyNaming.Action.RefuseEdited, FamilyNaming.Decide(true, Ours, "specB", "structEDITED", false));
            Assert.False(FamilyNaming.NeedsStructureCheck(true, Ours, "specB", true));
            Assert.Equal(FamilyNaming.Action.Regenerate, FamilyNaming.Decide(true, Ours, "specB", null, true));
        }

        [Fact]
        public void Decide_FamilyNotOurs_Refuses_UnlessReplace()
        {
            Assert.Equal(FamilyNaming.Action.RefuseForeign, FamilyNaming.Decide(true, null, "specA", null, false));
            Assert.Equal(FamilyNaming.Action.Regenerate, FamilyNaming.Decide(true, "", "specA", null, true));
        }

        [Fact]
        public void Decide_OlderGenerator_Regenerates()
        {
            var old = "vm0;specA;structA";
            Assert.True(FamilyNaming.NeedsStructureCheck(true, old, "specA", false));
            Assert.Equal(FamilyNaming.Action.Regenerate, FamilyNaming.Decide(true, old, "specA", "structA", false));
        }

        [Fact]
        public void Decide_StructureUnreadable_StillRegenerates()
            => Assert.Equal(FamilyNaming.Action.Regenerate, FamilyNaming.Decide(true, Ours, "specB", null, false));

        // ---------------- shared parameters ----------------

        [Fact]
        public void SharedParameterFile_HasFixedGuids_AndHidesTheStamp()
        {
            var text = GeneratedSharedParameters.FileText();
            Assert.Contains("PARAM\t5b2f0c1e-7a43-4f9e-9c1d-3e8a6b2d4f10\tVM_Length\tLENGTH\t\t1\t1\t", text);
            Assert.Contains("PARAM\tc4e8d2a7-0b6f-4b1e-a3d5-8f27e9c1b604\tVM_Source\tTEXT\t\t1\t0\t", text);
            Assert.StartsWith("# This is a Revit shared parameter file.", text);
            Assert.Equal(text, GeneratedSharedParameters.FileText());
        }

        // ---------------- path chaining ----------------

        private static PathChain.Piece Pc(double x0, double y0, double x1, double y1) => new PathChain.Piece(x0, y0, x1, y1);

        [Fact]
        public void PathChain_OrdersAndReversesPieces()
        {
            // Given out of order and one reversed: (10000,0)-(20000,0) ; (0,0)-(10000,0) ; (25000,5000)-(20000,0)
            var order = PathChain.Order(new[] { Pc(10000, 0, 20000, 0), Pc(0, 0, 10000, 0), Pc(25000, 5000, 20000, 0) }, 1, out var err);
            Assert.Null(err);
            Assert.Equal(new[] { 1, 0, 2 }, order.Select(o => o.Key).ToArray());
            Assert.Equal(new[] { false, false, true }, order.Select(o => o.Value).ToArray());
        }

        [Fact]
        public void PathChain_StartsAtTheFreeEnd_EvenWhenTheFirstPieceIsReversed()
        {
            var order = PathChain.Order(new[] { Pc(10000, 0, 0, 0), Pc(10000, 0, 20000, 0) }, 1, out var err);
            Assert.Null(err);
            Assert.Equal(0, order[0].Key);
            Assert.True(order[0].Value);
            Assert.False(order[1].Value);
        }

        [Fact]
        public void PathChain_RefusesAGap()
        {
            Assert.Null(PathChain.Order(new[] { Pc(0, 0, 10000, 0), Pc(10500, 0, 20000, 0) }, 1, out var err));
            Assert.Contains("not connected", err);
        }

        [Fact]
        public void PathChain_SinglePiece()
        {
            var order = PathChain.Order(new[] { Pc(0, 0, 1, 1) }, 1, out var err);
            Assert.Null(err);
            Assert.Single(order);
        }
    }
}
