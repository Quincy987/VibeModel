using System.Globalization;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// How long the conversation has become, for the line under an answer and the one-time
    /// "start a new chat" nudge. Every tool step re-reads the whole conversation (mostly as cheap
    /// cache reads, but they count against a subscription's limit), so a long chat makes EVERY
    /// following step expensive — and modellers who rarely use AI do not start a new chat by themselves.
    ///
    /// Judged on <see cref="TurnUsage.ContextTokens"/> (what the turn's last API call carried), not
    /// on the turn's total: measured in the chat pane's Claude Code transcripts, a fresh chat's
    /// first call carries 50-65k tokens and grows about 3-20k per question (130k after six), while a
    /// single first question with 17 tool steps already totals 1.4M tokens — the total counts steps,
    /// not length.
    /// </summary>
    public static class ConversationLength
    {
        public enum Level { Short, Long, VeryLong }

        /// <summary>About twice a fresh Claude Code chat: the line gets an orange dot.</summary>
        public const long LongFrom = 120000;

        /// <summary>Over three times a fresh chat: a red dot and, once per chat, the nudge.</summary>
        public const long VeryLongFrom = 200000;

        public static Level Classify(long contextTokens) =>
            contextTokens >= VeryLongFrom ? Level.VeryLong
            : contextTokens >= LongFrom ? Level.Long
            : Level.Short;

        public static Level Classify(TurnUsage usage) => Classify(usage?.ContextTokens ?? 0);

        /// <summary>
        /// "3.9M tokens", "3.9M tokens · lang gesprek" / "· zeer lang gesprek" (en: long / very long
        /// conversation); null when nothing was counted. No money: the estimate is in <see cref="Tooltip"/>.
        /// </summary>
        public static string Describe(TurnUsage usage, string language)
        {
            if (usage == null || usage.TotalTokens <= 0) return null;
            var text = ModelPricing.Tokens(usage.TotalTokens) + " tokens";
            switch (Classify(usage))
            {
                case Level.Long: return text + " · " + Strings.For(language, Strings.UsageLong);
                case Level.VeryLong: return text + " · " + Strings.For(language, Strings.UsageVeryLong);
                default: return text;
            }
        }

        /// <summary>The usage line's tooltip: what the tokens mean, the conversation length when known, the cost estimate when priced.</summary>
        public static string Tooltip(TurnUsage usage, string language)
        {
            var text = Strings.For(language, Strings.UsageTip);
            if (usage == null) return text;
            if (usage.ContextTokens > 0)
                text += "\n" + Strings.For(language, Strings.UsageTipContext, ModelPricing.Tokens(usage.ContextTokens));
            var usd = ModelPricing.CostUsd(usage);
            if (usd.HasValue)
            {
                var culture = Strings.IsEnglish(language) ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("nl-NL");
                var eur = usd.Value * ModelPricing.UsdToEur;
                text += "\n" + Strings.For(language, Strings.UsageTipCost, eur.ToString(eur > 0 && eur < 0.01 ? "0.000" : "0.00", culture));
            }
            return text;
        }

        /// <summary>The nudge shows once per chat, after a turn that left the conversation very long.</summary>
        public static bool ShouldNudge(TurnUsage usage, bool alreadyNudgedThisChat) =>
            !alreadyNudgedThisChat && Classify(usage) == Level.VeryLong;
    }
}
