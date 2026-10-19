using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 15, the pure part: which policy is in force, which runs get a run group, what a
    /// finished run does with it, the texts the client and the window see, and the session's
    /// decision log. The Revit side (group, isolate, window) is verified live.
    /// </summary>
    public class ChangeApprovalPolicyTests
    {
        [Theory]
        [InlineData("auto", ChangeApprovalMode.Auto)]
        [InlineData("AUTO", ChangeApprovalMode.Auto)]
        [InlineData("never", ChangeApprovalMode.Auto)]
        [InlineData("ask", ChangeApprovalMode.Ask)]
        [InlineData(" Ask ", ChangeApprovalMode.Ask)]
        [InlineData("readonly", ChangeApprovalMode.ReadOnly)]
        [InlineData("read-only", ChangeApprovalMode.ReadOnly)]
        [InlineData("read_only", ChangeApprovalMode.ReadOnly)]
        public void TryParse_KnownValues(string text, ChangeApprovalMode expected)
        {
            Assert.True(ChangeApprovalPolicy.TryParse(text, out var mode));
            Assert.Equal(expected, mode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("maybe")]
        public void TryParse_UnknownValues_Fail(string text)
        {
            Assert.False(ChangeApprovalPolicy.TryParse(text, out _));
        }

        [Fact]
        public void Name_RoundTrips()
        {
            foreach (ChangeApprovalMode m in Enum.GetValues(typeof(ChangeApprovalMode)))
            {
                Assert.True(ChangeApprovalPolicy.TryParse(ChangeApprovalPolicy.Name(m), out var back));
                Assert.Equal(m, back);
            }
        }

        [Fact]
        public void Resolve_DefaultIsAuto()
        {
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.Resolve(null, null, null, out var source));
            Assert.Equal(ChangeApprovalSource.Default, source);
        }

        [Fact]
        public void Resolve_ManagedThenEnvironmentThenSettings()
        {
            Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.Resolve("readonly", "ask", "auto", out var s1));
            Assert.Equal(ChangeApprovalSource.Managed, s1);
            Assert.Equal(ChangeApprovalMode.Ask, ChangeApprovalPolicy.Resolve(null, "ask", "readonly", out var s2));
            Assert.Equal(ChangeApprovalSource.Environment, s2);
            Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.Resolve(null, null, "readonly", out var s3));
            Assert.Equal(ChangeApprovalSource.Settings, s3);
        }

        [Fact]
        public void Resolve_SkipsAnUnreadableValue()
        {
            Assert.Equal(ChangeApprovalMode.Ask, ChangeApprovalPolicy.Resolve("bogus", "", "ask", out var source));
            Assert.Equal(ChangeApprovalSource.Settings, source);
        }

        [Fact]
        public void Effective_DontAskThisSession_TurnsAskIntoAuto_Only()
        {
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.Effective(ChangeApprovalMode.Ask, true));
            Assert.Equal(ChangeApprovalMode.Ask, ChangeApprovalPolicy.Effective(ChangeApprovalMode.Ask, false));
            Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.Effective(ChangeApprovalMode.ReadOnly, true));
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.Effective(ChangeApprovalMode.Auto, true));
        }

        [Fact]
        public void Describe_NamesTheSource()
        {
            Assert.Equal("auto (default)", ChangeApprovalPolicy.Describe(ChangeApprovalMode.Auto, ChangeApprovalSource.Default, false));
            Assert.Equal("ask (settings)", ChangeApprovalPolicy.Describe(ChangeApprovalMode.Ask, ChangeApprovalSource.Settings, false));
            Assert.Equal("readonly (VIBEMODEL_APPROVAL)", ChangeApprovalPolicy.Describe(ChangeApprovalMode.ReadOnly, ChangeApprovalSource.Environment, false));
            Assert.Equal("ask (managed.json)", ChangeApprovalPolicy.Describe(ChangeApprovalMode.Ask, ChangeApprovalSource.Managed, false));
            Assert.Contains("not asking", ChangeApprovalPolicy.Describe(ChangeApprovalMode.Ask, ChangeApprovalSource.Settings, true));
        }

        [Theory]
        [InlineData("delete", true)]
        [InlineData("exec", true)]
        [InlineData("set", true)]
        [InlineData("join", true)]
        [InlineData("unjoin", true)]
        [InlineData("phase", true)]
        [InlineData("wall", false)]
        [InlineData("place", false)]
        [InlineData("spot", false)]
        [InlineData("lwall", false)]
        public void Guarded_IsDeleteOrEdit_NeverCreation(string command, bool guarded)
        {
            Assert.Equal(guarded, ChangeApprovalPolicy.IsGuarded(command));
        }

        [Fact]
        public void GuardedBatch_WhenAnyItemIsGuarded()
        {
            Assert.True(ChangeApprovalPolicy.IsGuardedBatch(new[] { "wall", "delete" }));
            Assert.False(ChangeApprovalPolicy.IsGuardedBatch(new[] { "wall", "floor" }));
        }

        [Fact]
        public void NeedsRunGroup_AutoWithoutFlags_Never()
        {
            foreach (var guarded in new[] { true, false })
                Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, guarded, modifying: true, preview: false, readOnlyFlag: false));
        }

        [Fact]
        public void NeedsRunGroup_NonModifyingCommand_Never()
        {
            foreach (ChangeApprovalMode m in Enum.GetValues(typeof(ChangeApprovalMode)))
                Assert.False(ChangeApprovalPolicy.NeedsRunGroup(m, guarded: false, modifying: false, preview: true, readOnlyFlag: true));
        }

        [Fact]
        public void NeedsRunGroup_Cases()
        {
            Assert.True(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, false, true, preview: true, readOnlyFlag: false));
            Assert.True(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, true, false, readOnlyFlag: true));
            Assert.True(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Ask, guarded: true, modifying: true, preview: false, readOnlyFlag: false));
            Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Ask, guarded: false, modifying: true, preview: false, readOnlyFlag: false));
            Assert.True(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.ReadOnly, guarded: false, modifying: true, preview: false, readOnlyFlag: false));
        }

        [Fact]
        public void Decide_EveryBranch()
        {
            // preview wins, even with nothing changed
            Assert.Equal(RunDecision.Preview, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Ask, true, preview: true, readOnlyFlag: true, changesEmpty: true));
            // an empty change set is never prompted, refused or kept: the empty group goes
            Assert.Equal(RunDecision.Nothing, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Ask, true, false, false, changesEmpty: true));
            Assert.Equal(RunDecision.Nothing, ChangeApprovalPolicy.Decide(ChangeApprovalMode.ReadOnly, true, false, false, changesEmpty: true));
            Assert.Equal(RunDecision.Nothing, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Auto, true, false, readOnlyFlag: true, changesEmpty: true));
            // ?readonly=1 that changed something
            Assert.Equal(RunDecision.QueryModified, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Ask, true, false, readOnlyFlag: true, changesEmpty: false));
            // policy readonly
            Assert.Equal(RunDecision.ReadOnly, ChangeApprovalPolicy.Decide(ChangeApprovalMode.ReadOnly, false, false, false, false));
            // ask + guarded
            Assert.Equal(RunDecision.Prompt, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Ask, guarded: true, preview: false, readOnlyFlag: false, changesEmpty: false));
            // ask + creation, auto
            Assert.Equal(RunDecision.Keep, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Ask, guarded: false, preview: false, readOnlyFlag: false, changesEmpty: false));
            Assert.Equal(RunDecision.Keep, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Auto, guarded: true, preview: false, readOnlyFlag: false, changesEmpty: false));
        }

        [Fact]
        public void Waiting_OnlyForAGuardedChangeUnderAsk()
        {
            Assert.True(ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalMode.Ask, true, true, false));
            Assert.False(ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalMode.Ask, true, true, preview: true));
            Assert.False(ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalMode.Ask, guarded: false, modifying: true, preview: false));
            Assert.False(ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalMode.Auto, true, true, false));
            Assert.False(ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalMode.ReadOnly, true, true, false));
        }

        [Fact]
        public void TimeoutFor_RaisesToTenMinutes_NeverLowers()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), ChangeApprovalPolicy.TimeoutFor(TimeSpan.FromSeconds(30), false));
            Assert.Equal(TimeSpan.FromMinutes(10), ChangeApprovalPolicy.TimeoutFor(TimeSpan.FromSeconds(30), true));
            Assert.Equal(TimeSpan.FromMinutes(20), ChangeApprovalPolicy.TimeoutFor(TimeSpan.FromMinutes(20), true));
        }

        [Fact]
        public void UndoName_CommandAndArgs_CutAt60()
        {
            Assert.Equal("VibeModel: delete 1730959 1730980", ChangeApprovalPolicy.UndoName("delete", "1730959  1730980"));
            Assert.Equal("VibeModel: exec", ChangeApprovalPolicy.UndoName("exec", ""));
            var longName = ChangeApprovalPolicy.UndoName("exec", new string('x', 200));
            Assert.Equal(60, longName.Length);
            Assert.EndsWith("…", longName);
            Assert.StartsWith(ModelChangeTracker.TransactionPrefix, longName);
        }

        [Fact]
        public void Texts_ForTheClient()
        {
            Assert.Equal("-- approval: kept by the user (#7)", ChangeApprovalPolicy.KeptLine(7));
            Assert.Equal("ERROR: Discarded by the user.\nHint: do not retry this change; ask the user what they want instead.",
                ChangeApprovalPolicy.DiscardedText());
            var json = ChangeApprovalPolicy.ApprovalJson(8, ApprovalDecision.Discarded);
            Assert.Equal(8, json["id"]);
            Assert.Equal("discarded", json["decision"]);
            Assert.Equal("user", json["by"]);
        }
    }

    public class ChangeSetSummaryTests
    {
        private static ChangeSetSummary Sample() => new ChangeSetSummary(
            new[] { new ChangedElement(11, "Walls"), new ChangedElement(12, "Walls") },
            new[] { new ChangedElement(20, "Floors") },
            Enumerable.Empty<ChangedElement>());

        [Fact]
        public void PreviewLine_AsDocumented()
        {
            Assert.Equal("-- preview: nothing was changed. Would add 2 (Walls 2), modify 1 (Floors 1), delete 0. Ids are provisional.",
                Sample().PreviewLine());
        }

        [Fact]
        public void PreviewLine_NoAdded_NoProvisionalNote()
        {
            var s = new ChangeSetSummary(null, null, new[] { new ChangedElement(5, "Structural Foundations"), new ChangedElement(6, null) });
            Assert.Equal("-- preview: nothing was changed. Would add 0, modify 0, delete 2 (other 1, Structural Foundations 1).",
                s.PreviewLine());
        }

        [Fact]
        public void ByCategory_MostFirst_ThenName()
        {
            var list = new[]
            {
                new ChangedElement(1, "Walls"), new ChangedElement(2, "Dimensions"), new ChangedElement(3, "Dimensions"),
                new ChangedElement(4, "Doors"), new ChangedElement(5, "Walls"), new ChangedElement(6, "Dimensions")
            };
            Assert.Equal("Dimensions 3, Walls 2, Doors 1", ChangeSetSummary.ByCategory(list));
        }

        [Fact]
        public void PastCounts_ForTheQueryError()
        {
            Assert.Equal("added 2, modified 1, deleted 0", Sample().PastCounts());
            Assert.Equal("the snippet changed the model (added 2, modified 1, deleted 0) and was rolled back.",
                ChangeApprovalPolicy.QueryModifiedMessage(Sample(), batch: false));
            Assert.StartsWith("the batch changed", ChangeApprovalPolicy.QueryModifiedMessage(Sample(), batch: true));
        }

        [Fact]
        public void Json_IdsAndCategories_RolledBack()
        {
            var json = new JavaScriptSerializer().Serialize(Sample().ToJson(rolledBack: true));
            Assert.Equal("{\"added\":[{\"id\":11,\"category\":\"Walls\"},{\"id\":12,\"category\":\"Walls\"}],\"modified\":[{\"id\":20,\"category\":\"Floors\"}],\"deleted\":[],\"rolledBack\":true}", json);
        }

        [Fact]
        public void Empty_WhenNothingChanged()
        {
            Assert.True(new ChangeSetSummary(null, null, null).IsEmpty);
            Assert.False(Sample().IsEmpty);
        }

        [Fact]
        public void WindowSummary_IsLocalized()
        {
            var nl = Strings.For("nl", Strings.ApprovalDeleted);
            var en = Strings.For("en", Strings.ApprovalDeleted);
            Assert.Equal("Verwijderd", nl);
            Assert.Equal("Deleted", en);
            Assert.Equal("VibeModel wil deze wijziging behouden", Strings.For("nl", Strings.ApprovalTitle));
            Assert.Equal("Behouden", Strings.For("nl", Strings.ApprovalKeep));
            Assert.Equal("Ongedaan maken", Strings.For("nl", Strings.ApprovalDiscard));
        }
    }

    public class ApprovalLogTests
    {
        public ApprovalLogTests() => ApprovalLog.ResetForTests();

        [Fact]
        public void Ids_Increase_AndListIsOldestFirst()
        {
            var a = ApprovalLog.Add(new DateTime(2026, 10, 19, 14, 3, 12), "delete 1 2", ApprovalDecision.Kept, "deleted 2", true);
            var b = ApprovalLog.Add(new DateTime(2026, 10, 19, 14, 4, 0), "set 3 Comments x", ApprovalDecision.Discarded, "modified 1", true);
            Assert.Equal(1, a.Id);
            Assert.Equal(2, b.Id);
            var list = ApprovalLog.List();
            Assert.Equal(new[] { 1, 2 }, list.Select(e => e.Id).ToArray());
            Assert.Equal("#1 14:03:12 delete 1 2 → kept by the user (deleted 2)", ApprovalLog.FormatLine(list[0]));
            Assert.Equal("#2 14:04:00 set 3 Comments x → discarded by the user (modified 1)", ApprovalLog.FormatLine(list[1]));
        }

        [Fact]
        public void Undelivered_IsShownOnce()
        {
            ApprovalLog.Add(DateTime.Now, "delete 1", ApprovalDecision.Kept, "deleted 1", delivered: true);
            Assert.Null(ApprovalLog.TakeUndelivered());

            var e = ApprovalLog.Add(DateTime.Now, "delete 2", ApprovalDecision.Discarded, "deleted 1", delivered: true);
            ApprovalLog.MarkUndelivered(e.Id);
            Assert.Equal("approval #2: discarded", ApprovalLog.TakeUndelivered());
            Assert.Null(ApprovalLog.TakeUndelivered());
        }

        [Fact]
        public void Undelivered_Several_Joined()
        {
            ApprovalLog.Add(DateTime.Now, "a", ApprovalDecision.Kept, "", delivered: false);
            ApprovalLog.Add(DateTime.Now, "b", ApprovalDecision.Discarded, "", delivered: false);
            Assert.Equal("approval #1: kept, approval #2: discarded", ApprovalLog.TakeUndelivered());
        }

        [Fact]
        public void ShortCounts_OnlyNonZero()
        {
            var s = new ChangeSetSummary(null, new[] { new ChangedElement(1, "Walls") }, new[] { new ChangedElement(2, "Walls"), new ChangedElement(3, "Walls") });
            Assert.Equal("modified 1, deleted 2", ApprovalLog.ShortCounts(s));
            Assert.Equal("", ApprovalLog.ShortCounts(new ChangeSetSummary(null, null, null)));
        }
    }

    public class ChangeApprovalQueryTests
    {
        [Theory]
        [InlineData("preview=1", true, false)]
        [InlineData("args=0+0+5000+0&preview=true", true, false)]
        [InlineData("readonly=1&format=json", false, true)]
        [InlineData("preview=1&readonly=yes", true, true)]
        [InlineData("PREVIEW=1", true, false)]
        [InlineData("preview=0", false, false)]
        [InlineData("args=preview%3D1", false, false)]
        [InlineData("", false, false)]
        public void RunOptions_FromQuery(string query, bool preview, bool readOnly)
        {
            var o = RevitHttpServer.ParseRunOptions(query);
            Assert.Equal(preview, o.Preview);
            Assert.Equal(readOnly, o.ReadOnly);
        }

        [Fact]
        public void NoFlags_IsTheSharedEmptyInstance()
        {
            Assert.Same(RunOptions.None, RevitHttpServer.ParseRunOptions("args=x&format=json"));
        }

        [Fact]
        public void Flags_DoNotLeakIntoArgs()
        {
            Assert.Equal("0 0 5000 0", RevitHttpServer.ParseQueryArgs("args=0+0+5000+0&preview=1&readonly=1"));
        }
    }

    public class ChangeApprovalRulesTests
    {
        [Fact]
        public void Auto_KeepsTheUndoSentence_Exactly()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl");
            Assert.Contains("- " + ChatBackendBase.UndoRule, rules);
            Assert.DoesNotContain("approval", rules, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(rules, ChatBackendBase.BuildBehaviourRules("nl", ChangeApprovalMode.Auto));
        }

        [Fact]
        public void Ask_TellsTheModelTheUserDecides_AndHowLongToWait()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl", ChangeApprovalMode.Ask);
            Assert.Contains("ERROR: Discarded by the user", rules);
            Assert.Contains("do not retry", rules);
            Assert.Contains("600000", rules);
            Assert.Contains("approvals", rules);
            Assert.DoesNotContain(ChatBackendBase.UndoRule, rules);
        }

        [Fact]
        public void ReadOnly_SaysChangesAreRefused()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("en", ChangeApprovalMode.ReadOnly);
            Assert.Contains("read-only", rules);
            Assert.Contains("READ_ONLY", rules);
            Assert.DoesNotContain(ChatBackendBase.UndoRule, rules);
        }

        [Fact]
        public void TurnPrompt_CarriesThePolicy()
        {
            var ask = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl", ChangeApprovalMode.Ask);
            Assert.Contains(ChatBackendBase.ApprovalWaitRule, ask);
            var auto = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl");
            Assert.DoesNotContain(ChatBackendBase.ApprovalWaitRule, auto);
        }

        [Fact]
        public void PromptFile_ExplainsPreviewAndReadOnly_AndListsApprovals()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, null);
            Assert.Contains(ClaudeCodeBackend.PreviewRule, text);
            Assert.Contains("/approvals", text);
        }
    }

    public class ChangeApprovalJournalTests
    {
        [Fact]
        public void DiscardedRun_IsJournaled_WithTheMarker()
        {
            var line = ProjectJournal.FormatCommandLine(new DateTime(2026, 10, 19, 14, 3, 12), "delete", "1730959", null, null, null,
                new ChatTurnRef { Id = "t1", Question = "verwijder de paal" }, null, ProjectJournal.DiscardedOutcome);
            Assert.Equal("- 2026-10-19 14:03:12 · delete 1730959 · discarded by the user · chat t1 \"verwijder de paal\"", line);
        }
    }

    [Collection("ProcessSettings")]
    public class ChangeApprovalSettingsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _env;

        public ChangeApprovalSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-approval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _env = Environment.GetEnvironmentVariable(ChangeApprovalPolicy.EnvVar);
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, null);
            SettingsManager.OverridePathForTests(Path.Combine(_dir, "settings.json"));
            ManagedSettings.OverrideSearchPathsForTests(Path.Combine(_dir, "managed.json"));
            ManagedSettings.OverrideTrustCheckForTests(_ => null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, _env);
            SettingsManager.OverridePathForTests(null);
            ManagedSettings.OverrideSearchPathsForTests(null);
            ManagedSettings.OverrideTrustCheckForTests(null);
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Default_IsAuto_NothingWritten()
        {
            Assert.Equal(ChangeApprovalMode.Auto, SettingsManager.GetChangeApproval(out var source));
            Assert.Equal(ChangeApprovalSource.Default, source);
        }

        [Fact]
        public void Settings_RoundTrip_AutoRemovesTheKey()
        {
            SettingsManager.SetChangeApproval(ChangeApprovalMode.Ask);
            Assert.Contains("\"ChangeApproval\":\"ask\"", File.ReadAllText(Path.Combine(_dir, "settings.json")));
            Assert.Equal(ChangeApprovalMode.Ask, SettingsManager.GetChangeApproval(out var source));
            Assert.Equal(ChangeApprovalSource.Settings, source);

            SettingsManager.SetChangeApproval(ChangeApprovalMode.Auto);
            Assert.DoesNotContain("ChangeApproval", File.ReadAllText(Path.Combine(_dir, "settings.json")));
        }

        [Fact]
        public void Environment_BeatsSettings_ManagedBeatsBoth()
        {
            SettingsManager.SetChangeApproval(ChangeApprovalMode.Ask);
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, "readonly");
            Assert.Equal(ChangeApprovalMode.ReadOnly, SettingsManager.GetChangeApproval(out var s1));
            Assert.Equal(ChangeApprovalSource.Environment, s1);

            File.WriteAllText(Path.Combine(_dir, "managed.json"), "{\"ChangeApproval\":\"auto\"}");
            ManagedSettings.Reload();
            Assert.Equal(ChangeApprovalMode.Auto, SettingsManager.GetChangeApproval(out var s2));
            Assert.Equal(ChangeApprovalSource.Managed, s2);
            Assert.True(ManagedSettings.IsManaged(ManagedSettings.ChangeApproval));
            // The user's own choice is still what Settings would show unlocked.
            Assert.Equal(ChangeApprovalMode.Ask, SettingsManager.GetChangeApprovalSetting());
        }

        [Fact]
        public void ManagedKey_IsDocumentedInTheKeyList()
        {
            Assert.Contains(ManagedSettings.ChangeApproval, ManagedSettings.Keys);
        }
    }
}
