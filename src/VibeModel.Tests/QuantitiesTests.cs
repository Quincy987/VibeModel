using System.Collections.Generic;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.Services.Helpers;
using Xunit;
using Kind = VibeModel.Services.Helpers.QuantityKind;

namespace VibeModel.Tests
{
    /// <summary>
    /// Interview Ruben Wolbers 2026-11-12: quantities follow the model — the rule, schedule --takeoff /
    /// --calc, the calculated-field gap and the "-- quantities:" line.
    /// </summary>
    public class QuantitiesTests
    {
        // ------------------------------------------------------------------ (a) the rule

        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void Rules_CarryTheDynamicQuantitiesRule_RightAfterTheScheduleRule(string language)
        {
            var rules = ChatBackendBase.BuildBehaviourRules(language);
            var schedule = rules.IndexOf("- " + ChatBackendBase.QuantitiesRule);
            var dynamic = rules.IndexOf("- " + ChatBackendBase.DynamicQuantitiesRule);
            Assert.True(schedule >= 0 && dynamic > schedule);
            // Directly the next bullet: the merge with the parallel rule edit stays trivial.
            var between = rules.Substring(schedule, dynamic - schedule);
            Assert.Equal(1, between.Split('\n').Length - 1);
        }

        [Fact]
        public void DynamicQuantitiesRule_NamesTheRoutesAndForbidsTheFixedValue()
        {
            var rule = ChatBackendBase.DynamicQuantitiesRule;
            Assert.Contains("fixed value", rule);
            Assert.Contains("`schedule --takeoff`", rule);
            Assert.Contains("`schedule --calc`", rule);
            Assert.Contains("SetFormula", rule);
            Assert.Contains("ask how the user wants it solved", rule);
            Assert.Contains("never as the default", rule);
            // The API-gap exception lives here, not in UndoRule (pinned letter for letter).
            Assert.Contains("Known gap", rule);
            Assert.DoesNotContain("never refused", rule);
        }

        [Fact]
        public void AskFirst_NamesProjectParameters()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl");
            Assert.Contains("project parameters and parameter bindings (also through exec)", rules);
        }

        // ------------------------------------------------------------------ (b) arguments

        [Fact]
        public void Takeoff_WithoutFields_GetsTheMaterialColumns()
        {
            var a = ScheduleArgs.Parse("walls --takeoff");
            Assert.Null(a.Error);
            Assert.True(a.Takeoff);
            Assert.Equal(new[] { "Family and Type", "Material: Name", "Material: Area", "Material: Volume" }, a.Fields);
        }

        [Fact]
        public void Takeoff_WithFields_KeepsThem_AndGroupChecksThem()
        {
            var a = ScheduleArgs.Parse("walls --takeoff --fields \"Material: Name,Material: Area\" --group Material: Name --total");
            Assert.Null(a.Error);
            Assert.Equal(new[] { "Material: Name", "Material: Area" }, a.Fields);
            Assert.Equal("Material: Name", a.Group);
            Assert.True(a.Total);
        }

        [Fact]
        public void Takeoff_TakesNoValue()
        {
            Assert.Contains("--takeoff takes no value", ScheduleArgs.Parse("walls --takeoff yes --fields Length").Error);
        }

        [Fact]
        public void PlainSchedule_StillNeedsFields()
        {
            Assert.Contains("--fields is required", ScheduleArgs.Parse("walls --total").Error);
        }

        [Fact]
        public void Calc_SplitsNameAndFormula_AtTheFirstEquals()
        {
            var a = ScheduleArgs.Parse("structural foundations --fields \"Family and Type,Count\" --calc \"Paaloppervlak = Length * Width\"");
            Assert.Null(a.Error);
            Assert.Equal("Paaloppervlak", a.CalcName);
            Assert.Equal("Length * Width", a.CalcFormula);
            // A formula may hold its own '=' (a comparison inside if()).
            var b = ScheduleArgs.Parse("walls --fields Length --calc \"Lang=if(Length >= 1000, 1, 0)\"");
            Assert.Equal("if(Length >= 1000, 1, 0)", b.CalcFormula);
        }

