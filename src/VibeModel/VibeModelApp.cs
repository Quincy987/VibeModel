using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

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
        private bool _usingFallback;

        public Result OnStartup(UIControlledApplication application)
        {
            Logger.Info("========================================");
            Logger.Info("VibeModel v1.0 - AI Integration for Revit");
            Logger.Info("Revit version: " + application.ControlledApplication.VersionNumber);
            Logger.Info("========================================");

            try
            {
                // 1. Create command registry (auto-discovers all IClaudeCommand implementations)
                var registry = new ClaudeCommandRegistry();

                // 2. Create command handler (bridges HTTP thread → Revit main thread)
                _commandHandler = new RevitCommandHandler(registry);
                _commandHandler.Initialize();

                // 3. Try to start HTTP server
                _httpServer = new RevitHttpServer(_commandHandler);
                if (_httpServer.Start())
                {
                    Logger.Info("VibeModel HTTP server active on port " + _httpServer.ActivePort);
                    Logger.Info("Usage: curl -s http://localhost:" + _httpServer.ActivePort + "/help");
                }
                else
                {
                    // 4. Fall back to file-based polling
                    Logger.Warn("HTTP server failed to start - falling back to file-based polling");
                    _fallback = new FileBasedFallback(registry);
                    _usingFallback = true;
                    application.Idling += OnIdling;
                    Logger.Info("File-based fallback active (C:\\RevitClaudeLink\\)");
                }

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
                if (_usingFallback)
                {
                    application.Idling -= OnIdling;
                }

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

        /// <summary>
        /// Idling handler — only used when HTTP server fails and we fall back to file polling.
        /// </summary>
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
