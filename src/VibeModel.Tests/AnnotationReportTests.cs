using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Annotations survive replacements: the guard's report, matching maths, scope, and `spot` arguments.</summary>
    public class AnnotationReportTests
    {
        private static RehostedAnnotation Rehost(long from, long to, AnnotationKind kind, long host, string cat, string view = "Doorsnede 1-1")
            => new RehostedAnnotation { From = from, To = to, Kind = kind, View = view, Hosts = { new KeyValuePair<long, string>(host, cat) } };

        private static RemovedAnnotation Removed(long id, AnnotationKind kind, long referenced, string view = "Doorsnede kademuur 1-1", string sheet = "0003", bool byRevit = false)
            => new RemovedAnnotation { Id = id, Kind = kind, Referenced = { referenced }, View = view, Sheet = sheet, ByRevit = byRevit };

        // ------------------------------------------------------------------ text line

        [Fact]
        public void Empty_NoLine()
        {
            var o = new AnnotationOutcome();
            Assert.True(o.IsEmpty);
            Assert.Null(o.ToTextLine());
        }

        [Fact]
        public void Line_RehostedAndRemoved_AsInThePlan()
        {
            var o = new AnnotationOutcome();
            o.Rehosted.Add(Rehost(1730944, 1730970, AnnotationKind.SpotElevation, 1730959, "Floors"));
            o.Rehosted.Add(Rehost(1730946, 1730971, AnnotationKind.SpotElevation, 1730980, "Structural Foundations"));
            o.Removed.Add(Removed(1730990, AnnotationKind.Dimension, 1730914));
            Assert.Equal("-- annotations: re-hosted 2 spot elevations (1730944 → 1730970 on floor 1730959, 1730946 → 1730971 on structural foundation 1730980); " +
                         "removed 1 dimension that referenced 1730914 (view Doorsnede kademuur 1-1, sheet 0003)", o.ToTextLine());
        }

        [Fact]
        public void Line_SingularAndPlural_PerKind()
        {
            var o = new AnnotationOutcome();
            o.Rehosted.Add(Rehost(1, 2, AnnotationKind.Dimension, 10, "Walls"));
            o.Rehosted.Add(Rehost(3, 4, AnnotationKind.Tag, 11, "Floors"));
            o.Rehosted.Add(Rehost(5, 6, AnnotationKind.Tag, 12, "Floors"));
            var line = o.ToTextLine();
            Assert.Contains("re-hosted 1 dimension (1 → 2 on wall 10), 2 tags (3 → 4 on floor 11, 5 → 6 on floor 12)", line);
        }

        [Fact]
        public void Line_RemovedByRevit_IsMarked_AndSeparateFromTheGuardsOwn()
        {
            var o = new AnnotationOutcome();
            o.Removed.Add(Removed(7, AnnotationKind.SpotElevation, 70, byRevit: true));
            o.Removed.Add(Removed(8, AnnotationKind.SpotElevation, 80, sheet: null));
            var line = o.ToTextLine();
            Assert.Contains("1 spot elevation that referenced 80 (view Doorsnede kademuur 1-1)", line);
            Assert.Contains("1 spot elevation (Revit) that referenced 70 (view Doorsnede kademuur 1-1, sheet 0003)", line);
            Assert.DoesNotContain("re-hosted", line);
        }

        [Fact]
        public void Line_LongListsAreCut()
        {
            var o = new AnnotationOutcome();
            for (int i = 0; i < 13; i++) o.Removed.Add(Removed(100 + i, AnnotationKind.Tag, 1000 + i, view: "Plan"));
            var line = o.ToTextLine();
            Assert.Contains("removed 13 tags that referenced 1000, 1001", line);
            Assert.Contains("+3 more", line);
        }

        [Fact]
        public void Json_HasBothListsWithIdsKindsAndSource()
        {
            var o = new AnnotationOutcome();
            o.Rehosted.Add(Rehost(1730944, 1730970, AnnotationKind.SpotElevation, 1730959, "Floors"));
            o.Removed.Add(Removed(1730990, AnnotationKind.Dimension, 1730914, byRevit: true));
            var json = new JavaScriptSerializer().Serialize(o.ToJson());
            var back = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var re = (Dictionary<string, object>)((System.Collections.ArrayList)back["rehosted"])[0];
            Assert.Equal(1730944, re["from"]);
            Assert.Equal(1730970, re["to"]);
            Assert.Equal("spot", re["kind"]);
            Assert.Equal(1730959, re["host"]);
            var rm = (Dictionary<string, object>)((System.Collections.ArrayList)back["removed"])[0];
            Assert.Equal("dimension", rm["kind"]);
            Assert.Equal("0003", rm["sheet"]);
            Assert.Equal("revit", rm["by"]);
            Assert.Equal(1730914, ((System.Collections.ArrayList)rm["referenced"])[0]);
        }

        // ------------------------------------------------------------------ wording

        [Theory]
        [InlineData("Floors", "floor")]
        [InlineData("Structural Foundations", "structural foundation")]
        [InlineData("Walls", "wall")]
        [InlineData("Generic Models", "generic model")]
        [InlineData("Structural Framing", "structural framing")]
        [InlineData("Properties", "property")]
        [InlineData(null, "element")]
        public void CategoryWord_IsSingularLowercase(string category, string expected)
            => Assert.Equal(expected, AnnotationText.CategoryWord(category));

        [Fact]
        public void DependsVerdict_CountsAnnotationsViewsAndHosted()
        {
            var v = AnnotationText.DependsVerdict(new List<long> { 1730959 }, 2, new List<string> { "Doorsnede probe, sheet 0003" }, 0);
            Assert.StartsWith("Replacing 1730959 affects 2 annotations in 1 view (Doorsnede probe, sheet 0003) and 0 hosted elements.", v);
            Assert.EndsWith(AnnotationText.RehostRule, v);
            Assert.Contains("same height (±1 mm) within 50 mm", AnnotationText.RehostRule);
            Assert.Contains("same planes (±1 mm)", AnnotationText.RehostRule);
            var none = AnnotationText.DependsVerdict(new List<long> { 5 }, 0, new List<string>(), 1);
            Assert.Equal("Replacing 5 affects 0 annotations and 1 hosted element.", none);
        }

        [Fact]
        public void LikelyReplaced_FloorsWallsFoundations()
        {
            Assert.True(AnnotationText.LikelyReplaced(AnnotationText.BicFloors));
            Assert.True(AnnotationText.LikelyReplaced(AnnotationText.BicWalls));
            Assert.True(AnnotationText.LikelyReplaced(AnnotationText.BicStructuralFoundation));
            Assert.False(AnnotationText.LikelyReplaced(-2000080)); // furniture
        }

        // ------------------------------------------------------------------ matching maths

        [Fact]
        public void SamePlane_ParallelEitherSense_WithinOneMillimetre()
        {
            var n = new V3(0, 0, 1);
            var o = new V3(0, 0, 2000);
            Assert.True(AnnotationMatch.SamePlane(n, o, new V3(0, 0, -1), new V3(5000, 300, 2001.0)));
            Assert.False(AnnotationMatch.SamePlane(n, o, n, new V3(0, 0, 2001.01)));
            // 2.6° tilt: |dot| = 0.99897 < 0.999
            Assert.False(AnnotationMatch.SamePlane(n, o, new V3(0, 0.0454, 1), o));
            Assert.True(AnnotationMatch.SamePlane(n, o, new V3(0, 0.03, 1), o));
            Assert.False(AnnotationMatch.SamePlane(new V3(0, 0, 0), o, n, o));
        }

        [Fact]
        public void SpotMatch_SameHeightWithinTheSearchRadius()
        {
            var old = new V3(105015, 100000, -10051);
            // A 350 pile replacing a 320 one: its tip edge 15 mm further out, same height.
            Assert.True(AnnotationMatch.SpotMatch(old, new V3(105030, 100000, -10051)));
            Assert.True(AnnotationMatch.SpotMatch(old, new V3(105015, 100000, -10050.0)));
            Assert.False(AnnotationMatch.SpotMatch(old, new V3(105015, 100000, -10049.99)));   // 1.01 mm higher
            Assert.False(AnnotationMatch.SpotMatch(old, new V3(105066, 100000, -10051)));     // 51 mm away
            Assert.True(AnnotationMatch.SameHeight(1030, 1031));
            Assert.False(AnnotationMatch.SameHeight(1030, 1031.01));
        }

        [Fact]
        public void AssignPositions_NearestEndWins_RegardlessOfReferenceOrder()
        {
            // A face at 1100 and a level at 1300 whose references come in the "wrong" order.
            var ts = new List<double> { 1100, 1300 };
            Assert.Equal(new double?[] { 1300, 1100 }, AnnotationMatch.AssignPositions(ts, new List<double?> { 1300.2, 1099.9 }));
            Assert.Equal(new double?[] { 1100, 1300 }, AnnotationMatch.AssignPositions(ts, new List<double?> { 1100, 1300 }));
            // No estimate: the free ends that are left, in order.
            Assert.Equal(new double?[] { 1100, 1300, 1700 },
                AnnotationMatch.AssignPositions(new List<double> { 1100, 1300, 1700 }, new List<double?> { null, 1290, null }));
            // More references than ends: the rest stay unknown.
            Assert.Equal(new double?[] { 5, null }, AnnotationMatch.AssignPositions(new List<double> { 5 }, new List<double?> { null, null }));
        }

        [Fact]
        public void SameFacing_TopFaceDoesNotMatchABottomFace()
        {
            Assert.True(AnnotationMatch.SameFacing(new V3(0, 0, 1), new V3(0, 0, 5)));
            Assert.False(AnnotationMatch.SameFacing(new V3(0, 0, 1), new V3(0, 0, -1)));
        }

        // ------------------------------------------------------------------ scope

        [Theory]
        [InlineData("delete", true)]
        [InlineData("EXEC", true)]
        [InlineData("set", true)]
        [InlineData("join", true)]
        [InlineData("unjoin", true)]
        [InlineData("phase", true)]
        [InlineData("placeid", false)]
        [InlineData("wall", false)]
        [InlineData("spot", false)]
        [InlineData(null, false)]
        public void Applies_Table(string command, bool expected)
        {
            Assert.Equal(expected, AnnotationScope.Applies(command));
            if (command != null) Assert.Equal(expected, AnnotationGuard.Applies(command));
        }

        [Fact]
        public void TargetIds_PerCommand()
        {
            Assert.Equal(new[] { 1, 2, 3 }, AnnotationScope.TargetIds("delete", "1 2,3 1"));
            Assert.Equal(new[] { 12345 }, AnnotationScope.TargetIds("set", "12345 Mark 7"));
            Assert.Equal(new[] { 4, 5 }, AnnotationScope.TargetIds("phase", "4 5 demolish"));
            Assert.Equal(new[] { 8, 9 }, AnnotationScope.TargetIds("join", "8 9 --cut"));
            Assert.Null(AnnotationScope.TargetIds("exec", "doc.Delete(new ElementId(5));"));
            Assert.Null(AnnotationScope.TargetIds("phase", "selected demolish"));
            Assert.Empty(AnnotationScope.TargetIds("wall", "0 0 5000 0"));
        }

        [Fact]
        public void TargetIds_Batch_UnionOrEverything()
        {
            var items = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("placeid", "1355230 --along 0 0 1000 0 --count 3"),
                new KeyValuePair<string, string>("delete", "11 12"),
                new KeyValuePair<string, string>("set", "13 Comments x")
            };
            Assert.Equal(new[] { 11, 12, 13 }, AnnotationScope.TargetIds(items));
            Assert.True(AnnotationScope.AppliesToAny(items.Select(i => i.Key)));
            items.Add(new KeyValuePair<string, string>("exec", "sb.AppendLine(\"x\");"));
            Assert.Null(AnnotationScope.TargetIds(items));
            Assert.False(AnnotationScope.AppliesToAny(new[] { "wall", "placeid" }));
        }

        // ------------------------------------------------------------------ spot arguments

        [Fact]
        public void SpotArgs_PointForm()
        {
            var a = SpotArgs.Parse("103000 2000");
            Assert.Null(a.Error);
            Assert.Equal(new[] { 103000.0, 2000.0 }, a.Point);
            Assert.Null(a.ElementId);
            Assert.False(a.Fixed);
        }

        [Fact]
        public void SpotArgs_ElementForm_TopAndBottom()
        {
            var t = SpotArgs.Parse("1730980 top");
            Assert.Null(t.Error);
            Assert.Equal(1730980, t.ElementId);
            Assert.True(t.Top);
            var b = SpotArgs.Parse("1730980 BOTTOM --leader 104000 -11000 --type Peil NAP");
            Assert.Null(b.Error);
            Assert.False(b.Top);
            Assert.Equal(new[] { 104000.0, -11000.0 }, b.Leader);
            Assert.Equal("Peil NAP", b.Type);
        }

        [Fact]
        public void SpotArgs_FixedAndCommas()
        {
            var a = SpotArgs.Parse("101000,2000 --fixed");
            Assert.Null(a.Error);
            Assert.True(a.Fixed);
            Assert.Equal(new[] { 101000.0, 2000.0 }, a.Point);
        }

        [Theory]
        [InlineData("1730980", "top|bottom")]
        [InlineData("abc top", "not an element id")]
        [InlineData("100 abc", "not a number")]
        [InlineData("1 2 3", "Usage")]
        [InlineData("", "Usage")]
        [InlineData("100 200 --leader 5", "--leader needs 2")]
        [InlineData("100 200 --fixed yes", "--fixed takes no value")]
        [InlineData("100 200 --type", "--type needs")]
        [InlineData("100 200 --at 1 2", "Unknown flag")]
        public void SpotArgs_Errors(string args, string expected)
        {
            var a = SpotArgs.Parse(args);
            Assert.NotNull(a.Error);
            Assert.Contains(expected, a.Error);
        }
    }
}
