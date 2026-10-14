using System;
using System.Collections.Generic;
using System.Globalization;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Token counts of one chat turn (all API calls of its tool loop together) and, when known,
    /// what it cost. Filled from the Messages API stream (message_start usage + the last
    /// message_delta usage of each call) or from the Claude Code CLI's result event.
    /// </summary>
    public sealed class TurnUsage
    {
        public string Model { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long CacheReadTokens { get; set; }

        /// <summary>Set when the source states the cost itself (the CLI's total_cost_usd).</summary>
        public double? ReportedCostUsd { get; set; }

        public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;
        public bool IsEmpty => TotalTokens == 0 && ReportedCostUsd == null;

        // Per API call: input and cache counts arrive in message_start, the output count grows in
        // message_delta (cumulative within the call) — so only the call's last value is added.
        private long _callOutput;

        public void AddMessageStart(string model, IDictionary<string, object> usage)
        {
            if (!string.IsNullOrWhiteSpace(model)) Model = model;
            _callOutput = 0;
            if (usage == null) return;
            InputTokens += Long(usage, "input_tokens");
            CacheWriteTokens += Long(usage, "cache_creation_input_tokens");
            CacheReadTokens += Long(usage, "cache_read_input_tokens");
            _callOutput = Long(usage, "output_tokens");
        }

        public void AddMessageDelta(IDictionary<string, object> usage)
        {
            if (usage == null) return;
            var output = Long(usage, "output_tokens");
            if (output > _callOutput) _callOutput = output;
        }

        /// <summary>Closes one API call: its final output count joins the turn total.</summary>
        public void EndMessage()
        {
            OutputTokens += _callOutput;
            _callOutput = 0;
        }

        public TurnUsage Snapshot() => new TurnUsage
        {
            Model = Model,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens + _callOutput,
            CacheWriteTokens = CacheWriteTokens,
            CacheReadTokens = CacheReadTokens,
            ReportedCostUsd = ReportedCostUsd
        };

        private static long Long(IDictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// List prices of the Anthropic API (USD per million tokens, first-party rates as of
    /// 2026-10-06; Microsoft Foundry bills the same) and a fixed USD→EUR rate for the estimate the
    /// chat shows under an answer. An estimate, not an invoice: the Anthropic Console has the real
    /// bill. A model not in the table shows tokens only. Update the table when prices change.
    /// </summary>
    public static class ModelPricing
    {
        /// <summary>Fixed conversion for the "≈ €" estimate. Not a live rate — adjust when it drifts.</summary>
        public const double UsdToEur = 0.92;

        public struct Price
        {
            public double Input, Output, CacheWrite, CacheRead;
            public Price(double input, double output, double cacheWrite, double cacheRead)
            {
                Input = input; Output = output; CacheWrite = cacheWrite; CacheRead = cacheRead;
            }
        }

        // Cache write = 1.25 × input (5-minute cache); cache read as published per model, else 0.1 × input.
        private static readonly Dictionary<string, Price> Table = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
        {
            { "claude-fable-5-1",  new Price(10.00, 50.00, 12.50, 0.25) },
            { "claude-fable-5",    new Price(10.00, 50.00, 12.50, 1.00) },
            { "claude-opus-5-5",   new Price(4.00, 20.00, 5.00, 0.20) },
            { "claude-opus-5",     new Price(5.00, 25.00, 6.25, 0.50) },
            { "claude-opus-4-8",   new Price(5.00, 25.00, 6.25, 0.50) },
            { "claude-opus-4-7",   new Price(5.00, 25.00, 6.25, 0.50) },
            { "claude-opus-4-6",   new Price(5.00, 25.00, 6.25, 0.50) },
            { "claude-sonnet-5-5", new Price(2.00, 10.00, 2.50, 0.20) },
            { "claude-sonnet-5",   new Price(2.00, 10.00, 2.50, 0.20) },
            { "claude-sonnet-4-6", new Price(3.00, 15.00, 3.75, 0.30) },
            // Prompts up to 100K tokens; beyond that Haiku 5.5 costs $0.50 / $2.50.
            { "claude-haiku-5-5",  new Price(0.10, 0.50, 0.125, 0.01) },
            { "claude-haiku-4-5",  new Price(1.00, 5.00, 1.25, 0.10) },
        };

        /// <summary>The price of <paramref name="model"/>; a dated or prefixed id ("anthropic.claude-…") matches its base.</summary>
        public static bool TryGet(string model, out Price price)
        {
            price = default(Price);
            if (string.IsNullOrWhiteSpace(model)) return false;
            var m = model.Trim();
            var dot = m.IndexOf("claude-", StringComparison.OrdinalIgnoreCase);
            if (dot > 0) m = m.Substring(dot);
            if (Table.TryGetValue(m, out price)) return true;
            // "claude-sonnet-5-5-20261001" → the longest table id it starts with.
            string best = null;
            foreach (var id in Table.Keys)
                if (m.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) && (best == null || id.Length > best.Length))
                    best = id;
            return best != null && Table.TryGetValue(best, out price);
        }

        /// <summary>The turn's cost in USD: the reported cost, else tokens × list price; null when unknown.</summary>
        public static double? CostUsd(TurnUsage usage)
        {
            if (usage == null) return null;
            if (usage.ReportedCostUsd.HasValue) return usage.ReportedCostUsd;
            if (!TryGet(usage.Model, out var p)) return null;
            return (usage.InputTokens * p.Input + usage.OutputTokens * p.Output +
                    usage.CacheWriteTokens * p.CacheWrite + usage.CacheReadTokens * p.CacheRead) / 1000000.0;
        }

        /// <summary>"12.3k tokens · ≈ €0,04" (nl) / "12.3k tokens · ≈ €0.04" (en); tokens only when the price is unknown.</summary>
        public static string Describe(TurnUsage usage, string language)
        {
            if (usage == null || usage.IsEmpty) return null;
            var parts = new List<string>();
            if (usage.TotalTokens > 0) parts.Add(Tokens(usage.TotalTokens) + " tokens");
            var usd = CostUsd(usage);
            if (usd.HasValue)
            {
                var culture = Strings.IsEnglish(language) ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("nl-NL");
                var eur = usd.Value * UsdToEur;
                var format = eur > 0 && eur < 0.01 ? "0.000" : "0.00";
                parts.Add("≈ €" + eur.ToString(format, culture));
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        /// <summary>"950", "12.3k", "1.2M".</summary>
        public static string Tokens(long n)
        {
            var inv = CultureInfo.InvariantCulture;
            if (n < 1000) return n.ToString(inv);
            if (n < 1000000) return (n / 1000.0).ToString(n < 100000 ? "0.0" : "0", inv) + "k";
            return (n / 1000000.0).ToString("0.0", inv) + "M";
        }
    }
}
