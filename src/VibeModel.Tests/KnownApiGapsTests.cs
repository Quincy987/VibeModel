using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Claude;
using VibeModel.Tests.GapFixture;
using Xunit;

namespace VibeModel.Tests.GapFixture
{
    // Stand-ins for the Revit types the gaps are about (the Revit assemblies do not load headless).
    public class TextElement { public string Text { get; set; } }
    public static class LabelUtils { public static string GetLabelFor(int id) => ""; }
    public class LabelType { }
    public class FamilyItemFactory { public object NewModelText(string text) => null; public object NewDetailCurve() => null; }
}

namespace VibeModel.Tests
{
    /// <summary>Plan 18: the known-gaps table and how it rides along with apilookup and exec's compile hint.</summary>
    public class KnownApiGapsTests
    {
        private static readonly ApiLookup Engine = new ApiLookup(new[] { typeof(TextElement).Assembly }, "VibeModel.Tests.GapFixture");

        [Theory]
        [InlineData("Label", "labels")]
        [InlineData("labels", "labels")]
        [InlineData("NewLabel", "labels")]
        [InlineData("FamilyItemFactory.NewLabel", "labels")]
        [InlineData("Autodesk.Revit.Creation.FamilyItemFactory.NewLabel", "labels")]
        [InlineData("BuiltInParameter.TAG_TAG", "labels")]
        [InlineData("textelement", "labels")]
        [InlineData("Autodesk.Revit.DB.TextElement", "labels")]
        [InlineData("LabelType", "labels")]
        [InlineData("Legend", "legends")]
        [InlineData("ViewType.Legend", "legends")]
        [InlineData("LegendComponent", "legends")]
        [InlineData("Phase", "phases-design-options")]
        [InlineData("Phase.Create", "phases-design-options")]
        [InlineData("DesignOption.Create", "phases-design-options")]
        [InlineData("List<DesignOption>", null)]
        public void Match_KeysCaseAndDottedSuffixes(string query, string expected)
        {
            var gap = KnownApiGaps.Match(query);
            Assert.Equal(expected, gap?.Name);
        }

        [Theory]
        [InlineData("LabelUtils")]
        [InlineData("Wall")]
        [InlineData("Wall.Create")]
        [InlineData("TextNote")]
        [InlineData("Phase.Name")]
        [InlineData("TextElement.Text")]
        [InlineData("")]
        [InlineData(null)]
        public void Match_RealApiIsNotAGap(string query)
        {
            Assert.Null(KnownApiGaps.Match(query));
        }

        [Fact]
        public void EveryGap_FitsItsLineBudget_AndNamesTheManualStep()
        {
            foreach (var gap in KnownApiGaps.All)
            {
                var lines = KnownApiGaps.Lines(gap);
                Assert.True(lines.Count <= KnownApiGaps.MaxLines, gap.Name + ": " + lines.Count + " lines");
                Assert.StartsWith("Known gap: ", lines[0]);
                Assert.StartsWith("Cannot: ", lines[1]);
                Assert.StartsWith("Manual step: ", lines.Last());
            }
        }

        [Fact]
        public void LabelGap_CarriesTheMeasuredRecipe()
        {
            var text = KnownApiGaps.Describe(KnownApiGaps.Match("Label"));
            Assert.Contains("no FamilyItemFactory.NewLabel", text);
            Assert.Contains("TAG_SAMPLE_TEXT", text);
            Assert.Contains("returns true, but nothing changes", text);
            Assert.Contains("trans.RollBack(); var fd = doc.EditFamily(family);", text);
            Assert.Contains("fd.FamilyManager.AddParameter(\"Betonkwaliteit\", GroupTypeId.Text, SpecTypeId.String.Text, false)", text);
            Assert.Contains("ElementTransformUtils.CopyElement(fd, labelId, offset)", text);
            Assert.Contains("Can't paste Labels across Families of different Categories.", text);
            Assert.Contains("TextNote.Create(fd, viewId, point, \"(LABEL Betonkwaliteit)\", fd.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType))", text);
            Assert.Contains("Edit Label", text);
            Assert.Contains("Load into Project", text);
        }

        [Fact]
        public void Data_IsTheAdditiveJsonObject()
        {
            var d = KnownApiGaps.Data(KnownApiGaps.Match("Legend"));
            Assert.Equal("legends", d["name"]);
            Assert.IsType<string>(d["cannot"]);
            Assert.NotEmpty((List<object>)d["works"]);
            Assert.IsType<string>(d["manual"]);
        }