        [Theory]
        [InlineData("walls --fields Length --calc Oppervlak", "--calc needs")]
        [InlineData("walls --fields Length --calc \"=Length*2\"", "name before '=' is empty")]
        [InlineData("walls --fields Length --calc \"Oppervlak=\"", "formula after '=' is empty")]
        public void Calc_Refusals(string args, string expected)
        {
            Assert.Contains(expected, ScheduleArgs.Parse(args).Error);
        }

        [Theory]
        [InlineData("Palenstaat --rows --takeoff")]
        [InlineData("Palenstaat --rows --calc \"a=b\"")]
        public void Rows_RefusesTheCreateFlags(string args)
        {
            Assert.Contains("--rows reads an existing schedule", ScheduleArgs.Parse(args).Error);
        }

        [Fact]
        public void Usage_NamesTheNewFlags()
        {
            Assert.Contains("--takeoff", ScheduleArgs.Usage);
            Assert.Contains("--calc", ScheduleArgs.Usage);
        }

        // ------------------------------------------------------------------ (b) the calculated-field hint

        private static readonly string[] Fields = { "Family and Type", "Count", "Length", "Width", "Width 2", "Base Offset", "Modified", "Volume", "Area" };

        [Fact]
        public void FieldsIn_MatchesWholeNamesOnly()
        {
            Assert.Equal(new[] { "Length", "Width" }, CalculatedFieldHint.FieldsIn("Length * Width", Fields));
            Assert.Equal(new[] { "Width 2" }, CalculatedFieldHint.FieldsIn("Width 2 * 2", Fields));
            Assert.Equal(new[] { "Base Offset", "Length" }, CalculatedFieldHint.FieldsIn("(Base Offset+Length)", Fields));
            // "if" is not the start of "Modified", "Len" is not "Length".
            Assert.Equal(new[] { "Length" }, CalculatedFieldHint.FieldsIn("if(Length > 1000 mm, Length, 0 mm)", Fields));
            Assert.Empty(CalculatedFieldHint.FieldsIn("Len * Wid", Fields));
            Assert.Equal(new[] { "Length" }, CalculatedFieldHint.FieldsIn("LENGTH*2", Fields));
        }

        [Fact]
        public void WithExactNames_WritesTheFieldsAsTheScheduleNamesThem()
        {
            Assert.Equal("Length * Width", CalculatedFieldHint.WithExactNames("length * WIDTH", Fields));
            Assert.Equal("if(Length > 1000 mm, Base Offset, 0 mm)", CalculatedFieldHint.WithExactNames("if(LENGTH > 1000 mm, base offset, 0 mm)", Fields));
            Assert.Equal("Len * Hoogte", CalculatedFieldHint.WithExactNames("Len * Hoogte", Fields));   // unknown words untouched
        }

        [Fact]
        public void UnknownWords_SkipFieldsFunctionsAndUnits()
        {
            Assert.Empty(CalculatedFieldHint.UnknownWords("if(Length > 1000 mm, sqrt(Area), 0 m)", Fields));
            Assert.Empty(CalculatedFieldHint.UnknownWords("Length + 1000mm + 2m", Fields));
            Assert.Equal(new[] { "Hoogte" }, CalculatedFieldHint.UnknownWords("Length * Hoogte", Fields));
        }

