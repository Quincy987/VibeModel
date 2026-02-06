using System;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Bridges the HTTP server (background thread) to the Revit main thread.
    /// Uses ExternalEvent + ConcurrentQueue + ManualResetEventSlim for thread-safe handoff.
    /// </summary>
    public class RevitCommandHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<CommandRequest> _queue = new ConcurrentQueue<CommandRequest>();
        private readonly ClaudeCommandRegistry _registry;
        private ExternalEvent _externalEvent;

        public RevitCommandHandler(ClaudeCommandRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// Must be called after construction to create the ExternalEvent.
        /// Cannot be done in constructor because ExternalEvent.Create requires Revit context.
        /// </summary>
        public void Initialize()
        {
            _externalEvent = ExternalEvent.Create(this);
            Logger.Info("RevitCommandHandler initialized with ExternalEvent");
        }

        /// <summary>
        /// Called by HTTP server (background thread).
        /// Enqueues a command and blocks until the main thread processes it.
        /// </summary>
        public string EnqueueAndWait(string command, string args)
        {
            var request = new CommandRequest(command, args);
            _queue.Enqueue(request);
            _externalEvent.Raise();

            if (!request.ResponseReady.Wait(TimeSpan.FromSeconds(30)))
            {
                Logger.Warn("Command timed out: " + command);
                return "ERROR: Revit did not respond within 30 seconds. Is Revit in a modal dialog?";
            }

            return request.Result;
        }

        /// <summary>
        /// Called by Revit on the main thread via ExternalEvent.
        /// Processes all queued commands.
        /// </summary>
        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var request))
            {
                try
                {
                    Logger.Info("Executing: " + request.Command + " " + request.Args);
                    request.Result = _registry.Execute(request.Command, request.Args, app);
                }
                catch (Exception ex)
                {
                    Logger.Error("Command execution failed: " + request.Command, ex);
                    request.Result = "ERROR: " + ex.Message;
                }
                finally
                {
                    request.ResponseReady.Set();
                }
            }
        }

        public string GetName()
        {
            return "VibeModel Command Handler";
        }

        public void Dispose()
        {
            _externalEvent?.Dispose();
        }
    }

    /// <summary>
    /// Represents a single command request with its synchronization primitive.
    /// </summary>
    public class CommandRequest
    {
        public string Command { get; }
        public string Args { get; }
        public string Result { get; set; }
        public ManualResetEventSlim ResponseReady { get; } = new ManualResetEventSlim(false);

        public CommandRequest(string command, string args)
        {
            Command = command;
            Args = args;
        }
    }
}
