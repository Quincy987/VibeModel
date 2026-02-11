using System;
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
                int httpPort = 18884;
                if (_httpServer.Start())
                {
                    httpPort = _httpServer.ActivePort;
                    Logger.Info("VibeModel HTTP server active on port " + httpPort);
                    Logger.Info("Usage: curl -s http://localhost:" + httpPort + "/help");
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
                _chatBackend = CreateChatBackend(registry.GetCommands(), httpPort);
                _chatPane.InitializeBackend(_chatBackend);

                // Dismiss non-transaction dialogs during command execution
                application.DialogBoxShowing += OnDialogBoxShowing;

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

        private IChatBackend CreateChatBackend(System.Collections.Generic.IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            // Debug: set VIBEMODEL_FORCE_DIRECT=1 to skip Claude Code CLI detection
            var forceDirect = Environment.GetEnvironmentVariable("VIBEMODEL_FORCE_DIRECT") == "1";

            // 1. Claude Code CLI is the recommended backend (full agentic capabilities)
            if (!forceDirect)
            {
                var cliBackend = new ClaudeCodeBackend(commands, httpPort);
                if (cliBackend.IsAvailable)
                {
                    Logger.Info("Using ClaudeCodeBackend (CLI detected)");
                    return cliBackend;
                }
                cliBackend.Dispose();
            }
            else
            {
                Logger.Info("VIBEMODEL_FORCE_DIRECT=1, skipping Claude Code CLI detection");
            }

            // 2. Fall back to direct API if user has configured an API key
            var apiKey = SettingsManager.GetApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                Logger.Info("Using AnthropicDirectBackend (API key configured, CLI not found)");
                return new AnthropicDirectBackend(commands, httpPort);
            }

            // 3. Nothing configured — return direct backend, it will show setup instructions
            Logger.Info("No backend configured — will prompt user for setup");
            return new AnthropicDirectBackend(commands, httpPort);
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
