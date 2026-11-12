using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 21 — the annotation layout check (`annolayout`, the `-- layout:` line) and the
    /// placement helpers behind dimension rows, spot slots and tag heads. Numbers are the ones
    /// measured in Revit 2023 on the AL test section at 1:20 (docs/plans/21-annotation-layout.md).
    /// </summary>
    public class AnnotationLayoutTests
    {
        private static AnnoBox Text(int id, double x0, double y0, double x1, double y1, AnnoKind kind = AnnoKind.Text)
            => new AnnoBox { Id = id, Kind = kind, Rect = new MmRect(x0, y0, x1, y1) };

        private static AnnoBox DimLine(int id, double x0, double x1, double y)
            => new AnnoBox { Id = id, Kind = AnnoKind.DimensionLine, Rect = AnnotationLayout.Strip(x0, y, x1, y), AlongX = true };

        [Fact]
        public void TextExtent_MatchesTheMeasuredNotes()
        {
            // "Wapening K12-150", 16 characters at 2.5 mm NLCS-ISO: Revit's width 27.4 mm.
            AnnotationLayout.TextExtent(16, 2.5, 1, out var w, out var h);
            Assert.InRange(w, 27.0, 29.5);
            Assert.InRange(h, 4.5, 5.0);
            // A width factor narrows it; no size falls back to 2.5 mm.
            AnnotationLayout.TextExtent(16, 2.5, 0.8, out var narrow, out _);
            Assert.True(narrow < w);
            AnnotationLayout.TextExtent(4, 0, 0, out var fallback, out _);
            Assert.Equal(4 * 2.5 * AnnotationLayout.CharWidth, fallback, 3);
        }

        [Fact]
        public void TextBox_IsCentredOnTheTextPosition_AndRisesFromIt()
        {
            var r = AnnotationLayout.TextBox(60, 209, "600", 2.5, 1);
            Assert.Equal(60, r.Cx, 3);
            Assert.Equal(209, r.Y0, 3);
            Assert.True(r.Y1 > 213 && r.Y1 < 214.5);   // measured top 213.7
            var v = AnnotationLayout.TextBox(60, 209, "600", 2.5, 1, vertical: true);
            Assert.True(v.X1 <= 60.001 && v.H > v.W);   // a vertical dimension's text stands left of its line
        }

        [Fact]
        public void ThreeSpotsFiveMmApart_AreWarnedAsOverlapping()
        {
            // The before picture: three "+2.500" centred at 42.5 / 45 / 47.5, all at y 212.1.
            var boxes = new[] { 42.5, 45, 47.5 }.Select((cx, i) => new AnnoBox
            {
                Id = 1730946 + i, Kind = AnnoKind.SpotText, Rect = AnnotationLayout.TextBox(cx, 212.1, "+2.500", 2.5, 1)
            }).ToList();
            var o = AnnotationLayout.Overlaps(boxes);
            Assert.Equal(3, o.Count);
            Assert.All(o, x => Assert.Equal("WARN", x.Severity));
            Assert.Contains("spot 1730946 text and spot 1730947 text overlap", AnnotationLayout.Line(o.First(x => x.Involves(1730946) && x.Involves(1730947))));
        }

        [Fact]
        public void ATouchingStrip_IsInfoNotWarn()
        {
            var o = AnnotationLayout.Overlaps(new[] { Text(1, 0, 0, 10, 5), Text(2, 9.5, 0, 20, 5) });
            Assert.Single(o);
            Assert.Equal("INFO", o[0].Severity);
            Assert.Contains("thin strip", AnnotationLayout.Line(o[0]));
            Assert.Empty(AnnotationLayout.Overlaps(new[] { Text(1, 0, 0, 10, 5), Text(2, 11, 0, 20, 5) }));
        }

        [Fact]
        public void TwoDimensionStringsOnOneLine_AreStacked()
        {
            // Both measured strings at y 208.0 over 40..120.
            var o = AnnotationLayout.Overlaps(new[] { DimLine(1730940, 40, 120, 208), DimLine(1730941, 40, 120, 208) });
            Assert.Single(o);
            Assert.Equal("stacked", o[0].Kind);
            Assert.Equal("WARN", o[0].Severity);
            Assert.Contains("stacked", AnnotationLayout.Line(o[0]));
            // One row apart (8 mm): fine. Crossing lines are not reported either.
            Assert.Empty(AnnotationLayout.Overlaps(new[] { DimLine(1, 40, 120, 208), DimLine(2, 40, 120, 216) }));
            var vertical = new AnnoBox { Id = 3, Kind = AnnoKind.DimensionLine, Rect = AnnotationLayout.Strip(80, 150, 80, 250), AlongX = false };
            Assert.Empty(AnnotationLayout.Overlaps(new[] { DimLine(1, 40, 120, 208), vertical }));
        }

        [Fact]
        public void ALeaderThroughAnotherText_IsWarned_ButItsOwnTextIsNot()
        {
            var spotText = Text(10, 39.6, 212.1, 50.4, 216.8, AnnoKind.SpotText);
            var ownLeader = new AnnoBox { Id = 10, Kind = AnnoKind.Leader, LeaderOf = "spot", Rect = AnnotationLayout.Strip(40, 200, 40, 212) };
            var dimText = new AnnoBox { Id = 20, Kind = AnnoKind.DimensionText, Label = "200", Rect = AnnotationLayout.TextBox(40, 209, "200", 2.5, 1) };
            var o = AnnotationLayout.Overlaps(new[] { spotText, ownLeader, dimText });
            var through = o.Single(x => x.Kind == "line-through-text");
            Assert.Equal(10, through.A.Id);
            Assert.Equal(20, through.B.Id);
            Assert.Contains("spot 10 leader runs through dimension 20 text '200'", AnnotationLayout.Line(through));
        }

        [Fact]
        public void OnePairOfAnnotations_GivesOneFinding_NotOnePerSegment()
        {
            var dim = Enumerable.Range(0, 5).Select(i => new AnnoBox { Id = 1, Kind = AnnoKind.DimensionText, Label = "200", Rect = AnnotationLayout.TextBox(40 + i * 3, 209, "200", 2.5, 1) }).ToList();
            dim.Add(Text(2, 38, 208, 60, 215));
            Assert.Single(AnnotationLayout.Overlaps(dim));
        }

        [Fact]
        public void LayoutLine_NamesOnlyConflictsOfTheNewAnnotations()
        {
            var boxes = new List<AnnoBox> { Text(1, 0, 0, 10, 5), Text(2, 5, 0, 15, 5), Text(3, 100, 0, 110, 5), Text(4, 105, 0, 115, 5) };
            var o = AnnotationLayout.Overlaps(boxes);
            Assert.Null(AnnotationLayout.LayoutLine(new[] { 9 }, o, "AL test"));
            var line = AnnotationLayout.LayoutLine(new[] { 3 }, o, "AL test");
            Assert.StartsWith("-- layout: 1 overlap in 'AL test': text 3 ↔ text 4 (", line);
            Assert.EndsWith("→ run 'annolayout AL test' for the boxes", line);
            Assert.DoesNotContain("text 1", line);
        }

        [Fact]
        public void LayoutLine_CutsAtThree_AndJoinsViews()
        {
            var boxes = Enumerable.Range(0, 5).Select(i => Text(i + 1, 0, 0, 10, 5)).ToList();
            var line = AnnotationLayout.LayoutLine(new[] { 1, 2, 3, 4, 5 }, AnnotationLayout.Overlaps(boxes), "V");
            Assert.Contains("10 overlaps", line);
            Assert.Contains("+7 more", line);
            Assert.Null(AnnotationLayout.JoinLines(new string[] { null }));
            Assert.Equal("-- layout: a | b", AnnotationLayout.JoinLines(new[] { "-- layout: a", "-- layout: b" }));
        }

        [Fact]
        public void Rotated_FlagsSkewedTextAndTags_NotDimensions()
        {
            var boxes = new[]
            {
                new AnnoBox { Id = 1, Kind = AnnoKind.Text, AngleDeg = 0.2 },
                new AnnoBox { Id = 2, Kind = AnnoKind.Text, AngleDeg = 23 },
                new AnnoBox { Id = 3, Kind = AnnoKind.Tag, AngleDeg = -359.9 },
                new AnnoBox { Id = 4, Kind = AnnoKind.Tag, AngleDeg = 90 },
                new AnnoBox { Id = 5, Kind = AnnoKind.DimensionText, AngleDeg = 90 }
            };
            Assert.Equal(new[] { 2, 4 }, AnnotationLayout.Rotated(boxes).Select(b => b.Id));
            Assert.Equal(-90, AnnotationLayout.Normalize(270), 6);
            Assert.True(AnnotationLayout.IsSkewed(boxes[1]));
            Assert.False(AnnotationLayout.IsSkewed(boxes[3]));
            Assert.StartsWith("WARN  text 2 is rotated 23°", AnnotationLayout.RotatedLine(boxes[1]));
            Assert.StartsWith("INFO  tag 4 is rotated 90°", AnnotationLayout.RotatedLine(boxes[3]));
        }

        [Fact]
        public void ChooseRow_SkipsRowsTakenByParallelStrings()
        {
            // Elements end at 200 mm on paper; an existing string at 208 over the same span.
            var existing = new List<AnnotationLayout.DimLine> { new AnnotationLayout.DimLine(1730940, 208, 40, 120) };
            int row = AnnotationLayout.ChooseRow(200, 40, 120, existing, out var takenBy);
            Assert.Equal(2, row);
            Assert.Equal(new[] { "dimension 1730940" }, takenBy);
            // A string elsewhere along the line does not take the row.
            Assert.Equal(1, AnnotationLayout.ChooseRow(200, 200, 260, existing, out _));
            Assert.Equal(1, AnnotationLayout.ChooseRow(200, 40, 120, new List<AnnotationLayout.DimLine>(), out _));
        }

        [Fact]
        public void ChooseRow_KeepsClearOfOtherTexts()
        {
            // A spot's text across rows 1 and 2 (208..216.8): the new string goes to row 3.
            var obstacles = new List<AnnotationLayout.Obstacle> { new AnnotationLayout.Obstacle("spot 1730946 text", new MmRect(37, 212, 48, 216.8)) };
            int row = AnnotationLayout.ChooseRow(200, 40, 120, new List<AnnotationLayout.DimLine>(), out var takenBy, obstacles: obstacles);
            Assert.Equal(3, row);
            Assert.Equal(new[] { "spot 1730946 text", "spot 1730946 text" }, takenBy);
        }

        [Fact]
        public void SpotSlots_StartWithTheMeasuredDefault()
        {
            var slots = AnnotationLayout.SpotSlots(40, 200, top: true, textW: 10.8, textH: 4.75);
            Assert.Equal("up-right", slots[0].Name);
            Assert.Equal(12, slots[0].Rise);
            Assert.Equal(45, slots[0].Text.Cx, 3);
            Assert.Equal(212, slots[0].Text.Y0, 3);
            Assert.Equal("up-left", slots[1].Name);
            Assert.Equal(35, slots[1].Text.Cx, 3);
            Assert.Equal("up-right 24 mm", slots[2].Name);
            Assert.Equal(AnnotationLayout.SpotLeaderMm, slots[2].Run);
            var down = AnnotationLayout.SpotSlots(40, 200, top: false, textW: 10.8, textH: 4.75);
            Assert.Equal("down-right", down[0].Name);
            Assert.Equal(-12, down[0].Rise);
        }

        [Fact]
        public void PickSlot_TakesTheFirstFree_AndNamesWhatBlockedTheDefault()
        {
            // A spot at 60 (its text 59.6..70.4); a new one 10 mm to its left would print its text into
            // it, so its text goes up-left. 5 mm to the right nothing is clear: the leader crosses the text.
            var first = new AnnoBox { Id = 1730946, Kind = AnnoKind.SpotText, Rect = AnnotationLayout.TextBox(65, 212.1, "+2.500", 2.5, 1) };
            var slots = AnnotationLayout.SpotSlots(50, 200, true, 10.8, 4.75);
            int i = AnnotationLayout.PickSlot(slots, new[] { first }, out var blocker, out bool free);
            Assert.True(free);
            Assert.Equal("up-left", slots[i].Name);
            Assert.Equal(1730946, blocker.Id);
            AnnotationLayout.PickSlot(AnnotationLayout.SpotSlots(65, 200, true, 10.8, 4.75), new[] { first }, out _, out bool tooClose);
            Assert.False(tooClose);
            // Nothing around: the default.
            Assert.Equal(0, AnnotationLayout.PickSlot(slots, new AnnoBox[0], out _, out free));
            Assert.True(free);
        }

        [Fact]
        public void PickSlot_NoFreeSlot_GivesTheLeastCrowded()
        {
            var wall = Text(1, -100, -100, 200, 400);
            var slots = AnnotationLayout.SpotSlots(40, 200, true, 10.8, 4.75);
            int i = AnnotationLayout.PickSlot(slots, new[] { wall }, out _, out bool free);
            Assert.False(free);
            Assert.InRange(i, 0, slots.Count - 1);
        }

        [Fact]
        public void Overlaps_SweepFindsTheSamePairsInAnyOrder_AndFocusKeepsOnlyTheNewOnes()
        {
            // A long dimension line far left in the order must still meet a text far right on it.
            var boxes = new List<AnnoBox>
            {
                Text(3, 200, 205, 212, 212, AnnoKind.SpotText),
                DimLine(1, 0, 300, 208),
                Text(2, 50, 0, 60, 5),
                Text(4, 55, 1, 70, 6),
            };
            var all = AnnotationLayout.Overlaps(boxes);
            var reversed = AnnotationLayout.Overlaps(Enumerable.Reverse(boxes).ToList());
            Assert.Equal(2, all.Count);   // line through spot 3, text 2 ↔ text 4
            Assert.Equal(all.Select(o => o.Kind + Math.Min(o.A.Id, o.B.Id) + Math.Max(o.A.Id, o.B.Id)).OrderBy(x => x),
                         reversed.Select(o => o.Kind + Math.Min(o.A.Id, o.B.Id) + Math.Max(o.A.Id, o.B.Id)).OrderBy(x => x));
            var focused = AnnotationLayout.Overlaps(boxes, new HashSet<int> { 4 });
            Assert.Single(focused);
            Assert.True(focused[0].Involves(4));
            // Two vertical strings 1 mm apart: the sweep's 2 mm reach still pairs them.
            var v1 = new AnnoBox { Id = 7, Kind = AnnoKind.DimensionLine, Rect = AnnotationLayout.Strip(10, 0, 10, 100), AlongX = false };
            var v2 = new AnnoBox { Id = 8, Kind = AnnoKind.DimensionLine, Rect = AnnotationLayout.Strip(11, 0, 11, 100), AlongX = false };
            Assert.Equal("stacked", AnnotationLayout.Overlaps(new[] { v1, v2 }).Single().Kind);
        }

        [Fact]
        public void ALeaderGrazingATextsEdge_DoesNotCount()
        {
            var text = new MmRect(39.6, 212, 50.4, 216.75);
            Assert.True(AnnotationLayout.Crosses(AnnotationLayout.Strip(45, 200, 45, 230), false, text));    // through the middle
            Assert.False(AnnotationLayout.Crosses(AnnotationLayout.Strip(50, 200, 50, 230), false, text));   // 0.55 mm inside the edge
            Assert.False(AnnotationLayout.Crosses(AnnotationLayout.Strip(45, 200, 45, 212.5), false, text)); // ends 0.5 mm into it
            Assert.True(AnnotationLayout.Crosses(AnnotationLayout.Strip(30, 214, 60, 214), true, text));     // horizontal, through it
        }

        [Fact]
        public void SpotsTenMmApart_TheSecondGoesHigher()
        {
            // The first spot's text (default up-right, centred 5 mm past its point at 40): 39.6..50.4.
            // The second, 10 mm on, would print into it; 24 mm up its leader only grazes the first text.
            var first = new AnnoBox { Id = 1, Kind = AnnoKind.SpotText, Rect = AnnotationLayout.TextBox(45, 212, "+0.000", 2.5, 1) };
            var slots = AnnotationLayout.SpotSlots(50, 200, true, 10.8, 4.75);
            int i = AnnotationLayout.PickSlot(slots, new[] { first }, out var blocker, out bool free);
            Assert.True(free);
            Assert.Equal("up-right 24 mm", slots[i].Name);
            Assert.Equal(1, blocker.Id);
        }

        [Fact]
        public void AWitnessLineThroughAText_IsInfo_AndDoesNotBlockASlot()
        {
            var spot = Text(1, 39.6, 212, 50.4, 216.75, AnnoKind.SpotText);
            var witness = new AnnoBox { Id = 2, Kind = AnnoKind.Witness, Rect = AnnotationLayout.Strip(45, 201, 45, 236), AlongX = false };
            var o = AnnotationLayout.Overlaps(new[] { spot, witness }).Single();
            Assert.Equal("INFO", o.Severity);
            Assert.Contains("dimension 2 witness line runs through spot 1 text", AnnotationLayout.Line(o));
            Assert.Null(AnnotationLayout.LayoutLine(new[] { 1, 2 }, new[] { o }, "V"));
            var slots = AnnotationLayout.SpotSlots(40, 200, true, 10.8, 4.75);
            Assert.Equal(0, AnnotationLayout.PickSlot(slots, new[] { witness }, out _, out bool free));
            Assert.True(free);
        }

        [Fact]
        public void TagSlots_UseTheSamePicker()
        {
            // A tag head 10×5 mm: right above is taken by a text, left above is clear.
            var slots = new List<AnnotationLayout.Slot>
            {
                new AnnotationLayout.Slot { Name = "right above", Text = new MmRect(108, 54, 118, 59) },
                new AnnotationLayout.Slot { Name = "left above", Text = new MmRect(82, 54, 92, 59) },
            };
            int i = AnnotationLayout.PickSlot(slots, new[] { Text(5, 105, 50, 130, 60) }, out var blocker, out bool free);
            Assert.True(free);
            Assert.Equal(1, i);
            Assert.Equal(5, blocker.Id);
        }

        [Fact]
        public void Args_TagTypeElbowAndTextWidth()
        {
            var t = TagArgs.Parse("1730950 --elbow 300500 1600 --type IB_Tag_Rebar_aantal-x-dia");
            Assert.Null(t.Error);
            Assert.True(t.Leader);   // an elbow needs a leader
            Assert.Equal(new[] { 300500.0, 1600 }, t.Elbow);
            Assert.Equal("IB_Tag_Rebar_aantal-x-dia", t.Type);
            Assert.NotNull(TagArgs.Parse("1 --type").Error);
            Assert.NotNull(TagArgs.Parse("1 --elbow 5").Error);
            var x = TextArgs.Parse("0 0 Wand hout 200 --width 40");
            Assert.Null(x.Error);
            Assert.Equal(40, x.Width);
            Assert.Equal("Wand hout 200", x.Text);
            Assert.NotNull(TextArgs.Parse("0 0 a --width 0.5").Error);
        }

        [Fact]
        public void PromptRule_IsInTheRulesBlock_AndAnnolayoutHasStepLabels()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl");
            Assert.Contains(ChatBackendBase.AnnotationLayoutRule, rules);
            Assert.Contains("annolayout", ChatBackendBase.AnnotationLayoutRule);
            Assert.Contains("annolayout", StatusVerbs.EnglishCommands);
            Assert.Contains("annolayout", StatusVerbs.DutchCommands);
        }
    }
}
