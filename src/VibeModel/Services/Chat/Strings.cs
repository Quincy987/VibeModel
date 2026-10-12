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
        // Tables in answers.
        public const string TableMoreRowOne = "TableMoreRowOne";               // {0} = 1
        public const string TableMoreRowMany = "TableMoreRowMany";             // {0} = count
        public const string TableCopy = "TableCopy";
        public const string TableCopied = "TableCopied";
        public const string TableCopyTip = "TableCopyTip";
        // Source markers ([bron: …]) and their popup.
        public const string SourcesFooter = "SourcesFooter";                   // {0} = count
        public const string CitationOpenHint = "CitationOpenHint";
        public const string SourceLoading = "SourceLoading";
        public const string SourceFailed = "SourceFailed";                     // {0} = error
        public const string SourceSelected = "SourceSelected";                 // {0} = element id
        public const string SourceMatchNotFound = "SourceMatchNotFound";       // {0} = page / section
        public const string SourceClose = "SourceClose";
        // Welcome card at the top of an empty chat.
        public const string BackendLocal = "BackendLocal";
        public const string DefaultModel = "DefaultModel";
        public const string WelcomeFolder = "WelcomeFolder";                   // {0} = folder
        public const string WelcomeNoFolder = "WelcomeNoFolder";
        public const string WelcomeNoModel = "WelcomeNoModel";
        public const string WelcomeExample1 = "WelcomeExample1";
        public const string WelcomeExample2 = "WelcomeExample2";
        public const string WelcomeExample3 = "WelcomeExample3";
        public const string WelcomeExample4 = "WelcomeExample4";
        public const string WelcomeChipTip = "WelcomeChipTip";
        public const string WelcomeSetup = "WelcomeSetup";
        public const string WelcomeSetupCli = "WelcomeSetupCli";
        public const string WelcomeSetupApi = "WelcomeSetupApi";
        public const string WelcomeSetupLocal = "WelcomeSetupLocal";
        public const string ExamplesTitle = "ExamplesTitle";
        public const string ExamplesList = "ExamplesList";                     // one example per line
        public const string ExamplesTip = "ExamplesTip";
        // Settings (plan 08, project part).
        public const string SettingProjectMemory = "SettingProjectMemory";
        public const string SettingProjectMemoryHint = "SettingProjectMemoryHint";

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
                { TableMoreRowOne, "… {0} more row" },
                { TableMoreRowMany, "… {0} more rows" },
                { TableCopy, "Copy" },
                { TableCopied, "Copied" },
                { TableCopyTip, "Copy the table (pastes into Excel as cells)" },
                { SourcesFooter, "Sources ({0})" },
                { CitationOpenHint, "Click to open the source" },
                { SourceLoading, "Loading the source…" },
                { SourceFailed, "Could not open the source: {0}" },
                { SourceSelected, "Element {0} is selected and zoomed to in Revit." },
                { SourceMatchNotFound, "{0} not found in the text - showing the start." },
                { SourceClose, "Close (Esc)" },
                { BackendLocal, "Local LLM" },
                { DefaultModel, "default model" },
                { WelcomeFolder, "Project folder: {0}" },
                { WelcomeNoFolder, "no project folder" },
                { WelcomeNoModel, "No model open" },
                { WelcomeExample1, "What is in this model?" },
                { WelcomeExample2, "Why can't I see element …?" },
                { WelcomeExample3, "Make a section across this wall" },
                { WelcomeExample4, "Fit these views onto an A0 sheet" },
                { WelcomeChipTip, "Put this question in the input box" },
                { WelcomeSetup, "No chat backend is set up yet. Pick one:" },
                { WelcomeSetupCli, "Claude Code (recommended): npm install -g @anthropic-ai/claude-code" },
                { WelcomeSetupApi, "API key: Settings → Anthropic API key" },
                { WelcomeSetupLocal, "Local model: Settings → Local LLM (llama.cpp, Ollama, LM Studio)" },
                { ExamplesTitle, "Examples" },
                { ExamplesList, "What is in this model?\nList the 5 longest walls in a table with length, height and phase\nWhy can't I see element 12345 in this view?\nMake a section across this wall\nFit these views onto an A0 sheet\nWhich phase should a new section get according to the leidraad?\nWhich documents are in the project folder?\nCheck this model against the office standard\nCreate 4 walls forming a 5 x 5 m room\nColour the selected elements red" },
                { ExamplesTip, "Every change the chat makes is one Ctrl+Z. Click an example to use it." },
                { SettingProjectMemory, "Keep project memory and journal" },
                { SettingProjectMemoryHint, "The chat remembers project decisions (memory.md) and logs every model change with the question behind it (journal.md), in the project folder's VibeModel subfolder." },
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
                { TableMoreRowOne, "… nog {0} rij" },
                { TableMoreRowMany, "… nog {0} rijen" },
                { TableCopy, "Kopiëren" },
                { TableCopied, "Gekopieerd" },
                { TableCopyTip, "Tabel kopiëren (plakt in Excel als cellen)" },
                { SourcesFooter, "Bronnen ({0})" },
                { CitationOpenHint, "Klik om de bron te openen" },
                { SourceLoading, "Bron ophalen…" },
                { SourceFailed, "De bron kon niet worden geopend: {0}" },
                { SourceSelected, "Element {0} is in Revit geselecteerd en in beeld gebracht." },
                { SourceMatchNotFound, "{0} niet gevonden in de tekst - het begin wordt getoond." },
                { SourceClose, "Sluiten (Esc)" },
                { BackendLocal, "Lokaal taalmodel" },
                { DefaultModel, "standaardmodel" },
                { WelcomeFolder, "Projectmap: {0}" },
                { WelcomeNoFolder, "geen projectmap" },
                { WelcomeNoModel, "Geen model open" },
                { WelcomeExample1, "Wat zit er in dit model?" },
                { WelcomeExample2, "Waarom zie ik element … niet?" },
                { WelcomeExample3, "Maak een doorsnede dwars op deze wand" },
                { WelcomeExample4, "Zet deze views passend op een A0" },
                { WelcomeChipTip, "Zet deze vraag in het invoerveld" },
                { WelcomeSetup, "De chat is nog niet ingesteld. Kies één:" },
                { WelcomeSetupCli, "Claude Code (aanbevolen): npm install -g @anthropic-ai/claude-code" },
                { WelcomeSetupApi, "API-sleutel: Settings → Anthropic API key" },
                { WelcomeSetupLocal, "Lokaal model: Settings → Local LLM (llama.cpp, Ollama, LM Studio)" },
                { ExamplesTitle, "Voorbeelden" },
                { ExamplesList, "Wat zit er in dit model?\nGeef een tabel van de 5 langste wanden met lengte, hoogte en fase\nWaarom zie ik element 12345 niet in deze view?\nMaak een doorsnede dwars op deze wand\nZet deze views passend op een A0\nWelke fase hoort een nieuwe doorsnede te krijgen volgens de leidraad?\nWelke documenten zitten in de projectmap?\nControleer dit model tegen de kantoorstandaard\nMaak 4 wanden die een ruimte van 5 x 5 m vormen\nKleur de geselecteerde elementen rood" },
                { ExamplesTip, "Elke wijziging van de chat is één keer Ctrl+Z. Klik op een voorbeeld om het te gebruiken." },
                { SettingProjectMemory, "Projectgeheugen en journaal bijhouden" },
                { SettingProjectMemoryHint, "De chat onthoudt projectafspraken (memory.md) en legt elke modelwijziging met de vraag erachter vast (journal.md), in de submap VibeModel van de projectmap." },
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
