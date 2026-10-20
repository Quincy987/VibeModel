using System.Collections.Generic;
using VibeModel.Services.Chat;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The line under an answer and the one-time "start a new chat" nudge: judged on the context
    /// of the turn's last API call, never on the turn's total (which counts tool steps).
    /// </summary>
    public class ConversationLengthTests
    {
        private static TurnUsage Usage(long total, long context, string model = "claude-sonnet-5-5") =>
            new TurnUsage { Model = model, CacheReadTokens = total, ContextTokens = context };

        [Theory]
        [InlineData(0, ConversationLength.Level.Short)]
        [InlineData(119999, ConversationLength.Level.Short)]
        [InlineData(120000, ConversationLength.Level.Long)]
        [InlineData(199999, ConversationLength.Level.Long)]
        [InlineData(200000, ConversationLength.Level.VeryLong)]
        public void Classify_Boundaries(long context, ConversationLength.Level expected)
        {
            Assert.Equal(expected, ConversationLength.Classify(context));
        }

        [Fact]
        public void ManyStepsInAFreshChat_IsNotALongConversation()
        {
            // Measured: a first question with 17 tool steps totals 1.4M tokens at ~100k context.
            var u = Usage(1400000, 101000);
            Assert.Equal("1.4M tokens", ConversationLength.Describe(u, "nl"));
            Assert.False(ConversationLength.ShouldNudge(u, false));
        }

        [Theory]
        [InlineData("nl", 60000, "900k tokens")]
        [InlineData("nl", 150000, "900k tokens · lang gesprek")]
        [InlineData("nl", 250000, "900k tokens · zeer lang gesprek")]
        [InlineData("en", 60000, "900k tokens")]
        [InlineData("en", 150000, "900k tokens · long conversation")]
        [InlineData("en", 250000, "900k tokens · very long conversation")]
        public void Describe_NamesTheLength_NeverMoney(string language, long context, string expected)
        {
            var text = ConversationLength.Describe(Usage(900000, context), language);
            Assert.Equal(expected, text);
            Assert.DoesNotContain("€", text);
        }

        [Fact]
        public void Describe_NullWhenNothingWasCounted()
        {
            Assert.Null(ConversationLength.Describe(null, "nl"));
            Assert.Null(ConversationLength.Describe(new TurnUsage(), "nl"));
            Assert.Null(ConversationLength.Describe(new TurnUsage { ReportedCostUsd = 0.5 }, "nl"));
        }

        [Fact]
        public void Tooltip_HasTheEstimateOnlyWhenPriced_AndTheLengthWhenKnown()
        {
            var priced = ConversationLength.Tooltip(Usage(1000000, 210000), "nl");
            Assert.Contains("€", priced);
            Assert.Contains(",", priced.Substring(priced.IndexOf('€'))); // Dutch decimal comma
            Assert.Contains("210k tokens per stap", priced);
            Assert.Contains("nieuwe chat", priced);

            var unpriced = ConversationLength.Tooltip(Usage(1000000, 0, "my-local-model"), "en");
            Assert.DoesNotContain("€", unpriced);
            Assert.DoesNotContain("per step", unpriced);
            Assert.Equal(Strings.For("en", Strings.UsageTip), unpriced);

            Assert.Contains("€", ConversationLength.Tooltip(new TurnUsage { Model = "x", InputTokens = 5, ReportedCostUsd = 0.2 }, "en"));
        }

        [Fact]
        public void Nudge_OnlyForAVeryLongConversation_OncePerChat()
        {
            Assert.True(ConversationLength.ShouldNudge(Usage(5000000, 200000), false));
            Assert.False(ConversationLength.ShouldNudge(Usage(5000000, 200000), true));
            Assert.False(ConversationLength.ShouldNudge(Usage(5000000, 199999), false));
            Assert.False(ConversationLength.ShouldNudge(null, false));
        }

        [Fact]
        public void DirectBackend_ContextIsTheLastCallsInput()
        {
            var u = new TurnUsage();
            u.AddMessageStart("claude-sonnet-5-5", new Dictionary<string, object> { { "input_tokens", 1000 }, { "cache_read_input_tokens", 50000 } });
            u.EndMessage();
            u.AddMessageStart("claude-sonnet-5-5", new Dictionary<string, object> { { "input_tokens", 2000 }, { "cache_creation_input_tokens", 3000 }, { "cache_read_input_tokens", 51000 } });
            u.EndMessage();
            Assert.Equal(56000, u.ContextTokens);
            Assert.Equal(56000, u.Snapshot().ContextTokens);
            Assert.Equal(107000, u.TotalTokens);
        }

        [Fact]
        public void CliResult_ContextComesFromTheLastAssistantEvent_NotTheSums()
        {
            var result = new Dictionary<string, object>
            {
                { "type", "result" },
                { "usage", new Dictionary<string, object> { { "input_tokens", 10 }, { "cache_read_input_tokens", 3000000 } } }
            };
            Assert.Equal(0, ClaudeCodeBackend.UsageFromResult(result, "claude-opus-5-5").ContextTokens);
            Assert.Equal(180000, ClaudeCodeBackend.UsageFromResult(result, "claude-opus-5-5", 180000).ContextTokens);
        }

        [Fact]
        public void NewTexts_ExistInDutchAndEnglish()
        {
            foreach (var key in new[]
            {
                Strings.UsageTipContext, Strings.UsageTipCost, Strings.UsageLong, Strings.UsageVeryLong,
                Strings.NudgeNewChatText, Strings.NudgeNewChatButton, Strings.NudgeNewChatDismiss, Strings.WelcomeTipNewChat
            })
            {
                Assert.True(Strings.HasDutch(key), key);
                Assert.NotEqual(key, Strings.For("en", key));
            }
        }
    }
}
