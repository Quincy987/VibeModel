using System;
using System.Collections.Generic;
using System.Linq;
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

        // --- Tables and source markers (language null = the Settings language) -----------

        private static string Lang(string language) => language ?? Strings.Current;

        public static string MoreTableRows(int count, string language) =>
            Strings.For(Lang(language), count == 1 ? Strings.TableMoreRowOne : Strings.TableMoreRowMany, count);

        public static string CopyTable(string language) => Strings.For(Lang(language), Strings.TableCopy);
        public static string CopiedTable(string language) => Strings.For(Lang(language), Strings.TableCopied);
        public static string CopyTableTip(string language) => Strings.For(Lang(language), Strings.TableCopyTip);

        /// <summary>"Bronnen (3) ▸" collapsed, "Bronnen (3) ▾" open.</summary>
        public static string SourcesFooter(int count, bool open, string language) =>
            Strings.For(Lang(language), Strings.SourcesFooter, count) + (open ? " ▾" : " ▸");

        /// <summary>The marker's tooltip: the source, plus "Klik om de bron te openen" when it opens.</summary>
        public static string CitationTooltip(string source, bool canOpen, string language) =>
            canOpen ? source + "\n" + Strings.For(Lang(language), Strings.CitationOpenHint) : source;

        // --- Welcome card -----------------------------------------------------------------

        /// <summary>"Claude Code · claude-opus-4-1" / "Claude Code · standaardmodel".</summary>
        public static string WelcomeBackendLine(string backendLabel, string modelName, string language) =>
            backendLabel + " · " + (string.IsNullOrWhiteSpace(modelName)
                ? Strings.For(language, Strings.DefaultModel)
                : modelName.Trim());

        /// <summary>"Demogracht.rvt · Projectmap: C:\…" or "Geen model open".</summary>
        public static string WelcomeModelLine(string documentTitle, string projectFolder, string language)
        {
            if (string.IsNullOrWhiteSpace(documentTitle))
                return Strings.For(language, Strings.WelcomeNoModel);
            var folder = string.IsNullOrWhiteSpace(projectFolder)
                ? Strings.For(language, Strings.WelcomeNoFolder)
                : Strings.For(language, Strings.WelcomeFolder, projectFolder);
            return documentTitle.Trim() + " · " + folder;
        }

        /// <summary>The four example chips.</summary>
        public static string[] WelcomeExamples(string language) => Starters.Defaults(language).ToArray();

        /// <summary>The longer list behind the header's "?" button.</summary>
        public static string[] ExampleList(string language) =>
            Strings.For(language, Strings.ExamplesList).Split('\n');

        /// <summary>The label of one step row — see <see cref="StatusVerbs.StepLabel(string, string)"/>.</summary>
        public static string StepLabel(string command) => StatusVerbs.StepLabel(command);

        public static string StepLabel(string command, string language) => StatusVerbs.StepLabel(command, language);

        // --- In-app updates (plan 19) ------------------------------------------------------

        /// <summary>"12.3 MB" — sizes in the banner (one decimal, the language's decimal mark).</summary>
        public static string Megabytes(long bytes, string language)
        {
            if (bytes < 0) return "?";
            var culture = Strings.IsEnglish(language)
                ? System.Globalization.CultureInfo.InvariantCulture
                : System.Globalization.CultureInfo.GetCultureInfo("nl-NL");
            return (bytes / 1048576.0).ToString("0.0", culture) + " MB";
        }

        /// <summary>
        /// One line for the update state — the banner and Settings → Updates show it. Every state has
        /// its own wording (a test checks both languages).
        /// </summary>
        public static string UpdateLine(Services.Update.UpdateStatus s, Services.Update.InstallProgress progress, string language)
        {
            language = Lang(language);
            var latest = s?.Latest;
            switch (s?.State ?? Services.Update.UpdateState.NotChecked)
            {
                case Services.Update.UpdateState.Disabled:
                    return Strings.For(language, Strings.UpdateDisabled);
                case Services.Update.UpdateState.UpToDate:
                    return Strings.For(language, Strings.UpdateUpToDate, latest?.VersionText ?? "?");
                case Services.Update.UpdateState.Available:
                    // An install that just failed keeps the update available, with the reason.
                    return s.Reason != null
                        ? Strings.For(language, Strings.UpdateFailed, EndSentence(s.Reason))
                        : Strings.For(language, Strings.UpdateAvailable, latest?.VersionText ?? "?", Megabytes(latest?.Size ?? -1, language));
                case Services.Update.UpdateState.Installing:
                    if (progress != null && progress.Phase == "unpack") return Strings.For(language, Strings.UpdateUnpacking);
                    return Strings.For(language, Strings.UpdateDownloading,
                        Megabytes(progress?.Bytes ?? 0, language), Megabytes(progress?.Total ?? latest?.Size ?? -1, language));
                case Services.Update.UpdateState.ReadyAtRestart:
                    // A pending version older than the running one is a rollback.
                    return Services.Update.UpdateDecision.IsRollback(s.Pending, s.Running)
                        ? Strings.For(language, Strings.UpdateRolledBack, s.Pending)
                        : Strings.For(language, Strings.UpdateReady, s.Pending);
                case Services.Update.UpdateState.InPlaceBuild:
                    return Strings.For(language, Strings.UpdateDevBuild);
                case Services.Update.UpdateState.LoaderTooOld:
                    return Strings.For(language, Strings.UpdateLoaderTooOld, latest?.VersionText ?? "?");
                case Services.Update.UpdateState.Failed:
                    return Strings.For(language, Strings.UpdateCheckFailed, s.Reason ?? "?");
                default:
                    return Strings.For(language, Strings.UpdateNotChecked);
            }
        }

        private static string EndSentence(string text)
        {
            var t = (text ?? "").Trim();
            return t.Length == 0 || t.EndsWith(".", StringComparison.Ordinal) ? t : t + ".";
        }
    }
}
