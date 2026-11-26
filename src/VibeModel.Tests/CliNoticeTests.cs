using System;
using VibeModel.Services.Chat;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Phase 1, step 4: the CLI's own limit / login / command messages become one plain notice
    /// with what to do. The fixtures follow the raw texts seen in a colleague's export.
    /// </summary>
    public class CliNoticeTests
    {
        [Theory]
        [InlineData("You've hit your session limit · resets 2pm (Europe/Amsterdam)", CliNotice.Kind.UsageLimit, "2pm (Europe/Amsterdam)")]
        [InlineData("You've hit your session limit · resets 8:50pm (Europe/Amsterdam)", CliNotice.Kind.UsageLimit, "8:50pm (Europe/Amsterdam)")]
        [InlineData("You've hit your weekly limit", CliNotice.Kind.UsageLimit, null)]
        [InlineData("You’ve hit your session limit · resets 2pm (Europe/Amsterdam)", CliNotice.Kind.UsageLimit, "2pm (Europe/Amsterdam)")]
        [InlineData("Claude AI usage limit reached", CliNotice.Kind.UsageLimit, null)]
        [InlineData("/permissions isn’t available in this environment.", CliNotice.Kind.CommandUnavailable, null)]
        [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed", CliNotice.Kind.LoginExpired, null)]
        [InlineData("Invalid API key · Please run /login", CliNotice.Kind.LoginExpired, null)]
        [InlineData("API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"OAuth token has expired.\"}} · Please run /login", CliNotice.Kind.LoginExpired, null)]
        [InlineData("/permissions isn't available in this environment.", CliNotice.Kind.CommandUnavailable, null)]
        public void RawCliTexts_AreRecognised(string raw, CliNotice.Kind kind, string reset)
        {
            Assert.Equal(kind, CliNotice.Classify(raw, out var r));
            Assert.Equal(reset, r);
        }

        [Theory]
        [InlineData("De wand is 300 mm dik. You've hit your session limit is a sentence that does not start the text.")]
        [InlineData("Ik heb de limiet van 20 rijen bereikt; de rest staat in het schema.")]
        [InlineData("Je kunt /permissions gebruiken in een terminal.")]
        [InlineData("Your Autodesk login has expired, so the cloud model could not be opened; log in again in Revit.")]
        [InlineData("De link gaf: OAuth token expired. Log in BIM 360 opnieuw in.")]
        [InlineData("")]
        [InlineData(null)]
        public void AnswersThatMentionIt_AreNot(string text)
        {
            Assert.Equal(CliNotice.Kind.None, CliNotice.Classify(text, out _));
        }

        [Fact]
        public void OldCliLimit_ReadsTheResetFromUnixSeconds()
        {
            var resetAt = new DateTimeOffset(2026, 11, 26, 14, 0, 0, TimeSpan.Zero);
            Assert.Equal(CliNotice.Kind.UsageLimit,
                CliNotice.Classify("Claude AI usage limit reached|" + resetAt.ToUnixTimeSeconds(), out var reset));
            Assert.Equal(resetAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), reset);
        }

        [Fact]
        public void Messages_SayWhatToDo_InBothLanguages()
        {
            var nl = CliNotice.Message(CliNotice.Kind.UsageLimit, "2pm (Europe/Amsterdam)", "nl");
            Assert.Contains("2pm (Europe/Amsterdam)", nl);
            Assert.Contains("gebruikslimiet", nl);
            Assert.Contains("resets at 2pm", CliNotice.Message(CliNotice.Kind.UsageLimit, "2pm", "en"));
            Assert.Contains("claude", CliNotice.Message(CliNotice.Kind.LoginExpired, null, "nl"));
            Assert.Contains("/login", CliNotice.Message(CliNotice.Kind.LoginExpired, null, "en"));
            Assert.NotNull(CliNotice.Message(CliNotice.Kind.CommandUnavailable, null, "nl"));
            Assert.Null(CliNotice.Message(CliNotice.Kind.None, null, "nl"));
            Assert.DoesNotContain("You've hit", nl);
        }
    }
}
