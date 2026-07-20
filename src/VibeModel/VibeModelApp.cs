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

                // Track every committed transaction so command responses can report
                // "the user changed the model since your last command" (ModelChangeTracker).
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;

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
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.ControlledApplication.DocumentClosing -= OnDocumentClosing;

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

            // Auto mode: try CLI → API → Local → default
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

            Logger.Info("No backend configured — will prompt user for setup");
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

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            // Only dismiss dialogs when a VibeModel command is actively executing.
            // This prevents interference with normal user interaction.
            if (!RevitCommandHandler.IsProcessingCommand)
                return;

            // Transaction warnings are already handled by WarningSwallower in TransactionHelper.
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

        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;
                var key = ModelChangeTracker.DocKey(doc.PathName, doc.Title);

                switch (e.Operation)
                {
                    case Autodesk.Revit.DB.Events.UndoOperation.TransactionCommitted:
                        ModelChangeTracker.RecordCommit(key, e.GetTransactionNames());
                        break;
                    case Autodesk.Revit.DB.Events.UndoOperation.TransactionUndone:
                    case Autodesk.Revit.DB.Events.UndoOperation.TransactionRedone:
                        // Undo/redo is always user-driven — even undoing a VibeModel edit
                        // changes the model outside VibeModel's control.
                        ModelChangeTracker.RecordUndoRedo(key);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DocumentChanged tracking error", ex);
            }
        }

        private void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc != null)
                    ModelChangeTracker.Forget(ModelChangeTracker.DocKey(doc.PathName, doc.Title));
            }
            catch (Exception ex)
            {
                Logger.Error("DocumentClosing tracking error", ex);
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
