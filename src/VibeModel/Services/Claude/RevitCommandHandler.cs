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
        private const int MaxQueueSize = 50;
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

        private readonly ConcurrentQueue<CommandRequest> _queue = new ConcurrentQueue<CommandRequest>();
        private readonly ClaudeCommandRegistry _registry;
        private ExternalEvent _externalEvent;
        private volatile bool _disposed;

        public RevitCommandHandler(ClaudeCommandRegistry registry)
        {
            _registry = registry;
        }

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
            if (_disposed)
                return "ERROR: VibeModel is shutting down";

            if (_queue.Count >= MaxQueueSize)
                return "ERROR: Command queue full (" + MaxQueueSize + " pending). Revit may be in a modal dialog.";

            var request = new CommandRequest(command, args);
            _queue.Enqueue(request);

            try
            {
                _externalEvent.Raise();
            }
            catch (Exception)
            {
                // ExternalEvent already disposed during shutdown
                request.Cancel();
                return "ERROR: VibeModel is shutting down";
            }

            try
            {
                if (!request.ResponseReady.Wait(CommandTimeout))
                {
                    request.Cancel();
                    Logger.Warn("Command timed out: " + command);
                    return "ERROR: Revit did not respond within 30 seconds. Is Revit in a modal dialog?";
                }

                return request.Result;
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Called by Revit on the main thread via ExternalEvent.
        /// Processes all queued commands, skipping cancelled/timed-out ones.
        /// </summary>
        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var request))
            {
                if (request.IsCancelled)
                {
                    request.Dispose();
                    continue;
                }

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

        /// <summary>
        /// Drain remaining requests before disposing ExternalEvent.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;

            // Drain queue — unblock any waiting HTTP threads
            while (_queue.TryDequeue(out var request))
            {
                request.Result = "ERROR: VibeModel is shutting down";
                request.ResponseReady.Set();
                request.Dispose();
            }

            _externalEvent?.Dispose();
        }
    }

    /// <summary>
    /// Represents a single command request with its synchronization primitive.
    /// </summary>
    public class CommandRequest : IDisposable
    {
        public string Command { get; }
        public string Args { get; }
        public string Result { get; set; }
        public ManualResetEventSlim ResponseReady { get; } = new ManualResetEventSlim(false);

        private volatile bool _cancelled;
        public bool IsCancelled => _cancelled;

        public CommandRequest(string command, string args)
        {
            Command = command;
            Args = args;
        }

        public void Cancel()
        {
            _cancelled = true;
        }

        public void Dispose()
        {
            ResponseReady.Dispose();
        }
    }
}
