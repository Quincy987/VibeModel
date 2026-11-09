using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.Services.Standards;
using VibeModel.UI;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>The annotate policy, what the AI may never do, and the saved exec scripts (v1.2.16).</summary>
    public class AnnotateModeTests
    {
        private static ChangedElement Model(long id, string category = "Walls") => new ChangedElement(id, category, isModel: true);
        private static ChangedElement Drawing(long id, string category = "Views") => new ChangedElement(id, category);
        private static ChangedElement UnreadDeletion(long id) => new ChangedElement(id, null, isModel: true);

        [Theory]
        [InlineData("annotate")]
        [InlineData("Annotation")]
        [InlineData("annotation-only")]
        [InlineData("opmaak")]
        [InlineData("alleen-opmaak")]
        public void Parse_TakesTheNewMode(string value)
        {
            Assert.True(ChangeApprovalPolicy.TryParse(value, out var mode));
            Assert.Equal(ChangeApprovalMode.Annotate, mode);
        }

        [Fact]
        public void NameAndDescribe()
        {
            Assert.Equal("annotate", ChangeApprovalPolicy.Name(ChangeApprovalMode.Annotate));
            Assert.Equal("annotate (settings)", ChangeApprovalPolicy.Describe(ChangeApprovalMode.Annotate, ChangeApprovalSource.Settings, false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EveryModifyingCommandGetsARunGroup(bool modifying)
            => Assert.Equal(modifying, ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Annotate, false, modifying, false, false));

        [Fact]
        public void Decide_KeepsAnnotationsRefusesTheModel()
        {
            Assert.Equal(RunDecision.Keep, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Annotate, false, false, false, false, touchesModel: false));
            Assert.Equal(RunDecision.AnnotationOnly, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Annotate, false, false, false, false, touchesModel: true));
            Assert.Equal(RunDecision.Nothing, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Annotate, false, false, false, true, touchesModel: false));
            Assert.Equal(RunDecision.Preview, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Annotate, false, true, false, false, touchesModel: true));
        }

        [Fact]
        public void AnAddedModelElementIsRefusedAlsoFromAViewCommand()
        {
            var s = new ChangeSetSummary(new[] { Model(1) }, null, null);
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "wall" }));
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "section" }));
        }

        [Fact]
        public void ModifiedHostsAreRegenerationUnderViewCommandsOnly()
        {
            var s = new ChangeSetSummary(null, new[] { Model(1) }, null);
            Assert.False(ChangeApprovalPolicy.TouchesModel(s, new[] { "tag" }));
            Assert.False(ChangeApprovalPolicy.TouchesModel(s, new[] { "dimension", "text" }));
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "set" }));
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "exec" }));
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "text", "set" })); // a batch: the strictest
        }

        [Fact]
        public void AnUnreadDeletionIsModelExceptUnderViewCommands()
        {
            var s = new ChangeSetSummary(null, null, new[] { UnreadDeletion(1) });
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "exec" }));
            Assert.False(ChangeApprovalPolicy.TouchesModel(s, new[] { "viewport" }));
        }

        [Fact]
        public void AReadModelDeletionIsRefusedEvenUnderViewCommands()
        {
            var s = new ChangeSetSummary(null, null, new[] { Model(1) });
            Assert.True(ChangeApprovalPolicy.TouchesModel(s, new[] { "viewport" }));
        }

        [Fact]
        public void OnlyDrawingWork_IsKept()
        {
            var s = new ChangeSetSummary(new[] { Drawing(1) }, new[] { Drawing(2) }, new[] { Drawing(3) });
            Assert.False(ChangeApprovalPolicy.TouchesModel(s, new[] { "exec" }));
        }

        [Fact]
        public void Message_NamesOnlyTheModelPart()
        {
            var s = new ChangeSetSummary(new[] { Model(1), Drawing(2) }, null, new[] { UnreadDeletion(3) });
            Assert.Equal("VibeModel is set to annotation-only; this change touched the model (added 1 (Walls 1), deleted 1 (unread, treated as model 1)) and was rolled back.",
                ChangeApprovalPolicy.AnnotationOnlyMessage(s));
        }

        [Theory]
        [InlineData(-2000011, true, false, false, false, true)]
        [InlineData(-2001300, true, false, false, false, true)]
        [InlineData(-2000220, true, false, false, false, true)]
        [InlineData(-2000500, true, false, false, false, false)]
        [InlineData(-2000278, true, false, false, false, false)]
        [InlineData(-2003100, true, false, false, false, false)]
        [InlineData(-2002000, true, false, false, false, false)]
        [InlineData(-2000051, true, true, false, false, false)]
        [InlineData(-2000011, true, false, false, true, false)]     // a wall type
        [InlineData(-2000279, true, false, true, false, false)]
        [InlineData(-2000300, false, true, false, false, false)]
        public void Classification(int category, bool modelCategory, bool viewSpecific, bool isView, bool isType, bool expected)
            => Assert.Equal(expected, AnnotationScope.IsModelElement(category, modelCategory, viewSpecific, isView, isType));

        [Theory]
        [InlineData("nl", "Alleen opmaak")]
        [InlineData("en", "Annotation only")]
        public void TheChipNamesTheMode(string language, string expected)
        {
            Assert.Equal(expected, UiText.ModeName(ChangeApprovalMode.Annotate, language));
            Assert.NotEqual(UiText.ModeHint(ChangeApprovalMode.Auto, language), UiText.ModeHint(ChangeApprovalMode.Annotate, language));
        }

        [Fact]
        public void TheRulesFollowTheMode()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl", ChangeApprovalMode.Annotate);
            Assert.Contains("annotation-only", rules);
            Assert.DoesNotContain("never refused", rules);
            Assert.DoesNotContain("annotation-only", ChatBackendBase.BuildBehaviourRules("nl", ChangeApprovalMode.Auto));
        }
    }

    public class ForbiddenActionsTests
    {
        [Theory]
        [InlineData("var bp = BasePoint.GetProjectBasePoint(doc); bp.Position = new XYZ(1,0,0);")]
        [InlineData("var bp = BasePoint.GetSurveyPoint(doc); ElementTransformUtils.MoveElement(doc, bp.Id, new XYZ(1,0,0));")]
        [InlineData("foreach (BasePoint p in new FilteredElementCollector(doc).OfClass(typeof(BasePoint))) { doc.Delete(p.Id); }")]
        public void MovingABasePoint_IsRefused(string code) => Assert.Equal(ForbiddenActions.BasePointWhat, ForbiddenActions.ScanExec(code));

        [Theory]
        [InlineData("doc.ActiveProjectLocation = other;")]
        [InlineData("var l = doc.ActiveProjectLocation; l.SetProjectPosition(XYZ.Zero, pos);")]
        public void ChangingCoordinates_IsRefused(string code) => Assert.Equal(ForbiddenActions.CoordinatesWhat, ForbiddenActions.ScanExec(code));

        [Theory]
        [InlineData("grid.Pinned = false;")]
        [InlineData("foreach (var g in grids) { g.Pinned=false; }")]
        public void Unpinning_IsRefused(string code) => Assert.Equal(ForbiddenActions.PinWhat, ForbiddenActions.ScanExec(code));

        [Theory]
        [InlineData("view.ViewTemplateId = id;")]
        [InlineData("view.ApplyViewTemplateParameters(other);")]
        [InlineData("foreach (View t in views.Where(v => v.IsTemplate)) { t.get_Parameter(BuiltInParameter.VIEW_SCALE).Set(50); }")]
        [InlineData("var t = views.First(v => v.IsTemplate); t.AddFilter(id);")]
        public void EditingATemplate_IsRefused(string code) => Assert.Equal(ForbiddenActions.TemplateWhat, ForbiddenActions.ScanExec(code));

        [Theory]
        [InlineData("sb.AppendLine(BasePoint.GetProjectBasePoint(doc).Position.ToString());")]
        [InlineData("var p = BasePoint.GetSurveyPoint(doc).Position; sb.AppendLine(p.X.ToString());")]
        [InlineData("// move the BasePoint.Position = x\nsb.AppendLine(doc.Title);")]
        [InlineData("/* Doel: BasePoint = weg */ sb.AppendLine(doc.Title);")]
        [InlineData("sb.AppendLine(\"bp.Pinned = false\");")]
        [InlineData("var views = all.Where(v => !v.IsTemplate).ToList(); views[0].Name = \"x\";")]
        [InlineData("var loc = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Angle; sb.AppendLine(loc.ToString());")]
        [InlineData("BasePoint bp = BasePoint.GetSurveyPoint(doc); sb.AppendLine(bp.Position.X.ToString());")]
        [InlineData("SiteLocation site = doc.SiteLocation; sb.AppendLine(site.PlaceName);")]
        [InlineData("foreach (View v in views) { if (v.IsTemplate) found.Add(v); }")]
        [InlineData("if (v.IsTemplate) n = n + 1;")]
        [InlineData("grid.Pinned = true;")]
        [InlineData("wall.LookupParameter(\"Comments\").Set(\"x\");")]
        public void ReadingOrUnrelated_Passes(string code) => Assert.Null(ForbiddenActions.ScanExec(code));

        [Theory]
        [InlineData("12345 Pinned 0", true)]
        [InlineData("12345 \"Pinned\" 0", true)]
        [InlineData("12345 pinned=0", true)]
        [InlineData("12345 Comments pinned by hand", true)]
        [InlineData("12345 Comments x", false)]
        public void SetNamesPinned(string args, bool expected) => Assert.Equal(expected, ForbiddenActions.SetsPinned(args));

        private static Dictionary<string, ProtectedState> Reading(params (string key, ProtectedKind kind, string state)[] items)
            => items.ToDictionary(i => i.key, i => new ProtectedState(i.kind, i.key, i.state));

        [Fact]
        public void Difference_NewItemsAreNotChanges()
        {
            var before = Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.PinnedState));
            var after = Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.PinnedState),
                ("pin:2", ProtectedKind.Pin, ForbiddenActions.PinnedState), ("template:9", ProtectedKind.Template, "h"));
            Assert.Null(ForbiddenActions.Difference(before, after));
        }

        [Fact]
        public void Difference_PinOnlyCountsWhenUnpinned()
        {
            Assert.Equal("pin:1", ForbiddenActions.Difference(
                Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.PinnedState)),
                Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.UnpinnedState))));
            Assert.Null(ForbiddenActions.Difference(
                Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.UnpinnedState)),
                Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.PinnedState))));
        }

        [Fact]
        public void Difference_ADeletedGridIsNotAPinChange_ADeletedTemplateIs()
        {
            Assert.Null(ForbiddenActions.Difference(Reading(("pin:1", ProtectedKind.Pin, ForbiddenActions.PinnedState)), Reading()));
            Assert.Equal("template:9", ForbiddenActions.Difference(Reading(("template:9", ProtectedKind.Template, "h")), Reading()));
        }

        [Fact]
        public void Difference_AMovedPointCounts()
        {
            Assert.Equal("p", ForbiddenActions.Difference(Reading(("p", ProtectedKind.Position, "0")), Reading(("p", ProtectedKind.Position, "1"))));
            Assert.Null(ForbiddenActions.Difference(Reading(("p", ProtectedKind.Position, "0")), Reading(("p", ProtectedKind.Position, "0"))));
        }

        [Fact]
        public void TheRulesNeverAskAboutTemplates()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl");
            Assert.Contains(ChatBackendBase.ForbiddenRule, rules);
            Assert.DoesNotContain("view templates, project materials", rules);
        }
    }

    public class ProjectRecipesTests
    {
        private static readonly DateTime When = new DateTime(2026, 11, 9, 14, 3, 12);

        [Theory]
        [InlineData("Maak een muur: 5 m/3 m?", "Maak een muur 5 m 3 m")]
        [InlineData(null, "exec")]
        [InlineData("***", "exec")]
        public void Slug_IsSafe(string question, string expected) => Assert.Equal(expected, ProjectRecipes.Slug(question));

        [Fact]
        public void Slug_IsCut()
            => Assert.Equal(40, ProjectRecipes.Slug(new string('a', 80)).Length);

        [Fact]
        public void FileName_SortsByTimeAndCountsWithinASecond()
        {
            Assert.Equal("2026-11-09 140312 muur.cs", ProjectRecipes.FileName(When, "muur"));
            Assert.Equal("2026-11-09 140312 muur (2).cs", ProjectRecipes.FileName(When, "muur", 2));
            var first = ProjectRecipes.NextFileName(When.AddDays(1), "x");
            var second = ProjectRecipes.NextFileName(When.AddDays(1), "x");
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Content_HasTheHeaderThenTheSnippetVerbatim()
        {
            var snippet = "// Doel: muur\r\nsb.AppendLine(\"a\");";
            var text = ProjectRecipes.Content(When, "jjansen", "maak\neen muur", "-- changed: added 1 (Walls 1), modified 0, deleted 0", snippet);
            Assert.Contains("// Datum: 2026-11-09 14:03:12", text);
            Assert.Contains("// Door: jjansen", text);
            Assert.Contains("// Vraag: maak een muur", text);
            Assert.Contains("// -- changed: added 1", text);
            Assert.EndsWith(snippet, text);
        }

        [Theory]
        [InlineData("// Doel: de muur verplaatsen", "de muur verplaatsen")]
        [InlineData("/* Doel: de muur */ /* Aannames: x */ sb.AppendLine(1);", "de muur")]
        public void ParseHeader_ReadsTheGoal(string line, string goal)
        {
            var text = ProjectRecipes.Content(When, "jjansen", "q", null, line);
            var h = ProjectRecipes.ParseHeader(text.Replace("\r\n", "\n").Split('\n'));
            Assert.Equal("2026-11-09 14:03:12", h.Date);
            Assert.Equal("jjansen", h.User);
            Assert.StartsWith(goal, h.Goal);
        }

        [Theory]
        [InlineData("// Doel: x\\nsb.AppendLine(1);", true)]
        [InlineData("  // alleen commentaar  ", true)]
        [InlineData("// Doel: x\nsb.AppendLine(1);", false)]
        [InlineData("/* Doel: x */ sb.AppendLine(1);", false)]
        [InlineData("sb.AppendLine(1); // klaar", false)]
        public void OneCommentLine_IsDetected(string snippet, bool expected) => Assert.Equal(expected, ProjectRecipes.IsOnlyLineComment(snippet));

        [Fact]
        public void WriteFile_NeverOverwrites_ListSortsByName()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vm-recipes-" + Guid.NewGuid());
            try
            {
                ProjectRecipes.WriteFile(dir, "2026-11-09 140312 b.cs", "one");
                ProjectRecipes.WriteFile(dir, "2026-11-09 140312 b.cs", "two");
                ProjectRecipes.WriteFile(dir, "2026-11-08 090000 a.cs", "three");
                var files = ProjectRecipes.List(new[] { dir, Path.Combine(dir, "missing") });
                Assert.Equal(3, files.Count);
                Assert.EndsWith("2026-11-08 090000 a.cs", files[0]);
                Assert.Equal("one", File.ReadAllText(Path.Combine(dir, "2026-11-09 140312 b.cs")));
                Assert.Equal("two", File.ReadAllText(Path.Combine(dir, "2026-11-09 140312 b (2).cs")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Journal_NamesTheRecipe()
        {
            var line = ProjectJournal.FormatCommandLine(When, "exec", "x", new List<long> { 7 }, null, null, null, null, null, "jjansen",
                new List<string> { @"C:\a\2026-11-09 140312 muur.cs" });
            Assert.Equal("- 2026-11-09 14:03:12 · by jjansen · exec x · created 7 · recipe 2026-11-09 140312 muur.cs", line);
        }

        [Fact]
        public void TheRuleAsksForRealNewlines()
            => Assert.Contains("real newlines", ChatBackendBase.ExecRecipeRule);
    }

    public class PathProbeLocalDriveTests
    {
        [Fact]
        public void ATempFileIsOnALocalDrive_AndAnswersWithoutADeadline()
        {
            var real = Path.GetTempFileName();
            try
            {
                Assert.True(PathProbe.IsLocalDrive(real));
                Assert.Equal(true, PathProbe.FileExists(real, TimeSpan.Zero));
                Assert.Equal(false, PathProbe.FileExists(real + ".nope", TimeSpan.Zero));
                Assert.Equal(true, PathProbe.DirectoryExists(Path.GetDirectoryName(real), TimeSpan.Zero));
            }
            finally { File.Delete(real); }
        }

        [Theory]
        [InlineData(@"\\server\share\a.rvt")]
        [InlineData("//server/share/a.rvt")]
        [InlineData("relative\\a.rvt")]
        [InlineData("")]
        public void OtherPathsAreNot(string path) => Assert.False(PathProbe.IsLocalDrive(path));
    }
}
