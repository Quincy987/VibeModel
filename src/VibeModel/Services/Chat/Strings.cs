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
        public const string ExamplesTitle = "ExamplesTitle";
        public const string ExamplesList = "ExamplesList";                     // one example per line
        public const string ExamplesTip = "ExamplesTip";
        // Starter chips (empty chat; Starters.Pick) and quick replies (after an answer).
        public const string StarterSelection = "StarterSelection";
        public const string StarterSheet = "StarterSheet";
        public const string StarterCheck = "StarterCheck";
        public const string StarterDocs = "StarterDocs";
        public const string QuickReplyReadyHint = "QuickReplyReadyHint";
        public const string QuickReplyFillsTip = "QuickReplyFillsTip";       // {0} = prompt
        public const string QuickReplySendsTip = "QuickReplySendsTip";       // {0} = prompt
        // Settings (plan 08, project part).
        public const string SettingProjectMemory = "SettingProjectMemory";
        public const string SettingProjectMemoryHint = "SettingProjectMemoryHint";
        public const string SettingManaged = "SettingManaged";
        // Direct API backend.
        public const string ApiRefusal = "ApiRefusal";
        public const string ApiMaxTokens = "ApiMaxTokens";
        public const string ApiUnreachable = "ApiUnreachable";                 // {0} = url, {1} = reason
        public const string ClaudeDisabledByIt = "ClaudeDisabledByIt";
        public const string UsageTip = "UsageTip";
        public const string UsageTipContext = "UsageTipContext";               // {0} = tokens, e.g. "210k"
        public const string UsageTipCost = "UsageTipCost";                     // {0} = euro amount
        public const string UsageLong = "UsageLong";
        public const string UsageVeryLong = "UsageVeryLong";
        // The one-time "start a new chat" bar under a very long conversation (ConversationLength).
        public const string NudgeNewChatText = "NudgeNewChatText";
        public const string NudgeNewChatButton = "NudgeNewChatButton";
        public const string NudgeNewChatDismiss = "NudgeNewChatDismiss";
        public const string WelcomeTipNewChat = "WelcomeTipNewChat";
        public const string StatusNotConnected = "StatusNotConnected";
        // Connect wizard (the not-configured welcome card).
        public const string WizardTitle = "WizardTitle";
        public const string WizardIntro = "WizardIntro";
        public const string WizardApiHeading = "WizardApiHeading";
        public const string WizardApiHint = "WizardApiHint";
        public const string WizardApiTest = "WizardApiTest";
        public const string WizardApiSave = "WizardApiSave";
        public const string WizardApiEmpty = "WizardApiEmpty";
        public const string WizardApiTesting = "WizardApiTesting";
        public const string WizardApiOk = "WizardApiOk";                       // {0} = model
        public const string WizardApiInvalid = "WizardApiInvalid";
        public const string WizardApiNoAccess = "WizardApiNoAccess";
        public const string WizardApiModelUnknown = "WizardApiModelUnknown";   // {0} = model
        public const string WizardApiNetwork = "WizardApiNetwork";             // {0} = host, {1} = reason
        public const string WizardApiOther = "WizardApiOther";                 // {0} = status, {1} = detail
        public const string WizardCliHeading = "WizardCliHeading";
        public const string WizardCliChecking = "WizardCliChecking";
        public const string WizardCliFound = "WizardCliFound";                 // {0} = version
        public const string WizardCliNotFound = "WizardCliNotFound";
        public const string WizardCliInstall = "WizardCliInstall";
        public const string WizardCliInstallStarted = "WizardCliInstallStarted";
        public const string WizardCliLogin = "WizardCliLogin";
        public const string WizardCliLoginHint = "WizardCliLoginHint";
        public const string WizardCliUse = "WizardCliUse";
        public const string WizardRecheck = "WizardRecheck";
        public const string WizardLocal = "WizardLocal";
        public const string WizardFailedToStart = "WizardFailedToStart";       // {0} = reason
        // Change approval (plan 15): the approval window and the Settings section.
        public const string ApprovalTitle = "ApprovalTitle";
        public const string ApprovalAdded = "ApprovalAdded";
        public const string ApprovalModified = "ApprovalModified";
        public const string ApprovalDeleted = "ApprovalDeleted";
        public const string ApprovalOther = "ApprovalOther";
        public const string ApprovalModifiedNote = "ApprovalModifiedNote";
        public const string ApprovalCommandFailed = "ApprovalCommandFailed";
        public const string ApprovalIsolated = "ApprovalIsolated";             // {0} = count
        public const string ApprovalNotVisible = "ApprovalNotVisible";
        public const string ApprovalOnlyDeleted = "ApprovalOnlyDeleted";
        public const string ApprovalNothingToShow = "ApprovalNothingToShow";
        public const string ApprovalZoom = "ApprovalZoom";
        public const string ApprovalDontAsk = "ApprovalDontAsk";
        public const string ApprovalDiscard = "ApprovalDiscard";
        public const string ApprovalKeep = "ApprovalKeep";
        public const string SettingApprovalTitle = "SettingApprovalTitle";
        public const string SettingApprovalAuto = "SettingApprovalAuto";
        public const string SettingApprovalAutoHint = "SettingApprovalAutoHint";
        public const string SettingApprovalAsk = "SettingApprovalAsk";
        public const string SettingApprovalAskHint = "SettingApprovalAskHint";
        public const string SettingApprovalReadOnly = "SettingApprovalReadOnly";
        public const string SettingApprovalReadOnlyHint = "SettingApprovalReadOnlyHint";
        public const string SettingApprovalSession = "SettingApprovalSession";
        public const string SettingApprovalEnv = "SettingApprovalEnv";          // {0} = mode, {1} = variable
        // Diagnose (header button, `diagnose` command).
        public const string DiagnoseTitle = "DiagnoseTitle";
        public const string DiagnoseTip = "DiagnoseTip";
        public const string DiagnoseRunning = "DiagnoseRunning";
        public const string DiagnoseCopy = "DiagnoseCopy";
        public const string DiagnoseCopied = "DiagnoseCopied";
        public const string DiagnoseSave = "DiagnoseSave";
        public const string DiagnoseSaved = "DiagnoseSaved";                   // {0} = path
        public const string DiagnoseClose = "DiagnoseClose";
        public const string DiagnoseSendHint = "DiagnoseSendHint";

        // In-app updates (plan 19): the chat pane's banner and Settings → Updates.
        public const string UpdateAvailable = "UpdateAvailable";               // {0} = version, {1} = size
        public const string UpdateInstall = "UpdateInstall";
        public const string UpdateInstallTip = "UpdateInstallTip";
        public const string UpdateNotes = "UpdateNotes";
        public const string UpdateLater = "UpdateLater";
        public const string UpdateDownloading = "UpdateDownloading";           // {0} = done, {1} = total
        public const string UpdateUnpacking = "UpdateUnpacking";
        public const string UpdateReady = "UpdateReady";                       // {0} = version
        public const string UpdateRolledBack = "UpdateRolledBack";             // {0} = version
        public const string UpdateFailed = "UpdateFailed";                     // {0} = reason
        public const string UpdateRetry = "UpdateRetry";
        public const string UpdateLoaderTooOld = "UpdateLoaderTooOld";         // {0} = version
        public const string UpdateDownloadZip = "UpdateDownloadZip";
        public const string UpdateUpToDate = "UpdateUpToDate";                 // {0} = latest version
        public const string UpdateNotChecked = "UpdateNotChecked";
        public const string UpdateCheckFailed = "UpdateCheckFailed";           // {0} = reason
        public const string UpdateDisabled = "UpdateDisabled";
        public const string UpdateDevBuild = "UpdateDevBuild";
        public const string UpdateChecking = "UpdateChecking";
        public const string SettingsUpdatesTitle = "SettingsUpdatesTitle";
        public const string SettingsUpdateInstalled = "SettingsUpdateInstalled"; // {0} = version, {1} = loader
        public const string SettingsUpdateRunsFrom = "SettingsUpdateRunsFrom";   // {0} = folder
        public const string SettingsUpdateSource = "SettingsUpdateSource";       // {0} = source
        public const string SettingsUpdateCheck = "SettingsUpdateCheck";
        public const string SettingsUpdateCheckNow = "SettingsUpdateCheckNow";
        public const string SettingsUpdateRollback = "SettingsUpdateRollback";   // {0} = version
        public const string SettingsUpdatePending = "SettingsUpdatePending";     // {0} = version

        private static readonly Dictionary<string, string> En =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { BackendNotInitialized, "Chat backend not initialized. Click Settings to configure your API key." },
                { MessageInProgress, "A message is already being processed. Please wait or cancel first." },
                { ServerNotResponding, "VibeModel's HTTP server isn't responding on port {0}. Restart Revit (or check whether another program is blocking the port), then try again." },
                { ClaudeNotFound, "Claude Code is not installed on this computer. Use \"Connect to Claude\" in the chat, or run in PowerShell: irm https://claude.ai/install.ps1 | iex" },
                { ProcessStartFailed, "Failed to start Claude Code process" },
                { ProcessExitCode, "Claude Code exited with code {0}" },
                { CliTooOld, "This Claude Code version does not support the permission mode VibeModel needs (--permission-mode dontAsk). Update it with: claude update" },
                { StaleResume, "[Resumed session was stale - starting fresh chat.]" },
                { IdleStoppedMidTool, "[Stopped: task went idle for 3 min mid-tool. Session was reset to avoid a broken transcript - please rephrase your request.]" },
                { IdleStopped, "[Stopped: task went idle for 3 min. Say \"continue\" to resume.]" },
                { ApiKeyMissing, "Not connected to Claude yet. Use the Connect to Claude card in the chat (New Chat shows it), or Settings → Anthropic API." },
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
                { ExamplesTitle, "Examples" },
                { ExamplesList, "What is in this model?\nList the 5 longest walls in a table with length, height and phase\nWhy can't I see element 12345 in this view?\nMake a section across this wall\nFit these views onto an A0 sheet\nWhich phase should a new section get according to the leidraad?\nWhich documents are in the project folder?\nCheck this model against the office standard\nCreate 4 walls forming a 5 x 5 m room\nColour the selected elements red" },
                { ExamplesTip, "Every change the chat makes is one Ctrl+Z. Click an example to use it." },
                { StarterSelection, "What have I selected?" },
                { StarterSheet, "Fit these views onto this sheet" },
                { StarterCheck, "Check this model against the office standard" },
                { StarterDocs, "Which documents are in the project folder?" },
                { QuickReplyReadyHint, "Ready to send. Check it, then press Send." },
                { QuickReplyFillsTip, "{0}\n(fills the input box - this changes the model, so you press Send)" },
                { QuickReplySendsTip, "{0}\n(sends right away)" },
                { SettingProjectMemory, "Keep project memory and journal" },
                { SettingProjectMemoryHint, "The chat remembers project decisions (memory.md) and logs every model change with the question behind it (journal.md), in the project folder's VibeModel subfolder." },
                { SettingManaged, "managed by IT" },
                { ApiRefusal, "Claude declined to answer this request. Rephrase it, or split it into smaller steps." },
                { ApiMaxTokens, "[The answer was cut off at the length limit - ask \"continue\" for the rest.]" },
                { ApiUnreachable, "Cannot reach {0}: {1}. Check the network or proxy, or press Diagnose and send the report to your administrator." },
                { ClaudeDisabledByIt, "Claude Code is switched off on this computer by IT." },
                { StatusNotConnected, "Not connected" },
                { UsageTip, "Tokens this answer used. At every step Claude re-reads the whole conversation, so the longer the chat, the more each question costs: start a new chat for each task." },
                { UsageTipContext, "Conversation length now: {0} tokens per step." },
                { UsageTipCost, "At Anthropic's API list price: about €{0}. With a Claude subscription you do not pay this per question; it counts towards your usage limit." },
                { UsageLong, "long conversation" },
                { UsageVeryLong, "very long conversation" },
                { NudgeNewChatText, "This conversation has grown long: Claude re-reads all of it at every step, so each question now costs a lot more. Start a new chat for your next task." },
                { NudgeNewChatButton, "New chat" },
                { NudgeNewChatDismiss, "Later" },
                { WelcomeTipNewChat, "Tip: start a new chat for each task - it is faster and saves your limit." },
                { WizardTitle, "Connect to Claude" },
                { WizardIntro, "The chat needs a connection to Claude. Pick one way - you only do this once." },
                { WizardApiHeading, "1. API key (recommended at the office)" },
                { WizardApiHint, "Paste the key you got from your administrator (starts with sk-ant-). It is stored encrypted, for your Windows account only." },
                { WizardApiTest, "Test" },
                { WizardApiSave, "Save and start" },
                { WizardApiEmpty, "Paste a key first." },
                { WizardApiTesting, "Testing the key…" },
                { WizardApiOk, "Works - model {0} is available." },
                { WizardApiInvalid, "This key is not accepted (wrong, expired or revoked). Check it with your administrator." },
                { WizardApiNoAccess, "The key is valid but has no access to the API. Ask your administrator." },
                { WizardApiModelUnknown, "The key works, but model {0} is not available to it. Pick another model in Settings or ask your administrator." },
                { WizardApiNetwork, "Cannot reach {0}: {1}. The network or the office proxy blocks it - press Diagnose and send the report to IT." },
                { WizardApiOther, "Unexpected answer (HTTP {0}): {1}" },
                { WizardCliHeading, "2. Claude Code (your own Claude subscription)" },
                { WizardCliChecking, "Looking for Claude Code…" },
                { WizardCliFound, "Found: Claude Code {0}" },
                { WizardCliNotFound, "Not installed on this computer. Installing needs no administrator rights." },
                { WizardCliInstall, "Install" },
                { WizardCliInstallStarted, "A PowerShell window installs Claude Code. When it says it is done, press Check again." },
                { WizardCliLogin, "Log in" },
                { WizardCliLoginHint, "Log in opens a window: follow the steps (a browser opens for your Claude account), then close it and press Use Claude Code." },
                { WizardCliUse, "Use Claude Code" },
                { WizardRecheck, "Check again" },
                { WizardLocal, "Local model (llama.cpp, Ollama, LM Studio): Settings → Local LLM" },
                { WizardFailedToStart, "Could not start it: {0}" },
                { ApprovalTitle, "VibeModel wants to keep this change" },
                { ApprovalAdded, "Added" },
                { ApprovalModified, "Modified" },
                { ApprovalDeleted, "Deleted" },
                { ApprovalOther, "other" },
                { ApprovalModifiedNote, "Revit also counts elements it only updated (joins, dimensions, the view)." },
                { ApprovalCommandFailed, "Note: the command reported an error; this is what it changed anyway." },
                { ApprovalIsolated, "Isolated and selected in this view: {0}" },
                { ApprovalNotVisible, "Not visible in this view - Zoom shows them." },
                { ApprovalOnlyDeleted, "Only deletions: there is nothing to show." },
                { ApprovalNothingToShow, "Nothing to show in a view (only views or types changed)." },
                { ApprovalZoom, "Zoom" },
                { ApprovalDontAsk, "Don't ask again this Revit session" },
                { ApprovalDiscard, "Discard" },
                { ApprovalKeep, "Keep" },
                { SettingApprovalTitle, "Changes by the AI" },
                { SettingApprovalAuto, "Never ask - every change is one Ctrl+Z (default)" },
                { SettingApprovalAutoHint, "As before: the AI carries out changes directly." },
                { SettingApprovalAsk, "Ask before a deletion or edit is kept" },
                { SettingApprovalAskHint, "delete, parameter values, phases, joins and exec first show what changes; new elements never ask." },
                { SettingApprovalReadOnly, "Read-only - the AI may not change anything" },
                { SettingApprovalReadOnlyHint, "For viewers: every change is rolled back and refused; reading still works." },
                { SettingApprovalSession, "Not asking for the rest of this Revit session (ticked in the approval window) - untick to ask again" },
                { SettingApprovalEnv, "In force now: '{0}' from the environment variable {1}, which overrides the choice above." },
                { DiagnoseTitle, "VibeModel diagnosis" },
                { DiagnoseTip, "What VibeModel sees of this computer: versions, connection, proxy, recent errors. Secrets are masked." },
                { DiagnoseRunning, "Checking (network checks take up to 5 s each)…" },
                { DiagnoseCopy, "Copy" },
                { DiagnoseCopied, "Copied" },
                { DiagnoseSave, "Save" },
                { DiagnoseSaved, "Saved: {0}" },
                { DiagnoseClose, "Close" },
                { DiagnoseSendHint, "Something not working? Copy this report and send it to your VibeModel administrator." },
                { UpdateAvailable, "VibeModel {0} is available ({1})" },
                { UpdateInstall, "Update" },
                { UpdateInstallTip, "Downloads the new version next to the current one. It becomes active when you restart Revit; nothing changes in the model." },
                { UpdateNotes, "What's new" },
                { UpdateLater, "Later" },
                { UpdateDownloading, "Downloading… {0} / {1}" },
                { UpdateUnpacking, "Unpacking…" },
                { UpdateReady, "VibeModel {0} is ready - active after you restart Revit." },
                { UpdateRolledBack, "Revit starts VibeModel {0} next time." },
                { UpdateFailed, "Update failed: {0} Click Diagnose for details." },
                { UpdateRetry, "Try again" },
                { UpdateLoaderTooOld, "VibeModel {0} is available - it needs a one-time install from the zip (install.cmd)." },
                { UpdateDownloadZip, "Download" },
                { UpdateUpToDate, "Up to date ({0} is the latest version)." },
                { UpdateNotChecked, "Not checked yet." },
                { UpdateCheckFailed, "Check failed: {0}" },
                { UpdateDisabled, "Updates are switched off by IT." },
                { UpdateDevBuild, "A developer build in the Revit add-ins folder - updates do not apply." },
                { UpdateChecking, "Checking…" },
                { SettingsUpdatesTitle, "Updates" },
                { SettingsUpdateInstalled, "Installed: VibeModel {0} · loader {1}" },
                { SettingsUpdateRunsFrom, "Runs from: {0}" },
                { SettingsUpdateSource, "Update source: {0}" },
                { SettingsUpdateCheck, "Check for updates automatically (once a day, when Revit starts)" },
                { SettingsUpdateCheckNow, "Check now" },
                { SettingsUpdateRollback, "Back to {0}" },
                { SettingsUpdatePending, "Next Revit start: VibeModel {0}" },
            };

        private static readonly Dictionary<string, string> Nl =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { BackendNotInitialized, "De chat is nog niet ingesteld. Klik op Settings om je API-sleutel in te vullen." },
                { MessageInProgress, "Er wordt al een bericht verwerkt. Wacht even of annuleer eerst." },
                { ServerNotResponding, "De HTTP-server van VibeModel reageert niet op poort {0}. Start Revit opnieuw (of kijk of een ander programma de poort bezet houdt) en probeer het nog eens." },
                { ClaudeNotFound, "Claude Code staat niet op deze computer. Gebruik \"Verbind met Claude\" in de chat, of voer in PowerShell uit: irm https://claude.ai/install.ps1 | iex" },
                { ProcessStartFailed, "Claude Code kon niet worden gestart" },
                { ProcessExitCode, "Claude Code is gestopt met code {0}" },
                { CliTooOld, "Deze versie van Claude Code kent de permissiemodus niet die VibeModel nodig heeft (--permission-mode dontAsk). Werk hem bij met: claude update" },
                { StaleResume, "[De vorige sessie was verlopen - er is een nieuwe chat gestart.]" },
                { IdleStoppedMidTool, "[Gestopt: de taak stond 3 minuten stil midden in een stap. De sessie is opnieuw gestart om een kapot gesprek te voorkomen - formuleer je vraag opnieuw.]" },
                { IdleStopped, "[Gestopt: de taak stond 3 minuten stil. Typ \"ga door\" om verder te gaan.]" },
                { ApiKeyMissing, "Nog niet verbonden met Claude. Gebruik de kaart Verbind met Claude in de chat (New Chat toont hem), of Settings → Anthropic API." },
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
                { ExamplesTitle, "Voorbeelden" },
                { ExamplesList, "Wat zit er in dit model?\nGeef een tabel van de 5 langste wanden met lengte, hoogte en fase\nWaarom zie ik element 12345 niet in deze view?\nMaak een doorsnede dwars op deze wand\nZet deze views passend op een A0\nWelke fase hoort een nieuwe doorsnede te krijgen volgens de leidraad?\nWelke documenten zitten in de projectmap?\nControleer dit model tegen de kantoorstandaard\nMaak 4 wanden die een ruimte van 5 x 5 m vormen\nKleur de geselecteerde elementen rood" },
                { ExamplesTip, "Elke wijziging van de chat is één keer Ctrl+Z. Klik op een voorbeeld om het te gebruiken." },
                { StarterSelection, "Wat heb ik geselecteerd?" },
                { StarterSheet, "Zet deze views passend op dit blad" },
                { StarterCheck, "Controleer dit model tegen de kantoorstandaard" },
                { StarterDocs, "Welke documenten zitten in de projectmap?" },
                { QuickReplyReadyHint, "Klaar om te versturen. Controleer en druk op Send." },
                { QuickReplyFillsTip, "{0}\n(vult het invoerveld - dit wijzigt het model, dus jij drukt op Send)" },
                { QuickReplySendsTip, "{0}\n(wordt meteen verstuurd)" },
                { SettingProjectMemory, "Projectgeheugen en journaal bijhouden" },
                { SettingProjectMemoryHint, "De chat onthoudt projectafspraken (memory.md) en legt elke modelwijziging met de vraag erachter vast (journal.md), in de submap VibeModel van de projectmap." },
                { SettingManaged, "beheerd door IT" },
                { ApiRefusal, "Claude wil op deze vraag geen antwoord geven. Formuleer hem anders, of splits hem op in kleinere stappen." },
                { ApiMaxTokens, "[Het antwoord is afgebroken op de maximale lengte - vraag \"ga door\" voor de rest.]" },
                { ApiUnreachable, "{0} is niet bereikbaar: {1}. Controleer het netwerk of de proxy, of klik op Diagnose en stuur het rapport naar je beheerder." },
                { ClaudeDisabledByIt, "Claude Code is op deze computer uitgeschakeld door IT." },
                { StatusNotConnected, "Niet verbonden" },
                { UsageTip, "Tokens die dit antwoord gebruikte. Claude leest bij elke stap het hele gesprek opnieuw, dus hoe langer de chat, hoe meer elke vraag kost: begin per taak een nieuwe chat." },
                { UsageTipContext, "Lengte van het gesprek nu: {0} tokens per stap." },
                { UsageTipCost, "Tegen de API-prijslijst van Anthropic: ongeveer €{0}. Met een Claude-abonnement betaal je dit niet per vraag; het telt mee voor je gebruikslimiet." },
                { UsageLong, "lang gesprek" },
                { UsageVeryLong, "zeer lang gesprek" },
                { NudgeNewChatText, "Dit gesprek is lang geworden: Claude leest het bij elke stap helemaal opnieuw, dus elke vraag kost nu veel meer. Begin voor je volgende taak een nieuwe chat." },
                { NudgeNewChatButton, "Nieuwe chat" },
                { NudgeNewChatDismiss, "Niet nu" },
                { WelcomeTipNewChat, "Tip: begin per taak een nieuwe chat - dat is sneller en spaart je limiet." },
                { WizardTitle, "Verbind met Claude" },
                { WizardIntro, "De chat heeft een verbinding met Claude nodig. Kies één manier - dit doe je maar één keer." },
                { WizardApiHeading, "1. API-sleutel (aanbevolen op kantoor)" },
                { WizardApiHint, "Plak de sleutel die je van je beheerder kreeg (begint met sk-ant-). Hij wordt versleuteld bewaard, alleen voor jouw Windows-account." },
                { WizardApiTest, "Testen" },
                { WizardApiSave, "Opslaan en starten" },
                { WizardApiEmpty, "Plak eerst een sleutel." },
                { WizardApiTesting, "Sleutel testen…" },
                { WizardApiOk, "Werkt - model {0} is beschikbaar." },
                { WizardApiInvalid, "Deze sleutel wordt niet geaccepteerd (onjuist, verlopen of ingetrokken). Controleer hem bij je beheerder." },
                { WizardApiNoAccess, "De sleutel is geldig maar heeft geen toegang tot de API. Vraag het je beheerder." },
                { WizardApiModelUnknown, "De sleutel werkt, maar model {0} is er niet voor beschikbaar. Kies een ander model in Settings of vraag het je beheerder." },
                { WizardApiNetwork, "{0} is niet bereikbaar: {1}. Het netwerk of de kantoorproxy houdt het tegen - klik op Diagnose en stuur het rapport naar IT." },
                { WizardApiOther, "Onverwacht antwoord (HTTP {0}): {1}" },
                { WizardCliHeading, "2. Claude Code (je eigen Claude-abonnement)" },
                { WizardCliChecking, "Claude Code zoeken…" },
                { WizardCliFound, "Gevonden: Claude Code {0}" },
                { WizardCliNotFound, "Niet geïnstalleerd op deze computer. Installeren kan zonder beheerdersrechten." },
                { WizardCliInstall, "Installeren" },
                { WizardCliInstallStarted, "Een PowerShell-venster installeert Claude Code. Klik op Opnieuw controleren als het klaar is." },
                { WizardCliLogin, "Inloggen" },
                { WizardCliLoginHint, "Inloggen opent een venster: volg de stappen (er opent een browser voor je Claude-account), sluit het daarna en klik op Claude Code gebruiken." },
                { WizardCliUse, "Claude Code gebruiken" },
                { WizardRecheck, "Opnieuw controleren" },
                { WizardLocal, "Lokaal taalmodel (llama.cpp, Ollama, LM Studio): Settings → Local LLM" },
                { WizardFailedToStart, "Kon het niet starten: {0}" },
                { ApprovalTitle, "VibeModel wil deze wijziging behouden" },
                { ApprovalAdded, "Toegevoegd" },
                { ApprovalModified, "Gewijzigd" },
                { ApprovalDeleted, "Verwijderd" },
                { ApprovalOther, "overig" },
                { ApprovalModifiedNote, "Revit telt ook elementen mee die het alleen heeft bijgewerkt (aansluitingen, maatvoering, de view)." },
                { ApprovalCommandFailed, "Let op: de opdracht meldde een fout; dit is wat er toch al gewijzigd is." },
                { ApprovalIsolated, "Geïsoleerd en geselecteerd in deze view: {0}" },
                { ApprovalNotVisible, "Niet zichtbaar in deze view - Inzoomen toont ze." },
                { ApprovalOnlyDeleted, "Alleen verwijderingen: er is niets te tonen." },
                { ApprovalNothingToShow, "Niets te tonen in een view (alleen views of types gewijzigd)." },
                { ApprovalZoom, "Inzoomen" },
                { ApprovalDontAsk, "Deze Revit-sessie niet meer vragen" },
                { ApprovalDiscard, "Ongedaan maken" },
                { ApprovalKeep, "Behouden" },
                { SettingApprovalTitle, "Wijzigingen door de AI" },
                { SettingApprovalAuto, "Nooit vragen - elke wijziging is één Ctrl+Z (standaard)" },
                { SettingApprovalAutoHint, "Zoals altijd: de AI voert wijzigingen direct uit." },
                { SettingApprovalAsk, "Vragen voordat een verwijdering of bewerking blijft staan" },
                { SettingApprovalAskHint, "Verwijderen, parameterwaarden, fases, joins en exec tonen eerst wat er verandert; nieuwe elementen vragen nooit." },
                { SettingApprovalReadOnly, "Alleen lezen - de AI mag niets wijzigen" },
                { SettingApprovalReadOnlyHint, "Voor meekijkers: elke wijziging wordt teruggedraaid en geweigerd; lezen kan wel." },
                { SettingApprovalSession, "Deze Revit-sessie wordt niet meer gevraagd (aangevinkt in het venster) - vink uit om weer te vragen" },
                { SettingApprovalEnv, "Nu in werking: '{0}' uit de omgevingsvariabele {1}, die de keuze hierboven overschrijft." },
                { DiagnoseTitle, "VibeModel-diagnose" },
                { DiagnoseTip, "Wat VibeModel van deze computer ziet: versies, verbinding, proxy, recente fouten. Geheimen zijn afgeschermd." },
                { DiagnoseRunning, "Bezig met controleren (netwerkcontroles duren elk tot 5 s)…" },
                { DiagnoseCopy, "Kopiëren" },
                { DiagnoseCopied, "Gekopieerd" },
                { DiagnoseSave, "Opslaan" },
                { DiagnoseSaved, "Opgeslagen: {0}" },
                { DiagnoseClose, "Sluiten" },
                { DiagnoseSendHint, "Werkt er iets niet? Kopieer dit rapport en stuur het naar je VibeModel-beheerder." },
                { UpdateAvailable, "VibeModel {0} beschikbaar ({1})" },
                { UpdateInstall, "Bijwerken" },
                { UpdateInstallTip, "Downloadt de nieuwe versie naast de huidige. Die wordt actief als je Revit opnieuw start; in het model verandert niets." },
                { UpdateNotes, "Wat is nieuw" },
                { UpdateLater, "Niet nu" },
                { UpdateDownloading, "Downloaden… {0} / {1}" },
                { UpdateUnpacking, "Uitpakken…" },
                { UpdateReady, "VibeModel {0} is klaar - actief na het opnieuw starten van Revit." },
                { UpdateRolledBack, "Revit start de volgende keer VibeModel {0}." },
                { UpdateFailed, "Bijwerken mislukt: {0} Klik op Diagnose voor details." },
                { UpdateRetry, "Opnieuw" },
                { UpdateLoaderTooOld, "VibeModel {0} beschikbaar - eenmalig installeren met install.cmd uit de zip." },
                { UpdateDownloadZip, "Downloaden" },
                { UpdateUpToDate, "Up-to-date ({0} is de nieuwste versie)." },
                { UpdateNotChecked, "Nog niet gecontroleerd." },
                { UpdateCheckFailed, "Controle mislukt: {0}" },
                { UpdateDisabled, "Updates zijn uitgezet door IT." },
                { UpdateDevBuild, "Een ontwikkelversie in de Revit-add-insmap - updates zijn niet van toepassing." },
                { UpdateChecking, "Bezig met controleren…" },
                { SettingsUpdatesTitle, "Versie en updates" },
                { SettingsUpdateInstalled, "Geïnstalleerd: VibeModel {0} · loader {1}" },
                { SettingsUpdateRunsFrom, "Draait vanuit: {0}" },
                { SettingsUpdateSource, "Updatebron: {0}" },
                { SettingsUpdateCheck, "Automatisch controleren op updates (eens per dag, bij het starten van Revit)" },
                { SettingsUpdateCheckNow, "Nu controleren" },
                { SettingsUpdateRollback, "Terug naar {0}" },
                { SettingsUpdatePending, "Bij de volgende start van Revit: VibeModel {0}" },
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
