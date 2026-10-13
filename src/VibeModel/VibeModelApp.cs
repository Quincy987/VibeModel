using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.Services.Helpers;
using VibeModel.UI;

namespace VibeModel
{
    /// <summary>
    /// VibeModel - AI Integration for Autodesk Revit.
    ///
    /// Starts an embedded HTTP server on localhost:18884 that allows
    /// AI assistants to inspect and modify Revit models via curl commands.
    ///
    /// Falls back to file-based polling if HTTP server cannot start.
    /// </summary>
    public class VibeModelApp : IExternalApplication
    {
        private RevitHttpServer _httpServer;
        private RevitCommandHandler _commandHandler;
        private FileBasedFallback _fallback;
        private ChatPane _chatPane;
        private IChatBackend _chatBackend;
        private bool _usingFallback;
        private int _httpPort = 18884;

        public static ClaudeCommandRegistry Registry { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            Logger.Info("========================================");
            Logger.Info("VibeModel v1.0 - AI Integration for Revit");
            Logger.Info("Revit version: " + application.ControlledApplication.VersionNumber);
            Logger.Info("========================================");

            try
            {
                var registry = new ClaudeCommandRegistry();
                Registry = registry;

                // Register dockable chat pane (must happen before Revit UI is fully loaded)
                _chatPane = new ChatPane();
                application.RegisterDockablePane(ChatPane.PaneId, "VibeModel Chat", _chatPane);
                Logger.Info("Chat pane registered");

                // Create ribbon tab and button
                RibbonBuilder.CreateRibbon(application);

                _commandHandler = new RevitCommandHandler(registry);
                _commandHandler.Initialize();

                // Start HTTP server before creating chat backend (backend needs the port)
                _httpServer = new RevitHttpServer(_commandHandler);
                if (_httpServer.Start())
                {
                    _httpPort = _httpServer.ActivePort;
                    Logger.Info("VibeModel HTTP server active on port " + _httpPort);
                    Logger.Info("Usage: curl -s http://localhost:" + _httpPort + "/help");
                }
                else
                {
                    Logger.Warn("HTTP server failed to start - falling back to file-based polling");
                    _fallback = new FileBasedFallback(registry);
                    _usingFallback = true;
                    application.Idling += OnIdling;
                    Logger.Info("File-based fallback active (C:\\RevitClaudeLink\\)");
                }

                // Create chat backend with the actual HTTP port and inject into pane
                _chatBackend = CreateChatBackend(registry.GetCommands(), _httpPort);
                _chatPane.InitializeBackend(_chatBackend);
                _chatPane.BackendChangeRequested += OnBackendChangeRequested;

                // Dismiss non-transaction dialogs during command execution
                application.DialogBoxShowing += OnDialogBoxShowing;

                // Safety net for Revit failures in transactions VibeModel did not open itself
                // (an exec snippet's own `new Transaction`): handled while a command runs, so
                // Revit's modal failure dialog never blocks the main thread.
                application.ControlledApplication.FailuresProcessing += OnFailuresProcessing;

                // Track the active document title (fires on the Revit UI thread) so the
                // chat pane can label saved sessions with the project name without
                // touching the Revit API outside an API context.
                application.ViewActivated += OnViewActivated;
                // Track every committed transaction so command responses can report
                // "the user changed the model since your last command" (ModelChangeTracker).
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;

                // Office standards (plan 11): keep the per-turn TEMPLATE/STANDARDS prompt
                // blocks current. They are rendered on Idling (main thread, only when marked
                // dirty) because the chat backends build prompts on worker threads.
                application.Idling += OnStandardsIdling;
                application.ControlledApplication.DocumentOpened += OnDocumentOpenedOrClosed;
                application.ControlledApplication.DocumentClosed += OnDocumentOpenedOrClosed;
                StartStandardsBackgroundWork();

                Logger.Info("VibeModel startup complete");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("VibeModel startup failed", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.DialogBoxShowing -= OnDialogBoxShowing;
                application.ControlledApplication.FailuresProcessing -= OnFailuresProcessing;
                application.ViewActivated -= OnViewActivated;
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.Idling -= OnStandardsIdling;
                application.ControlledApplication.DocumentOpened -= OnDocumentOpenedOrClosed;
                application.ControlledApplication.DocumentClosed -= OnDocumentOpenedOrClosed;

                if (_usingFallback)
                {
                    application.Idling -= OnIdling;
                }

                // Cleanup chat pane (cancels running generation)
                _chatPane?.Cleanup();
                // Dispose backend (kills any lingering claude process)
                _chatBackend?.Dispose();

                // Order matters: stop accepting new connections first,
                // then drain pending requests, then dispose ExternalEvent.
                _httpServer?.Stop();
                _httpServer?.Dispose();
                _commandHandler?.Dispose();

                Logger.Info("VibeModel shutdown complete");
            }
            catch (Exception ex)
            {
                Logger.Error("VibeModel shutdown error", ex);
            }

            return Result.Succeeded;
        }

        private IChatBackend CreateChatBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            var preferred = SettingsManager.GetPreferredBackend();
            // Normalize legacy value
            if (preferred == "direct") preferred = "anthropic-api";

            Logger.Info("Preferred backend: " + preferred);

            // Explicit selection
            if (preferred == "claude-cli")
                return TryCreateCliBackend(commands, httpPort) ?? TryCreateFallback(commands, httpPort);

            if (preferred == "anthropic-api")
                return new AnthropicDirectBackend(commands, httpPort);

            if (preferred == "local-llm")
                return new LocalLlmBackend(commands, httpPort);

            // Auto mode: try CLI â†’ API â†’ Local â†’ default
            var forceDirect = Environment.GetEnvironmentVariable("VIBEMODEL_FORCE_DIRECT") == "1";

            if (!forceDirect)
            {
                var cli = TryCreateCliBackend(commands, httpPort);
                if (cli != null) return cli;
            }
            else
            {
                Logger.Info("VIBEMODEL_FORCE_DIRECT=1, skipping Claude Code CLI detection");
            }

            var apiKey = SettingsManager.GetApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                Logger.Info("Using AnthropicDirectBackend (API key configured)");
                return new AnthropicDirectBackend(commands, httpPort);
            }

