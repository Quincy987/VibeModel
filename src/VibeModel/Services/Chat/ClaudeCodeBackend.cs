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
    public class ClaudeCodeBackend : IChatBackend
    {
        private const string NotFoundMessage =
            "Claude Code not found. Install with: npm install -g @anthropic-ai/claude-code";

        private const int DetectTimeoutMs = 2000;
        private const int ProcessTimeoutMs = 120000;

        private readonly int _httpPort;
        private readonly string _systemPromptPath;
        private readonly object _processLock = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private string _claudePath;
        private string _sessionId;
        private Process _currentProcess;
        private bool _isSending;
        private bool _disposed;

        public bool IsAvailable { get; private set; }
        public string StatusMessage { get; private set; }

        public ClaudeCodeBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            _httpPort = httpPort;
            _systemPromptPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel",
                "system-prompt.txt");

            DetectClaude();
            WriteSystemPrompt(commands);
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
                            IsAvailable = true;
                            StatusMessage = "Claude Code " + version;
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

            IsAvailable = false;
            StatusMessage = NotFoundMessage;
            Logger.Warn("Claude Code CLI not found in any known location");
        }

        private void WriteSystemPrompt(IReadOnlyDictionary<string, IClaudeCommand> commands)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_systemPromptPath));

                var sb = new StringBuilder();
                sb.AppendLine("You are inside Autodesk Revit via the VibeModel add-in.");
                sb.AppendLine("You can control Revit by running curl commands against the embedded HTTP server.");
                sb.AppendLine();
                sb.AppendLine("IMPORTANT: Always use bash with curl to execute commands. The server is at http://localhost:" + _httpPort);
                sb.AppendLine();
                sb.AppendLine("Available commands:");
                sb.AppendLine();

                foreach (var cmd in commands.Values.OrderBy(c => c.Name))
                {
                    sb.AppendLine("  " + cmd.Name + " - " + cmd.Description);
                    if (!string.IsNullOrEmpty(cmd.Usage))
                        sb.AppendLine("    Usage: curl -s \"http://localhost:" + _httpPort + "/" + cmd.Usage + "\"");
                    else
                        sb.AppendLine("    Usage: curl -s http://localhost:" + _httpPort + "/" + cmd.Name);
                }

                sb.AppendLine();
                sb.AppendLine("Tips:");
                sb.AppendLine("- All dimensions are in millimeters (mm)");
                sb.AppendLine("- Use /batch with POST for multiple commands: curl -s -X POST http://localhost:" + _httpPort + "/batch -d \"command1 args\\ncommand2 args\"");
                sb.AppendLine("- Always check /info first to understand the current document");
                sb.AppendLine("- Use /selected to inspect what the user has selected");
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

                File.WriteAllText(_systemPromptPath, sb.ToString(), Encoding.UTF8);
                Logger.Info("System prompt written to: " + _systemPromptPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to write system prompt", ex);
            }
        }

        private string GatherModelContext()
        {
            var baseUrl = "http://localhost:" + _httpPort;
            var sb = new StringBuilder();

            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;

                    // Fetch document info
                    try
                    {
                        var info = client.DownloadString(baseUrl + "/info");
                        if (!string.IsNullOrWhiteSpace(info))
                        {
                            sb.AppendLine("CURRENT REVIT CONTEXT:");
                            sb.AppendLine(info.Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("GatherModelContext: /info failed — " + ex.Message);
                    }

                    // Fetch active view
                    try
                    {
                        var view = client.DownloadString(baseUrl + "/activeview");
                        if (!string.IsNullOrWhiteSpace(view))
                        {
                            sb.AppendLine("Active view: " + view.Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("GatherModelContext: /activeview failed — " + ex.Message);
                    }

                    // Fetch selection
                    try
                    {
                        var selected = client.DownloadString(baseUrl + "/selected");
                        if (!string.IsNullOrWhiteSpace(selected))
                        {
                            sb.AppendLine("Selection: " + selected.Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("GatherModelContext: /selected failed — " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("GatherModelContext failed — " + ex.Message);
            }

            return sb.ToString().Trim();
        }

        public void SendMessage(
            string prompt,
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

        private void RunClaudeProcess(
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            var args = new StringBuilder();
            args.Append("-p ");
            args.Append(EscapeArg(prompt));
            args.Append(" --output-format stream-json --verbose --allowedTools Bash,Read");

            if (!string.IsNullOrEmpty(_sessionId))
            {
                args.Append(" --resume ");
                args.Append(EscapeArg(_sessionId));
            }

            // Gather live model context before each invocation
            var context = GatherModelContext();

            var appendPrompt = new StringBuilder();
            appendPrompt.Append("You are inside Revit. Read " + _systemPromptPath +
                " for available commands. Always use curl to interact with Revit.");

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

                var timeoutTimer = new Timer(
                    _ => KillProcess(process), null, ProcessTimeoutMs, Timeout.Infinite);

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

                        ProcessNdjsonLine(line, onToken, fullResponse, ref lastWasText);
                    }
                }
                finally
                {
                    timeoutTimer.Dispose();
                }

                process.WaitForExit(5000);

                if (cancellationToken.IsCancellationRequested)
                {
                    onComplete("[Cancelled]");
                    return;
                }

                var stderr = stderrBuilder.ToString().Trim();
                if (process.ExitCode != 0 && fullResponse.Length == 0)
                {
                    onError(!string.IsNullOrEmpty(stderr)
                        ? stderr
                        : "Claude Code exited with code " + process.ExitCode);
                    return;
                }

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

                    foreach (var item in contentArray)
                    {
                        if (item is Dictionary<string, object> block)
                        {
                            object blockType;
                            if (block.TryGetValue("type", out blockType) && (string)blockType == "text")
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

        public void Cancel()
        {
            lock (_processLock)
            {
                if (_currentProcess != null)
                    KillProcess(_currentProcess);
            }
        }

        public void ResetSession()
        {
            _sessionId = null;
            Logger.Info("Chat session reset");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
        }
    }
}
