using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Chat
{
    public class ClaudeCodeBackend : ChatBackendBase
    {
        private const string NotFoundMessage =
            "Claude Code not found. Install with: npm install -g @anthropic-ai/claude-code";

        private const int DetectTimeoutMs = 2000;
        // Pre-flight /health probe before spawning the CLI: fail fast with a clear
        // message instead of letting the model burn minutes on failed curl calls.
        private const int HealthCheckTimeoutMs = 2000;
        // Idle timeout: kill the process if no NDJSON line is read for this long.
        // Reset on every non-empty stdout line so streaming tasks aren't cut off.
        private const int ProcessTimeoutMs = 180000;

        private readonly string _systemPromptPath;
        // ClaudeCode drives a child process, so it has its own lock and does NOT use the
        // base CTS lifecycle. It still uses the inherited _isSending flag (under _processLock).
        private readonly object _processLock = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private string _claudePath;
        private string _sessionId;
        private Process _currentProcess;

        // Set by the idle-timer callback (other thread) -> must be volatile.
        // _lastBlockWasToolUse is touched only on the read-loop thread, no volatile needed.
        private volatile bool _killedByIdleTimeout;
        private bool _lastBlockWasToolUse;

        private bool _isAvailable;
        private string _statusMessage;
        public override bool IsAvailable => _isAvailable;
        public override string StatusMessage => _statusMessage;

        public ClaudeCodeBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
            : base(commands, httpPort)
        {
            // Per-port filename: multiple Revit instances (fallback ports 18885+)
            // can never clobber each other's prompt file.
            _systemPromptPath = GetSystemPromptPath(httpPort);

            DetectClaude();
            CleanUpLegacyPromptFile();
            WriteSystemPrompt();
        }

        internal static string GetSystemPromptPath(int httpPort)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel",
                "system-prompt-" + httpPort + ".txt");
        }

        // Older builds wrote a single shared system-prompt.txt that a second Revit
        // instance could overwrite with its own (possibly fallback) port and leave
        // stale after closing. Delete it so no session ever reads the poisoned copy.
        private static void CleanUpLegacyPromptFile()
        {
            try
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VibeModel",
                    "system-prompt.txt");
                if (File.Exists(legacy))
                    File.Delete(legacy);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private void DetectClaude()
        {
            var candidates = new List<string>();

            // Bare name — resolved via PATH
            candidates.Add("claude");

            // Common install locations
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));
            candidates.Add(Path.Combine(appData, "npm", "claude"));
            candidates.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));
            candidates.Add(Path.Combine(userProfile, ".claude", "local", "claude.exe"));

            foreach (var candidate in candidates)
            {
                // Skip file-path candidates that don't exist on disk
                if (candidate != "claude" && !File.Exists(candidate))
                    continue;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        var version = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(DetectTimeoutMs);
                        if (proc.ExitCode == 0)
                        {
                            _claudePath = candidate;
                            _isAvailable = true;
                            _statusMessage = "Claude Code " + version;
                            Logger.Info("Claude Code found at: " + candidate + " (" + version + ")");
                            return;
                        }
                    }
                }
                catch
                {
                    // Not found at this location — try next
                }
            }

            _isAvailable = false;
            _statusMessage = NotFoundMessage;
            Logger.Warn("Claude Code CLI not found in any known location");
        }

        // Rewritten before every message (not just at startup) so the file always
        // matches THIS instance's live server, even if another instance started later.
        private void WriteSystemPrompt()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_systemPromptPath));
                File.WriteAllText(_systemPromptPath,
                    BuildSystemPromptText(_commands, _httpPort, _authToken), Encoding.UTF8);
                Logger.Info("System prompt written to: " + _systemPromptPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to write system prompt", ex);
            }
        }

        internal static string BuildSystemPromptText(
            IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort, string authToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are inside Autodesk Revit via the VibeModel add-in.");
            sb.AppendLine("You can control Revit by running curl commands against the embedded HTTP server.");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: Always use bash with curl to execute commands. The server is at http://" + RevitHttpServer.LoopbackHost + ":" + httpPort);
            sb.AppendLine();
            if (authToken != null)
            {
                sb.AppendLine("AUTH: This server requires a token. Add this header to EVERY curl call:");
                sb.AppendLine("  -H \"" + RevitHttpServer.TokenHeader + ": $" + RevitHttpServer.TokenEnvVar + "\"");
                sb.AppendLine("The " + RevitHttpServer.TokenEnvVar + " environment variable is already set in your shell. Requests without this header return 401.");
                sb.AppendLine();
            }
            sb.AppendLine("Available commands:");
            sb.AppendLine();

            foreach (var cmd in commands.Values.OrderBy(c => c.Name))
            {
                sb.AppendLine("  " + cmd.Name + " - " + cmd.Description);
                if (!string.IsNullOrEmpty(cmd.Usage))
                    sb.AppendLine("    Usage: curl -s \"http://" + RevitHttpServer.LoopbackHost + ":" + httpPort + "/" + cmd.Usage + "\"");
                else
                    sb.AppendLine("    Usage: curl -s http://" + RevitHttpServer.LoopbackHost + ":" + httpPort + "/" + cmd.Name);
            }

            sb.AppendLine();
            sb.AppendLine("Tips:");
            sb.AppendLine("- All dimensions are in millimeters (mm)");
            sb.AppendLine("- Use /batch with POST for multiple commands: curl -s -X POST http://" + RevitHttpServer.LoopbackHost + ":" + httpPort + "/batch -d \"command1 args\\ncommand2 args\"");
            sb.AppendLine("- Always check /info first to understand the current document");
            sb.AppendLine("- Use /selected to inspect what the user has selected");
            sb.AppendLine("- You can SEE the model: run /screenshot, then Read the returned Path (PNG). Decide on your own when looking helps — after changing visible geometry to verify it, when asked how something looks, or when a layout decision needs visual context. Skip it for pure data queries or when nothing changed visually.");
            sb.AppendLine("- For complex operations, use /exec to run arbitrary C# code against the Revit API");
            sb.AppendLine();
            sb.AppendLine("BEHAVIOR:");
            sb.AppendLine("- You are a senior Revit modeling assistant. Speak in clear, professional language using standard AEC/BIM terminology.");
            sb.AppendLine("- Keep responses concise — prefer short, direct answers with exact values, element IDs, and parameter names.");
            sb.AppendLine("- Adapt your detail level to the user: give brief answers to experienced users, add context when a question suggests less familiarity.");
            sb.AppendLine("- When you complete a task, suggest 1-2 logical next steps based on the current model context. Keep suggestions brief and at the end of your response.");
            sb.AppendLine("- Always confirm destructive operations (delete, overwrite) before executing.");
            sb.AppendLine("- When a task involves multiple steps, outline the full sequence up front so the user can approve or adjust before you proceed.");
            sb.AppendLine("- Prefer concrete Revit operations over abstract explanations. If a question can be answered by querying the model, query it rather than speculating.");
            sb.AppendLine("- When something fails, explain what went wrong in plain terms and suggest an alternative approach.");
            sb.AppendLine("- Stay within the boundaries of what VibeModel commands can do. If a request falls outside available commands, say so honestly and suggest a workaround.");

            return sb.ToString();
        }

        public override void SendMessage(
            string prompt,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable)
            {
                onError(StatusMessage);
                return;
            }

            // Attachments are already persisted on disk (AttachmentStore); the CLI's
            // allowed tools include Read, which handles PDFs and images natively, so
            // pointing it at the paths is all that's needed. Session resume keeps the
            // read content in the CLI's own history afterwards.
            prompt = BuildPromptWithAttachments(prompt, attachments);

            lock (_processLock)
            {
                if (_isSending)
                {
                    onError("A message is already being processed. Please wait or cancel first.");
                    return;
                }
                _isSending = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RunClaudeProcess(prompt, onToken, onComplete, onError, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    onComplete("[Cancelled]");
                }
                catch (Exception ex)
                {
                    Logger.Error("Claude process error", ex);
                    onError(ex.Message);
                }
                finally
                {
                    lock (_processLock)
                    {
                        _isSending = false;
                    }
                }
            });
        }

        /// <summary>
        /// Prepends attachment file paths to the prompt so the CLI reads them itself.
        /// Null/empty attachments return the prompt unchanged.
        /// </summary>
        internal static string BuildPromptWithAttachments(
            string prompt, IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return prompt;

            var sb = new StringBuilder();
            sb.AppendLine("The user attached the following file(s) — read them with the Read tool before answering:");
            foreach (var a in attachments)
                sb.AppendLine(a.StoredPath);
            sb.AppendLine();
            sb.Append(prompt);
            return sb.ToString();
        }

        private void RunClaudeProcess(
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken,
            bool isResumeRetry = false)
        {
            // Per-request state reset - must happen before each invocation so a
            // previous turn's flags never leak into this one (e.g., wrongly clearing
            // _sessionId on a clean turn because the prior turn was killed mid-tool).
            _killedByIdleTimeout = false;
            _lastBlockWasToolUse = false;

            // Pre-flight: if our own server is down, surface a clear message instead of
            // spawning the CLI and letting the model flail on connection-refused curls.
            if (!IsServerHealthy())
            {
                onError("VibeModel's HTTP server isn't responding on port " + _httpPort +
                        ". Restart Revit (or check whether another program is blocking the port), then try again.");
                return;
            }

            // Refresh the prompt file every turn so it always matches this server's
            // live port and auth state — never a stale copy from an earlier instance.
            WriteSystemPrompt();

            // Capture whether this invocation tries to resume - used after WaitForExit
            // to detect stale-session failures and retry without --resume.
            bool resumeAttempted = !string.IsNullOrEmpty(_sessionId);

            var args = new StringBuilder();
            args.Append("-p ");
            args.Append(EscapeArg(prompt));
            args.Append(" --output-format stream-json --verbose --allowedTools Bash,Read");

            if (resumeAttempted)
            {
                args.Append(" --resume ");
                args.Append(EscapeArg(_sessionId));
            }

            // Gather live model context before each invocation
            var context = GatherModelContext();

            var appendPrompt = new StringBuilder();
            appendPrompt.Append("You are inside Revit. Read " + _systemPromptPath +
                " for available commands. Always use curl to interact with Revit. " +
                "After a visual change, run /screenshot and Read the returned PNG path to verify the result.");
            appendPrompt.AppendLine();
            // Heals resumed sessions whose history mentions a dead port (e.g. after a
            // fallback-port instance closed): the live port always wins.
            appendPrompt.Append(BuildPortDirective(_httpPort));

            if (!string.IsNullOrEmpty(context))
            {
                appendPrompt.AppendLine();
                appendPrompt.AppendLine();
                appendPrompt.AppendLine(context);
                appendPrompt.AppendLine();
                appendPrompt.AppendLine("IMPORTANT: Be proactive. Based on the context above, suggest 2-3 specific things");
                appendPrompt.AppendLine("you can help with. If the user has elements selected, comment on them.");
                appendPrompt.Append("Keep suggestions relevant to what's in the model. Use plain language.");
            }

            args.Append(" --append-system-prompt ");
            args.Append(EscapeArg(appendPrompt.ToString()));

            var psi = new ProcessStartInfo
            {
                FileName = _claudePath,
                Arguments = args.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Logger.Info("Starting claude: " + _claudePath + " " + args);

            Process process;
            lock (_processLock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onComplete("[Cancelled]");
                    return;
                }

                process = Process.Start(psi);
                _currentProcess = process;
            }

            if (process == null)
            {
                onError("Failed to start Claude Code process");
                return;
            }

            var registration = cancellationToken.Register(() => KillProcess(process));

            try
            {
                var fullResponse = new StringBuilder();

                // Read stderr asynchronously to prevent deadlock:
                // If we read stdout first and stderr buffer fills, the process blocks
                // on stderr writes, stdout never closes, and ReadLine never returns.
                var stderrBuilder = new StringBuilder();
                process.ErrorDataReceived += (s, ea) =>
                {
                    if (ea.Data != null)
                        stderrBuilder.AppendLine(ea.Data);
                };
                process.BeginErrorReadLine();

                var timeoutTimer = new Timer(_ =>
                {
                    _killedByIdleTimeout = true;
                    Logger.Warn("Claude process killed: idle timeout (" + ProcessTimeoutMs + " ms) exceeded");
                    KillProcess(process);
                }, null, ProcessTimeoutMs, Timeout.Infinite);

                try
                {
                    bool lastWasText = false;
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        // Reset idle timer on every non-empty line - streaming tasks
                        // with continuous tool calls should never be cut off.
                        timeoutTimer.Change(ProcessTimeoutMs, Timeout.Infinite);

                        ProcessNdjsonLine(line, onToken, fullResponse, ref lastWasText);
                    }
                }
                finally
                {
                    timeoutTimer.Dispose();
                }

                process.WaitForExit(5000);

                // Post-WaitForExit ordered branches (see plan Change 5).
                // Order is load-bearing: idle-kill notice must populate fullResponse
                // before the stale-resume check evaluates Length == 0; stale-resume
                // must intercept before the existing exit-code error path.

                // 1. Cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    onComplete("[Cancelled]");
                    return;
                }

                // 2. Idle-kill notice - emit via BOTH onToken (so the streaming chat
                //    bubble shows it; ChatPane prefers _streamingContent over fullText)
                //    AND fullResponse (so chat history captures it).
                if (_killedByIdleTimeout)
                {
                    string notice;
                    if (_lastBlockWasToolUse)
                    {
                        notice = "\n\n[Stopped: task went idle for 3 min mid-tool. " +
                                 "Session was reset to avoid a broken transcript - " +
                                 "please rephrase your request.]";
                        _sessionId = null;
                    }
                    else
                    {
                        notice = "\n\n[Stopped: task went idle for 3 min. " +
                                 "Say \"continue\" to resume.]";
                    }
                    onToken(notice);
                    fullResponse.Append(notice);
                }

                var stderr = stderrBuilder.ToString().Trim();

                // 3. Stale-resume retry - silent failure of a --resume invocation
                //    means the session ID is no longer valid in the CLI's store.
                //    Clear it and retry once as a fresh chat.
                if (resumeAttempted && !isResumeRetry
                    && process.ExitCode != 0 && fullResponse.Length == 0)
                {
                    Logger.Warn("Resume failed (exit " + process.ExitCode +
                                "), retrying as fresh chat. stderr: " + stderr);
                    _sessionId = null;
                    var prefix = "[Resumed session was stale - starting fresh chat.]\n\n";
                    onToken(prefix);
                    RunClaudeProcess(prompt, onToken, onComplete, onError,
                        cancellationToken, isResumeRetry: true);
                    return;
                }

                // 4. Exit-code error (no output at all)
                if (process.ExitCode != 0 && fullResponse.Length == 0)
                {
                    onError(!string.IsNullOrEmpty(stderr)
                        ? stderr
                        : "Claude Code exited with code " + process.ExitCode);
                    return;
                }

                // 5. Normal completion
                onComplete(fullResponse.ToString());
            }
            finally
            {
                registration.Dispose();
                lock (_processLock)
                {
                    if (_currentProcess == process)
                        _currentProcess = null;
                }
                try { process.Dispose(); }
                catch { }
            }
        }

        private void ProcessNdjsonLine(string line, Action<string> onToken, StringBuilder fullResponse, ref bool lastWasText)
        {
            try
            {
                var obj = _json.DeserializeObject(line) as Dictionary<string, object>;
                if (obj == null)
                    return;

                object typeObj;
                if (!obj.TryGetValue("type", out typeObj))
                    return;

                var type = typeObj as string;

                if (type == "system")
                {
                    // Capture session_id from the FIRST event of the stream so we keep
                    // it even if the process is killed before emitting `result`.
                    object sysSessionObj;
                    if (obj.TryGetValue("session_id", out sysSessionObj) && sysSessionObj is string sysSid
                        && !string.IsNullOrEmpty(sysSid) && sysSid != _sessionId)
                    {
                        _sessionId = sysSid;
                        Logger.Info("Session ID (from system init): " + _sessionId);
                    }
                    return;
                }

                if (type == "result")
                {
                    lastWasText = false;
                    object sessionObj;
                    if (obj.TryGetValue("session_id", out sessionObj) && sessionObj is string sid)
                    {
                        _sessionId = sid;
                        Logger.Info("Session ID: " + _sessionId);
                    }
                    return;
                }

                if (type == "user")
                {
                    // User events carry tool_result blocks back from the CLI.
                    // Seeing one means the assistant's most recent tool_use has been
                    // answered, so the transcript is no longer "mid-tool".
                    object userMsgObj;
                    if (!obj.TryGetValue("message", out userMsgObj))
                        return;
                    var userMsg = userMsgObj as Dictionary<string, object>;
                    if (userMsg == null)
                        return;
                    object userContentObj;
                    if (!userMsg.TryGetValue("content", out userContentObj))
                        return;
                    var userContentArray = userContentObj as object[]
                        ?? (userContentObj as System.Collections.ArrayList)?.ToArray();
                    if (userContentArray == null)
                        return;

                    foreach (var item in userContentArray)
                    {
                        if (item is Dictionary<string, object> block)
                        {
                            object blockType;
                            if (block.TryGetValue("type", out blockType)
                                && (string)blockType == "tool_result")
                            {
                                _lastBlockWasToolUse = false;
                                break;
                            }
                        }
                    }
                    return;
                }

                if (type == "content_block_delta")
                {
                    object deltaObj;
                    if (obj.TryGetValue("delta", out deltaObj) && deltaObj is Dictionary<string, object> delta)
                    {
                        object deltaType;
                        if (delta.TryGetValue("type", out deltaType) && (string)deltaType == "text_delta")
                        {
                            object textObj;
                            if (delta.TryGetValue("text", out textObj) && textObj is string text)
                            {
                                // Insert paragraph break between separate response blocks
                                // (e.g., after tool use: "Let me read..." → tool → "You're looking at...")
                                if (!lastWasText && fullResponse.Length > 0)
                                {
                                    fullResponse.Append("\n\n");
                                    onToken("\n\n");
                                }
                                lastWasText = true;
                                fullResponse.Append(text);
                                onToken(text);
                            }
                        }
                    }
                    return;
                }

                if (type == "assistant")
                {
                    // --verbose format: { "message": { "content": [...] } }
                    // Reset text tracking — new assistant message means a new response block
                    lastWasText = false;

                    object messageObj;
                    if (!obj.TryGetValue("message", out messageObj))
                        return;
                    var message = messageObj as Dictionary<string, object>;
                    if (message == null)
                        return;
                    object contentObj;
                    if (!message.TryGetValue("content", out contentObj))
                        return;
                    var contentArray = contentObj as object[]
                        ?? (contentObj as System.Collections.ArrayList)?.ToArray();
                    if (contentArray == null)
                        return;

                    string lastBlockType = null;
                    foreach (var item in contentArray)
                    {
                        if (item is Dictionary<string, object> block)
                        {
                            string currentBlockType = null;
                            object blockType;
                            if (block.TryGetValue("type", out blockType))
                                currentBlockType = blockType as string;
                            lastBlockType = currentBlockType;

                            if (currentBlockType == "text")
                            {
                                object textObj;
                                if (block.TryGetValue("text", out textObj) && textObj is string text)
                                {
                                    if (fullResponse.Length > 0)
                                    {
                                        fullResponse.Append("\n\n");
                                        onToken("\n\n");
                                    }
                                    fullResponse.Append(text);
                                    onToken(text);
                                    lastWasText = true;
                                }
                            }
                        }
                    }

                    // After processing all blocks: if the final block is a tool_use,
                    // we are now "awaiting a tool_result". An idle kill in this state
                    // would orphan the tool_use and break --resume.
                    _lastBlockWasToolUse = (lastBlockType == "tool_use");
                    return;
                }

                // Any non-text event (tool_use, tool_result, message_stop, etc.)
                // resets the text tracking so the next text block gets a separator
                lastWasText = false;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to parse NDJSON line: " + line, ex);
            }
        }

        internal static string BuildPortDirective(int httpPort)
        {
            return "The VibeModel server is at http://" + RevitHttpServer.LoopbackHost + ":" + httpPort +
                ". This is the ONLY correct port right now — if this conversation or any file " +
                "mentions a different port, ignore it and use " + httpPort + ".";
        }

        // GET /health with a short timeout. /health is unauthenticated by design,
        // so this works whether or not VIBEMODEL_TOKEN is set.
        //
        // Dial 127.0.0.1, never "localhost": the server binds IPAddress.Loopback (IPv4
        // only), and Windows resolves "localhost" to ::1 first. If that IPv6 attempt is
        // dropped rather than refused — seen with corporate endpoint protection — the
        // probe eats its full timeout and the chat pane tells the user the server is
        // down while it is in fact answering IPv4 in single-digit milliseconds.
        private bool IsServerHealthy()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(
                    "http://" + RevitHttpServer.LoopbackHost + ":" + _httpPort + "/health");
                request.Timeout = HealthCheckTimeoutMs;
                request.ReadWriteTimeout = HealthCheckTimeoutMs;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Pre-flight health check failed on port " + _httpPort + ": " + ex.Message);
                return false;
            }
        }

        private void KillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }
        }

        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "\"\"";

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\')
                {
                    int numBackslashes = 0;
                    while (i < arg.Length && arg[i] == '\\')
                    {
                        numBackslashes++;
                        i++;
                    }
                    if (i == arg.Length)
                    {
                        sb.Append('\\', numBackslashes * 2);
                    }
                    else if (arg[i] == '"')
                    {
                        sb.Append('\\', numBackslashes * 2 + 1);
                        sb.Append('"');
                    }
                    else
                    {
                        sb.Append('\\', numBackslashes);
                        sb.Append(arg[i]);
                    }
                }
                else if (c == '"')
                {
                    sb.Append('\\');
                    sb.Append('"');
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Override: ClaudeCode kills the child process instead of cancelling a CTS.
        // Base Dispose calls this (and finds no _httpClient), so no Dispose override is needed.
        public override void Cancel()
        {
            lock (_processLock)
            {
                if (_currentProcess != null)
                    KillProcess(_currentProcess);
            }
        }

        public override void ResetSession()
        {
            _sessionId = null;
            Logger.Info("Chat session reset");
        }

        // The CLI's context lives in its own --resume session; a saved transcript
        // cannot be replayed into it. Start fresh — the pane shows the transcript
        // read-only and new turns begin a new CLI session.
        public override void RestoreHistory(System.Collections.Generic.IEnumerable<ChatSessionMessage> messages)
        {
            ResetSession();
            Logger.Info("ClaudeCode: transcript restored for display only; CLI session starts fresh");
        }
    }
}