        private static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            { "Length", Kind.Length }, { "Width", Kind.Length }, { "Width 2", Kind.Length }, { "Base Offset", Kind.Length },
            { "Area", Kind.Area }, { "Volume", Kind.Volume }, { "Count", Kind.Number }, { "Family and Type", Kind.Other }
        };

        [Theory]
        [InlineData("Length * Width", Kind.Area)]
        [InlineData("Length*Width*Base Offset", Kind.Volume)]
        [InlineData("Area * Length", Kind.Volume)]
        [InlineData("Length", Kind.Length)]
        [InlineData("Length + Width 2", Kind.Length)]
        [InlineData("2 * (Length + Width)", Kind.Length)]
        [InlineData("Length * 1 m", Kind.Area)]
        [InlineData("pi() * Width * Length", Kind.Area)]
        [InlineData("Volume / Length", Kind.Area)]
        [InlineData("Count * 2", Kind.Number)]
        [InlineData("Length ^ 2", Kind.Area)]
        [InlineData("sqrt(Area)", Kind.Length)]
        [InlineData("if(Length > 1000 mm, Length * Width, 0 m²)", Kind.Area)]
        public void InferType_FollowsTheDimension(string formula, Kind expected)
        {
            Assert.Equal(expected, CalculatedFieldHint.InferType(formula, Kinds));
        }

        [Theory]
        [InlineData("Length + Area")]          // mixed dimensions
        [InlineData("Family and Type * 2")]    // a field of no measurable kind
        [InlineData("Length * Hoogte")]        // an unknown word
        [InlineData("(Length * Width")]        // unbalanced
        public void InferType_IsNull_WhenItCannotTell(string formula)
        {
            Assert.Null(CalculatedFieldHint.InferType(formula, Kinds));
        }

        [Fact]
        public void ManualStep_NamesTheTypeAndTheApiLimit()
        {
            var line = CalculatedFieldHint.ManualStep("VM test calc", "Paaloppervlak", "Length * Width", Kind.Area);
            Assert.StartsWith("Manual step (the API cannot add a calculated field):", line);
            Assert.Contains("\"VM test calc\"", line);
            Assert.Contains("fx (Add calculated parameter)", line);
            Assert.Contains("Name \"Paaloppervlak\"", line);
            Assert.Contains("Type \"Area\"", line);
            Assert.Contains("Inconsistent units", line);
            Assert.Contains("Formula: Length * Width", line);
            Assert.DoesNotContain("\n", line);

            var unknown = CalculatedFieldHint.ManualStep("S", "X", "Length + Area", null);
            Assert.Contains("Type = the unit of the result", unknown);

            var json = CalculatedFieldHint.ManualStepJson("S", "X", "Length*Width", Kind.Area);
            Assert.Equal(true, json["apiCannot"]);
            Assert.Equal("Area", json["type"]);
        }

        // ------------------------------------------------------------------ (b) the known gap

        [Theory]
        [InlineData("ScheduleFieldType.Formula", KnownApiGaps.QueryKind.Member)]
        [InlineData("Autodesk.Revit.DB.ScheduleFieldType.Formula", KnownApiGaps.QueryKind.Unknown)]
        [InlineData("ScheduleField.IsCalculatedField", KnownApiGaps.QueryKind.Member)]
        [InlineData("AddCalculatedField", KnownApiGaps.QueryKind.Unknown)]
        [InlineData("ScheduleDefinition.AddCalculatedField", KnownApiGaps.QueryKind.Member)]
        public void CalculatedFieldGap_Matches(string query, KnownApiGaps.QueryKind kind)
        {
            Assert.Equal("calculated-fields", KnownApiGaps.Match(query, kind)?.Name);
        }

        [Theory]
        [InlineData("ScheduleField", KnownApiGaps.QueryKind.Type)]
        [InlineData("ScheduleDefinition.AddField", KnownApiGaps.QueryKind.Member)]
        [InlineData("AddField", KnownApiGaps.QueryKind.Unknown)]
        [InlineData("FamilyManager.SetFormula", KnownApiGaps.QueryKind.Member)]
        [InlineData("ScheduleFieldType", KnownApiGaps.QueryKind.Type)]
        [InlineData("Formula", KnownApiGaps.QueryKind.Unknown)]
        public void CalculatedFieldGap_LeavesWorkingApiAlone(string query, KnownApiGaps.QueryKind kind)
        {
            Assert.Null(KnownApiGaps.Match(query, kind));
        }

        [Fact]
        public void CalculatedFieldGap_SaysWhatWorks()
        {
            var text = KnownApiGaps.Describe(KnownApiGaps.Match("ScheduleFieldType.Formula"));
            Assert.Contains("fieldType is not a schedulable field type", text);
            Assert.Contains("--takeoff", text);
            Assert.Contains("--calc", text);
            Assert.Contains("Inconsistent units", text);
        }

        // ------------------------------------------------------------------ (c) the "-- quantities:" line

        [Fact]
        public void QuantitiesLine_OneParameter()
        {
            var line = StaticQuantityReport.Line(new List<StaticQuantityReport.Entry>
                { new StaticQuantityReport.Entry { Parameter = "Oppervlak", Elements = 12, Spec = "Area" } });
            Assert.Equal("-- quantities: parameter 'Oppervlak' (Area, new this run) was filled with fixed numbers on 12 elements; " +
                         "they do not follow model changes — use a schedule field, a takeoff or a family formula for quantities that must stay current", line);
        }

        [Fact]
        public void QuantitiesLine_SeveralParameters_AndOneElement()
        {
            var line = StaticQuantityReport.Line(new List<StaticQuantityReport.Entry>
            {
                new StaticQuantityReport.Entry { Parameter = "A", Elements = 1, Spec = "Area" },
                new StaticQuantityReport.Entry { Parameter = "V", Elements = 3, Spec = "Volume" },
            });
            Assert.StartsWith("-- quantities: new parameters 'A' (Area, 1 element), 'V' (Volume, 3 elements) were filled with fixed numbers this run; ", line);
        }

        [Fact]
        public void QuantitiesLine_IsConditional_ForPlainNumbers()
        {
            // A Number parameter may be a pile number or a sequence, not a quantity.
            var number = StaticQuantityReport.Line(new List<StaticQuantityReport.Entry>
                { new StaticQuantityReport.Entry { Parameter = "Paalnummer", Elements = 40, Spec = "Number" } });
            Assert.Contains("on 40 elements; if they are quantities, they do not follow model changes", number);
            // Mixed with a real quantity: the warning is not softened.
            var mixed = StaticQuantityReport.Line(new List<StaticQuantityReport.Entry>
            {
                new StaticQuantityReport.Entry { Parameter = "Paalnummer", Elements = 40, Spec = "Number" },
                new StaticQuantityReport.Entry { Parameter = "Oppervlak", Elements = 40, Spec = "Area" },
            });
            Assert.DoesNotContain("if they are quantities", mixed);
        }

        [Fact]
        public void ClickPath_IsTheSameInTheGapAndInCalc()
        {
            var gap = KnownApiGaps.Describe(KnownApiGaps.Match("ScheduleFieldType.Formula"));
            var calc = CalculatedFieldHint.ManualStep("S", "X", "Length*Width", Kind.Area);
            Assert.Contains(CalculatedFieldHint.ClickPath, gap);
            Assert.Contains(CalculatedFieldHint.ClickPath, calc);
        }

        [Fact]
        public void QuantitiesLine_SaysWhenItStoppedReading()
        {
            var line = StaticQuantityReport.Line(new List<StaticQuantityReport.Entry>
                { new StaticQuantityReport.Entry { Parameter = "Oppervlak", Elements = 300, StoppedAfter = 5000, Spec = "Area" } });
            Assert.Contains("on at least 300 elements (stopped reading after 5000 changed elements); ", line);
        }

        [Fact]
        public void QuantitiesLine_IsNull_WhenNothingWasFilled()
        {
            Assert.Null(StaticQuantityReport.Line(null));
            Assert.Null(StaticQuantityReport.Line(new List<StaticQuantityReport.Entry>()));
            Assert.Null(StaticQuantityReport.Json(new List<StaticQuantityReport.Entry>()));
        }

        [Fact]
        public void QuantitiesJson_Shape()
        {
            var json = StaticQuantityReport.Json(new List<StaticQuantityReport.Entry>
                { new StaticQuantityReport.Entry { Parameter = "Oppervlak", Elements = 2, Spec = "Area" } });
            var e = Assert.IsType<Dictionary<string, object>>(Assert.Single(json));
            Assert.Equal("Oppervlak", e["parameter"]);
            Assert.Equal(true, e["newThisRun"]);
            Assert.Equal(2, e["elements"]);
            Assert.Equal(false, e["atLeast"]);
            Assert.Equal("Area", e["spec"]);
        }
    }
}
