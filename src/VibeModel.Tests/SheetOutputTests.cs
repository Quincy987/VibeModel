using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Claude;
using VibeModel.Services.Standards;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Sheets 2: schedule / callout / region / export / viewport --move --to argument parsing.</summary>
    public class SheetOutputArgsTests
    {
        [Fact]
        public void Schedule_Create_FieldsFilterGroupTotal()
        {
            var a = ScheduleArgs.Parse("structural foundations --fields \"Family and Type,Count,Length\" --filter \"Length gt 12000\" --group Family and Type --name Palenstaat --total");
            Assert.Null(a.Error);
            Assert.Equal("structural foundations", a.Category);
            Assert.Equal(new[] { "Family and Type", "Count", "Length" }, a.Fields);
            Assert.Equal("Length", a.Filter.Field);
            Assert.Equal(ScheduleOp.Greater, a.Filter.Op);
            Assert.Equal("12000", a.Filter.Value);
            Assert.Equal("Family and Type", a.Group);
            Assert.Equal("Palenstaat", a.Name);
            Assert.True(a.Total);
            Assert.Null(a.ReadTarget);
        }

        [Fact]
        public void Schedule_FieldsWithoutQuotes_JoinAcrossTokens()
        {
            var a = ScheduleArgs.Parse("walls --fields Family and Type, Length");
            Assert.Null(a.Error);
            Assert.Equal(new[] { "Family and Type", "Length" }, a.Fields);
        }

        [Fact]
        public void Schedule_ReadBack()
        {
            var a = ScheduleArgs.Parse("Palenstaat 1 --rows");
            Assert.Null(a.Error);
            Assert.Equal("Palenstaat 1", a.ReadTarget);
            Assert.Equal(200, a.RowLimit);
            Assert.Equal(50, ScheduleArgs.Parse("4711 --rows 50").RowLimit);
        }

        [Theory]
        [InlineData("walls", "--fields is required")]
        [InlineData("--fields Length", "category first")]
        [InlineData("walls --fields Length,length", "given twice")]
        [InlineData("walls --fields Length --group Count", "must be one of the --fields")]
        [InlineData("walls --fields Length --total yes", "--total takes no value")]
        [InlineData("walls --fields Length --filter Length", "No operator")]
        [InlineData("walls --fields Length --filter \"Length >\"", "no value")]
        [InlineData("Palenstaat --rows --fields Length", "--rows reads an existing schedule")]
        [InlineData("Palenstaat --rows --total", "--rows reads an existing schedule")]
        [InlineData("--rows", "before --rows")]
        [InlineData("Palenstaat --rows many", "at most one number")]
        [InlineData("walls --fields Length --sort Length", "Unknown flag")]
        public void Schedule_Errors(string args, string fragment) => Assert.Contains(fragment, ScheduleArgs.Parse(args).Error);

        [Theory]
        [InlineData("Length gt 12000", "Length", "Greater", "12000")]
        [InlineData("Length >= 5000", "Length", "GreaterOrEqual", "5000")]
        [InlineData("Length<=5000", "Length", "LessOrEqual", "5000")]
        [InlineData("Family and Type contains Pile", "Family and Type", "Contains", "Pile")]
        [InlineData("Family and Type not contains Pile", "Family and Type", "NotContains", "Pile")]
        [InlineData("Comments = Damwand A", "Comments", "Equal", "Damwand A")]
        [InlineData("Mark != \"P 1\"", "Mark", "NotEqual", "P 1")]
        [InlineData("Type Mark beginswith HP", "Type Mark", "BeginsWith", "HP")]
        public void ScheduleFilter_Parses(string text, string field, string op, string value)
        {
            var f = ScheduleFilterSpec.Parse(text, out var err);
            Assert.Null(err);
            Assert.Equal(field, f.Field);
            Assert.Equal(op, f.Op.ToString());
            Assert.Equal(value, f.Value);
        }

        [Fact]
        public void ScheduleFilter_WordInsideAFieldNameIsNotAnOperator()
        {
            // A word operator only counts with spaces around it.
            var f = ScheduleFilterSpec.Parse("Containsfield eq 3", out var err);
            Assert.Null(err);
            Assert.Equal("Containsfield", f.Field);
            Assert.Equal(ScheduleOp.Equal, f.Op);
        }

        [Fact]
        public void Callout_BoxIsNormalised()
        {
            var a = CalloutArgs.Parse("Bovenaanzicht --box 5000 3000 1000 -2000 --name Detail A --scale 20 --section");
            Assert.Null(a.Error);
            Assert.Equal("Bovenaanzicht", a.Parent);
            Assert.Equal(new[] { 1000.0, -2000, 5000, 3000 }, a.Box);
            Assert.Equal("Detail A", a.Name);
            Assert.Equal(20, a.Scale);
            Assert.True(a.Section);
        }

        [Fact]
        public void Callout_Reference()
        {
            var a = CalloutArgs.Parse("101 --box 0 0 10 10 --reference Detail A");
            Assert.Null(a.Error);
            Assert.Equal("Detail A", a.Reference);
        }

        [Theory]
        [InlineData("--box 0 0 10 10", "parent view")]
        [InlineData("101", "--box x1 y1 x2 y2 is required")]
        [InlineData("101 --box 0 0 10", "4 number")]
        [InlineData("101 --box 0 0 0 10", "no area")]
        [InlineData("101 --box 0 0 10 10 --scale 1.5", "whole number")]
        [InlineData("101 --box 0 0 10 10 --section yes", "--section takes no value")]
        public void Callout_Errors(string args, string fragment) => Assert.Contains(fragment, CalloutArgs.Parse(args).Error);

        [Fact]
        public void Region_PointsDropTheClosingCopy()
        {
            var a = RegionArgs.Parse("Level 1 --points 0,0 4000,0 4000,3000 0,3000 0,0 --type Beton");
            Assert.Null(a.Error);
            Assert.Equal("Level 1", a.View);
            Assert.Equal(4, a.Points.Count);
            Assert.Equal("Beton", a.Type);
            Assert.Equal(12e6, Math.Abs(Polygon.Area(a.Points)), 3);
        }

        [Fact]
        public void Region_PointsAsPlainNumbers()
        {
            var a = RegionArgs.Parse("101 --points 0 0 1000 0 1000 1000");
            Assert.Null(a.Error);
            Assert.Equal(3, a.Points.Count);
        }

        [Fact]
        public void Region_Detail()
        {
            var a = RegionArgs.Parse("Doorsnede 1-1 --detail Grondslag : 600 --at -1200 850 --rotate 90");
            Assert.Null(a.Error);
            Assert.Equal("Grondslag : 600", a.Detail);
            Assert.Equal(new[] { -1200.0, 850 }, a.At);
            Assert.Equal(90, a.Rotate);
        }

        [Theory]
        [InlineData("101", "Pick one")]
        [InlineData("101 --points 0,0 1,1 --detail X --at 0 0", "Pick one")]
        [InlineData("101 --points 0,0 1000,0 1000", "odd count")]
        [InlineData("101 --points 0,0 1000,0", "at least 3")]
        [InlineData("101 --points 0,0 1000,0 2000,0", "no area")]
        [InlineData("101 --points 0,0 2000,1000 2000,0 0,2000", "crosses itself")]
        [InlineData("101 --points 0,0 1000,0 1000,1000 --at 5 5", "goes with --detail")]
        [InlineData("101 --detail X --type Beton --at 0 0", "--type is the filled region type")]
        [InlineData("101 --detail X", "needs --at")]
        [InlineData("--points 0,0 1000,0 1000,1000", "view (id or name) first")]
        public void Region_Errors(string args, string fragment) => Assert.Contains(fragment, RegionArgs.Parse(args).Error);

        [Fact]
        public void Export_SheetList()
        {
            var a = ExportArgs.Parse("pdf 0001,0002 0003 --to C:\\tmp\\out --name {number}_{name}");
            Assert.Null(a.Error);
            Assert.Equal(ExportFormat.Pdf, a.Format);
            Assert.Equal(new[] { "0001", "0002", "0003" }, a.Targets);
            Assert.Equal("C:\\tmp\\out", a.To);
            Assert.Equal("{number}_{name}", a.NamePattern);
        }

        [Fact]
        public void Export_AllSheetsDwgWithSetup_AndIfcWholeModel()
        {
            var d = ExportArgs.Parse("DWG --all-sheets --setup Office DWG");
            Assert.Null(d.Error);
            Assert.True(d.AllSheets);
            Assert.Equal("Office DWG", d.Setup);
            var i = ExportArgs.Parse("ifc");
            Assert.Null(i.Error);
            Assert.Equal(ExportFormat.Ifc, i.Format);
            Assert.Empty(i.Targets);
        }

        [Theory]
        [InlineData("", "format first")]
        [InlineData("png 0001", "Unknown format")]
        [InlineData("pdf", "or --all-sheets")]
        [InlineData("pdf 0001 --all-sheets", "not both")]
        [InlineData("ifc --all-sheets", "not sheets")]
        [InlineData("dwg 0001 --combine", "--combine is for pdf")]
        [InlineData("pdf 0001 --setup X", "does not apply to pdf")]
        [InlineData("ifc 101,102", "at most one view")]
        [InlineData("pdf 0001 --to", "--to needs a folder")]
        public void Export_Errors(string args, string fragment) => Assert.Contains(fragment, ExportArgs.Parse(args).Error);

        [Fact]
        public void Viewport_MoveToAnotherSheet()
        {
            var a = ViewportArgs.Parse("--move 4711 --to 0002 --at 300 200");
            Assert.Null(a.Error);
            Assert.Equal(4711, a.MoveId);
            Assert.Equal("0002", a.MoveToSheet);
            Assert.Equal(new[] { 300.0, 200 }, a.At);
            Assert.Null(a.MoveTo);
            // The old in-sheet move is unchanged.
            var m = ViewportArgs.Parse("--move 4711 300 200");
            Assert.Null(m.MoveToSheet);
            Assert.Equal(new[] { 300.0, 200 }, m.MoveTo);
        }
    }

    /// <summary>Flag combinations that would silently misbehave are refused (sheets 2).</summary>
    public class SheetOutputArgsConflictTests
    {
        [Theory]
        [InlineData("--move 4711 300 200 --to 0002", "no x y after the id")]
        [InlineData("0001 101 --to 0002", "--to goes with --move")]
        [InlineData("--move 4711 --to 0002 --scale 50", "does not go with --move --to")]
        [InlineData("--move 4711 --to 0002 --grid 2x1", "does not go with --move --to")]
        [InlineData("--move 4711 --to", "needs a sheet")]
        public void Viewport_MoveTo(string args, string fragment) => Assert.Contains(fragment, ViewportArgs.Parse(args).Error);

        [Theory]
        [InlineData("101 --box 0 0 10 10 --reference 202 --scale 20", "does not go with --reference")]
        [InlineData("101 --box 0 0 10 10 --reference 202 --name X", "does not go with --reference")]
        [InlineData("101 --box 0 0 10 10 --template T --no-template", "not both")]
        public void Callout(string args, string fragment) => Assert.Contains(fragment, CalloutArgs.Parse(args).Error);

        [Fact]
        public void Schedule_RowsWithCreateFlags() => Assert.Contains("--rows reads", ScheduleArgs.Parse("X --rows --group Count").Error);

        [Fact]
        public void Export_CombineTakesNoValue() => Assert.Contains("takes no value", ExportArgs.Parse("pdf 0001 --combine yes").Error);
    }

    public class ScheduleTextTests
    {
        [Theory]
        [InlineData("autodesk.unit.unit:millimeters-1.0.1", "mm")]
        [InlineData("autodesk.unit.unit:meters-1.0.1", "m")]
        [InlineData("autodesk.unit.unit:squareMeters-1.0.1", "m²")]
        [InlineData("autodesk.unit.unit:cubicMeters-1.0.0", "m³")]
        [InlineData("autodesk.unit.unit:kilonewtonMeters-1.0.1", "kilonewton meters")]
        [InlineData("autodesk.unit.unit:general-1.0.1", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void UnitSymbol(string id, string symbol) => Assert.Equal(symbol, ScheduleText.UnitSymbol(id));

        [Fact]
        public void Header_AddsUnitOnlyWhenThereIsOne()
        {
            Assert.Equal("Length (mm)", ScheduleText.Header("Length", "mm"));
            Assert.Equal("Count", ScheduleText.Header("Count", null));
        }

        [Fact]
        public void DataRows_DropHeadingRowAndBlanks()
        {
            var body = new List<IList<string>>
            {
                new List<string> { "Family and Type", "Count", "Length" },
                new List<string> { "", "", "" },
                new List<string> { "Pile: HP 300", "12", "180000" },
                new List<string> { " Pile: HP 400 ", "3", "45000" },
                new List<string> { "Grand total: 15", "", "225000" },
            };
            var rows = ScheduleText.DataRows(body, new[] { "Family and Type", "Count", "Length" });
            Assert.Equal(3, rows.Count);
            Assert.Equal("Pile: HP 400", rows[1][0]);
            Assert.Equal("225000", rows[2][2]);
        }

        [Fact]
        public void Table_AlignsAndCutsOff()
        {
            var rows = Enumerable.Range(1, 25).Select(i => new List<string> { "Pile " + i, (i * 1000).ToString() }).ToList();
            var lines = ScheduleText.Table(new[] { "Type", "Length (mm)" }, rows, 20, "'schedule 7 --rows' for all");
            Assert.Equal("Type    | Length (mm)", lines[0]);
            Assert.Equal("--------+------------", lines[1]);
            Assert.Equal("Pile 1  | 1000", lines[2]);
            Assert.Equal(2 + 20 + 1, lines.Count);
            Assert.Equal("… 5 more row(s) — 'schedule 7 --rows' for all", lines.Last());
        }

        [Fact]
        public void Table_ShortensLongCells_AndSaysWhenEmpty()
        {
            var lines = ScheduleText.Table(new[] { "Comments" }, new List<List<string>> { new List<string> { new string('x', 60) } }, 20);
            Assert.Equal(40, lines[2].Length);
            Assert.EndsWith("…", lines[2]);
            Assert.Contains("no rows", ScheduleText.Table(new[] { "A" }, new List<List<string>>(), 20).Last());
        }
    }

    public class ExportNamingTests
    {
        [Fact]
        public void Expand_TokensAndInvalidCharacters()
        {
            var d = new DateTime(2026, 10, 12);
            Assert.Equal("0001 - Situatie_ A_B", ExportNaming.Expand(ExportNaming.SheetPattern, "0001", "Situatie: A/B", "PRG1002", d, "pdf"));
            Assert.Equal("PRG1002_2026-10-12_dwg", ExportNaming.Expand("{project}_{date}_{format}", "", "", "PRG1002", d, "dwg"));
            Assert.Equal("export", ExportNaming.Expand("{name}", "", "...", "", d, "pdf"));
        }

        [Fact]
        public void MakeUnique_NumbersRepeats()
            => Assert.Equal(new[] { "A", "a (2)", "B", "A (3)" }, ExportNaming.MakeUnique(new[] { "A", "a", "B", "A" }));

        [Theory]
        [InlineData(512L, "512 B")]
        [InlineData(1536L, "1.5 KB")]
        [InlineData(3 * 1024L * 1024 + 200 * 1024, "3.2 MB")]
        public void Size(long bytes, string text) => Assert.Equal(text, ExportNaming.Size(bytes));

        [Fact]
        public void Pack_ExportDwgSetup()
        {
            var root = PackJson.Parse("{\"id\":\"x\",\"export\":{\"dwgSetup\":\" Office DWG \"}}");
            Assert.Equal("Office DWG", StandardsPack.FromJson(root, null).Export.DwgSetup);
            Assert.Null(StandardsPack.FromJson(PackJson.Parse("{\"id\":\"x\"}"), null).Export.DwgSetup);
        }
    }

    public class PolygonTests
    {
        [Fact]
        public void Area_SignFollowsOrientation()
        {
            var ccw = new List<double[]> { new[] { 0.0, 0 }, new[] { 2000.0, 0 }, new[] { 2000.0, 1000 }, new[] { 0.0, 1000 } };
            Assert.Equal(2e6, Polygon.Area(ccw), 3);
            ccw.Reverse();
            Assert.Equal(-2e6, Polygon.Area(ccw), 3);
        }

        [Fact]
        public void SelfIntersects_BowTieYes_LShapeNo()
        {
            Assert.True(Polygon.SelfIntersects(new List<double[]> { new[] { 0.0, 0 }, new[] { 10.0, 10 }, new[] { 10.0, 0 }, new[] { 0.0, 10 } }));
            Assert.False(Polygon.SelfIntersects(new List<double[]>
                { new[] { 0.0, 0 }, new[] { 20.0, 0 }, new[] { 20.0, 10 }, new[] { 10.0, 10 }, new[] { 10.0, 20 }, new[] { 0.0, 20 } }));
        }
    }
}
