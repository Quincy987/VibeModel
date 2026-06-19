using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Autodesk.Revit.DB;
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

        /// <summary>
        /// True while the Execute method is processing commands on the main thread.
        /// Used by DialogBoxShowing handler to only dismiss dialogs during command execution.
        /// </summary>
        public static volatile bool IsProcessingCommand;

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
        /// Called by HTTP server (background thread) for a POST /batch.
        /// Enqueues the whole command list as ONE request so a single TransactionGroup can
        /// wrap the entire loop (one undo entry). Uses a timeout scaled to the batch size.
        /// </summary>
        public string EnqueueBatchAndWait(IReadOnlyList<BatchCommand> commands, bool atomic)
        {
            if (_disposed)
                return "ERROR: VibeModel is shutting down";

            if (_queue.Count >= MaxQueueSize)
                return "ERROR: Command queue full (" + MaxQueueSize + " pending). Revit may be in a modal dialog.";

            var request = new CommandRequest(commands, atomic);
            _queue.Enqueue(request);

            try
            {
                _externalEvent.Raise();
            }
            catch (Exception)
            {
                request.Cancel();
                return "ERROR: VibeModel is shutting down";
            }

            var timeout = BatchTimeout(commands.Count);
            try
            {
                if (!request.ResponseReady.Wait(timeout))
                {
                    request.Cancel();
                    Logger.Warn("Batch timed out (" + commands.Count + " commands)");
                    return "ERROR: Revit did not respond within " + (int)timeout.TotalSeconds +
                           " seconds. Is Revit in a modal dialog?";
                }

                return request.Result;
            }
            finally
            {
                request.Dispose();
            }
        }

        // Scale the wait to the batch size: max(30s, 5s + 2s/command), capped at 5 min.
        private static TimeSpan BatchTimeout(int commandCount)
        {
            var seconds = Math.Max(30, 5 + 2 * commandCount);
            return TimeSpan.FromSeconds(Math.Min(seconds, 300));
        }

        /// <summary>
        /// Called by Revit on the main thread via ExternalEvent.
        /// Processes all queued commands, skipping cancelled/timed-out ones.
        /// </summary>
        public void Execute(UIApplication app)
        {
            IsProcessingCommand = true;
            try
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
                        if (request.IsBatch)
                        {
                            Logger.Info("Executing batch: " + request.Batch.Count + " commands" +
                                        (request.Atomic ? " (atomic)" : ""));
                            request.Result = ExecuteBatch(app, request);
                        }
                        else
                        {
                            Logger.Info("Executing: " + request.Command + " " + request.Args);
                            request.Result = _registry.Execute(request.Command, request.Args, app);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Command execution failed: " + request.Command, ex);
                        request.Result = "ERROR: " + ex.Message;
                    }
                    finally
                    {
                        try { request.ResponseReady.Set(); }
                        catch (ObjectDisposedException) { /* Timed-out request already disposed by HTTP thread */ }
                    }
                }
            }
            finally
            {
                IsProcessingCommand = false;
            }
        }

        /// <summary>
        /// Runs a batch as one TransactionGroup so it collapses to a single undo entry.
        /// Default is resilient (keep every command that succeeded, then Assimilate). Atomic
        /// mode rolls the whole group back if any command errored. The group is NEVER left
        /// open — an open group locks the document.
        /// </summary>
        private string ExecuteBatch(UIApplication app, CommandRequest req)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var sb = new StringBuilder();
            bool anyModified = false, anyError = false;

            using (var tg = new TransactionGroup(doc, BuildGroupName(req.Batch)))
            {
                tg.Start();
                try
                {
                    foreach (var item in req.Batch)
                    {
                        sb.AppendLine(">>> " + item.Command +
                                      (string.IsNullOrEmpty(item.Args) ? "" : " " + item.Args));
                        var result = _registry.Execute(item.Command, item.Args, app);
                        sb.AppendLine(result);
                        sb.AppendLine();

                        if (result != null && result.StartsWith("ERROR")) anyError = true;
                        else if (_registry.IsModification(item.Command)) anyModified = true;
                    }

                    if (req.Atomic && anyError)
                    {
                        tg.RollBack();
                        sb.AppendLine("[atomic] rolled back — a command failed.");
                    }
                    else if (anyModified)
                    {
                        tg.Assimilate(); // merge all committed child transactions into one undo entry
                    }
                    else
                    {
                        tg.RollBack(); // read-only / nothing committed — avoid an empty undo entry
                    }
                }
                catch
                {
                    if (tg.HasStarted()) tg.RollBack(); // safety: never leave the group open
                    throw;
                }
            }

            return sb.ToString();
        }

        // Readable undo-dropdown label, e.g. "VibeModel: 3× wall, 1× floor".
        private static string BuildGroupName(IReadOnlyList<BatchCommand> batch)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var item in batch)
            {
                if (!counts.ContainsKey(item.Command)) { counts[item.Command] = 0; order.Add(item.Command); }
                counts[item.Command]++;
            }

            var parts = new List<string>();
            foreach (var name in order)
                parts.Add(counts[name] + "× " + name);

            return "VibeModel: " + string.Join(", ", parts);
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
    /// One command in a batch request.
    /// </summary>
    public class BatchCommand
    {
        public string Command { get; }
        public string Args { get; }

        public BatchCommand(string command, string args)
        {
            Command = command;
            Args = args;
        }
    }

    /// <summary>
    /// Represents a single command request (or a whole batch) with its sync primitive.
    /// </summary>
    public class CommandRequest : IDisposable
    {
        public string Command { get; }
        public string Args { get; }

        // Batch payload — null for single commands.
        public IReadOnlyList<BatchCommand> Batch { get; }
        public bool Atomic { get; }
        public bool IsBatch => Batch != null;

        public string Result { get; set; }
        public ManualResetEventSlim ResponseReady { get; } = new ManualResetEventSlim(false);

        private volatile bool _cancelled;
        public bool IsCancelled => _cancelled;

        public CommandRequest(string command, string args)
        {
            Command = command;
            Args = args;
        }

        public CommandRequest(IReadOnlyList<BatchCommand> batch, bool atomic)
        {
            Batch = batch;
            Atomic = atomic;
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
