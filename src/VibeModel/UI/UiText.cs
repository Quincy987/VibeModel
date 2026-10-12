using System;
using System.Collections.Generic;
using VibeModel.Services.Chat;

namespace VibeModel.UI
{
    /// <summary>
    /// The chat pane's own chrome texts (activity block, step labels, summaries), composed from
    /// the single Dutch/English tables: <see cref="Strings"/> for the fixed texts and
    /// <see cref="StatusVerbs"/> for the step labels. The language is the Settings choice
    /// (<see cref="Strings.Current"/>); every composed form also takes it explicitly so it is
    /// testable. WPF-free.
    /// </summary>
    public static class UiText
    {
        public static string Thinking => Strings.Get(Strings.ActivityThinking);
        public static string Working => Strings.Get(Strings.ActivityWorking);
        public static string WritingAnswer => Strings.Get(Strings.ActivityWritingAnswer);
        public static string StallHint => Strings.Get(Strings.ActivityStallHint);

        public static string EarlierSteps(int count) => EarlierSteps(count, Strings.Current);

        public static string EarlierSteps(int count, string language) =>
            Strings.For(language, count == 1 ? Strings.EarlierStepOne : Strings.EarlierStepMany, count);

        /// <summary>"7 stappen · 1 mislukt · 48 s" — the collapsed one-line summary of a turn.</summary>
        public static string StepSummary(int steps, int failed, TimeSpan elapsed) =>
            StepSummary(steps, failed, elapsed, Strings.Current);

        public static string StepSummary(int steps, int failed, TimeSpan elapsed, string language)
        {
            var parts = new List<string>
            {
                Strings.For(language, steps == 1 ? Strings.StepOne : Strings.StepMany, steps)
            };
            if (failed > 0) parts.Add(Strings.For(language, Strings.StepsFailed, failed));
            parts.Add(Duration(elapsed));
            return string.Join(" · ", parts);
        }

        /// <summary>"48 s", "1 min 12 s" (the same in both languages).</summary>
        public static string Duration(TimeSpan elapsed)
        {
            var total = (int)Math.Round(Math.Max(0, elapsed.TotalSeconds));
            if (total < 60) return total + " s";
            var sec = total % 60;
            return (total / 60) + " min" + (sec > 0 ? " " + sec + " s" : "");
        }

        /// <summary>The live counter: "00:12", "01:05", "61:00".</summary>
        public static string Clock(TimeSpan elapsed)
        {
            var total = (int)Math.Max(0, elapsed.TotalSeconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        public static string MoreCodeLines(int count) => Strings.Get(Strings.MoreCodeLines, count);

        /// <summary>The label of one step row — see <see cref="StatusVerbs.StepLabel(string, string)"/>.</summary>
        public static string StepLabel(string command) => StatusVerbs.StepLabel(command);

        public static string StepLabel(string command, string language) => StatusVerbs.StepLabel(command, language);
    }
}
