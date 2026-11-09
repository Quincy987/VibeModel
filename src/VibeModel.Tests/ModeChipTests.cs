using System;
using System.IO;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.UI;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 18, the pure part: the chip's session choice in the precedence (managed.json → chip →
    /// environment → settings → auto), "next message as preview" (the turn flag, the run options,
    /// the rules sentence) and the chip's texts. The WPF chip itself is verified live.
    /// </summary>
    public class ModeChipPolicyTests
    {
        [Fact]
        public void Managed_BeatsTheChip()
        {
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveOf(
                ChangeApprovalMode.Auto, ChangeApprovalSource.Managed, ChangeApprovalMode.Ask, false));
            ChangeApprovalPolicy.WithSessionOverride(ChangeApprovalMode.ReadOnly, ChangeApprovalSource.Managed,
                ChangeApprovalMode.Auto, out var source);
            Assert.Equal(ChangeApprovalSource.Managed, source);
        }

        [Theory]
        [InlineData(ChangeApprovalSource.Environment)]
        [InlineData(ChangeApprovalSource.Settings)]
        [InlineData(ChangeApprovalSource.Default)]
        public void Chip_BeatsEnvironmentSettingsAndDefault(ChangeApprovalSource configuredSource)
        {
            var mode = ChangeApprovalPolicy.WithSessionOverride(ChangeApprovalMode.ReadOnly, configuredSource,
                ChangeApprovalMode.Ask, out var source);
            Assert.Equal(ChangeApprovalMode.Ask, mode);
            Assert.Equal(ChangeApprovalSource.Session, source);
        }

        [Fact]
        public void ChipAuto_BeatsSettingsAsk()
        {
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveOf(
                ChangeApprovalMode.Ask, ChangeApprovalSource.Settings, ChangeApprovalMode.Auto, false));
        }

        [Fact]
        public void NoChip_IsTheConfiguredPolicy()
        {
            Assert.Equal(ChangeApprovalMode.Ask, ChangeApprovalPolicy.EffectiveOf(
                ChangeApprovalMode.Ask, ChangeApprovalSource.Settings, null, false));
            ChangeApprovalPolicy.WithSessionOverride(ChangeApprovalMode.Ask, ChangeApprovalSource.Settings, null, out var source);
            Assert.Equal(ChangeApprovalSource.Settings, source);
        }

        [Fact]
        public void DontAskThisSession_StillTurnsAskIntoAuto_AlsoUnderTheChip()
        {
            // The approval window's checkbox works the same whichever way Ask was chosen.
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveOf(
                ChangeApprovalMode.Ask, ChangeApprovalSource.Settings, null, true));
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveOf(
                ChangeApprovalMode.Auto, ChangeApprovalSource.Default, ChangeApprovalMode.Ask, true));
            Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.EffectiveOf(
                ChangeApprovalMode.Auto, ChangeApprovalSource.Default, ChangeApprovalMode.ReadOnly, true));
        }

        [Fact]
        public void Describe_NamesTheSession()
        {
            Assert.Equal("ask (this session)",
                ChangeApprovalPolicy.Describe(ChangeApprovalMode.Ask, ChangeApprovalSource.Session, false));
        }

        [Fact]
        public void RunOptions_ForTurn_MergesThePreview()
        {
            Assert.Same(RunOptions.None, RunOptions.ForTurn(null, false));
            var given = new RunOptions { ReadOnly = true, TimeoutSeconds = 60 };
            Assert.Same(given, RunOptions.ForTurn(given, false));

            var merged = RunOptions.ForTurn(given, true);
            Assert.True(merged.Preview);
            Assert.True(merged.ReadOnly);
            Assert.Equal(60, merged.TimeoutSeconds);
            Assert.False(merged.IsEmpty);
            Assert.False(given.Preview);                                     // the request's own options stay as sent
            Assert.True(RunOptions.ForTurn(null, true).Preview);
            Assert.False(RunOptions.None.Preview);                          // the shared instance is never changed

            var preview = new RunOptions { Preview = true };
            Assert.Same(preview, RunOptions.ForTurn(preview, true));
        }

        [Fact]
        public void Rules_CarryThePreviewSentence_OnlyInAPreviewTurn()
        {
            foreach (var mode in new[] { ChangeApprovalMode.Auto, ChangeApprovalMode.Ask, ChangeApprovalMode.ReadOnly })
            {
                var normal = ChatBackendBase.BuildBehaviourRules("nl", mode);
                Assert.DoesNotContain(ChatBackendBase.PreviewTurnRule, normal);
                Assert.Equal(normal, ChatBackendBase.BuildBehaviourRules("nl", mode, false));
                var preview = ChatBackendBase.BuildBehaviourRules("nl", mode, true);
                Assert.Contains("- " + ChatBackendBase.PreviewTurnRule, preview);
            }
            Assert.Contains("PREVIEW", ChatBackendBase.PreviewTurnRule);
            Assert.Contains("-- preview:", ChatBackendBase.PreviewTurnRule);
            Assert.Contains("do not retry", ChatBackendBase.PreviewTurnRule);
        }

        [Fact]
        public void TurnPrompt_CarriesThePreviewSentence()
        {
            var preview = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl", ChangeApprovalMode.Auto, true);
            Assert.Contains(ChatBackendBase.PreviewTurnRule, preview);
            var normal = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl");
            Assert.DoesNotContain(ChatBackendBase.PreviewTurnRule, normal);
            Assert.Equal(normal, ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl", ChangeApprovalMode.Auto, false));
        }

        [Theory]
        [InlineData(ChangeApprovalMode.Auto, "nl", "Automatisch")]
        [InlineData(ChangeApprovalMode.Ask, "nl", "Vragen")]
        [InlineData(ChangeApprovalMode.ReadOnly, "nl", "Alleen lezen")]
        [InlineData(ChangeApprovalMode.Auto, "en", "Automatic")]
        [InlineData(ChangeApprovalMode.Ask, "en", "Ask")]
        [InlineData(ChangeApprovalMode.ReadOnly, "en", "Read-only")]
        public void ModeNames_InBothLanguages(ChangeApprovalMode mode, string language, string expected)
        {
            Assert.Equal(expected, UiText.ModeName(mode, language));
            Assert.False(string.IsNullOrWhiteSpace(UiText.ModeHint(mode, language)));
        }

        [Fact]
        public void ChipTip_SaysWhenITLockedIt()
        {
            var managed = UiText.ModeChipTip(ChangeApprovalMode.Ask, ChangeApprovalSource.Managed, false, "nl");
            Assert.Contains("beheerd door IT", managed);
            Assert.Contains("ask (managed.json)", managed);
            var session = UiText.ModeChipTip(ChangeApprovalMode.Ask, ChangeApprovalSource.Session, false, "en");
            Assert.Contains("ask (this session)", session);
            Assert.DoesNotContain("managed by IT", session);
        }
    }

    /// <summary>The process-wide statics the chip and the preview turn set (one test at a time).</summary>
    [Collection("ProcessSettings")]
    public class ModeChipSessionTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _env;

        public ModeChipSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-modechip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _env = Environment.GetEnvironmentVariable(ChangeApprovalPolicy.EnvVar);
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, null);
            SettingsManager.OverridePathForTests(Path.Combine(_dir, "settings.json"));
            ManagedSettings.OverrideSearchPathsForTests(Path.Combine(_dir, "managed.json"));
            ManagedSettings.OverrideTrustCheckForTests(_ => null);
            Reset();
        }

        public void Dispose()
        {
            Reset();
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, _env);
            SettingsManager.OverridePathForTests(null);
            ManagedSettings.OverrideSearchPathsForTests(null);
            ManagedSettings.OverrideTrustCheckForTests(null);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static void Reset()
        {
            ChangeApprovalPolicy.SessionOverride = null;
            ChangeApprovalPolicy.DontAskThisSession = false;
            ChangeApprovalPolicy.ArmNextTurn(false);
            ChangeApprovalPolicy.EndTurn();
        }

        [Fact]
        public void NothingChosen_IsTheDefault()
        {
            Assert.Null(ChangeApprovalPolicy.SessionOverride);
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveMode);
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.Chosen(out var source));
            Assert.Equal(ChangeApprovalSource.Default, source);
            Assert.False(ChangeApprovalPolicy.PreviewTurn);
        }

        [Fact]
        public void ChoosingAsk_ClearsDontAskThisSession()
        {
            ChangeApprovalPolicy.DontAskThisSession = true;
            ChangeApprovalPolicy.ChooseForSession(ChangeApprovalMode.Ask);
            Assert.False(ChangeApprovalPolicy.DontAskThisSession);
            Assert.Equal(ChangeApprovalMode.Ask, ChangeApprovalPolicy.EffectiveMode);
            Assert.Equal(ChangeApprovalMode.Ask, ChangeApprovalPolicy.Chosen(out var source));
            Assert.Equal(ChangeApprovalSource.Session, source);

            // Ticking "don't ask again" in the window afterwards still works.
            ChangeApprovalPolicy.DontAskThisSession = true;
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveMode);
        }

        [Fact]
        public void ChoosingAuto_KeepsDontAsk_AndBeatsSettings()
        {
            SettingsManager.SetChangeApproval(ChangeApprovalMode.ReadOnly);
            Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.EffectiveMode);
            ChangeApprovalPolicy.DontAskThisSession = true;
            ChangeApprovalPolicy.ChooseForSession(ChangeApprovalMode.Auto);
            Assert.True(ChangeApprovalPolicy.DontAskThisSession);
            Assert.Equal(ChangeApprovalMode.Auto, ChangeApprovalPolicy.EffectiveMode);

            ChangeApprovalPolicy.SessionOverride = null;                     // Settings → untick
            Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.EffectiveMode);
        }

        [Fact]
        public void ManagedJson_BeatsTheChip()
        {
            File.WriteAllText(Path.Combine(_dir, "managed.json"), "{\"ChangeApproval\":\"readonly\"}");
            ManagedSettings.Reload();
            try
            {
                ChangeApprovalPolicy.ChooseForSession(ChangeApprovalMode.Auto);
                Assert.Equal(ChangeApprovalMode.ReadOnly, ChangeApprovalPolicy.EffectiveMode);
                ChangeApprovalPolicy.Chosen(out var source);
                Assert.Equal(ChangeApprovalSource.Managed, source);
            }
            finally
            {
                File.Delete(Path.Combine(_dir, "managed.json"));
                ManagedSettings.Reload();
            }
        }

        [Fact]
        public void PreviewTurn_IsTakenByOneTurn_AndClearedAfterIt()
        {
            ChangeApprovalPolicy.ArmNextTurn(true);
            Assert.False(ChangeApprovalPolicy.PreviewTurn);                  // armed, not yet running
            Assert.True(ChangeApprovalPolicy.BeginTurn());
            Assert.True(ChangeApprovalPolicy.PreviewTurn);
            ChangeApprovalPolicy.EndTurn();
            Assert.False(ChangeApprovalPolicy.PreviewTurn);

            Assert.False(ChangeApprovalPolicy.BeginTurn());                   // the next turn is normal again
            ChangeApprovalPolicy.EndTurn();
        }

        [Fact]
        public void Changed_FiresOncePerRealChange()
        {
            int fired = 0;
            EventHandler h = (s, e) => fired++;
            ChangeApprovalPolicy.Changed += h;
            try
            {
                ChangeApprovalPolicy.DontAskThisSession = true;               // the approval window's checkbox
                ChangeApprovalPolicy.DontAskThisSession = true;
                Assert.Equal(1, fired);
                ChangeApprovalPolicy.ChooseForSession(ChangeApprovalMode.Ask); // clears it + sets the chip
                Assert.Equal(3, fired);
                ChangeApprovalPolicy.SessionOverride = ChangeApprovalMode.Ask;
                Assert.Equal(3, fired);
                ChangeApprovalPolicy.SessionOverride = null;
                Assert.Equal(4, fired);
            }
            finally
            {
                ChangeApprovalPolicy.Changed -= h;
            }
        }

        [Fact]
        public void ArmingFalse_OverwritesAStaleArm()
        {
            // A send that never started a turn (an error before the backend ran) must not leave
            // the next turn previewed: the pane arms the flag on every send.
            ChangeApprovalPolicy.ArmNextTurn(true);
            ChangeApprovalPolicy.ArmNextTurn(false);
            Assert.False(ChangeApprovalPolicy.BeginTurn());
            ChangeApprovalPolicy.EndTurn();
        }
    }
}