        [Fact]
        public void NotFound_GetsTheBlock_BeforeTheClosestNames()
        {
            var r = Engine.Lookup("NewLabel", null, false);
            Assert.False(r.Found);
            var lines = r.Text.Split('\n');
            Assert.StartsWith("No type or member 'NewLabel'", lines[0]);
            Assert.StartsWith("Known gap: labels", lines[1]);
            Assert.True(r.Data.ContainsKey("knownGap"));
            Assert.Equal(false, r.Data["found"]);
        }

        [Fact]
        public void Ambiguous_GetsTheBlock_BeforeTheCandidates()
        {
            var r = Engine.Lookup("Label", null, false);
            Assert.False(r.Found);
            Assert.Equal(true, r.Data["ambiguous"]);
            var lines = r.Text.Split('\n').ToList();
            Assert.StartsWith("'Label' matches several types", lines[0]);
            Assert.StartsWith("Known gap: labels", lines[1]);
            int gapEnd = lines.FindIndex(l => l.StartsWith("Manual step: "));
            Assert.True(lines.IndexOf("  VibeModel.Tests.GapFixture.LabelUtils") > gapEnd);
        }

        [Fact]
        public void NoSuchMember_GetsTheBlock()
        {
            var r = Engine.Lookup("FamilyItemFactory.NewLabel", null, false);
            Assert.False(r.Found);
            var lines = r.Text.Split('\n');
            Assert.Equal("VibeModel.Tests.GapFixture.FamilyItemFactory has no public member 'NewLabel'.", lines[0]);
            Assert.StartsWith("Known gap: labels", lines[1]);
            Assert.StartsWith("using ", lines.Last());   // the using line still ends it
            Assert.Equal("labels", ((Dictionary<string, object>)r.Data["knownGap"])["name"]);
        }

        [Fact]
        public void FoundKeyType_GetsTheBlockAtTheEnd()
        {
            var r = Engine.Lookup("TextElement", null, false);
            Assert.True(r.Found);
            Assert.StartsWith("VibeModel.Tests.GapFixture.TextElement", r.Text);
            Assert.Contains("Text : string { get; set; }", r.Text);
            Assert.StartsWith("Manual step: ", r.Text.Split('\n').Last());
            Assert.True(r.Data.ContainsKey("knownGap"));
        }

        [Fact]
        public void FoundNonKey_IsUnchanged()
        {
            var r = Engine.Lookup("LabelUtils", null, false);
            Assert.True(r.Found);
            Assert.DoesNotContain("Known gap", r.Text);
            Assert.False(r.Data.ContainsKey("knownGap"));
            var member = Engine.Lookup("FamilyItemFactory.NewModelText", null, false);
            Assert.True(member.Found);
            Assert.DoesNotContain("Known gap", member.Text);
        }

        [Fact]
        public void CompileHint_ForNewLabel_CarriesTheWholeBlock()
        {
            // Measured live (Dutch compiler): doc.FamilyCreate.NewLabel(v, XYZ.Zero, "x")
            var e = new ApiLookup.CompileError
            {
                Number = "CS1061", Column = 9, Line = "var x = doc.FamilyCreate.NewLabel(v, XYZ.Zero, \"x\");",
                Message = "Autodesk.Revit.Creation.FamilyItemFactory bevat geen definitie voor NewLabel en er is geen extensiemethode NewLabel gevonden"
            };
            var hint = Engine.CompileErrorHint(new[] { e });
            Assert.StartsWith("Hint: apilookup FamilyItemFactory.NewLabel", hint);
            Assert.Contains("  Known gap: labels", hint);
            Assert.Contains("  Manual step: ", hint);
            Assert.Contains("  using ", hint);
        }

        [Fact]
        public void CompileHint_WithoutAGap_IsCappedAsBefore()
        {
            var e = new ApiLookup.CompileError
            {
                Number = "CS1061", Column = 9, Line = "var x = f.NewLabl();",
                Message = "'VibeModel.Tests.GapFixture.FamilyItemFactory' does not contain a definition for 'NewLabl'"
            };
            // "NewLabl" is no key: the hint is the plain lookup, nothing added.
            var hint = Engine.CompileErrorHint(new[] { e });
            Assert.StartsWith("Hint: apilookup FamilyItemFactory.NewLabl", hint);
            Assert.DoesNotContain("Known gap", hint);
            Assert.True(hint.Split('\n').Length <= 1 + ApiLookup.HintLines);
        }
    }
}
