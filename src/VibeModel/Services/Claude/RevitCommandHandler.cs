using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

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

        // Writing files takes longer than a model edit: `export --all-sheets` gets the batch ceiling.
        private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(300);

        internal static TimeSpan TimeoutFor(string command)
            => string.Equals(command, "export", StringComparison.OrdinalIgnoreCase) ? ExportTimeout : CommandTimeout;

        /// <summary>
        /// Plan 17: exec waits for its loop-check budget (25 s, ?timeout= up to 300 s) plus 10 s for
        /// compiling and answering — the check must fire before the client gives up.
        /// </summary>
        internal static TimeSpan TimeoutFor(string command, RunOptions options)
            => ExecDeadline.Wait(TimeoutFor(command), IsExec(command), options?.TimeoutSeconds);

        private static bool IsExec(string command) => string.Equals(command, "exec", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True while the Execute method is processing commands on the main thread.
        /// Used by DialogBoxShowing handler to only dismiss dialogs during command execution.
        /// </summary>
        public static volatile bool IsProcessingCommand;

        private readonly ConcurrentQueue<CommandRequest> _queue = new ConcurrentQueue<CommandRequest>();
        private readonly ClaudeCommandRegistry _registry;
        private readonly JavaScriptSerializer _json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
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
        public string EnqueueAndWait(string command, string args, ResponseFormat fmt = ResponseFormat.Text,
            ChatTurnRef turn = null, RunOptions options = null)
        {
            if (_disposed)
                return "ERROR: VibeModel is shutting down";

            if (_queue.Count >= MaxQueueSize)
                return "ERROR: Command queue full (" + MaxQueueSize + " pending). Revit may be in a modal dialog.";

            var request = new CommandRequest(command, args, fmt) { Turn = turn, Options = options ?? RunOptions.None };
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
                // Plan 15: a delete/edit under "ask" waits for the user's Keep / Discard.
                bool mayWait = ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalPolicy.EffectiveMode,
                    ChangeApprovalPolicy.IsGuarded(command), _registry.IsModification(command), request.Options.Preview);
                var timeout = ChangeApprovalPolicy.TimeoutFor(TimeoutFor(command, request.Options), mayWait);
                if (!request.ResponseReady.Wait(timeout))
                {
                    request.CancelAfterTimeout();
                    Logger.Warn("Command timed out: " + command);
                    if (mayWait) return PendingResponse(fmt);
                    return "ERROR: Revit did not respond within " + (int)timeout.TotalSeconds + " seconds. Is Revit in a modal dialog?" +
                           (IsExec(command) ? "\nHint: " + ExecDeadline.UninterruptibleHint : "");
                }

                return request.Result;
            }
            finally
            {
                request.Dispose();
            }
        }

        // The client stopped waiting while the approval window may still be up: the decision
        // lands in the approval log and in the next stamp, never re-run the change.
        private string PendingResponse(ResponseFormat fmt)
            => CommandResult.Error(ChangeApprovalPolicy.PendingCode, ChangeApprovalPolicy.PendingMessage,
                ChangeApprovalPolicy.PendingHint).Render(fmt, _json);

        /// <summary>
        /// Called by HTTP server (background thread) for a POST /batch.
        /// Enqueues the whole command list as ONE request so a single TransactionGroup can
        /// wrap the entire loop (one undo entry). Uses a timeout scaled to the batch size.
        /// </summary>
        public string EnqueueBatchAndWait(IReadOnlyList<BatchCommand> commands, bool atomic,
            ResponseFormat fmt = ResponseFormat.Text, ChatTurnRef turn = null, RunOptions options = null)
        {
            if (_disposed)
                return "ERROR: VibeModel is shutting down";

            if (_queue.Count >= MaxQueueSize)
                return "ERROR: Command queue full (" + MaxQueueSize + " pending). Revit may be in a modal dialog.";

            var request = new CommandRequest(commands, atomic, fmt) { Turn = turn, Options = options ?? RunOptions.None };
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

            bool mayWait = ChangeApprovalPolicy.MayWaitForUser(ChangeApprovalPolicy.EffectiveMode,
                ChangeApprovalPolicy.IsGuardedBatch(commands.Select(c => c.Command)),
                commands.Any(c => _registry.IsModification(c.Command)), request.Options.Preview);
            var usual = BatchTimeout(commands.Count);
            // Plan 17: an exec in the batch may use its whole loop-check budget.
            usual = ExecDeadline.Wait(usual, commands.Any(c => IsExec(c.Command)), request.Options.TimeoutSeconds);
            var timeout = ChangeApprovalPolicy.TimeoutFor(usual, mayWait);
            try
            {
                if (!request.ResponseReady.Wait(timeout))
                {
                    request.CancelAfterTimeout();
                    Logger.Warn("Batch timed out (" + commands.Count + " commands)");
                    if (mayWait) return PendingResponse(fmt);
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
                        TrackActiveDocument(app);
                        RunContext.Current = request.Options;
                        if (request.IsBatch)
                        {
                            Logger.Info("Executing batch: " + request.Batch.Count + " commands" +
                                        (request.Atomic ? " (atomic)" : ""));
                            request.Result = ExecuteBatch(app, request);
                        }
                        else
                        {
                            Logger.Info("Executing: " + request.Command + " " + request.Args);
                            request.Result = ExecuteSingle(app, request);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Command execution failed: " + request.Command, ex);
                        request.Result = "ERROR: " + ex.Message;
                    }
                    finally
                    {
                        RunContext.Current = null;
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

        // Keeps the worker-thread view of the active document (project folder, chat labels)
        // current even when the user switched documents or saved-as without a view change.
        private static void TrackActiveDocument(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc != null) ActiveDocumentTracker.Update(doc.Title, doc.PathName);
            }
            catch
            {
                // Best-effort; never disturbs the command.
            }
        }

        // Commands whose responses must stay untouched by the model-state stamp.
        // (/health never reaches this handler — the HTTP server answers it directly.)
        private static readonly HashSet<string> StampExcluded =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "screenshot" };

        /// <summary>
        /// Runs a single command and appends the model-state stamp: a trailing text line on
        /// successful text responses, a "meta" object on every JSON envelope. Centralized here
        /// so no individual command can forget it.
        /// <para>
        /// Plan 15: when the change-approval policy, ?preview=1 or ?readonly=1 asks for it
        /// (<see cref="ChangeApprovalPolicy.NeedsRunGroup"/>), the command runs inside a "run group"
        /// that is kept, shown to the user first, or rolled back (<see cref="RunDecision"/>). Under
        /// the default policy without flags nothing of that happens: the run is exactly as before.
        /// </para>
        /// </summary>
        private string ExecuteSingle(UIApplication app, CommandRequest request)
        {
            // Record what a modifying command adds so the office rules can check exactly that.
            bool modifying = _registry.IsModification(request.Command);
            var options = request.Options ?? RunOptions.None;
            var doc = app.ActiveUIDocument?.Document;
            var mode = ChangeApprovalPolicy.EffectiveMode;
            bool guarded = ChangeApprovalPolicy.IsGuarded(request.Command);
            bool runControl = doc != null && ChangeApprovalPolicy.NeedsRunGroup(mode, guarded, modifying, options.Preview, options.ReadOnly);
            bool isExec = request.Command.Equals("exec", StringComparison.OrdinalIgnoreCase);
            RecordedChanges changes = null;
            CommandResult cr;
            FailureReport revit = null;

            // Annotations on the elements this command replaces/changes (spot elevations,
            // dimensions, tags): recorded now, re-hosted or removed afterwards — in the same undo
            // step (one TransactionGroup), except for exec, whose snippet may save, open or close
            // documents (not allowed inside a group): there the re-host is its own undo step —
            // unless the run needs a group anyway (plan 15); exec then runs inside it.
            var gdoc = AnnotationGuard.Applies(request.Command) ? doc : null;
            var snapshot = gdoc != null ? GuardCapture(gdoc, AnnotationScope.TargetIds(request.Command, request.Args)) : null;
            var preRead = runControl ? ApprovalRun.PreRead(doc, AnnotationScope.TargetIds(request.Command, request.Args)) : null;
            long editsMark = runControl ? ApprovalRun.EditsMark(doc) : 0;
            TransactionGroup group = null;
            AnnotationOutcome annotations = null;
            if (runControl || (snapshot != null && !isExec))
            {
                var groupName = runControl
                    ? ChangeApprovalPolicy.UndoName(request.Command, request.Args)
                    : "VibeModel: " + request.Command;
                try { group = new TransactionGroup(doc, groupName); group.Start(); }
                catch (Exception ex)
                {
                    Logger.Warn((runControl ? "Run group" : "Annotation guard") + ": no transaction group (" + ex.Message + ")");
                    group?.Dispose();
                    group = null;
                    // A run that must be previewable, refusable or approvable never runs without its group.
                    if (runControl)
                        return Render(request, CommandResult.Error(ChangeApprovalPolicy.RunGroupCode,
                            "Could not open a transaction group for this run: " + ex.Message,
                            "Revit may be in an edit mode or another transaction; finish it and retry."), app);
                }
            }

            var run = new RunOutcome();
            RunContext.InRunGroup = runControl && group != null;
            try
            {
                if (modifying) CreatedElementsRecorder.Arm();
                try
                {
                    cr = FailureCollector.Collect(() => _registry.ExecuteCore(request.Command, request.Args, app), out revit);
                }
                finally
                {
                    if (modifying) changes = CreatedElementsRecorder.DisarmChanges();
                }
                if (cr.Success && snapshot != null) annotations = GuardReconcile(snapshot, changes, revit);
                if (runControl)
                    run = DecideRun(app, doc, request, mode, guarded, options, changes, preRead, cr, revit, annotations, batch: false);
            }
            finally
            {
                RunContext.InRunGroup = false;
                if (runControl) EndRunGroup(group, run.Keep);
                else EndGroup(group, anyCommitted: annotations != null || (changes != null && !changes.IsEmpty));
            }

            if (runControl) FinishRun(app, doc, request, run, changes, editsMark, request.Command, request.Args);
            // Nothing a rolled-back run did is checked or journaled.
            bool rolledBack = runControl && !run.Keep;
            if (rolledBack) changes = null;

            var findings = cr.Success && changes != null ? PostCommandCheck.Run(app, changes.Added) : null;
            if (cr.Success && changes != null)
            {
                // A modifying command that also writes a file (a generated family's .rfa) names it.
                IList<string> written = null;
                if (_registry.GetCommands().TryGetValue(request.Command, out var cmd) && cmd is IJournaledCommand j)
                    try { written = j.JournalFiles(cr); } catch { written = null; }
                Journal(app, request.Command, request.Args, changes, request.Turn, written);
            }
            else if (cr.Success && !rolledBack && _registry.GetCommands().TryGetValue(request.Command, out var command) && command is IJournaledCommand journaled)
                JournalFiles(app, request.Command, request.Args, journaled, cr, request.Turn);

            return Render(request, run.Replace ?? cr, app, revit, findings, annotations, run);
        }

        /// <summary>
        /// The response of a single command: the result, then the "-- revit", "-- check",
        /// "-- annotations" and plan-15 lines, then the stamp (text); the same as additive JSON
        /// objects next to the envelope.
        /// </summary>
        private string Render(CommandRequest request, CommandResult cr, UIApplication app, FailureReport revit = null,
            List<CheckFinding> findings = null, AnnotationOutcome annotations = null, RunOutcome run = null)
        {
            bool json = request.Format == ResponseFormat.Json;

            // Take the stamp only when it will actually be delivered — taking it resets the
            // "user edits since last command" counter (reset means "the client has been told").
            ModelStamp stamp = null;
            if (!StampExcluded.Contains(request.Command) && (json || cr.Success))
                stamp = TryTakeStamp(app, !request.IsCancelled);

            var checkLine = findings != null ? CheckReport.SummaryLine(findings) : null;
            var annotationLine = annotations?.ToTextLine();

            if (json)
            {
                var obj = cr.ToJsonObject();
                if (obj is Dictionary<string, object> dict)
                {
                    if (stamp != null) dict["meta"] = stamp.ToMeta();
                    if (checkLine != null) dict["check"] = CheckEnvelope(checkLine, findings);
                    if (revit != null && !revit.IsEmpty) dict["revit"] = revit.ToJson();
                    if (annotationLine != null) dict["annotations"] = annotations.ToJson();
                    run?.AddJson(dict);
                }
                return _json.Serialize(obj);
            }

            // READ_ONLY carries the preview line between its message and its hint (errors are unstamped).
            if (run?.TextOverride != null) return run.TextOverride;

            var text = cr.RenderText();
            // What Revit's failure processing did (warnings dismissed, annotations deleted). An
            // error already carries Revit's reason, so its text stays as it is.
            if (cr.Success && revit != null)
                foreach (var line in revit.ToTextLines()) text = AppendLine(text, line);
            if (checkLine != null) text = AppendLine(text, checkLine);
            if (annotationLine != null) text = AppendLine(text, annotationLine);
            if (run?.Line != null) text = AppendLine(text, run.Line);
            return stamp != null ? stamp.AppendToText(text) : text;
        }

        // ------------------------------------------------------------------ run group (plan 15)

        /// <summary>What a run group's decision turned into, for the group and the response.</summary>
        private sealed class RunOutcome
        {
            /// <summary>Assimilate the group (else roll it back). False until a decision says keep.</summary>
            public bool Keep;
            public RunDecision Decision = RunDecision.Nothing;
            public ChangeSetSummary Summary;
            /// <summary>The user's answer (Prompt only).</summary>
            public ApprovalLog.Entry Approval;
            public ApprovalRun.PromptResult Prompt;
            /// <summary>The result that replaces the command's own (READ_ONLY, QUERY_MODIFIED_MODEL, DISCARDED).</summary>
            public CommandResult Replace;
            /// <summary>Text mode: the whole response instead of the rendered result (READ_ONLY).</summary>
            public string TextOverride;
            /// <summary>Text mode: one extra line after the annotations line ("-- preview: …", "-- approval: …").</summary>
            public string Line;

            /// <summary>The additive JSON objects: "preview" for a rolled-back run, "approval" for a decision.</summary>
            public void AddJson(Dictionary<string, object> envelope)
            {
                if (Summary != null && (Decision == RunDecision.Preview || Decision == RunDecision.ReadOnly ||
                                        Decision == RunDecision.QueryModified))
                    envelope["preview"] = Summary.ToJson(rolledBack: true);
                if (Approval != null)
                    envelope["approval"] = ChangeApprovalPolicy.ApprovalJson(Approval.Id, Approval.Decision);
            }
        }

        /// <summary>
        /// Plan 15's decision for a finished run, while its group is still open: what to keep,
        /// what to show, what to say. For <see cref="RunDecision.Prompt"/> this shows the approval
        /// window (modal) and logs the answer.
        /// </summary>
        private static RunOutcome DecideRun(UIApplication app, Document doc, CommandRequest request, ChangeApprovalMode mode,
            bool guarded, RunOptions options, RecordedChanges changes, IDictionary<int, string> preRead,
            CommandResult cr, FailureReport revit, AnnotationOutcome annotations, bool batch)
        {
            var run = new RunOutcome();
            bool empty = changes == null || changes.IsEmpty;
            run.Decision = ChangeApprovalPolicy.Decide(mode, guarded, options.Preview, options.ReadOnly, empty);
            run.Summary = ApprovalRun.Summarize(doc, changes, preRead);
            switch (run.Decision)
            {
                case RunDecision.Keep:
                    run.Keep = true;
                    break;
                case RunDecision.Nothing:
                    break;
                case RunDecision.Preview:
                    // A failed command keeps its own error; there is nothing to preview.
                    if (cr == null || cr.Success) run.Line = run.Summary.PreviewLine();
                    break;
                case RunDecision.QueryModified:
                    run.Replace = CommandResult.Error(ChangeApprovalPolicy.QueryModifiedCode,
                        ChangeApprovalPolicy.QueryModifiedMessage(run.Summary, batch), ChangeApprovalPolicy.QueryModifiedHint);
                    break;
                case RunDecision.ReadOnly:
                    run.Replace = CommandResult.Error(ChangeApprovalPolicy.ReadOnlyCode,
                        ChangeApprovalPolicy.ReadOnlyMessage, ChangeApprovalPolicy.ReadOnlyHint);
                    run.TextOverride = "ERROR: " + ChangeApprovalPolicy.ReadOnlyMessage + "\n" + run.Summary.PreviewLine() +
                                       "\nHint: " + ChangeApprovalPolicy.ReadOnlyHint;
                    break;
                case RunDecision.Prompt:
                    var lines = new List<string>();
                    if (revit != null) lines.AddRange(revit.ToTextLines());
                    var annotationLine = annotations?.ToTextLine();
                    if (annotationLine != null) lines.Add(annotationLine);
                    var commandText = request.IsBatch
                        ? ProjectJournal.BatchText(BatchItems(request.Batch))
                        : request.Command + (string.IsNullOrWhiteSpace(request.Args) ? "" : " " + request.Args);
                    var question = (request.Turn ?? ProjectJournal.CurrentTurn)?.Question;
                    run.Prompt = ApprovalRun.Ask(app, doc, run.Summary, changes, commandText, question, lines,
                        commandFailed: cr != null && !cr.Success);
                    if (run.Prompt.DontAskAgain) ChangeApprovalPolicy.DontAskThisSession = true;
                    run.Approval = ApprovalLog.Add(DateTime.Now, commandText, run.Prompt.Decision,
                        ApprovalLog.ShortCounts(run.Summary), delivered: true);
                    request.AttachApproval(run.Approval.Id);
                    Logger.Info("Approval #" + run.Approval.Id + ": " + ApprovalLog.DecisionWord(run.Prompt.Decision) +
                                " by the user — " + commandText);
                    if (run.Prompt.Decision == ApprovalDecision.Kept)
                    {
                        run.Keep = true;
                        run.Line = ChangeApprovalPolicy.KeptLine(run.Approval.Id);
                    }
                    else
                    {
                        // A command that had failed half-way (an exec that changed something, then
                        // threw) keeps its own error in the message: the model needs both facts.
                        run.Replace = CommandResult.Error(ChangeApprovalPolicy.DiscardedCode,
                            ChangeApprovalPolicy.DiscardedMessageFor(cr != null && !cr.Success ? cr.RenderText() : null),
                            ChangeApprovalPolicy.DiscardedHint);
                    }
                    break;
            }
            return run;
        }

        /// <summary>Closes a run group: Assimilate (one undo entry) when kept, else RollBack. Never leaves it open.</summary>
        private static void EndRunGroup(TransactionGroup group, bool keep)
        {
            if (group == null) return;
            try
            {
                if (group.HasStarted() && !group.HasEnded())
                {
                    if (keep) group.Assimilate();
                    else group.RollBack();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Run group: closing failed — " + ex.Message);
                try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch { }
            }
            finally
            {
                group.Dispose();
            }
        }

        /// <summary>
        /// After the run group closed: the bookkeeping of a rollback, the selection the user had
        /// before the approval window, and the journal line of a discarded run.
        /// </summary>
        private static void FinishRun(UIApplication app, Document doc, CommandRequest request, RunOutcome run,
            RecordedChanges changes, long editsMark, string command, string args)
        {
            if (!run.Keep) ApprovalRun.AfterRollback(doc, changes, editsMark);
            if (run.Prompt != null) ApprovalRun.RestoreSelection(app, run.Prompt.PreviousSelection);
            if (run.Approval != null && run.Approval.Decision == ApprovalDecision.Discarded)
                JournalDiscarded(app, command, args, request.Turn);
        }

        /// <summary>Project journal: "… · delete 1730959 · discarded by the user · chat …".</summary>
        private static void JournalDiscarded(UIApplication app, string command, string args, ChatTurnRef turn)
        {
            try
            {
                if (!ProjectData.Enabled) return;
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return;
                var line = ProjectJournal.FormatCommandLine(DateTime.Now, command, args, null, null, null,
                    turn ?? ProjectJournal.CurrentTurn, null, ProjectJournal.DiscardedOutcome);
                ProjectJournal.Enqueue(doc.Title, doc.PathName, line);
            }
            catch (Exception ex)
            {
                Logger.Warn("Journal line skipped: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ annotation guard

        /// <summary>Never lets the guard break a command: a failure means "no snapshot".</summary>
        private static AnnotationSnapshot GuardCapture(Document doc, ICollection<int> targets)
        {
            try { return AnnotationGuard.Capture(doc, targets); }
            catch (Exception ex)
            {
                Logger.Warn("Annotation guard: capture failed — " + ex.Message);
                return null;
            }
        }

        /// <summary>Null when nothing happened (or the guard failed — logged, the command's result stands).</summary>
        private static AnnotationOutcome GuardReconcile(AnnotationSnapshot snapshot, RecordedChanges changes, FailureReport revit)
        {
            try
            {
                var outcome = AnnotationGuard.Reconcile(snapshot, changes, revit);
                return outcome == null || outcome.IsEmpty ? null : outcome;
            }
            catch (Exception ex)
            {
                Logger.Warn("Annotation guard: reconcile failed — " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Closes the guard's group: one undo step when anything was committed, else rolled back
        /// (an empty group would leave an empty undo entry). Never leaves it open — an open
        /// group locks the document.
        /// </summary>
        private static void EndGroup(TransactionGroup group, bool anyCommitted)
        {
            if (group == null) return;
            try
            {
                if (group.HasStarted() && !group.HasEnded())
                {
                    if (anyCommitted) group.Assimilate();
                    else group.RollBack();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Annotation guard: closing the group failed — " + ex.Message);
                try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch { }
            }
            finally
            {
                group.Dispose();
            }
        }

        private static string AppendLine(string text, string line)
        {
            if (string.IsNullOrEmpty(text)) return line + "\n";
            return (text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n") + line + "\n";
        }

        // Additive JSON: only the problems (OK rules are noise after a single command).
        private static Dictionary<string, object> CheckEnvelope(string summary, List<CheckFinding> findings)
        {
            return new Dictionary<string, object>
            {
                { "summary", summary },
                { "findings", CheckReport.ToJson(findings.FindAll(f => f.IsProblem)) }
            };
        }

        /// <summary>
        /// Builds the stamp on the Revit main thread (active view + selection must not be read
        /// from the HTTP thread). Never throws — a stamp failure must not break a command that
        /// would otherwise have succeeded; it just falls back to no stamp.
        /// <paramref name="includeApproval"/>: carry an approval decision no client received yet
        /// (plan 15) — false when this response itself will not be delivered (its client gave up).
        /// </summary>
        private static ModelStamp TryTakeStamp(UIApplication app, bool includeApproval = true)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null)
                    return null; // no document open — omit the stamp gracefully

                string viewName = null;
                try { viewName = uidoc.ActiveView?.Name; }
                catch { /* view mid-switch — stamp survives without a name */ }

                int selectedCount = 0;
                try { selectedCount = uidoc.Selection.GetElementIds().Count; }
                catch { /* selection unavailable — 0 is a safe default */ }

                // The chat pane's starter chips read the same facts.
                ActiveDocumentTracker.UpdateView(viewName, uidoc.ActiveView is ViewSheet, selectedCount);

                return ModelChangeTracker.TakeStamp(
                    ModelChangeTracker.DocKey(doc.PathName, doc.Title), viewName, selectedCount,
                    includeApproval ? ApprovalLog.TakeUndelivered() : null);
            }
            catch (Exception ex)
            {
                Logger.Warn("Model-state stamp unavailable: " + ex.Message);
                return null;
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
            bool json = req.Format == ResponseFormat.Json;

            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return json
                    ? _json.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", new Dictionary<string, object>
                                {
                                    { "code", "NO_DOCUMENT" },
                                    { "message", "No document open" },
                                    { "suggestion", "Open a Revit project before running commands." }
                                }
                            }
                        })
                    : "ERROR: No document open";

            var sb = new StringBuilder();          // text form (byte-identical)
            var entries = new List<object>();      // json form (per-command results)
            bool anyModified = false, anyError = false, rolledBack = false;

            // Annotations on what the batch replaces: recorded now, re-hosted before the group
            // is assimilated (same undo step); skipped when an atomic batch rolls back.
            var batchItems = BatchItems(req.Batch).ToList();
            var snapshot = AnnotationScope.AppliesToAny(batchItems.Select(i => i.Key))
                ? GuardCapture(doc, AnnotationScope.TargetIds(batchItems)) : null;
            var batchRevit = new FailureReport();
            AnnotationOutcome annotations = null;

            // Plan 15: the batch group doubles as the run group when the policy or a flag asks.
            var options = req.Options ?? RunOptions.None;
            var mode = ChangeApprovalPolicy.EffectiveMode;
            bool guarded = ChangeApprovalPolicy.IsGuardedBatch(batchItems.Select(i => i.Key));
            bool batchModifies = req.Batch.Any(i => _registry.IsModification(i.Command));
            bool runControl = ChangeApprovalPolicy.NeedsRunGroup(mode, guarded, batchModifies, options.Preview, options.ReadOnly);
            var preRead = runControl ? ApprovalRun.PreRead(doc, AnnotationScope.TargetIds(batchItems) ?? new List<int>()) : null;
            long editsMark = runControl ? ApprovalRun.EditsMark(doc) : 0;
            var run = new RunOutcome();

            RecordedChanges changes = null;
            using (var tg = new TransactionGroup(doc, BuildGroupName(req.Batch)))
            {
                tg.Start();
                CreatedElementsRecorder.Arm();
                RunContext.InRunGroup = runControl;
                try
                {
                    foreach (var item in req.Batch)
                    {
                        // ExecuteCore returns the structured result; decide on .Success, not a
                        // string prefix. Text output stays byte-identical via RenderText(), plus
                        // "-- revit" lines only when Revit's failure processing did something.
                        var cr = FailureCollector.Collect(() => _registry.ExecuteCore(item.Command, item.Args, app), out var revit);
                        batchRevit.Merge(revit);

                        sb.AppendLine(">>> " + item.Command +
                                      (string.IsNullOrEmpty(item.Args) ? "" : " " + item.Args));
                        sb.AppendLine(cr.RenderText());
                        if (cr.Success)
                            foreach (var line in revit.ToTextLines()) sb.AppendLine(line);
                        sb.AppendLine();

                        var entry = new Dictionary<string, object>
                        {
                            { "command", item.Command },
                            { "args", item.Args },
                            { "result", cr.ToJsonObject() }
                        };
                        if (!revit.IsEmpty) entry["revit"] = revit.ToJson();
                        entries.Add(entry);

                        if (!cr.Success) anyError = true;
                        else if (_registry.IsModification(item.Command)) anyModified = true;
                    }

                    if (req.Atomic && anyError)
                    {
                        tg.RollBack();
                        rolledBack = true;
                        sb.AppendLine("[atomic] rolled back — a command failed.");
                    }
                    else if (runControl)
                    {
                        if (anyModified)
                        {
                            changes = CreatedElementsRecorder.DisarmChanges();
                            if (snapshot != null) annotations = GuardReconcile(snapshot, changes, batchRevit);
                        }
                        // Nothing modified (every command failed or only read): an empty change set.
                        run = DecideRun(app, doc, req, mode, guarded, options, anyModified ? changes : null, preRead,
                            null, batchRevit, annotations, batch: true);
                        if (run.Keep) tg.Assimilate();
                        else
                        {
                            tg.RollBack();
                            rolledBack = run.Decision != RunDecision.Nothing;
                        }
                    }
                    else if (anyModified)
                    {
                        if (snapshot != null)
                        {
                            changes = CreatedElementsRecorder.DisarmChanges();
                            annotations = GuardReconcile(snapshot, changes, batchRevit);
                        }
                        tg.Assimilate(); // merge all committed child transactions into one undo entry
                    }
                    else
                    {
                        tg.RollBack(); // read-only / nothing committed — avoid an empty undo entry
                    }
                }
                catch
                {
                    if (tg.HasStarted() && !tg.HasEnded()) tg.RollBack(); // safety: never leave the group open
                    throw;
                }
                finally
                {
                    RunContext.InRunGroup = false;
                    if (CreatedElementsRecorder.IsArmed)
                    {
                        var rest = CreatedElementsRecorder.DisarmChanges();
                        if (changes == null) changes = rest;
                    }
                }
            }

            if (runControl)
            {
                FinishRun(app, doc, req, run, changes, editsMark, ProjectJournal.BatchText(batchItems), null);
                if (!run.Keep) anyModified = false; // nothing kept: no check, no journal line
                var line = BatchRunLine(run);
                if (line != null) sb.AppendLine(line);
            }

            // Office rules over everything the batch created (nothing when rolled back).
            List<CheckFinding> findings = anyModified && !rolledBack ? PostCommandCheck.Run(app, changes.Added) : null;
            if (anyModified && !rolledBack)
                Journal(app, ProjectJournal.BatchText(BatchItems(req.Batch)), null, changes, req.Turn);
            var checkLine = findings != null ? CheckReport.SummaryLine(findings) : null;
            if (checkLine != null) sb.AppendLine(checkLine);
            var annotationLine = annotations?.ToTextLine();
            if (annotationLine != null) sb.AppendLine(annotationLine);
            if (run.Keep && run.Line != null) sb.AppendLine(run.Line);

            // One stamp for the whole batch, never per sub-command.
            var stamp = TryTakeStamp(app, !req.IsCancelled);

            if (json)
            {
                var envelope = new Dictionary<string, object>
                {
                    { "ok", !anyError && run.Replace == null },
                    { "atomic", req.Atomic },
                    { "rolledBack", rolledBack },
                    { "results", entries }
                };
                if (stamp != null)
                    envelope["meta"] = stamp.ToMeta();
                if (checkLine != null)
                    envelope["check"] = CheckEnvelope(checkLine, findings);
                if (annotationLine != null)
                    envelope["annotations"] = annotations.ToJson();
                run.AddJson(envelope);
                // Additive: why a run-controlled batch kept nothing (READ_ONLY, QUERY_MODIFIED_MODEL, DISCARDED).
                if (run.Replace != null && run.Replace.ToJsonObject() is Dictionary<string, object> replaced &&
                    replaced.TryGetValue("error", out var error))
                    envelope["error"] = error;
                return _json.Serialize(envelope);
            }

            return stamp != null ? stamp.AppendToText(sb.ToString()) : sb.ToString();
        }

        /// <summary>
        /// The batch's plan-15 line after its per-command results: why it kept nothing, or the
        /// preview. The kept line ("-- approval: kept by the user (#7)") goes after the check and
        /// annotations lines, like a single command's.
        /// </summary>
        private static string BatchRunLine(RunOutcome run)
        {
            switch (run.Decision)
            {
                case RunDecision.Preview:
                    return run.Summary.PreviewLine();
                case RunDecision.ReadOnly:
                    return "[read-only] rolled back — " + ChangeApprovalPolicy.ReadOnlyMessage + "\n" +
                           run.Summary.PreviewLine() + "\nHint: " + ChangeApprovalPolicy.ReadOnlyHint;
                case RunDecision.QueryModified:
                    return "[readonly] rolled back — " + run.Replace.Message + "\nHint: " + ChangeApprovalPolicy.QueryModifiedHint;
                case RunDecision.Prompt:
                    if (run.Approval != null && run.Approval.Decision == ApprovalDecision.Discarded)
                        return "[approval] rolled back — " + ChangeApprovalPolicy.DiscardedMessage + " (#" + run.Approval.Id + ")" +
                               "\nHint: " + ChangeApprovalPolicy.DiscardedHint;
                    return null;
                default:
                    return null;
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> BatchItems(IReadOnlyList<BatchCommand> batch)
        {
            foreach (var item in batch) yield return new KeyValuePair<string, string>(item.Command, item.Args);
        }

        /// <summary>
        /// Project journal: one line for a modifying command / batch that changed the model.
        /// Only formats and queues here — the background writer touches the (maybe network)
        /// folder. A command that changed nothing (an exec that only read) is not journaled.
        /// Attributed to the chat turn of its X-VibeModel-Turn header, else to the chat turn
        /// running in this session (the Claude Code CLI's curls carry no header).
        /// </summary>
        private static void Journal(UIApplication app, string command, string args, RecordedChanges changes, ChatTurnRef turn, IList<string> written = null)
        {
            try
            {
                if (changes == null || changes.IsEmpty || !ProjectData.Enabled) return;
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return;
                var line = ProjectJournal.FormatCommandLine(DateTime.Now, command, args,
                    RecordedChanges.Values(changes.Added), RecordedChanges.Values(changes.Modified),
                    RecordedChanges.Values(changes.Deleted), turn ?? ProjectJournal.CurrentTurn, written);
                ProjectJournal.Enqueue(doc.Title, doc.PathName, line);
            }
            catch (Exception ex)
            {
                Logger.Warn("Journal line skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Project journal: one line for a command that changed no element but wrote files
        /// (export), naming the files. Nothing is journaled when it wrote none.
        /// </summary>
        private static void JournalFiles(UIApplication app, string command, string args, IJournaledCommand journaled, CommandResult result, ChatTurnRef turn)
        {
            try
            {
                if (!ProjectData.Enabled) return;
                var files = journaled.JournalFiles(result);
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || files == null || files.Count == 0) return;
                var line = ProjectJournal.FormatCommandLine(DateTime.Now, command, args, null, null, null,
                    turn ?? ProjectJournal.CurrentTurn, files);
                ProjectJournal.Enqueue(doc.Title, doc.PathName, line);
            }
            catch (Exception ex)
            {
                Logger.Warn("Journal line skipped: " + ex.Message);
            }
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
    /// Per-request run options from the query string (plan 15): <c>?preview=1</c> runs the
    /// command and rolls everything back, reporting what would change; <c>?readonly=1</c> fails a
    /// snippet or batch that changed the model (rolled back, QUERY_MODIFIED_MODEL).
    /// </summary>
    public sealed class RunOptions
    {
        public static readonly RunOptions None = new RunOptions();

        public bool Preview { get; set; }
        public bool ReadOnly { get; set; }

        /// <summary>?timeout=&lt;s&gt; (plan 17): exec's loop-check budget; null = the default 25 s.</summary>
        public int? TimeoutSeconds { get; set; }

        public bool IsEmpty => !Preview && !ReadOnly && !TimeoutSeconds.HasValue;
    }

    /// <summary>
    /// The options of the request running on the main thread right now (plan 17: exec reads its
    /// budget here). Set by <see cref="RevitCommandHandler"/> around each request; null outside.
    /// </summary>
    public static class RunContext
    {
        public static RunOptions Current { get; internal set; }

        /// <summary>
        /// True while the command runs inside a plan-15 run group (preview, read-only, approval):
        /// saving, closing or synchronizing is refused by Revit there, and exec explains why.
        /// </summary>
        public static bool InRunGroup { get; internal set; }
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
        public ResponseFormat Format { get; }

        // Batch payload — null for single commands.
        public IReadOnlyList<BatchCommand> Batch { get; }
        public bool Atomic { get; }
        public bool IsBatch => Batch != null;

        /// <summary>The chat turn that sent it (X-VibeModel-Turn header); null for other clients.</summary>
        public ChatTurnRef Turn { get; set; }

        /// <summary>?preview=1 / ?readonly=1 (plan 15); never null.</summary>
        public RunOptions Options { get; set; } = RunOptions.None;

        public string Result { get; set; }
        public ManualResetEventSlim ResponseReady { get; } = new ManualResetEventSlim(false);

        private volatile bool _cancelled;
        public bool IsCancelled => _cancelled;

        // Plan 15: the approval decision of this run, and who knows about it. The main thread
        // attaches it, the HTTP thread may give up on the request at the same moment; under one
        // lock exactly one of them marks the decision undelivered when the client never got it.
        private readonly object _approvalSync = new object();
        private int _approvalId;

        /// <summary>Main thread: this run's decision is logged; undelivered when the client already gave up.</summary>
        public void AttachApproval(int id)
        {
            lock (_approvalSync)
            {
                _approvalId = id;
                if (_cancelled) ApprovalLog.MarkUndelivered(id);
            }
        }

        /// <summary>HTTP thread: the wait timed out — the result (and any decision in it) will not reach the client.</summary>
        public void CancelAfterTimeout()
        {
            lock (_approvalSync)
            {
                _cancelled = true;
                if (_approvalId > 0) ApprovalLog.MarkUndelivered(_approvalId);
            }
        }

        public CommandRequest(string command, string args, ResponseFormat format = ResponseFormat.Text)
        {
            Command = command;
            Args = args;
            Format = format;
        }

        public CommandRequest(IReadOnlyList<BatchCommand> batch, bool atomic,
            ResponseFormat format = ResponseFormat.Text)
        {
            Batch = batch;
            Atomic = atomic;
            Format = format;
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
