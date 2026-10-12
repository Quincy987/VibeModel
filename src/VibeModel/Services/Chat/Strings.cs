using System;
using System.Collections.Generic;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// The fixed texts the chat pane shows (status and error messages), in Dutch (default) and
    /// English, keyed by the Settings → Taal / Language choice. One static dictionary per language;
    /// a key missing from Dutch falls back to English, a key missing everywhere comes back as itself.
    /// Status verbs and activity-block step labels live in <see cref="StatusVerbs"/> (composed
    /// for the pane by <see cref="UI.UiText"/>); the model-facing rules in
    /// <see cref="ChatBackendBase.BuildBehaviourRules"/>.
    /// </summary>
    public static class Strings
    {
        public const string Dutch = "nl";
        public const string English = "en";

        // Keys (stable identifiers; never shown).
        public const string BackendNotInitialized = "BackendNotInitialized";
        public const string MessageInProgress = "MessageInProgress";
        public const string ServerNotResponding = "ServerNotResponding";       // {0} = port
        public const string ClaudeNotFound = "ClaudeNotFound";
        public const string ProcessStartFailed = "ProcessStartFailed";
        public const string ProcessExitCode = "ProcessExitCode";               // {0} = exit code
        public const string CliTooOld = "CliTooOld";
        public const string StaleResume = "StaleResume";
        public const string IdleStoppedMidTool = "IdleStoppedMidTool";
        public const string IdleStopped = "IdleStopped";
        public const string ApiKeyMissing = "ApiKeyMissing";
        public const string LocalEndpointMissing = "LocalEndpointMissing";
        public const string LocalNotReachable = "LocalNotReachable";           // {0} = endpoint
        public const string LocalError = "LocalError";                         // {0} = status, {1} = body
        public const string Working = "Working";                               // {0} = command
        // Activity block (the pane's live step list; composed by UI.UiText).
        public const string ActivityThinking = "ActivityThinking";
        public const string ActivityWorking = "ActivityWorking";
        public const string ActivityWritingAnswer = "ActivityWritingAnswer";
        public const string ActivityStallHint = "ActivityStallHint";
        public const string StepOne = "StepOne";                               // {0} = 1
        public const string StepMany = "StepMany";                             // {0} = count
        public const string StepsFailed = "StepsFailed";                       // {0} = count
        public const string EarlierStepOne = "EarlierStepOne";                 // {0} = 1
        public const string EarlierStepMany = "EarlierStepMany";               // {0} = count
        public const string MoreCodeLines = "MoreCodeLines";                   // {0} = count

        private static readonly Dictionary<string, string> En =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { BackendNotInitialized, "Chat backend not initialized. Click Settings to configure your API key." },
                { MessageInProgress, "A message is already being processed. Please wait or cancel first." },
                { ServerNotResponding, "VibeModel's HTTP server isn't responding on port {0}. Restart Revit (or check whether another program is blocking the port), then try again." },
                { ClaudeNotFound, "Claude Code not found. Install with: npm install -g @anthropic-ai/claude-code" },
                { ProcessStartFailed, "Failed to start Claude Code process" },
                { ProcessExitCode, "Claude Code exited with code {0}" },
                { CliTooOld, "This Claude Code version does not support the permission mode VibeModel needs (--permission-mode dontAsk). Update it with: claude update" },
                { StaleResume, "[Resumed session was stale - starting fresh chat.]" },
                { IdleStoppedMidTool, "[Stopped: task went idle for 3 min mid-tool. Session was reset to avoid a broken transcript - please rephrase your request.]" },
                { IdleStopped, "[Stopped: task went idle for 3 min. Say \"continue\" to resume.]" },
                { ApiKeyMissing, "API key not configured. Click Settings to add your Anthropic API key." },
                { LocalEndpointMissing, "Local LLM endpoint not configured. Click Settings to set it up." },
                { LocalNotReachable, "Local LLM not reachable at {0}. Is the server running?" },
                { LocalError, "Local LLM error {0}: {1}" },
                { Working, "Working… ({0})" },
                { ActivityThinking, "Thinking…" },
                { ActivityWorking, "Working…" },
                { ActivityWritingAnswer, "Writing the answer…" },
                { ActivityStallHint, "Taking a while — Revit may be busy" },
                { StepOne, "{0} step" },
                { StepMany, "{0} steps" },
                { StepsFailed, "{0} failed" },
                { EarlierStepOne, "+{0} earlier step" },
                { EarlierStepMany, "+{0} earlier steps" },
                { MoreCodeLines, "… {0} more lines (Copy shows everything)" },
            };

        private static readonly Dictionary<string, string> Nl =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { BackendNotInitialized, "De chat is nog niet ingesteld. Klik op Settings om je API-sleutel in te vullen." },
                { MessageInProgress, "Er wordt al een bericht verwerkt. Wacht even of annuleer eerst." },
                { ServerNotResponding, "De HTTP-server van VibeModel reageert niet op poort {0}. Start Revit opnieuw (of kijk of een ander programma de poort bezet houdt) en probeer het nog eens." },
                { ClaudeNotFound, "Claude Code is niet gevonden. Installeer het met: npm install -g @anthropic-ai/claude-code" },
                { ProcessStartFailed, "Claude Code kon niet worden gestart" },
                { ProcessExitCode, "Claude Code is gestopt met code {0}" },
                { CliTooOld, "Deze versie van Claude Code kent de permissiemodus niet die VibeModel nodig heeft (--permission-mode dontAsk). Werk hem bij met: claude update" },
                { StaleResume, "[De vorige sessie was verlopen - er is een nieuwe chat gestart.]" },
                { IdleStoppedMidTool, "[Gestopt: de taak stond 3 minuten stil midden in een stap. De sessie is opnieuw gestart om een kapot gesprek te voorkomen - formuleer je vraag opnieuw.]" },
                { IdleStopped, "[Gestopt: de taak stond 3 minuten stil. Typ \"ga door\" om verder te gaan.]" },
                { ApiKeyMissing, "Er is geen API-sleutel ingesteld. Klik op Settings om je Anthropic API-sleutel toe te voegen." },
                { LocalEndpointMissing, "Er is geen adres voor het lokale taalmodel ingesteld. Klik op Settings om het in te stellen." },
                { LocalNotReachable, "Het lokale taalmodel is niet bereikbaar op {0}. Draait de server?" },
                { LocalError, "Fout van het lokale taalmodel {0}: {1}" },
                { Working, "Bezig… ({0})" },
                { ActivityThinking, "Aan het denken…" },
                { ActivityWorking, "Bezig…" },
                { ActivityWritingAnswer, "Antwoord schrijven…" },
                { ActivityStallHint, "Duurt lang — Revit is misschien bezig" },
                { StepOne, "{0} stap" },
                { StepMany, "{0} stappen" },
                { StepsFailed, "{0} mislukt" },
                { EarlierStepOne, "+{0} eerdere stap" },
                { EarlierStepMany, "+{0} eerdere stappen" },
                { MoreCodeLines, "… nog {0} regels (Kopiëren geeft alles)" },
            };

        /// <summary>"en" for anything that names English; "nl" for everything else (the default).</summary>
        public static string Normalize(string language)
        {
            var l = (language ?? "").Trim();
            return l.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Dutch;
        }

        /// <summary>The language chosen in Settings (normalized).</summary>
        public static string Current => SettingsManager.GetLanguage();

        public static bool IsEnglish(string language) => Normalize(language) == English;

        /// <summary>"Dutch" / "English" — how the rules block names the fallback language to the model.</summary>
        public static string LanguageName(string language) => IsEnglish(language) ? "English" : "Dutch";

        /// <summary>The text for <paramref name="key"/> in the Settings language.</summary>
        public static string Get(string key, params object[] args) => For(Current, key, args);

        /// <summary>The text for <paramref name="key"/> in <paramref name="language"/>.</summary>
        public static string For(string language, string key, params object[] args)
        {
            string text;
            if (!(IsEnglish(language) ? En : Nl).TryGetValue(key ?? "", out text) &&
                !En.TryGetValue(key ?? "", out text))
                text = key ?? "";
            return args == null || args.Length == 0 ? text : string.Format(text, args);
        }

        /// <summary>Every key, for tests that check both languages are complete.</summary>
        internal static IEnumerable<string> Keys => En.Keys;

        internal static bool HasDutch(string key) => Nl.ContainsKey(key);
    }
}
