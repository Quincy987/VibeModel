using System;
using System.Linq;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Modelling flags: wall/floor --type/--level, place --rotate/--set/--level/--along, the
    /// along-line layout maths, `set` with multi-word parameter names, and the 3D camera maths.
    /// </summary>
    public class ModelingArgsTests
    {
        // ---------------- NameMatch ----------------

        private static readonly NameMatch.Item[] WallTypes =
        {
            new NameMatch.Item(101, "Generic - 200mm"),
            new NameMatch.Item(102, "Generic - 300mm"),
            new NameMatch.Item(103, "Exterior - Brick on CMU"),
        };

        [Theory]
        [InlineData("Generic - 200mm", 101)]   // exact
        [InlineData("generic - 300MM", 102)]   // exact, ignoring case
        [InlineData("brick", 103)]             // unique substring
        [InlineData("102", 102)]               // id
        public void NameMatch_Finds(string q, int id)
        {
            var hit = NameMatch.Find(WallTypes, q, "wall type", out var err);
            Assert.Null(err);
            Assert.Equal(id, hit.Id);
        }

        [Fact]
        public void NameMatch_ExactCaseBeatsOtherCase()
        {
            var items = new[] { new NameMatch.Item(1, "Wall"), new NameMatch.Item(2, "wall") };
            Assert.Equal(2, NameMatch.Find(items, "wall", "wall type", out _).Id);
        }

        [Fact]
        public void NameMatch_Ambiguous_ListsTheMatches()
        {
            Assert.Null(NameMatch.Find(WallTypes, "Generic", "wall type", out var err));
            Assert.Contains("matches 2 wall types", err);
            Assert.Contains("'Generic - 200mm' (101)", err);
            Assert.Contains("'Generic - 300mm' (102)", err);
            Assert.DoesNotContain("Brick", err);
        }

        [Fact]
        public void NameMatch_Missing_ListsWhatThereIs()
        {
            Assert.Null(NameMatch.Find(WallTypes, "Steel", "wall type", out var err));
            Assert.Contains("No wall type matches 'Steel'", err);
            Assert.Contains("Exterior - Brick on CMU", err);
        }

        // ---------------- wall / floor ----------------

        [Fact]
        public void Wall_PositionalFormUnchanged()
        {
            var a = WallArgs.Parse("0 0 5000 0 3000");
            Assert.Null(a.Error);
            Assert.Equal(5000, a.X2);
            Assert.Equal(3000, a.Height);
            Assert.Null(a.Type);

            var c = WallArgs.Parse("0,0,5000,0");
            Assert.Null(c.Error);
            Assert.Null(c.Height);
        }

        [Fact]
        public void Wall_Flags()
        {
            var a = WallArgs.Parse("-92722 14921 -73243 10385 --type Generic - 200mm --level Level 1 --base-offset -500");
            Assert.Null(a.Error);
            Assert.Equal(-92722, a.X1);
            Assert.Equal("Generic - 200mm", a.Type);
            Assert.Equal("Level 1", a.Level);
            Assert.Equal(-500, a.BaseOffset);
            Assert.Null(a.Height);

            var t = WallArgs.Parse("0 0 5000 0 --top-level Level 2 --top-offset -100 --type \"Basic Wall 300\"");
            Assert.Null(t.Error);
            Assert.Equal("Level 2", t.TopLevel);
            Assert.Equal(-100, t.TopOffset);
            Assert.Equal("Basic Wall 300", t.Type);
        }

        [Theory]
        [InlineData("0 0 5000 0 --top-offset 100", "--top-offset needs --top-level")]
        [InlineData("0 0 5000 0 3000 --top-level Level 2", "either a height or --top-level")]
        [InlineData("0 0 0 0", "identical")]
        [InlineData("0 0 5000", "Usage")]
        [InlineData("0 0 5000 0 --type", "--type needs")]
        [InlineData("0 0 5000 0 --colour red", "Unknown flag")]
        [InlineData("0 0 5000 0 --base-offset abc", "not a number")]
        public void Wall_Errors(string args, string expected)
        {
            Assert.Contains(expected, WallArgs.Parse(args).Error);
        }

        [Fact]
        public void Floor_FlagsLeaveThePoints()
        {
            var plain = FloorArgs.Parse("0,0 5000,0 5000,5000");
            Assert.Equal("0,0 5000,0 5000,5000", plain.Points);
            Assert.Null(plain.Type);

            var f = FloorArgs.Parse("0,0 5000,0 5000,5000 --type Concrete 200 --level Level 0");
            Assert.Null(f.Error);
            Assert.Equal("0,0 5000,0 5000,5000", f.Points);
            Assert.Equal("Concrete 200", f.Type);
            Assert.Equal("Level 0", f.Level);
        }

        // ---------------- place / placeid ----------------

        [Fact]
        public void Place_PositionalFormUnchanged()
        {
            var a = PlaceArgs.Parse("Door Single-Flush 5000 2500", false);
            Assert.Null(a.Error);
            Assert.Equal("Door", a.Family);
            Assert.Equal("Single-Flush", a.TypeName);
            Assert.Equal(5000, a.X);
            Assert.Equal(2500, a.Y);
            Assert.Null(a.Z);
            Assert.Equal(0, a.Rotate);
            Assert.False(a.Along);

            var z = PlaceArgs.Parse("67890 100 200 -3000", true);
            Assert.Null(z.Error);
            Assert.Equal(67890, z.TypeId);
            Assert.Equal(-3000, z.Z);
        }

        [Fact]
        public void Place_QuotedNames()
        {
            var a = PlaceArgs.Parse("\"Pile family\" \"Type A\" 1 2", false);
            Assert.Null(a.Error);
            Assert.Equal("Pile family", a.Family);
            Assert.Equal("Type A", a.TypeName);
        }

        [Fact]
        public void Place_RotateSetLevel()
        {
            var a = PlaceArgs.Parse("67890 5000 2500 --rotate 30 --set \"Comments=hello world\" --set Base Offset=-2000 --level Level 1", true);
            Assert.Null(a.Error);
            Assert.Equal(30, a.Rotate);
            Assert.False(a.AutoRotate);
            Assert.Equal("Level 1", a.Level);
            Assert.Equal(2, a.Sets.Count);
            Assert.Equal("Comments", a.Sets[0].Key);
            Assert.Equal("hello world", a.Sets[0].Value);
            Assert.Equal("Base Offset", a.Sets[1].Key);
            Assert.Equal("-2000", a.Sets[1].Value);

            Assert.Equal(-45, PlaceArgs.Parse("1 0 0 --rotate -45°", true).Rotate);
        }

        [Fact]
        public void Place_AlongById()
        {
            var a = PlaceArgs.Parse("Pile T --along 4711 --spacing 2500 --rotate auto+90", false);
            Assert.Null(a.Error);
            Assert.True(a.Along);
            Assert.Equal(4711, a.AlongId);
            Assert.Null(a.AlongLine);
            Assert.Equal(2500, a.Spacing);
            Assert.True(a.AutoRotate);
            Assert.Equal(90, a.Rotate);
        }

        [Fact]
        public void Place_AlongByCoordinates()
        {
            var a = PlaceArgs.Parse("5 --along 0 0 12000 0 --count 6 --offset 500 --rotate auto", true);
            Assert.Null(a.Error);
            Assert.Equal(new double[] { 0, 0, 12000, 0 }, a.AlongLine);
            Assert.Equal(6, a.Count);
            Assert.Equal(500, a.Offset);
            Assert.True(a.AutoRotate);
            Assert.Equal(0, a.Rotate);
        }

        [Theory]
        [InlineData("Pile T 0 0 --along 1 --spacing 100", "give no x y")]
        [InlineData("Pile T 0 0 --rotate auto", "needs --along")]
        [InlineData("Pile T 0 0 --spacing 100", "only works with --along")]
        [InlineData("Pile T --along 1", "--spacing <mm> and/or --count")]
        [InlineData("Pile T --along 1 --count 0", "whole number")]
        [InlineData("Pile T --along 1 --count 2.5", "whole number")]
        [InlineData("Pile T --along 1 --spacing -5", "positive")]
        [InlineData("Pile T --along 1 2 3 --count 2", "line id")]
        [InlineData("Pile T --along 5 5 5 5 --count 2", "no length")]
        [InlineData("Pile T 0 0 --rotate abc", "not a number of degrees")]
        [InlineData("Pile T --rotate auto90x --along 1 --count 2", "auto+90")]
        [InlineData("Pile T 0 0 --set NoEquals", "Parameter=value")]
        [InlineData("Pile T 0 0 --foo 1", "Unknown flag")]
        [InlineData("Pile T 0", "Usage")]
        public void Place_Errors(string args, string expected)
        {
            Assert.Contains(expected, PlaceArgs.Parse(args, false).Error);
        }

        // ---------------- along-line layout ----------------

        [Fact]
        public void Along_SpacingDividesEvenly()
        {
            var d = AlongLayout.Distances(12000, 3000, null, 0, out var s, out var err);
            Assert.Null(err);
            Assert.Equal(new double[] { 0, 3000, 6000, 9000, 12000 }, d);
            Assert.Equal(3000, s);
        }

        [Fact]
        public void Along_SpacingIsReducedNeverIncreased()
        {
            var d = AlongLayout.Distances(12100, 3000, null, 0, out var s, out _);
            Assert.Equal(6, d.Count);
            Assert.Equal(2420, s, 6);
            Assert.Equal(12100, d.Last(), 6);
        }

        [Fact]
        public void Along_OnePercentSlackKeepsTheCount()
        {
            var d = AlongLayout.Distances(12020, 3000, null, 0, out var s, out _);
            Assert.Equal(5, d.Count);
            Assert.Equal(3005, s, 6);
        }

        [Fact]
        public void Along_CountWithOffset()
        {
            var d = AlongLayout.Distances(10000, null, 6, 500, out var s, out _);
            Assert.Equal(1800, s, 6);
            Assert.Equal(new double[] { 500, 2300, 4100, 5900, 7700, 9500 }, d.Select(x => Math.Round(x, 6)));
        }

        [Fact]
        public void Along_CountOne_IsTheMiddle()
        {
            Assert.Equal(new double[] { 5000 }, AlongLayout.Distances(10000, null, 1, 0, out _, out _));
        }

        [Fact]
        public void Along_SpacingAndCount_AreExact()
        {
            var d = AlongLayout.Distances(10000, 2000, 4, 1000, out var s, out _);
            Assert.Equal(new double[] { 1000, 3000, 5000, 7000 }, d);
            Assert.Equal(2000, s);
        }

        [Fact]
        public void Along_SpacingAndCount_ThatDoNotFit_AreRefused()
        {
            Assert.Null(AlongLayout.Distances(10000, 3000, 5, 0, out _, out var err));
            Assert.Contains("needs 12000 mm, the line is 10000 mm", err);
        }

        [Fact]
        public void Along_OffsetLongerThanTheLine_IsRefused()
        {
            Assert.Null(AlongLayout.Distances(1000, 500, null, 600, out _, out var err));
            Assert.Contains("--offset", err);
            Assert.Null(AlongLayout.Distances(1000, null, 3, 500, out _, out err));
            Assert.Contains("No room for 3", err);
        }

        [Fact]
        public void Along_TooMany_IsRefused()
        {
            Assert.Null(AlongLayout.Distances(1000000, 1, null, 0, out _, out var err));
            Assert.Contains("at most", err);
        }

        [Fact]
        public void Along_LinePoint()
        {
            var p = AlongLayout.LinePoint(0, 0, 3000, 4000, 2500);
            Assert.Equal(1500, p[0], 6);
            Assert.Equal(2000, p[1], 6);
            var q = AlongLayout.LinePoint(1000, 1000, 1000, -9000, 4000);
            Assert.Equal(1000, q[0], 6);
            Assert.Equal(-3000, q[1], 6);
        }

        [Theory]
        [InlineData(1, 0, 0, 0)]
        [InlineData(0, 1, 0, 90)]
        [InlineData(-1, 0, 0, 180)]
        [InlineData(0, -1, 90, 0)]     // 270 + 90 wraps to 0
        [InlineData(1, 1, -90, 315)]
        [InlineData(1, 1, 0, 45)]
        public void Along_AutoRotation(double dx, double dy, double extra, double expected)
        {
            Assert.Equal(expected, AlongLayout.AngleDeg(dx, dy, extra), 6);
        }

        // ---------------- set ----------------

        [Fact]
        public void Set_ThreeTokenForm_IsStillACandidate()
        {
            Assert.True(SetArgs.TryParseId("123 Comments Hello World", out int id, out var rest));
            Assert.Equal(123, id);
            var c = SetArgs.Candidates(rest);
            Assert.Contains(c, kv => kv.Key == "Comments" && kv.Value == "Hello World");
        }

        [Fact]
        public void Set_QuotedName()
        {
            var c = SetArgs.Candidates("\"Base Offset\" 500");
            Assert.Single(c);
            Assert.Equal("Base Offset", c[0].Key);
            Assert.Equal("500", c[0].Value);

            var e = SetArgs.Candidates("\"Base Offset\"=-250");
            Assert.Equal("Base Offset", e[0].Key);
            Assert.Equal("-250", e[0].Value);
        }

        [Fact]
        public void Set_EqualsForm_ComesFirst()
        {
            var c = SetArgs.Candidates("Base Offset=500");
            Assert.Equal("Base Offset", c[0].Key);
            Assert.Equal("500", c[0].Value);
        }

        [Fact]
        public void Set_UnquotedNameWithSpaces_LongestNameFirst()
        {
            var c = SetArgs.Candidates("Base Offset 500");
            Assert.Equal("Base Offset", c[0].Key);
            Assert.Equal("500", c[0].Value);
            Assert.Equal("Base", c[1].Key);
            Assert.Equal("Offset 500", c[1].Value);
        }

        [Fact]
        public void Set_ValueWithEquals_FallsBackToTheFirstWord()
        {
            var c = SetArgs.Candidates("Comments a=b");
            Assert.Equal("Comments a", c[0].Key);
            Assert.Contains(c, kv => kv.Key == "Comments" && kv.Value == "a=b");
        }

        [Fact]
        public void Set_QuotedValue_IsUnquoted()
        {
            Assert.Contains(SetArgs.Candidates("Comments \"Hello World\""), kv => kv.Key == "Comments" && kv.Value == "Hello World");
        }

        [Fact]
        public void Set_BadId()
        {
            Assert.False(SetArgs.TryParseId("abc Comments x", out _, out _));
            Assert.Empty(SetArgs.Candidates(""));
        }

        // ---------------- camera ----------------

        [Fact]
        public void Camera_ZwStandsSouthWestLookingNorthEastDown()
        {
            var f = CameraMath.Forward("zw", 30);
            Assert.Equal(Math.Cos(Math.PI / 6) / Math.Sqrt(2), f[0], 6);
            Assert.Equal(f[0], f[1], 9);
            Assert.Equal(-0.5, f[2], 6);
            Assert.Equal("from the south-west, looking north-east, 30° down", CameraMath.Describe(f));
        }

        [Theory]
        [InlineData("n", "from the north, looking south, 30° down")]
        [InlineData("o", "from the east, looking west, 30° down")]
        [InlineData("zo", "from the south-east, looking north-west, 30° down")]
        [InlineData("nw", "from the north-west, looking south-east, 30° down")]
        public void Camera_CompassDescriptions(string key, string expected)
        {
            Assert.Equal(expected, CameraMath.Describe(CameraMath.Forward(key, 30)));
        }

        [Fact]
        public void Camera_UpIsPerpendicularAndUpward()
        {
            foreach (var k in new[] { "n", "no", "o", "zo", "z", "zw", "w", "nw", "iso" })
            {
                var f = CameraMath.Forward(k, 30);
                var u = CameraMath.Up(f);
                Assert.Equal(0, f[0] * u[0] + f[1] * u[1] + f[2] * u[2], 9);
                Assert.True(u[2] > 0, k);
                Assert.Equal(1, Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]), 9);
            }
        }

        [Fact]
        public void Camera_TopLooksDownWithNorthUp()
        {
            var f = CameraMath.Forward("top", 30);
            Assert.Equal(new double[] { 0, 0, -1 }, f);
            Assert.Equal(new double[] { 0, 1, 0 }, CameraMath.Up(f));
            Assert.Contains("straight down", CameraMath.Describe(f));
        }

        [Fact]
        public void Camera_IsoIsRevitsDefault()
        {
            var f = CameraMath.Forward("iso", 30);
            var s = 1 / Math.Sqrt(3);
            Assert.Equal(-s, f[0], 9);
            Assert.Equal(s, f[1], 9);
            Assert.Equal(-s, f[2], 9);
        }

        [Fact]
        public void Camera_EyeIsBehindTheTarget()
        {
            var e = CameraMath.Eye(new double[] { 100, 200, 0 }, new double[] { 0, 1, 0 }, 50);
            Assert.Equal(new double[] { 100, 150, 0 }, e);
        }

        [Theory]
        [InlineData("SW", "zw")]
        [InlineData("e", "o")]
        [InlineData("ne", "no")]
        [InlineData("s", "z")]
        [InlineData("boven", "top")]
        [InlineData("zuidwest", "zw")]
        [InlineData("x", null)]
        public void Camera_Aliases(string key, string canonical)
        {
            Assert.Equal(canonical, CameraMath.Canonical(key));
        }

        [Fact]
        public void Look_ViewNameBeforeTheDirection()
        {
            var s = LookSpec.ParseLook("VM hoek zuidelijke inham zw --at 3090893,3112719 --margin 1800", out var view);
            Assert.Null(s.Error);
            Assert.Equal("VM hoek zuidelijke inham", view);
            Assert.Equal("zw", s.Key);
            Assert.Equal(new double[] { 3090893, 3112719 }, s.AtNumbers);
            Assert.Equal(1800, s.Margin);
            Assert.True(s.AtIsIds(_ => false));   // not 3 numbers → ids
        }

        [Fact]
        public void Look_ActiveViewAndFlags()
        {
            var s = LookSpec.ParseLook("iso --distance 5000 --pitch 45 --box", out var view);
            Assert.Null(s.Error);
            Assert.Null(view);
            Assert.Equal("iso", s.Key);
            Assert.Equal(5000, s.Distance);
            Assert.Equal(45, s.Pitch);
            Assert.True(s.Box);
        }

        [Fact]
        public void Look_CameraPointAndTargetPoint()
        {
            var s = LookSpec.ParseLook("4711 -1000 -1000 3000 --at 0 0 0", out var view);
            Assert.Null(s.Error);
            Assert.Equal("4711", view);
            Assert.Null(s.Key);
            Assert.Equal(new double[] { -1000, -1000, 3000 }, s.Eye);
            Assert.False(s.AtIsIds(_ => false));                 // 0 0 0 → a point
            var ids = LookSpec.ParseLook("zw --at 11 12 13", out _);
            Assert.True(ids.AtIsIds(_ => true));                 // three existing ids
            Assert.False(ids.AtIsIds(v => v != 13));             // one is not an element → a point
        }

        [Fact]
        public void Look_Selected()
        {
            Assert.True(LookSpec.ParseLook("zw --at selected", out _).AtSelected);
        }

        [Theory]
        [InlineData("", "Usage")]
        [InlineData("My view", "No direction")]
        [InlineData("zw --foo 1", "Unknown flag")]
        [InlineData("zw --at 1.5 2", "whole numbers")]
        [InlineData("zw --at abc", "not a number")]
        [InlineData("zw --pitch 95", "between -89 and 89")]
        [InlineData("zw --distance 0", "positive")]
        [InlineData("1 2 3 --distance 5000", "camera position")]
        public void Look_Errors(string args, string expected)
        {
            Assert.Contains(expected, LookSpec.ParseLook(args, out _).Error);
        }

        [Fact]
        public void ViewArgs_CameraFlags()
        {
            var r = ViewArgs.Parse("3d --name Hoek zw --look zw --at 1543814,1543815 --margin 1500 --box");
            Assert.Equal("3d", r.Remaining);
            Assert.Equal("Hoek zw", r.Name);
            Assert.Equal("zw", r.Look);
            Assert.Equal("1543814,1543815", r.At);
            Assert.Equal("1500", r.Margin);
            Assert.True(r.Box);
            Assert.True(r.AnyCameraFlag);

            var p = ViewArgs.Parse("3d --look 100 200 300 --pitch 45 --activate");
            Assert.Equal("3d", p.Remaining);
            Assert.Equal("100 200 300", p.Look);
            Assert.Equal("45", p.Pitch);
            Assert.True(p.Activate);

            Assert.False(ViewArgs.Parse("plan Level 1 --activate").AnyCameraFlag);
        }
    }
}
