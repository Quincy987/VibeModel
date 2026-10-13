using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Plan 12 grammar: `profile`, `extrude`, `sweep`, `lwall`.</summary>
    public class ProfileArgsTests
    {
        // ---------------- profile ----------------

        [Fact]
        public void Profile_Define()
        {
            var a = ProfileArgs.ParseProfile("Deksteen --points 0,0 500,0 500,250 470,280 0,280");
            Assert.Null(a.Error);
            Assert.Equal("Deksteen", a.ProfileName);
            Assert.Equal(5, a.Points.Count);
            Assert.False(a.Replace);
        }

        [Fact]
        public void Profile_NameWithSpaces_AndReplace()
        {
            var a = ProfileArgs.ParseProfile("Deksteen hoek --points 0,0 500,0 0,280 --replace");
            Assert.Null(a.Error);
            Assert.Equal("Deksteen hoek", a.ProfileName);
            Assert.True(a.Replace);
        }

        [Fact]
        public void Profile_ShowAndList()
        {
            var show = ProfileArgs.ParseProfile("Deksteen");
            Assert.Null(show.Error);
            Assert.Null(show.Points);
            var list = ProfileArgs.ParseProfile("");
            Assert.Null(list.Error);
            Assert.Null(list.ProfileName);
        }

        [Theory]
        [InlineData("--points 0,0 1,0 0,1", "name")]
        [InlineData("X --points", "u,v pairs")]
        [InlineData("X --points 0,0 1,q", "1,q")]
        [InlineData("X --along 1", "Unknown flag")]
        public void Profile_Errors(string args, string expected)
        {
            Assert.Contains(expected, ProfileArgs.ParseProfile(args).Error);
        }

        // ---------------- extrude ----------------

        [Fact]
        public void Extrude_NamedProfile_AlongTwoPoints_WithFlags()
        {
            var a = ProfileArgs.ParseExtrude("Deksteen --along 0 -10000 10000 -10000 --z -400 --level Level 1 --flip --type Prefab 500 --material Beton --set \"Comments=KW-01\" --category foundation --replace --no-join");
            Assert.Null(a.Error);
            Assert.Equal("Deksteen", a.ProfileName);
            Assert.Equal(new double[] { 0, -10000, 10000, -10000 }, a.Line);
            Assert.Equal(-400, a.Z);
            Assert.Equal("Level 1", a.Level);
            Assert.True(a.Flip);
            Assert.Equal("Prefab 500", a.TypeName);
            Assert.Equal("Beton", a.Material);
            Assert.Single(a.Sets);
            Assert.Equal("Comments", a.Sets[0].Key);
            Assert.Equal("Foundation", a.Category.ToString());
            Assert.True(a.Replace);
            Assert.True(a.NoJoin);
        }

        [Fact]
        public void Extrude_InlinePoints_AlongAnId_WithName()
        {
            var a = ProfileArgs.ParseExtrude("--points 0,0 500,0 500,280 0,280 --along 4711 --name Deksteen recht");
            Assert.Null(a.Error);
            Assert.Null(a.ProfileName);
            Assert.Equal(4, a.Points.Count);
            Assert.Equal(new[] { 4711 }, a.AlongIds.ToArray());
            Assert.Equal("Deksteen recht", a.Name);
        }

        [Fact]
        public void Extrude_Vertical()
        {
            var a = ProfileArgs.ParseExtrude("Damwandpaar --vertical 15000 --at 1000 2000 --rotate 30");
            Assert.Null(a.Error);
            Assert.True(a.Vertical);
            Assert.Equal(15000, a.VerticalHeight);
            Assert.Equal(new double[] { 1000, 2000 }, a.At);
            Assert.Equal(30, a.Rotate);
        }

        [Theory]
        [InlineData("Deksteen", "--along")]
        [InlineData("--along 1", "needs a profile")]
        [InlineData("Deksteen --points 0,0 1,0 0,1 --along 1", "not both")]
        [InlineData("Deksteen --along 1 2 3", "x1 y1 x2 y2")]
        [InlineData("Deksteen --along 0 0 0 0", "no length")]
        [InlineData("Deksteen --along 1 --category wall", "generic, foundation or framing")]
        [InlineData("Deksteen --along 1 --at 0 0", "--vertical")]
        [InlineData("Deksteen --vertical 1000", "--at")]
        [InlineData("Deksteen --vertical -5 --at 0 0", "positive")]
        [InlineData("Deksteen --vertical 1000 --at 0 0 --flip", "does not go with --vertical")]
        [InlineData("Deksteen --along 1 --name X", "--name is for --points")]
        [InlineData("Deksteen --along 1 --set Comments", "Parameter=value")]
        [InlineData("Deksteen --along 1 --stem 600x4000", "Unknown flag")]
        public void Extrude_Errors(string args, string expected)
        {
            Assert.Contains(expected, ProfileArgs.ParseExtrude(args).Error);
        }

        // ---------------- sweep ----------------

        [Fact]
        public void Sweep_Chain()
        {
            var a = ProfileArgs.ParseSweep("Kade L --along 4711,4712 4713 --name Kade bocht --level Level 0");
            Assert.Null(a.Error);
            Assert.Equal("Kade L", a.ProfileName);
            Assert.Equal(new[] { 4711, 4712, 4713 }, a.AlongIds.ToArray());
            Assert.Equal("Kade bocht", a.Name);
            Assert.Equal("Level 0", a.Level);
        }

        [Theory]
        [InlineData("Kade", "--along")]
        [InlineData("Kade --along 0 0 10 10 x", "not an element id")]
        [InlineData("Kade --along 1 --vertical 100", "Unknown flag")]
        public void Sweep_Errors(string args, string expected)
        {
            Assert.Contains(expected, ProfileArgs.ParseSweep(args).Error);
        }

        // ---------------- lwall ----------------

        [Fact]
        public void Lwall_ThePlannedForm()
        {
            var a = ProfileArgs.ParseLwall("-92722 14921 -73243 10385 --stem 600x4000 --foot 2500x800");
            Assert.Null(a.Error);
            Assert.Equal(new double[] { -92722, 14921, -73243, 10385 }, a.Line);
            Assert.Equal(600, a.Sizes.Stem);
            Assert.Equal(4000, a.Sizes.Height);
            Assert.Equal(2500, a.Sizes.Foot);
            Assert.Equal(800, a.Sizes.FootThickness);
            Assert.Equal(0, a.Sizes.Toe);
            Assert.Null(a.Sizes.Slope);
            Assert.False(a.RightSide);
        }

        [Fact]
        public void Lwall_AllFlags()
        {
            var a = ProfileArgs.ParseLwall("0,0,20000,0 --stem 800 x 5000 --foot 3200*1000 --toe 400 --slope 600 --side right " +
                                           "--type-name Kade KW1 --level Level 0 --z -2500 --material Beton C30/37 --category foundation --no-join");
            Assert.Null(a.Error);
            Assert.Equal(800, a.Sizes.Stem);
            Assert.Equal(5000, a.Sizes.Height);
            Assert.Equal(3200, a.Sizes.Foot);
            Assert.Equal(1000, a.Sizes.FootThickness);
            Assert.Equal(400, a.Sizes.Toe);
            Assert.Equal(600, a.Sizes.Slope);
            Assert.True(a.RightSide);
            Assert.Equal("Kade KW1", a.TypeName);
            Assert.Equal(-2500, a.Z);
            Assert.Equal("Beton C30/37", a.Material);
            Assert.Equal("Foundation", a.Category.ToString());
            Assert.True(a.NoJoin);
        }

        [Fact]
        public void Lwall_AlongAWall()
        {
            var a = ProfileArgs.ParseLwall("--along 1543814 --stem 600x4000 --foot 2500x800 --type Kade 1");
            Assert.Null(a.Error);
            Assert.Equal(new[] { 1543814 }, a.AlongIds.ToArray());
            Assert.Equal("Kade 1", a.TypeName);
        }

        [Theory]
        [InlineData("0 0 1000 0 --foot 2500x800", "--stem")]
        [InlineData("0 0 1000 0 --stem 600x4000", "--foot")]
        [InlineData("0 0 1000 0 --stem 600 --foot 2500x800", "--stem '600'")]
        [InlineData("0 0 1000 --stem 600x4000 --foot 2500x800", "x1 y1 x2 y2")]
        [InlineData("0 0 1000 0 --along 5 --stem 600x4000 --foot 2500x800", "either")]
        [InlineData("--stem 600x4000 --foot 2500x800", "Usage")]
        [InlineData("0 0 1000 0 --stem 600x4000 --foot 500x800", "too narrow")]
        [InlineData("0 0 1000 0 --stem 600x4000 --foot 2500x800 --side up", "left or right")]
        [InlineData("0 0 1000 0 --stem 600x4000 --foot 2500x800 --category framing", "generic or foundation")]
        [InlineData("0 0 1000 0 --stem 600x4000 --foot 2500x800 --type A --type-name B", "not both")]
        [InlineData("0 0 1000 0 --stem 600x4000 --foot 2500x800 --points 0,0", "Unknown flag")]
        public void Lwall_Errors(string args, string expected)
        {
            Assert.Contains(expected, ProfileArgs.ParseLwall(args).Error);
        }

        [Theory]
        [InlineData("600x4000", 600, 4000)]
        [InlineData("600X4000", 600, 4000)]
        [InlineData("600 x 4000", 600, 4000)]
        [InlineData("600×4000", 600, 4000)]
        [InlineData("600*4000", 600, 4000)]
        public void TryParsePair(string text, double a, double b)
        {
            Assert.True(ProfileArgs.TryParsePair(text, out var x, out var y));
            Assert.Equal(a, x);
            Assert.Equal(b, y);
        }

        [Theory]
        [InlineData("gm", "Generic")]
        [InlineData("Fundering", "Foundation")]
        [InlineData("structural foundation", "Foundation")]
        [InlineData("ligger", "Framing")]
        public void CategoryNames(string text, string expected)
        {
            Assert.Equal(expected, ProfileArgs.ParseCategoryName(text).ToString());
            Assert.Null(ProfileArgs.ParseCategoryName("wall"));
        }

        [Fact]
        public void Usages_StartWithTheCommandName()
        {
            Assert.StartsWith("profile ", ProfileArgs.ProfileUsage);
            Assert.StartsWith("extrude ", ProfileArgs.ExtrudeUsage);
            Assert.StartsWith("sweep ", ProfileArgs.SweepUsage);
            Assert.StartsWith("lwall ", ProfileArgs.LwallUsage);
        }
    }
}
