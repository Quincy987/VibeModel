using System;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// The Claude Code CLI's own messages that are not an answer: a reached usage limit, an expired
    /// login, a slash command the headless CLI cannot run. In a colleague's export they reached the
    /// user raw and English, stored as if the model had answered ("You've hit your session limit ·
    /// resets 2pm (Europe/Amsterdam)" ×3, "Failed to authenticate: OAuth session expired and could
    /// not be refreshed" ×4, "/permissions isn't available in this environment." ×1). Matched on the
    /// whole text block (anchored), so an answer that merely mentions a limit is never caught. Pure.
    /// </summary>
    public static class CliNotice
    {
        public enum Kind { None, UsageLimit, LoginExpired, CommandUnavailable }

        // Straight or typographic apostrophe: the CLI may print either.
        private static readonly Regex Limit = new Regex(
            @"^(?:You['’]ve|You have) (?:hit|reached) your (?:session|usage|weekly|daily|5-hour|opus|monthly)?\s*limit\b(?:.*?\bresets?\s+(?<reset>[^\r\n]+?))?\s*\.?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Older CLI builds: "Claude AI usage limit reached|1764158400" (the reset as Unix seconds).
        private static readonly Regex OldLimit = new Regex(
            @"^Claude AI usage limit reached(?:\|(?<epoch>\d{9,11}))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Only the CLI's own openings: an answer that explains some other login ("your Autodesk login
        // has expired") must stay an answer.
        private static readonly Regex Login = new Regex(
            @"^(?:Failed to authenticate\b|Invalid API key\b|OAuth (?:session|token) (?:has )?expired\b|Not logged in\b|Please run /login\b|API Error: 401\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Unavailable = new Regex(
            @"^/[\w-]+ (?:isn['’]t|is not) available in this environment\.?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>What a text block (or the result event's error text) is; <paramref name="reset"/> = "2pm (Europe/Amsterdam)" when the limit says.</summary>
        public static Kind Classify(string text, out string reset)
        {
            reset = null;
            var t = (text ?? "").Trim();
            if (t.Length == 0 || t.Length > 400) return Kind.None;
            var m = Limit.Match(t);
            if (m.Success)
            {
                if (m.Groups["reset"].Success) reset = m.Groups["reset"].Value.Trim().TrimEnd('.');
                return Kind.UsageLimit;
            }
            var old = OldLimit.Match(t);
            if (old.Success)
            {
                if (old.Groups["epoch"].Success && long.TryParse(old.Groups["epoch"].Value, out var seconds))
                    reset = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                        .ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                return Kind.UsageLimit;
            }
            if (Login.IsMatch(t)) return Kind.LoginExpired;
            if (Unavailable.IsMatch(t)) return Kind.CommandUnavailable;
            return Kind.None;
        }

        /// <summary>The plain message with what to do, in the pane's language; null for <see cref="Kind.None"/>.</summary>
        public static string Message(Kind kind, string reset, string language)
        {
            switch (kind)
            {
                case Kind.UsageLimit:
                    return string.IsNullOrEmpty(reset)
                        ? Strings.For(language, Strings.CliUsageLimit)
                        : Strings.For(language, Strings.CliUsageLimitResets, reset);
                case Kind.LoginExpired:
                    return Strings.For(language, Strings.CliLoginExpired);
                case Kind.CommandUnavailable:
                    return Strings.For(language, Strings.CliCommandUnavailable);
                default:
                    return null;
            }
        }
    }
}
