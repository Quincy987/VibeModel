using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Plan 12 — sheet assembly: argument parsing for section/viewport/dimension/text/tag/sheet.</summary>
    public class SheetArgsTests
    {
        [Fact]
        public void FlagArgs_ValuesRunToNextFlag_NegativeNumbersAreValues()
        {
            var f = FlagArgs.Parse("0001 12,34 --at -92722 14921 --name Doorsnede 1-1 --activate");
            Assert.Equal(new[] { "0001", "12,34" }, f.Positional);
            Assert.Equal(new[] { "-92722", "14921" }, f.Values("at"));
            Assert.Equal("Doorsnede 1-1", f.Text("name"));
            Assert.True(f.Has("activate"));
            Assert.Empty(f.Values("activate"));
        }

        [Fact]
        public void FlagArgs_QuotesGroupAndRepeatsAreKept()
        {
            var f = FlagArgs.Parse("--set \"Opsteller=Q. de Klerk\" --set Status=Concept --name=\"Situatie 0\"");
            var sets = f.All("set").Select(v => string.Join(" ", v)).ToList();
            Assert.Equal(new[] { "Opsteller=Q. de Klerk", "Status=Concept" }, sets);
            Assert.Equal("Situatie 0", f.Text("name"));
        }

        [Fact]
        public void FlagArgs_UnknownFlagIsReported()
        {
            var f = FlagArgs.Parse("1 2 --bogus 3");
            Assert.Contains("--bogus", f.Unknown("at"));
            Assert.Null(f.Unknown("bogus"));
        }

        [Fact]
        public void Section_FourCoordinates()
        {
            var a = SectionArgs.Parse("-92722 14921 -73243 10385 --depth 1500 --name Doorsnede 1-1 --scale 50 --activate");
            Assert.Null(a.Error);
            Assert.Equal(new[] { -92722.0, 14921, -73243, 10385 }, a.Line);
            Assert.Equal(1500, a.Depth);
            Assert.Equal("Doorsnede 1-1", a.Name);
            Assert.Equal(50, a.Scale);
            Assert.True(a.Activate);
            Assert.Null(a.WallId);
        }

        [Fact]
        public void Section_WallIdWithOffset()
        {
            var a = SectionArgs.Parse("1543814 --offset 3000 --no-template");
            Assert.Null(a.Error);
            Assert.Equal(1543814, a.WallId);
            Assert.Equal(3000, a.Offset);
            Assert.True(a.NoTemplate);
        }

        [Theory]
        [InlineData("1 2 3", "4 coordinates")]
        [InlineData("0 0 0 0", "no length")]
        [InlineData("0 0 10 0 --depth -5", "positive")]
        [InlineData("0 0 10 0 --scale 2.5", "whole number")]
        [InlineData("0 0 10 0 --top 0 --bottom 100", "above")]
        [InlineData("0 0 10 0 --depht 100", "Unknown flag")]
        public void Section_Errors(string args, string fragment)
            => Assert.Contains(fragment, SectionArgs.Parse(args).Error);

        [Fact]
        public void Viewport_GridWithAreaAndScale()
        {
            var a = ViewportArgs.Parse("0001 101,102,Doorsnede 1-1 --grid 3x1 --area 20 20 980 300 --scale 50 --title off");
            Assert.Null(a.Error);
            Assert.Equal("0001", a.Sheet);
            Assert.Equal(new[] { "101", "102", "Doorsnede 1-1" }, a.Views);
            Assert.Equal(3, a.Cols);
            Assert.Equal(1, a.Rows);
            Assert.Equal(new[] { 20.0, 20, 980, 300 }, a.Area);
            Assert.Equal(50, a.Scale);
            Assert.False(a.Title);
        }

        [Fact]
        public void Viewport_SingleAt()
        {
            var a = ViewportArgs.Parse("A101 Bovenaanzicht demo --at 500 650");
            Assert.Null(a.Error);
            Assert.Equal(new[] { "Bovenaanzicht demo" }, a.Views);
            Assert.Equal(new[] { 500.0, 650 }, a.At);
            Assert.Equal(10, a.Margin);
        }

        [Fact]
        public void Viewport_MoveAndRemove()
        {
            var m = ViewportArgs.Parse("--move 4711 300 -20");
            Assert.Null(m.Error);
            Assert.Equal(4711, m.MoveId);
            Assert.Equal(new[] { 300.0, -20 }, m.MoveTo);
            var r = ViewportArgs.Parse("--remove 4711");
            Assert.Equal(4711, r.RemoveId);
        }

        [Theory]
        [InlineData("0001", "at least one view")]
        [InlineData("0001 1,2,3 --grid 2x1", "2 cells for 3 views")]
        [InlineData("0001 1 --grid three", "<cols>x<rows>")]
        [InlineData("0001 1,2 --at 10 10", "single view")]
        [InlineData("0001 1 --title maybe", "on or off")]
        [InlineData("0001 1 --area 10 10 5 5", "x1 > x0")]
        [InlineData("0001 1 --at 10", "2 number")]
        [InlineData("--move 1 2", "viewport --move")]
        public void Viewport_Errors(string args, string fragment)
            => Assert.Contains(fragment, ViewportArgs.Parse(args).Error);

        [Theory]
        [InlineData("3x2", 3, 2)]
        [InlineData("1X5", 1, 5)]
        [InlineData("2×2", 2, 2)]
        public void Viewport_GridForms(string s, int c, int r)
        {
            Assert.True(ViewportArgs.TryGrid(s, out int cols, out int rows));
            Assert.Equal(c, cols);
            Assert.Equal(r, rows);
        }

        [Fact]
        public void Dimension_Ids()
        {
            var a = DimensionArgs.Parse("101 102,103 --dir y --offset 500");
            Assert.Null(a.Error);
            Assert.Equal(new[] { 101, 102, 103 }, a.Ids);
            Assert.Equal(DimDirection.Y, a.Dir);
            Assert.Equal(500, a.Offset);
        }

        [Fact]
        public void Dimension_PointsAndLevels()
        {
            var p = DimensionArgs.Parse("--points 0 0 5000 0");
            Assert.Null(p.Error);
            Assert.Equal(new[] { 0.0, 0, 5000, 0 }, p.Points);
            var l = DimensionArgs.Parse("--levels");
            Assert.True(l.Levels);
            Assert.Null(l.LevelsX);
            var lx = DimensionArgs.Parse("--levels -4000");
            Assert.Equal(-4000, lx.LevelsX);
        }

        [Theory]
        [InlineData("", "element ids")]
        [InlineData("12.5 13", "not an element id")]
        [InlineData("1 2 --dir diagonal", "x, y or auto")]
        [InlineData("--points 0 0 0 0", "same")]
        [InlineData("--levels 1 2", "at most one")]
        [InlineData("5 --levels", "no element ids")]
        public void Dimension_Errors(string args, string fragment)
            => Assert.Contains(fragment, DimensionArgs.Parse(args).Error);

        [Fact]
        public void Text_WithLeaderSizeAndNewline()
        {
            var a = TextArgs.Parse("-1200 850 Damwand AZ26-700\\nlg. 15.05m --size 2.5 --leader -400 -1030");
            Assert.Null(a.Error);
            Assert.Equal(-1200, a.X);
            Assert.Equal(850, a.Y);
            Assert.Equal("Damwand AZ26-700\nlg. 15.05m", a.Text);
            Assert.Equal(2.5, a.Size);
            Assert.Equal(new[] { -400.0, -1030 }, a.Leader);
        }

        [Theory]
        [InlineData("10 20", "Usage")]
        [InlineData("x 20 hello", "Usage")]
        [InlineData("10 20 hi --size 0", "paper in mm")]
        [InlineData("10 20 hi --leader 5", "2 number")]
        public void Text_Errors(string args, string fragment)
            => Assert.Contains(fragment, TextArgs.Parse(args).Error);

        [Fact]
        public void Tag_PlainIdStillParses()
        {
            var a = TagArgs.Parse(" 12345 ");
            Assert.Null(a.Error);
            Assert.Equal(12345, a.Id);
            Assert.False(a.Leader);
            Assert.Null(a.At);
        }

        [Fact]
        public void Tag_LeaderAndAt()
        {
            var a = TagArgs.Parse("12345 --leader --at 1000 2000");
            Assert.Null(a.Error);
            Assert.True(a.Leader);
            Assert.Equal(new[] { 1000.0, 2000 }, a.At);
            Assert.Contains("takes no value", TagArgs.Parse("1 --leader 5 6").Error);
        }

        [Fact]
        public void Sheet_NoFlags_Unchanged()
        {
            var a = SheetCreateArgs.Parse("A1 Title Block 12345");
            Assert.Null(a.Error);
            Assert.Equal("A1 Title Block 12345", a.Remaining);
            Assert.Null(a.Name);
        }

        [Fact]
        public void Sheet_Flags()
        {
            var a = SheetCreateArgs.Parse("IB A0 --number 0001 --name Situatie 0 demo --set \"Opsteller=Q. de Klerk\" --set Status=Concept");
            Assert.Null(a.Error);
            Assert.Equal("IB A0", a.Remaining);
            Assert.Equal("0001", a.Number);
            Assert.Equal("Situatie 0 demo", a.Name);
            Assert.Equal(2, a.Sets.Count);
            Assert.Equal("Opsteller", a.Sets[0].Key);
            Assert.Equal("Q. de Klerk", a.Sets[0].Value);
            Assert.Contains("Parameter=Value", SheetCreateArgs.Parse("--set nothing").Error);
        }
    }

    /// <summary>Plan 12 — paper layout maths, overlap checks, section frame, dimension bookkeeping.</summary>
    public class SheetLayoutTests
    {
        private static readonly MmRect A0 = new MmRect(0, 0, 1189, 841);

        [Fact]
        public void UsableArea_KeepsMarginAndStampStrip()
        {
            var area = SheetLayout.UsableArea(A0, 10, SheetLayout.DefaultReserve(A0.W));
            Assert.Equal(10, area.X0);
            Assert.Equal(10, area.Y0);
            Assert.Equal(1189 - 10 - 200, area.X1);
            Assert.Equal(831, area.Y1);
            Assert.Equal(0, SheetLayout.DefaultReserve(297));
        }

        [Fact]
        public void UsableArea_FallsBackWhenReserveEatsEverything()
        {
            var area = SheetLayout.UsableArea(new MmRect(0, 0, 210, 297), 10, 500);
            Assert.Equal(190, area.W, 3);
        }

        [Fact]
        public void Cells_AreRowMajorTopToBottom_AndInsideTheArea()
        {
            var area = new MmRect(0, 0, 900, 600);
            var cells = SheetLayout.Cells(area, 3, 2, 10);
            Assert.Equal(6, cells.Count);
            // first cell top-left, last bottom-right
            Assert.Equal(5, cells[0].X0, 3);
            Assert.Equal(595, cells[0].Y1, 3);
            Assert.Equal(895, cells[5].X1, 3);
            Assert.Equal(5, cells[5].Y0, 3);
            Assert.Equal(290, cells[0].W, 3);
            Assert.Equal(290, cells[0].H, 3);
            Assert.True(cells.All(c => c.Inside(area)));
            Assert.Empty(SheetLayout.Overlaps(cells.Select((c, i) => new KeyValuePair<string, MmRect>("c" + i, c)).ToList()));
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(4, 2, 2)]
        [InlineData(5, 3, 2)]
        [InlineData(9, 3, 3)]
        public void AutoGrid(int n, int cols, int rows)
        {
            SheetLayout.AutoGrid(n, out int c, out int r);
            Assert.Equal(cols, c);
            Assert.Equal(rows, r);
        }

        [Fact]
        public void FitWarning_NamesSizesAndScale()
        {
            var cell = new MmRect(0, 0, 280, 200);
            Assert.False(SheetLayout.Fits(420, 300, cell));
            int s = SheetLayout.SuggestScale(420 * 50, 300 * 50, cell);
            Assert.Equal(100, s);
            var w = SheetLayout.FitWarning("'Doorsnede 1'", 50, 420, 300, cell, s);
            Assert.Equal("WARN: view 'Doorsnede 1' at 1:50 is 420×300 mm, cell is 280×200 mm — use --scale 100, a bigger grid/--area, or crop the view", w);
        }

        [Fact]
        public void Overlaps_AndOffPaper()
        {
            var rects = new List<KeyValuePair<string, MmRect>>
            {
                new KeyValuePair<string, MmRect>("a", new MmRect(10, 10, 300, 300)),
                new KeyValuePair<string, MmRect>("b", new MmRect(290, 10, 500, 300)),    // overlaps a by 10 mm
                new KeyValuePair<string, MmRect>("c", new MmRect(500, 10, 700, 300)),    // touches b: not an overlap
                new KeyValuePair<string, MmRect>("d", new MmRect(1100, 700, 1250, 900)), // off the A0
            };
            var o = SheetLayout.Overlaps(rects);
            Assert.Single(o);
            Assert.Equal("a", o[0].Item1);
            Assert.Equal("b", o[0].Item2);
            Assert.Equal(new[] { "d" }, SheetLayout.OffPaper(rects, A0));
        }

        [Theory]
        [InlineData(1189, 841, "A0")]
        [InlineData(841, 1189, "A0")]
        [InlineData(841, 594, "A1")]
        [InlineData(420, 297, "A3")]
        [InlineData(1000, 500, null)]
        public void PaperName(double w, double h, string name) => Assert.Equal(name, SheetLayout.PaperName(w, h));

        [Fact]
        public void SectionFrame_EastLine_LooksNorth_ToTheLeft()
        {
            var f = SectionFrame.FromLine(0, 0, 10000, 0);
            Assert.Equal(5000, f.Ox, 6);
            Assert.Equal(0, f.Oy, 6);
            Assert.Equal(10000, f.Length, 6);
            Assert.Equal(1, f.Dx, 6);
            // looks to the left of the walking direction: walking east → looking north
            Assert.Equal(0, f.Lx, 6);
            Assert.Equal(1, f.Ly, 6);
            Assert.Equal("north", SectionFrame.Compass(f.Lx, f.Ly));
        }

        [Fact]
        public void SectionFrame_IsRightHanded()
        {
            var f = SectionFrame.FromLine(-92722, 14921, -73243, 10385);
            // Revit input basis: (-D) × Z must equal the looking direction (BasisZ)
            Assert.Equal(-f.Dy, f.Lx, 9);
            Assert.Equal(f.Dx, f.Ly, 9);
            Assert.Equal(1, Math.Sqrt(f.Dx * f.Dx + f.Dy * f.Dy), 9);
        }

        [Fact]
        public void SectionFrame_AcrossWall_IsPerpendicularThroughMidpoint()
        {
            // wall along +X from (0,0) to (8000,0); section 2x2150 mm long across it
            var f = SectionFrame.AcrossWall(0, 0, 8000, 0, 2150);
            Assert.Equal(4000, f.Ox, 6);
            Assert.Equal(0, f.Oy, 6);
            Assert.Equal(4300, f.Length, 6);
            Assert.Equal(0, f.Dx * 1 + f.Dy * 0, 6);   // perpendicular to the wall
            Assert.Null(SectionFrame.AcrossWall(0, 0, 0, 0, 100));
            Assert.Null(SectionFrame.FromLine(5, 5, 5, 5));
        }

        [Theory]
        [InlineData(1, 0, "east")]
        [InlineData(0, 1, "north")]
        [InlineData(-1, -1, "south-west")]
        [InlineData(0.2, -1, "south")]
        public void Compass(double x, double y, string word) => Assert.Equal(word, SectionFrame.Compass(x, y));

        [Fact]
        public void Extremes_PicksOuterTwo()
        {
            Assert.Equal(new[] { 2, 0 }, DimensionMath.Extremes(new[] { 9.0, 5, 1, 7 }, 0.01));
            Assert.Equal(new[] { 0 }, DimensionMath.Extremes(new[] { 3.0, 3.0 }, 0.01));
            Assert.Empty(DimensionMath.Extremes(new double[0], 0.01));
        }

        [Fact]
        public void OrderDistinct_SortsAndDropsCoincident()
        {
            Assert.Equal(new[] { 1, 3, 0 }, DimensionMath.OrderDistinct(new[] { 10.0, 0, 10.0001, 4 }, 0.01));
        }

        [Fact]
        public void Segments_Text()
        {
            Assert.Equal("300 + 2700 = 3000 mm", DimensionMath.Segments(new[] { 300.0, 2700 }));
            Assert.Equal("3000 mm", DimensionMath.Segments(new[] { 3000.0 }));
        }
    }

    /// <summary>Plan 12 — sheetinfo: office stamp patterns, overlap grading, overhang.</summary>
    public class SheetInfoLogicTests
    {
        private static readonly string[] Fields =
        {
            "NL_META_DOCUMENTSTATUS", "NL_META_PARAAF", "Drawn By", "IB_AANVULLENDE_BEPALING_1",
            "IB_AANVULLENDE_BEPALING_2", "IB_AANVULLENDE_BEPALING_4", "Sheet Issue Date"
        };

        [Fact]
        public void Stamp_GlobRangeAndPlain()
        {
            var names = StampPatterns.Expand(new[] { "NL_META_*", "IB_AANVULLENDE_BEPALING_1..5", "drawn by", "Missing Field" },
                Fields, out var unmatched);
            Assert.Equal(new[] { "NL_META_DOCUMENTSTATUS", "NL_META_PARAAF", "IB_AANVULLENDE_BEPALING_1", "IB_AANVULLENDE_BEPALING_2",
                                 "IB_AANVULLENDE_BEPALING_4", "Drawn By" }, names);
            Assert.Equal(new[] { "Missing Field" }, unmatched);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("  ", true)]
        [InlineData("ㅤ", true)]      // the invisible filler IB title blocks use
        [InlineData("Concept", false)]
        public void Stamp_Blank(string v, bool blank) => Assert.Equal(blank, StampPatterns.IsBlank(v));

        [Fact]
        public void OverlapLines_GradeSmallVsLarge()
        {
            var rects = new List<KeyValuePair<string, MmRect>>
            {
                new KeyValuePair<string, MmRect>("'Bovenaanzicht'", new MmRect(22, 611, 1178, 846)),
                new KeyValuePair<string, MmRect>("'Vooraanzicht'", new MmRect(32, 527, 1144, 638)),   // 27 mm strip: small
                new KeyValuePair<string, MmRect>("'A'", new MmRect(0, 0, 100, 100)),
                new KeyValuePair<string, MmRect>("'B'", new MmRect(20, 20, 120, 120)),               // 64% of B: large
            };
            var lines = SheetLayout.OverlapLines(rects);
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("INFO  'Bovenaanzicht' and 'Vooraanzicht' boxes overlap 1112×27 mm", lines[0]);
            Assert.StartsWith("WARN  'A' and 'B' boxes overlap 80×80 mm", lines[1]);
        }

        [Fact]
        public void Overhang_IsTheWorstSide()
        {
            var paper = new MmRect(0, 0, 1189, 841);
            Assert.Equal(5, new MmRect(22, 611, 1178, 846).Overhang(paper), 6);
            Assert.Equal(17, new MmRect(972, 77, 1206, 271).Overhang(paper), 6);
            Assert.True(new MmRect(10, 10, 20, 20).Overhang(paper) < 0);
        }
    }

    public class CropArgsTests
    {
        [Fact]
        public void Fit_WithElementsAndMargin()
        {
            var a = CropArgs.Parse("VM Bovenaanzicht --fit --elements 1561029,1561031 --margin 5000");
            Assert.Null(a.Error);
            Assert.Equal("VM Bovenaanzicht", a.View);
            Assert.True(a.Fit);
            Assert.Equal(new[] { 1561029, 1561031 }, a.Elements);
            Assert.Equal(5000, a.Margin);
        }

        [Fact]
        public void Box_AndOff()
        {
            var b = CropArgs.Parse("1730920 --box 100000 -10000 230000 10000");
            Assert.Null(b.Error);
            Assert.Equal(new[] { 100000.0, -10000, 230000, 10000 }, b.Box);
            Assert.True(CropArgs.Parse("1730920 --off").Off);
        }

        [Theory]
        [InlineData("--fit", "view")]
        [InlineData("12", "Pick one")]
        [InlineData("12 --fit --off", "Pick one")]
        [InlineData("12 --box 5 5 1 1", "x1 > x0")]
        [InlineData("12 --off --elements 3", "goes with --fit")]
        public void Errors(string args, string fragment) => Assert.Contains(fragment, CropArgs.Parse(args).Error);
    }

    public class CropFitTests
    {
        [Fact]
        public void FitKeep_DropsFarOffStragglers()
        {
            var boxes = new List<MmRect>();
            for (int i = 0; i < 100; i++) boxes.Add(new MmRect(i * 2000, 0, i * 2000 + 500, 600));   // a 200 m quay
            boxes.Add(new MmRect(-185000, -62000, -184000, -61000));                                  // stray family
            boxes.Add(new MmRect(203000, 150000, 204000, 151000));                                    // another
            var keep = SheetLayout.FitKeep(boxes);
            Assert.Equal(100, keep.Count);
            Assert.DoesNotContain(100, keep);
            Assert.DoesNotContain(101, keep);
        }

        [Fact]
        public void FitKeep_FewBoxesAreNeverTrimmed()
        {
            var boxes = new List<MmRect> { new MmRect(0, 0, 1, 1), new MmRect(1e6, 1e6, 1e6 + 1, 1e6 + 1) };
            Assert.Equal(2, SheetLayout.FitKeep(boxes).Count);
        }
    }

    public class CropFitSpreadTests
    {
        [Fact]
        public void FitKeep_ScatteredContextDoesNotWidenTheQuay()
        {
            var boxes = new List<MmRect>();
            for (int i = 0; i < 200; i++) boxes.Add(new MmRect(i * 1000, 0, i * 1000 + 300, 600));       // 200 m quay, dense
            for (int i = 0; i < 30; i++) boxes.Add(new MmRect(-180000 + i * 13000, 120000, -179000 + i * 13000, 121000)); // 13% scattered far north
            var keep = SheetLayout.FitKeep(boxes);
            Assert.True(keep.All(i => i < 200), "scattered context should be dropped");
            Assert.Equal(200, keep.Count);   // the evenly spread quay ends survive the trim
        }
    }

    /// <summary>Review pass: flag combinations that used to be accepted and silently misbehave.</summary>
    public class SheetArgsConflictTests
    {
        [Theory]
        [InlineData("0001 101 --at 500 500 --grid 1x1", "--at places a single view")]
        [InlineData("0001 101 --at 500 500 --area 10 10 900 300", "--at places a single view")]
        public void Viewport_AtWithGridOrArea(string args, string fragment)
            => Assert.Contains(fragment, ViewportArgs.Parse(args).Error);

        [Theory]
        [InlineData("--levels --points 0 0 1 1", "not both")]
        [InlineData("12 --points 0 0 5000 0", "no element ids")]
        public void Dimension_Conflicts(string args, string fragment)
            => Assert.Contains(fragment, DimensionArgs.Parse(args).Error);

        [Fact]
        public void Text_TypeAndSize() => Assert.Contains("not both", TextArgs.Parse("0 0 hi --type 2.5_NLCS --size 3.5").Error);

        [Fact]
        public void Crop_MarginOnlyWithFit() => Assert.Contains("goes with --fit", CropArgs.Parse("12 --box 0 0 10 10 --margin 5").Error);
    }

    public class SheetLayoutReviewTests
    {
        [Fact]
        public void FitKeep_LargeViewsClusterASampleAndKeepEverythingInsideIt()
        {
            var boxes = new List<MmRect>();
            for (int i = 0; i < 6000; i++) boxes.Add(new MmRect(i * 30, 0, i * 30 + 20, 600));              // 180 m quay, 6000 piles
            for (int i = 0; i < 50; i++) boxes.Add(new MmRect(-300000 - i * 13000, 90000, -299000 - i * 13000, 91000)); // scattered far away
            var keep = SheetLayout.FitKeep(boxes, 5000, 1000);
            Assert.Equal(6000, keep.Count);
            Assert.True(keep.All(k => k < 6000));
        }

        [Fact]
        public void OffPaperLines_SayHowFar()
        {
            var rects = new List<KeyValuePair<string, MmRect>>
            {
                new KeyValuePair<string, MmRect>("'in'", new MmRect(10, 10, 100, 100)),
                new KeyValuePair<string, MmRect>("'out'", new MmRect(1100, 700, 1201, 830)),
            };
            Assert.Equal(new[] { "WARN  'out' runs 12 mm off the paper" }, SheetLayout.OffPaperLines(rects, new MmRect(0, 0, 1189, 841)));
        }

        [Fact]
        public void MmRect_ToJson_RoundsToWholeMm()
            => Assert.Equal(new object[] { 10.0, 20.0, 1000.0, 841.0 }, new MmRect(9.6, 20.4, 999.5, 841.2).ToJson());
    }
}
