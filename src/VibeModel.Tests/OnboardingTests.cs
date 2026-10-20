using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.Services.Diagnostics;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Settings and managed.json touch process-wide state (the settings cache, ANTHROPIC_API_KEY),
    /// so these run one at a time and restore everything they change.
    /// </summary>
    [CollectionDefinition("ProcessSettings", DisableParallelization = true)]
    public class ProcessSettingsCollection { }

    [Collection("ProcessSettings")]
    public class OnboardingSettingsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settings;
        private readonly string _envKey;

        public OnboardingSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-onboarding-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settings = Path.Combine(_dir, "settings.json");
            _envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            SettingsManager.OverridePathForTests(_settings);
            ManagedSettings.OverrideSearchPathsForTests(Path.Combine(_dir, "managed.json"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _envKey);
            SettingsManager.OverridePathForTests(null);
            ManagedSettings.OverrideSearchPathsForTests(null);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private const string Key = "sk-ant-api03-TESTKEY-abcdefghijklmnopqrstuvwxyz-1234";

        [Fact]
        public void ApiKey_IsStoredEncrypted_NeverInPlainText()
        {
            SettingsManager.SetApiKey(Key);
            var json = File.ReadAllText(_settings);
            Assert.DoesNotContain("TESTKEY", json);
            Assert.Contains("AnthropicApiKeyProtected", json);

            SettingsManager.ClearCache();
            Assert.Equal(Key, SettingsManager.GetApiKey(out var source));
            Assert.Equal(ApiKeySource.Settings, source);
        }

        [Fact]
        public void LegacyPlainTextKey_IsMigratedOnFirstRead()
        {
            File.WriteAllText(_settings, "{\"AnthropicApiKey\":\"" + Key + "\",\"Language\":\"nl\"}");
            SettingsManager.ClearCache();

            Assert.Equal(Key, SettingsManager.GetApiKey());
            var json = File.ReadAllText(_settings);
            Assert.DoesNotContain("TESTKEY", json);
            Assert.Contains("AnthropicApiKeyProtected", json);
            Assert.Contains("\"Language\":\"nl\"", json);
        }

        [Fact]
        public void ClearingTheKey_RemovesItCompletely()
        {
            SettingsManager.SetApiKey(Key);
            SettingsManager.SetApiKey("");
            Assert.Null(SettingsManager.GetApiKey(out var source));
            Assert.Equal(ApiKeySource.None, source);
            Assert.DoesNotContain("AnthropicApiKey", File.ReadAllText(_settings));
        }

        [Fact]
        public void KeySources_ManagedBeatsEnvironmentBeatsSettings()
        {
            SettingsManager.SetApiKey("sk-ant-from-settings-000000000000");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-from-environment-0000000");
            Assert.Equal("sk-ant-from-environment-0000000", SettingsManager.GetApiKey(out var s1));
            Assert.Equal(ApiKeySource.Environment, s1);

            File.WriteAllText(Path.Combine(_dir, "managed.json"),
                "{\"AnthropicApiKey\":\"sk-ant-from-it-000000000000\",\"Model\":\"claude-opus-5-5\",\"DisableClaudeCode\":true}");
            ManagedSettings.Reload();
            Assert.Equal("sk-ant-from-it-000000000000", SettingsManager.GetApiKey(out var s2));
            Assert.Equal(ApiKeySource.Managed, s2);
            Assert.Equal("claude-opus-5-5", SettingsManager.GetModel());
            Assert.True(SettingsManager.IsClaudeCodeDisabled());
            Assert.True(ManagedSettings.IsManaged(ManagedSettings.Model));
            Assert.False(ManagedSettings.IsManaged(ManagedSettings.Language));
        }

        [Fact]
        public void PreferredBackend_EmptyOrMissing_IsAuto_ManagedWins()
        {
            Assert.Equal("auto", SettingsManager.GetPreferredBackend());
            SettingsManager.SetPreferredBackend("");
            Assert.Equal("auto", SettingsManager.GetPreferredBackend());
            SettingsManager.SetPreferredBackend("claude-cli");
            Assert.Equal("claude-cli", SettingsManager.GetPreferredBackend());
            File.WriteAllText(Path.Combine(_dir, "managed.json"), "{\"PreferredBackend\":\"anthropic-api\"}");
            ManagedSettings.Reload();
            Assert.Equal("anthropic-api", SettingsManager.GetPreferredBackend());
        }

        [Fact]
        public void Model_DefaultsToCurrentSonnet_UserChoiceKept()
        {
            Assert.Equal("claude-sonnet-5-5", SettingsManager.GetModel());
            SettingsManager.SetModel("claude-opus-5-5");
            Assert.Equal("claude-opus-5-5", SettingsManager.GetModel());
            SettingsManager.SetModel("");
            Assert.Equal(SettingsManager.DefaultModel, SettingsManager.GetModel());
        }

        [Fact]
        public void ClaudeCliPath_EnvironmentNone_SwitchesDiscoveryOff()
        {
            var before = Environment.GetEnvironmentVariable("VIBEMODEL_CLAUDE_CLI");
            try
            {
                Environment.SetEnvironmentVariable("VIBEMODEL_CLAUDE_CLI", "none");
                Assert.Null(ClaudeCodeBackend.LocateCli(out _));
                Assert.Contains(ClaudeCodeBackend.LastSearch, l => l.Contains("switched off"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VIBEMODEL_CLAUDE_CLI", before);
            }
        }
    }

    public class OnboardingPureTests
    {
        [Theory]
        [InlineData(null, "https://api.anthropic.com/v1/messages")]
        [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
        [InlineData("https://gateway.example/anthropic/", "https://gateway.example/anthropic/v1/messages")]
        [InlineData("https://gateway.example/v1", "https://gateway.example/v1/messages")]
        [InlineData("https://gateway.example/v1/messages", "https://gateway.example/v1/messages")]
        public void MessagesUrl_IsCompletedNotDoubled(string baseUrl, string expected)
        {
            Assert.Equal(expected, SettingsManager.MessagesUrl(baseUrl));
        }

        [Fact]
        public void Tls_SystemDefaultIsLeftAlone_AnOlderPinnedListGetsTls12()
        {
            SecurityProtocolType? set = null;
            AnthropicHttp.EnsureTls12(SecurityProtocolType.SystemDefault, p => set = p);
            Assert.Null(set);                                   // Windows keeps choosing (TLS 1.3 too)
            AnthropicHttp.EnsureTls12(SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11, p => set = p);
            Assert.Null(set);
            AnthropicHttp.EnsureTls12(SecurityProtocolType.Tls11, p => set = p);
            Assert.Equal(SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12, set);
        }

        [Fact]
        public void ProxyForClaudeCode_HttpsOnly_LoopbackBypassed_ExplicitSettingsWin()
        {
            var proxy = new Uri("http://proxy.example:8080/");
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Func<string, string> get = n => env.TryGetValue(n, out var v) ? v : null;

            var add = ClaudeCodeBackend.ProxyVariables(get, proxy);
            Assert.Equal("http://proxy.example:8080", add["HTTPS_PROXY"]);
            Assert.Equal("127.0.0.1,localhost,::1", add["NO_PROXY"]);
            // Never HTTP_PROXY: curl would send the CLI's calls to http://127.0.0.1 through it.
            Assert.False(add.ContainsKey("HTTP_PROXY"));

            Assert.Empty(ClaudeCodeBackend.ProxyVariables(get, null));
            env["NO_PROXY"] = "corp.local";
            Assert.False(ClaudeCodeBackend.ProxyVariables(get, proxy).ContainsKey("NO_PROXY"));
            env["https_proxy"] = "http://other:3128";
            Assert.Empty(ClaudeCodeBackend.ProxyVariables(get, proxy));
        }

        [Fact]
        public void Timeout_IsSaidAsATimeout()
        {
            var ex = new AggregateException(new TaskCanceledException());
            Assert.Contains("timed out", AnthropicHttp.Innermost(ex));
            Assert.Equal("boom", AnthropicHttp.Innermost(new Exception("outer", new InvalidOperationException("boom"))));
        }

        [Fact]
        public void ModelsUrl_SitsNextToMessages()
        {
            Assert.Equal("https://api.anthropic.com/v1/models", AnthropicHttp.ModelsUrl("https://api.anthropic.com/v1/messages"));
            Assert.Equal("https://gw.example/x/v1/models", AnthropicHttp.ModelsUrl(SettingsManager.MessagesUrl("https://gw.example/x")));
        }

        [Fact]
        public void MaskKey_ShowsPrefixAndLastFour()
        {
            Assert.Equal("sk-ant-…5678", SettingsManager.MaskKey("sk-ant-api03-abcdefghij-12345678"));
            Assert.Equal("****", SettingsManager.MaskKey("abcd"));
            Assert.Equal("", SettingsManager.MaskKey(null));
        }

        [Fact]
        public void CliCandidates_LocalBinFirst_ThenPath_ThenNpm()
        {
            var list = ClaudeCliLocator.Candidates(null, @"C:\Users\u", @"C:\Users\u\AppData\Roaming",
                @"C:\Users\u\AppData\Local", new[] { @"C:\Tools", @"C:\Tools" });
            Assert.Equal(@"C:\Users\u\.local\bin\claude.exe", list[0]);
            Assert.Contains(@"C:\Tools\claude.exe", list);
            Assert.Contains(@"C:\Users\u\AppData\Roaming\npm\claude.cmd", list);
            Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void CliCandidates_ExplicitPathOrNone()
        {
            Assert.Empty(ClaudeCliLocator.Candidates("none", @"C:\Users\u", null, null, null));
            Assert.Empty(ClaudeCliLocator.Candidates(" NONE ", @"C:\Users\u", null, null, null));
            Assert.Equal(new[] { @"D:\x\claude.exe" }, ClaudeCliLocator.Candidates("\"D:\\x\\claude.exe\"", @"C:\Users\u", null, null, null));
        }

        [Fact]
        public void SplitPath_ExpandsAndDeduplicates()
        {
            var dirs = ClaudeCliLocator.SplitPath(new[] { @"%SystemRoot%\System32;C:\A;;C:\A\", null, @"C:\B" });
            Assert.DoesNotContain(dirs, d => d.Contains("%"));
            Assert.Equal(1, dirs.Count(d => d.TrimEnd('\\').Equals(@"C:\A", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(@"C:\B", dirs);
        }

        [Fact]
        public void PromptFile_TellsPowerShellToUseCurlExe()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, null);
            Assert.Contains("curl.exe", text);
            Assert.Contains(ClaudeCodeBackend.PowerShellRule, text);
        }

        [Fact]
        public void InstallCall_IsTheOfficialPerUserInstaller_InAVisibleWindow()
        {
            var psi = ClaudeSetup.InstallStartInfo("nl");
            Assert.EndsWith("powershell.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("irm https://claude.ai/install.ps1 | iex", psi.Arguments);
            Assert.Contains("-NoExit", psi.Arguments);
            Assert.True(psi.UseShellExecute);
        }

        [Theory]
        [InlineData(AnthropicHttp.KeyTestOutcome.Ok, "Werkt")]
        [InlineData(AnthropicHttp.KeyTestOutcome.InvalidKey, "niet geaccepteerd")]
        [InlineData(AnthropicHttp.KeyTestOutcome.Network, "niet bereikbaar")]
        public void KeyTest_IsExplainedInPlainWords(AnthropicHttp.KeyTestOutcome outcome, string expected)
        {
            var text = ClaudeSetup.DescribeKeyTest("nl", new AnthropicHttp.KeyTestResult { Outcome = outcome, Detail = "x" }, "claude-sonnet-5-5");
            Assert.Contains(expected, text);
        }

        [Fact]
        public void KeyTest_NetworkFailure_NamesTheHostThatWasTried()
        {
            var result = new AnthropicHttp.KeyTestResult
            {
                Outcome = AnthropicHttp.KeyTestOutcome.Network,
                Detail = "x",
                Url = "https://gateway.example/v1/models/m"
            };
            Assert.Contains("gateway.example", ClaudeSetup.DescribeKeyTest("en", result, "m"));
        }

        [Fact]
        public void ApiErrorMessage_ReadsTheApiError_NotAProxyPage()
        {
            Assert.Equal("invalid x-api-key", AnthropicHttp.ErrorMessage("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}"));
            Assert.Null(AnthropicHttp.ErrorMessage("<html>Proxy says no</html>"));
        }

        // --- usage and price --------------------------------------------------------------

        [Fact]
        public void TurnUsage_SumsCalls_TakesTheLastOutputPerCall()
        {
            var u = new TurnUsage();
            u.AddMessageStart("claude-sonnet-5-5", new Dictionary<string, object> { { "input_tokens", 1000 }, { "output_tokens", 1 } });
            u.AddMessageDelta(new Dictionary<string, object> { { "output_tokens", 50 } });
            u.AddMessageDelta(new Dictionary<string, object> { { "output_tokens", 200 } });
            u.EndMessage();
            u.AddMessageStart("claude-sonnet-5-5", new Dictionary<string, object> { { "input_tokens", 1500 }, { "cache_read_input_tokens", 500 } });
            u.AddMessageDelta(new Dictionary<string, object> { { "output_tokens", 100 } });
            u.EndMessage();

            Assert.Equal(2500, u.InputTokens);
            Assert.Equal(300, u.OutputTokens);
            Assert.Equal(500, u.CacheReadTokens);
            Assert.Equal(3300, u.TotalTokens);
        }

        [Fact]
        public void Price_SonnetFiveFive_ListPrice()
        {
            var u = new TurnUsage { Model = "claude-sonnet-5-5", InputTokens = 1000000, OutputTokens = 100000 };
            Assert.Equal(2.0 + 1.0, ModelPricing.CostUsd(u).Value, 6);
        }

        [Theory]
        [InlineData(950, "950")]
        [InlineData(12345, "12.3k")]
        [InlineData(99949, "99.9k")]
        [InlineData(99960, "100k")]
        [InlineData(999499, "999k")]
        [InlineData(999600, "1.0M")]
        [InlineData(1234567, "1.2M")]
        public void Tokens_NeverRoundIntoTheNextUnitsName(long n, string expected)
        {
            Assert.Equal(expected, ModelPricing.Tokens(n));
        }

        [Fact]
        public void Price_DatedOrPrefixedIdMatchesItsBase_UnknownHasNoEstimate()
        {
            Assert.True(ModelPricing.TryGet("claude-sonnet-5-5-20261001", out _));
            Assert.True(ModelPricing.TryGet("anthropic.claude-haiku-5-5", out _));
            Assert.False(ModelPricing.TryGet("gpt-oss-20b", out _));
            var unknown = new TurnUsage { Model = "my-local-model", InputTokens = 12345 };
            Assert.Null(ModelPricing.EstimateEuro(unknown, "nl"));
        }

        [Fact]
        public void EstimateEuro_DutchUsesDecimalComma_ReportedCostWins()
        {
            var u = new TurnUsage { Model = "whatever", InputTokens = 900, ReportedCostUsd = 0.10 };
            var nl = ModelPricing.EstimateEuro(u, "nl");
            Assert.Equal((0.10 * ModelPricing.UsdToEur).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("nl-NL")), nl);
            Assert.Contains(",", nl);
            Assert.Contains(".", ModelPricing.EstimateEuro(u, "en"));
            Assert.Equal("0.005", ModelPricing.EstimateEuro(new TurnUsage { ReportedCostUsd = 0.005 / ModelPricing.UsdToEur }, "en"));
        }

        [Fact]
        public void TurnActivity_KeepsUsage_EvenWhenItArrivesAfterTheAnswer()
        {
            var t = new TurnActivity(DateTime.UtcNow);
            t.Finish(DateTime.UtcNow);
            t.Apply(ChatActivityEvent.ForUsage(new TurnUsage { Model = "claude-sonnet-5-5", InputTokens = 10 }), DateTime.UtcNow);
            Assert.Equal(10, t.Usage.InputTokens);
            Assert.Equal(0, t.StepCount);
        }

        [Fact]
        public void CliResultEvent_GivesCostAndTokens()
        {
            var result = new Dictionary<string, object>
            {
                { "type", "result" },
                { "total_cost_usd", 0.0421 },
                { "usage", new Dictionary<string, object> { { "input_tokens", 10 }, { "output_tokens", 20 }, { "cache_read_input_tokens", 3000 } } }
            };
            var u = ClaudeCodeBackend.UsageFromResult(result, "claude-opus-5-5");
            Assert.Equal(0.0421, u.ReportedCostUsd.Value, 6);
            Assert.Equal(3030, u.TotalTokens);
            Assert.Null(ClaudeCodeBackend.UsageFromResult(new Dictionary<string, object> { { "type", "result" } }, null));
        }

        // --- thinking blocks in the API history -----------------------------------------

        [Fact]
        public void StripThinkingBlocks_KeepsTextAndToolCalls()
        {
            var history = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "user" }, { "content", "hi" } },
                new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", new object[]
                        {
                            new Dictionary<string, object> { { "type", "thinking" }, { "thinking", "" }, { "signature", "abc" } },
                            new Dictionary<string, object> { { "type", "text" }, { "text", "ok" } },
                            new Dictionary<string, object> { { "type", "redacted_thinking" }, { "data", "zzz" } },
                            new Dictionary<string, object> { { "type", "tool_use" }, { "id", "t1" }, { "name", "revit_info" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", new object[] { new Dictionary<string, object> { { "type", "thinking" }, { "signature", "s" } } } }
                }
            };

            Assert.Equal(3, AnthropicDirectBackend.StripThinkingBlocks(history));
            var first = (object[])history[1]["content"];
            Assert.Equal(new[] { "text", "tool_use" }, first.Cast<Dictionary<string, object>>().Select(b => (string)b["type"]));
            // A message left without blocks gets a placeholder (the API refuses an empty one).
            var second = (object[])history[2]["content"];
            Assert.Single(second);
            Assert.Equal("text", ((Dictionary<string, object>)second[0])["type"]);
        }

        private static Dictionary<string, object> Screenshot(string text) => new Dictionary<string, object>
        {
            { "type", "tool_result" },
            { "tool_use_id", text },
            { "content", new object[]
                {
                    new Dictionary<string, object> { { "type", "text" }, { "text", text } },
                    new Dictionary<string, object> { { "type", "image" }, { "source", "…" } }
                }
            }
        };

        [Fact]
        public void OlderScreenshots_LoseTheirImage_TheNewestKeepsIt()
        {
            var first = Screenshot("Path: a.png");
            var second = Screenshot("Path: b.png");
            var history = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "user" }, { "content", new object[] { first } } },
                new Dictionary<string, object> { { "role", "user" }, { "content", new object[] { second } } }
            };

            Assert.True(AnthropicDirectBackend.DropOldScreenshotImages(history));
            Assert.IsType<string>(first["content"]);
            Assert.StartsWith("Path: a.png", (string)first["content"]);
            Assert.IsType<object[]>(second["content"]);       // the newest stays an image
            Assert.False(AnthropicDirectBackend.DropOldScreenshotImages(history));   // nothing more to drop
        }

        [Fact]
        public void HasThinkingBlocks_OnlyAssistantThinkingCounts()
        {
            var history = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "user" }, { "content", "thinking about it" } },
                new Dictionary<string, object> { { "role", "assistant" }, { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", "x" } } } } }
            };
            Assert.False(AnthropicDirectBackend.HasThinkingBlocks(history));
            ((object[])history[1]["content"])[0] = new Dictionary<string, object> { { "type", "redacted_thinking" }, { "data", "z" } };
            Assert.True(AnthropicDirectBackend.HasThinkingBlocks(history));
        }

        [Fact]
        public void CleanAssistantContent_DropsEmptyTextKeepsEmptyThinking()
        {
            var blocks = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "type", "thinking" }, { "thinking", "" }, { "signature", "sig" } },
                new Dictionary<string, object> { { "type", "text" }, { "text", "" } },
                new Dictionary<string, object> { { "type", "tool_use" }, { "id", "t" }, { "_partial_json", "{}" } }
            };
            var clean = AnthropicDirectBackend.CleanAssistantContent(blocks).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "thinking", "tool_use" }, clean.Select(b => (string)b["type"]));
            Assert.False(clean[1].ContainsKey("_partial_json"));
        }

        // --- diagnose ------------------------------------------------------------------------

        [Fact]
        public void Diagnose_MasksSecrets()
        {
            Assert.Equal("sk-ant-…5678", DiagnoseReport.MaskIfSecret("AnthropicApiKey", "sk-ant-api03-abcdefghij-12345678"));
            Assert.StartsWith("(encrypted,", DiagnoseReport.MaskIfSecret("AnthropicApiKeyProtected", "AQAAANCMnd8BFdERjHoAwE"));
            Assert.Equal("nl", DiagnoseReport.MaskIfSecret("Language", "nl"));
            Assert.Equal("{2 entries}", DiagnoseReport.MaskIfSecret("ProjectContextFolders", new Dictionary<string, object> { { "a", "b" }, { "c", "d" } }));
            var line = DiagnoseReport.MaskSecretsInText("API error: key sk-ant-api03-SECRETSECRETSECRET-9999 refused");
            Assert.DoesNotContain("SECRET", line);
            Assert.Contains("sk-ant-…9999", line);
        }

        [Fact]
        public void Diagnose_HasStepLabelsInBothLanguages()
        {
            Assert.NotEqual("diagnose", StatusVerbs.StepLabel("diagnose", "nl"));
            Assert.NotEqual(StatusVerbs.StepLabel("diagnose", "nl"), StatusVerbs.StepLabel("diagnose", "en"));
        }

        [Fact]
        public void EveryNewText_ExistsInDutchAndEnglish()
        {
            foreach (var key in new[]
            {
                Strings.WizardTitle, Strings.WizardApiOk, Strings.WizardCliInstall, Strings.WizardCliLoginHint,
                Strings.DiagnoseSendHint, Strings.ApiRefusal, Strings.ApiUnreachable, Strings.UsageTip, Strings.SettingManaged
            })
            {
                Assert.True(Strings.HasDutch(key), key);
                Assert.NotEqual(key, Strings.For("en", key));
            }
            Assert.DoesNotContain("npm", Strings.For("nl", Strings.ClaudeNotFound));
            Assert.DoesNotContain("npm", Strings.For("en", Strings.ClaudeNotFound));
        }
    }
}