            // Check if local LLM endpoint is reachable
            var localBackend = new LocalLlmBackend(commands, httpPort);
            if (localBackend.IsAvailable)
            {
                Logger.Info("Using LocalLlmBackend (server reachable)");
                return localBackend;
            }
            localBackend.Dispose();

            Logger.Info("No backend configured â€” will prompt user for setup");
            return new AnthropicDirectBackend(commands, httpPort);
        }

        private ClaudeCodeBackend TryCreateCliBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            var cliBackend = new ClaudeCodeBackend(commands, httpPort);
            if (cliBackend.IsAvailable)
            {
                Logger.Info("Using ClaudeCodeBackend (CLI detected)");
                return cliBackend;
            }
            cliBackend.Dispose();
            return null;
        }

        private IChatBackend TryCreateFallback(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            var apiKey = SettingsManager.GetApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
                return new AnthropicDirectBackend(commands, httpPort);

            var local = new LocalLlmBackend(commands, httpPort);
            if (local.IsAvailable) return local;
            local.Dispose();

            return new AnthropicDirectBackend(commands, httpPort);
        }

        private void OnBackendChangeRequested(object sender, EventArgs e)
        {
            try
            {
                var commands = Registry.GetCommands();
                _chatBackend?.Dispose();
                _chatBackend = CreateChatBackend(commands, _httpPort);
                _chatPane.InitializeBackend(_chatBackend);
                Logger.Info("Backend switched to: " + _chatBackend.GetType().Name);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to switch backend", ex);
            }
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                ActiveDocumentTracker.Update(e.Document?.Title, e.Document?.PathName);
                if (sender is UIApplication viewApp) TrackViewAndSelection(viewApp.ActiveUIDocument);
                // The TEMPLATE prompt block names the active view and its phase.
                Services.Standards.StandardsContext.MarkPromptDirty();
            }
            catch
            {
                // Title tracking is best-effort; never disturb view activation.
            }
        }

        private void OnDocumentOpenedOrClosed(object sender, EventArgs e)
        {
            Services.Standards.StandardsContext.MarkPromptDirty();
            try
            {
                // Last document closed: no project folder (and no stale title) for the next chat turn.
                if (e is Autodesk.Revit.DB.Events.DocumentClosedEventArgs &&
                    sender is Autodesk.Revit.ApplicationServices.Application app && app.Documents.Size == 0)
                    ActiveDocumentTracker.Update("", "");
            }
            catch
            {
                // Tracking is best-effort.
            }
        }

        private void OnStandardsIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                if (sender is UIApplication uiApp)
                {
                    Services.Standards.StandardsContext.RefreshPromptIfDirty(uiApp);
                    SampleViewAndSelection(uiApp);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Standards idling refresh failed: " + ex.Message);
            }
        }

        // The chat pane's starter chips follow the selection and the active view. Revit has no
        // selection event in every supported version, so a cheap sample on Idling (at most every
        // 400 ms) keeps the tracker current; the tracker raises Changed only on a real change.
        private int _lastViewSampleTick;

        private void SampleViewAndSelection(UIApplication uiApp)
        {
            // Unsigned difference: Environment.TickCount goes negative after ~25 days of uptime.
            var now = Environment.TickCount;
            if (unchecked((uint)(now - _lastViewSampleTick)) < 400u) return;
            _lastViewSampleTick = now;
            TrackViewAndSelection(uiApp.ActiveUIDocument);
        }

        /// <summary>Active view name, sheet flag and selection size → <see cref="ActiveDocumentTracker"/>.</summary>
        internal static void TrackViewAndSelection(UIDocument uidoc)
        {
            try
            {
                if (uidoc?.Document == null) return;
                var view = uidoc.ActiveView;
                int selected = 0;
                try { selected = uidoc.Selection.GetElementIds().Count; } catch { }
                ActiveDocumentTracker.UpdateView(view?.Name, view is Autodesk.Revit.DB.ViewSheet, selected);
            }
            catch
            {
                // Best-effort: a view mid-switch just waits for the next sample.
            }
        }

        /// <summary>
        /// Off the main thread: mirror a network pack folder, discover packs, and refresh each
        /// pack's library index and source document index when stale. Network shares can be slow or absent — nothing here may
        /// block startup, and every failure is only logged.
        /// </summary>
        private static void StartStandardsBackgroundWork()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!SettingsManager.GetStandardsEnabled()) return;
                    // A pack folder on a network share is mirrored locally first; the Revit
                    // thread only ever reads the mirror.
                    var share = SettingsManager.GetStandardsPackPath()?.Trim();
                    if (Services.Standards.PathProbe.IsNetworkPath(share))
                        Services.Standards.StandardsPackRegistry.SyncShareNow(share);
                    foreach (var pack in Services.Standards.StandardsPackRegistry.GetPacks())
                    {
                        Services.Standards.LibraryIndexService.Get(pack);
                        if (!string.IsNullOrWhiteSpace(Services.Standards.StandardsPackRegistry.ResolveStandardsFolder(pack)))
                            Services.Standards.DocumentIndexService.StartIndex(pack);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("Standards background work failed: " + ex.Message);
                }
            });
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            // Only dismiss dialogs when a VibeModel command is actively executing.
            // This prevents interference with normal user interaction.
            if (!RevitCommandHandler.IsProcessingCommand)
                return;

            // Revit failures are handled before this point (RevitFailureHandler). Log every
            // dialog that still shows up during a command so a gap is visible in the log.
            Logger.Warn("Dialog during command: " + e.DialogId + " (" + e.GetType().Name + ")");

            // This catches non-failure dialogs that Revit shows during our operations.
            if (e is Autodesk.Revit.UI.Events.TaskDialogShowingEventArgs taskArgs)
            {
                Logger.Info("Auto-dismissing TaskDialog during command: " + taskArgs.DialogId);
                e.OverrideResult((int)Autodesk.Revit.UI.TaskDialogResult.Close);
            }
            else if (e is Autodesk.Revit.UI.Events.MessageBoxShowingEventArgs)
            {
                Logger.Info("Auto-dismissing MessageBox during command");
                e.OverrideResult(1); // IDOK
            }
        }

        /// <summary>
        /// Application-wide failure handling, only while a VibeModel command executes (the
        /// user's own work keeps Revit's normal dialogs). Our own transactions are handled by
        /// their preprocessor first; this sees what is left, including snippet transactions.
        /// </summary>
        private void OnFailuresProcessing(object sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
        {
            if (!RevitCommandHandler.IsProcessingCommand)
                return;

            var result = Services.Helpers.RevitFailureHandler.Process(e.GetFailuresAccessor(), out _);
            e.SetProcessingResult(result);
        }

        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;
                var key = ModelChangeTracker.DocKey(doc.PathName, doc.Title);

                // Office standards: the post-command check looks at what a command added (the
                // project journal also at what it modified and deleted), and
                // the prompt snapshot is re-rendered on the next idle.
                Services.Standards.StandardsContext.MarkPromptDirty();
                if (Services.Standards.CreatedElementsRecorder.IsArmed &&
                    e.Operation == Autodesk.Revit.DB.Events.UndoOperation.TransactionCommitted)
                    Services.Standards.CreatedElementsRecorder.Record(
                        e.GetAddedElementIds(), e.GetModifiedElementIds(), e.GetDeletedElementIds());

                switch (e.Operation)
                {
                    case Autodesk.Revit.DB.Events.UndoOperation.TransactionCommitted:
                        ModelChangeTracker.RecordCommit(key, e.GetTransactionNames());
                        break;
                    case Autodesk.Revit.DB.Events.UndoOperation.TransactionUndone:
                    case Autodesk.Revit.DB.Events.UndoOperation.TransactionRedone:
                        // Undo/redo is always user-driven â€” even undoing a VibeModel edit
                        // changes the model outside VibeModel's control.
                        ModelChangeTracker.RecordUndoRedo(key);
                        break;
                    // TransactionRolledBack is deliberately not counted: a rollback restores
                    // the pre-transaction state, so there is no net model change to report.
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DocumentChanged tracking error", ex);
            }
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                e.SetRaiseWithoutDelay();
                var uiApp = sender as UIApplication;
                if (uiApp != null)
                {
                    _fallback.CheckAndExecute(uiApp);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Idling handler error", ex);
            }
        }
    }
}
