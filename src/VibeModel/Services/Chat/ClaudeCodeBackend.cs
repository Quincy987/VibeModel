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
        // How the headless CLI is allowed to act. Explicit on purpose: without --permission-mode
        // the CLI inherits the user's own defaultMode, and in "auto" mode its safety classifier
        // ignores the broad Bash allow and refused `curl …/delete` (and then even `select`) as
        // "Irreversible Local Destruction" — workshop 2026-10-12. "dontAsk" makes --allowedTools
        // the whole policy: Bash, Read and the read-only Grep/Glob run without a prompt, every other tool (Write, Edit,
        // WebFetch, …) is denied outright. Nothing the CLI does through VibeModel is irreversible:
        // every command is one Revit undo step. PowerShell: on a laptop without Git for Windows the
        // CLI has no Bash and shells out through its PowerShell tool instead — without it here every
        // command would be denied there.
        internal const string PermissionArgs = "--permission-mode dontAsk --allowedTools Bash,PowerShell,Read,Grep,Glob";

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

        public override string DisplayName => "Claude Code";

        /// <summary>The CLI executable found at startup (null when none) — also used for the quick replies.</summary>
        internal string CliPath => _claudePath;

        // The CLI picks its own model; its stream's init event names it. Static: a New Chat (or a
        // backend switch back) still knows which model the last turn ran on.
        private static string _lastSeenModel;

        /// <summary>The model of the last turn, else the CLI's configured model, else null (its default).</summary>
        public override string ModelName => _lastSeenModel ?? ConfiguredCliModel();

        private static string _configuredModel;
        private static DateTime _configuredModelReadUtc;

        // ANTHROPIC_MODEL, then "model" in ~/.claude/settings.json — what the CLI itself reads.
        // Cached for a minute: the welcome card asks on the UI thread each time it rebuilds.
        private static string ConfiguredCliModel()
        {
            if (DateTime.UtcNow - _configuredModelReadUtc < TimeSpan.FromMinutes(1)) return _configuredModel;
            _configuredModel = ReadConfiguredCliModel();
            _configuredModelReadUtc = DateTime.UtcNow;
            return _configuredModel;
        }

        private static string ReadConfiguredCliModel()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
                if (!System.IO.File.Exists(path)) return null;
                var settings = new JavaScriptSerializer().DeserializeObject(System.IO.File.ReadAllText(path))
                    as Dictionary<string, object>;
                return settings != null && settings.TryGetValue("model", out var m) && m is string model
                       && !string.IsNullOrWhiteSpace(model)
                    ? model.Trim()
                    : null;
            }
            catch
            {
                return null;
            }
        }

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

        /// <summary>What the last search checked, one line per candidate (Diagnose shows it).</summary>
        internal static IReadOnlyList<string> LastSearch { get; private set; } = new string[0];

        /// <summary>
        /// The first candidate (<see cref="ClaudeCliLocator"/>) that answers `--version`, or null.
        /// Shared with the office-standards distiller, which runs one-shot CLI calls.
        /// </summary>
        internal static string LocateCli(out string version)
        {
            version = null;
            var search = new List<string>();
            try
            {
                if (SettingsManager.IsClaudeCodeDisabled())
                {
                    search.Add("switched off by IT (managed.json DisableClaudeCode)");
                    return null;
                }
                var explicitPath = SettingsManager.GetClaudeCliPath();
                if (ClaudeCliLocator.IsDisabled(explicitPath))
                {
                    search.Add("discovery switched off (ClaudeCliPath / VIBEMODEL_CLAUDE_CLI = none)");
                    return null;
                }

                foreach (var candidate in ClaudeCliLocator.CandidatesForThisMachine())
                {
                    if (!File.Exists(candidate)) continue;
                    var answer = ProbeVersion(candidate);
                    search.Add(candidate + " -> " + (answer ?? "no answer to --version"));
                    if (answer != null)
                    {
                        version = answer;
                        return candidate;
                    }
                }
                if (search.Count == 0) search.Add("no claude.exe in %USERPROFILE%\\.local\\bin, on the PATH or in the npm folders");
                return null;
            }
            finally
            {
                LastSearch = search;
            }
        }

        // `claude --version` with a deadline (a hung CLI must not hang Revit's startup).
        private static string ProbeVersion(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEndAsync();
                    if (!proc.WaitForExit(DetectTimeoutMs * 3))
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }
                    return proc.ExitCode == 0 ? output.Result.Trim() : null;
                }
            }
            catch
            {
                // Not runnable at this location (blocked by policy, broken install) — try the next one.
                return null;
            }
        }

        private void DetectClaude()
        {
            var path = LocateCli(out var version);
            if (path != null)
            {
                _claudePath = path;
                _isAvailable = true;
                _statusMessage = "Claude Code " + version;
                Logger.Info("Claude Code found at: " + path + " (" + version + ")");
                return;
            }

            _isAvailable = false;
            _statusMessage = Strings.Get(SettingsManager.IsClaudeCodeDisabled() ? Strings.ClaudeDisabledByIt : Strings.ClaudeNotFound);
            Logger.Warn("Claude Code CLI not found: " + string.Join(" | ", LastSearch));
        }

        private static bool _loggedProxy;

        /// <summary>
        /// The CLI does not read Windows' proxy settings: when Windows routes the API through a
        /// proxy and the environment names none, hand it over. Only HTTPS_PROXY — the API is HTTPS —
        /// plus NO_PROXY for loopback: the CLI talks to VibeModel with curl over http://127.0.0.1, and
        /// curl honours http_proxy, so a proxy there would carry every Revit command off the machine.
        /// NODE_EXTRA_CA_CERTS (a TLS-inspecting proxy's root) passes through untouched.
        /// </summary>
        internal static void ApplyProxyEnvironment(ProcessStartInfo psi)
        {
            var env = psi.EnvironmentVariables;
            var add = ProxyVariables(name => env[name], AnthropicHttp.SystemProxyFor("https://api.anthropic.com/"));
            foreach (var kv in add) env[kv.Key] = kv.Value;
            if (add.Count > 0 && !_loggedProxy)
                Logger.Info("Claude Code: Windows proxy handed over - " + string.Join(", ", add.Select(kv => kv.Key + "=" + kv.Value)));
            if (add.Count > 0) _loggedProxy = true;
        }

        /// <summary>The variables to add for <paramref name="proxy"/> given the current environment (empty = none).</summary>
        internal static IDictionary<string, string> ProxyVariables(Func<string, string> getEnv, Uri proxy)
        {
            var add = new Dictionary<string, string>();
            if (proxy == null) return add;
            if (new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" }.Any(n => !string.IsNullOrEmpty(getEnv(n))))
                return add; // the user (or IT) configured it explicitly
            add["HTTPS_PROXY"] = proxy.GetLeftPart(UriPartial.Authority);
            if (string.IsNullOrEmpty(getEnv("NO_PROXY")) && string.IsNullOrEmpty(getEnv("no_proxy")))
                add["NO_PROXY"] = "127.0.0.1,localhost,::1";
            return add;
        }

        /// <summary>The usage of a CLI result event: total_cost_usd and the token counts; null when absent.</summary>
        internal static TurnUsage UsageFromResult(Dictionary<string, object> result, string model)
        {
            var usage = new TurnUsage { Model = model };
            if (result.TryGetValue("usage", out var u) && u is Dictionary<string, object> counts)
            {
                usage.AddMessageStart(model, counts);
                usage.EndMessage();
            }
            if (result.TryGetValue("total_cost_usd", out var c) && c != null)
            {
                try { usage.ReportedCostUsd = Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture); }
                catch { /* unparsable: tokens only */ }
            }
            return usage.IsEmpty ? null : usage;
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

        // The model kept writing `curl ".../wall 0 0 5000 0"` (the old Usage lines put the
        // grammar straight after the slash) and got "Unknown command 'wall%200…'" several times a
        // turn. Stated in the prompt file AND in every turn prompt (a resumed session may not
        // re-read the file); the server also accepts the path form as a safety net.
        internal const string ArgsRule =
            "Arguments always go in ?args=, URL-encoded, or in a POST /batch body; never in the path.";

        // A laptop without Git for Windows has no Bash: the CLI shells out through PowerShell, where
        // plain `curl` is an alias of Invoke-WebRequest and $'..' strings do not exist.
        internal const string PowerShellRule =
            "If your shell tool is PowerShell (no Git Bash on this machine): call curl.exe, not curl (an alias of Invoke-WebRequest there), with the same URLs; " +
            "for a POST body write it to a file with Set-Content first, then curl.exe -s -X POST <url> --data-binary \"@C:\\path\\file.txt\".";

        /// <summary>
        /// The argument part of a command's Usage: "wall &lt;x1&gt; … | wall …" → "&lt;x1&gt; … | …".
        /// Empty for a command without arguments.
        /// </summary>
        internal static string ArgsGrammar(string name, string usage)
        {
            if (string.IsNullOrWhiteSpace(usage)) return "";
            var forms = System.Text.RegularExpressions.Regex.Split(usage.Trim(), @"\s+\|\s+")
                .Select(f => f.Trim())
                .Select(f => f == name ? ""
                    : f.StartsWith(name + " ", StringComparison.Ordinal) ? f.Substring(name.Length + 1).Trim()
                    : f)
                .ToList();
            if (forms.All(f => f.Length == 0)) return "";
            return string.Join(" | ", forms.Select(f => f.Length == 0 ? "(none)" : f));
        }

        internal static string BuildSystemPromptText(
            IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort, string authToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are inside Autodesk Revit via the VibeModel add-in.");
            sb.AppendLine("You can control Revit by running curl commands against the embedded HTTP server.");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: Always use your shell tool with curl to execute commands (Bash, or PowerShell with curl.exe). The server is at " + RevitHttpServer.BaseUrl(httpPort));
            sb.AppendLine();
            if (authToken != null)
            {
                sb.AppendLine("AUTH: This server requires a token. Add this header to EVERY curl call:");
                sb.AppendLine("  -H \"" + RevitHttpServer.TokenHeader + ": $" + RevitHttpServer.TokenEnvVar + "\"");
                sb.AppendLine("The " + RevitHttpServer.TokenEnvVar + " environment variable is already set in your shell (PowerShell: $env:" + RevitHttpServer.TokenEnvVar + "). Requests without this header return 401.");
                sb.AppendLine();
            }
            var baseUrl = RevitHttpServer.BaseUrl(httpPort);
            sb.AppendLine("HOW TO CALL A COMMAND (the only form that works):");
            sb.AppendLine("- " + ArgsRule);
            sb.AppendLine("- Encoding: space -> %20, a literal + -> %2B, & -> %26, # -> %23, % -> %25, newline -> %0A.");
            sb.AppendLine("- Examples:");
            sb.AppendLine("    curl -s \"" + baseUrl + "/info\"");
            sb.AppendLine("    curl -s \"" + baseUrl + "/wall?args=0%200%206000%200\"");
            sb.AppendLine("    curl -s \"" + baseUrl + "/color?args=12345%20255%200%200\"");
            sb.AppendLine("    curl -s -G \"" + baseUrl + "/exec\" --data-urlencode \"args=sb.AppendLine(doc.Title);\"   (curl encodes ?args= for you - use this for code and free text)");
            sb.AppendLine("    curl -s -X POST \"" + baseUrl + "/batch\" --data-binary $'wall 0 0 5000 0\\nwall 5000 0 5000 5000'   (plain text, one command per line, one undo step)");
            sb.AppendLine("- " + PowerShellRule);
            sb.AppendLine();
            sb.AppendLine("Available commands (ARGS = what goes in ?args=, before encoding; [..] optional, a | b alternatives):");
            sb.AppendLine();

            foreach (var cmd in commands.Values.Where(ProjectMemoryCommands.IsOffered).OrderBy(c => c.Name))
            {
                sb.AppendLine("  " + cmd.Name + " - " + cmd.Description);
                var grammar = ArgsGrammar(cmd.Name, cmd.Usage);
                if (grammar.Length > 0)
                    sb.AppendLine("    Usage: curl -s \"" + baseUrl + "/" + cmd.Name + "?args=ARGS\"   ARGS: " + grammar);
                else
                    sb.AppendLine("    Usage: curl -s \"" + baseUrl + "/" + cmd.Name + "\"");
            }

            sb.AppendLine();
            sb.AppendLine("Tips:");
            sb.AppendLine("- All dimensions are in millimeters (mm)");
            sb.AppendLine("- Use POST /batch for several commands in one undo step (see the example above).");
            sb.AppendLine("- Always check /info first to understand the current document");
            sb.AppendLine("- Use /selected to inspect what the user has selected");
            sb.AppendLine("- You can SEE the model: run /screenshot, then Read the returned Path (PNG). Decide on your own when looking helps — after changing visible geometry to verify it, when asked how something looks, or when a layout decision needs visual context. Skip it for pure data queries or when nothing changed visually.");
            sb.AppendLine("- For complex operations, use /exec to run arbitrary C# code against the Revit API");
            sb.AppendLine();
            // The RULES block (language, undo, ask-first, compact answers) is not in this file: it
            // travels in every turn's --append-system-prompt, so a resumed session that read this
            // file long ago still gets the current language and wording.
            AppendCommonBehavior(sb);
            sb.AppendLine("- When a task involves multiple steps, name the sequence in one line, then carry it out — stop for approval only where the Ask first rule says so.");
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
            var question = prompt;
            prompt = BuildPromptWithAttachments(prompt, attachments);

            lock (_processLock)
            {
                if (_isSending)
                {
                    onError(Strings.Get(Strings.MessageInProgress));
                    return;
                }
                _isSending = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RunJournaledTurn(question, onComplete,
                        complete => RunClaudeProcess(prompt, onToken, complete, onError, cancellationToken));
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
                onError(Strings.Get(Strings.ServerNotResponding, _httpPort));
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
            args.Append(" --output-format stream-json --verbose ");
            args.Append(PermissionArgs);

            if (resumeAttempted)
            {
                args.Append(" --resume ");
                args.Append(EscapeArg(_sessionId));
            }

            // Gather live model context before each invocation
            var context = GatherModelContext();

            var appendPrompt = new StringBuilder(
                BuildTurnPrompt(_systemPromptPath, _httpPort, context, Strings.Current));

            // Office standards (plan 11): live template facts + the active pack's core rules go
            // into the per-turn system prompt — not into the prompt FILE, which the model reads
            // as well (it would carry them twice) and which must not go stale between turns.
            AppendOfficeContext(appendPrompt, Standards.StandardsPrompt.Combined);

            // Project folder (plan 10): the manifest goes into the prompt, and the folder itself
            // is made readable to the CLI's own tools.
            AppendOfficeContext(appendPrompt, ProjectDocs.BuildPromptBlockForActiveDocument(true, out var projectFolder));
            // Project memory (plan 08): saved facts + the rule to save more (nothing when off).
            AppendOfficeContext(appendPrompt, ProjectMemory.BuildPromptBlockForActiveDocument());
            if (projectFolder.IsSet)
            {
                args.Append(" --add-dir ");
                args.Append(EscapeArg(projectFolder.Path));
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
            ApplyProxyEnvironment(psi);
            // The prompt file tells the CLI to send -H "X-VibeModel-Token: $VIBEMODEL_TOKEN"; this is
            // where that variable comes from (the session token, or the user's own when they set one).
            if (_authToken != null)
                psi.EnvironmentVariables[RevitHttpServer.TokenEnvVar] = _authToken;

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
                onError(Strings.Get(Strings.ProcessStartFailed));
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
                        notice = "\n\n" + Strings.Get(Strings.IdleStoppedMidTool);
                        _sessionId = null;
                    }
                    else
                    {
                        notice = "\n\n" + Strings.Get(Strings.IdleStopped);
                    }
                    onToken(notice);
                    fullResponse.Append(notice);
                }

                var stderr = stderrBuilder.ToString().Trim();

                // An older CLI that does not know the permission mode fails on argument parsing —
                // that is not a stale session, so say what to do instead of resetting the chat.
                if (process.ExitCode != 0 && fullResponse.Length == 0 && IsPermissionModeRejected(stderr))
                {
                    onError(Strings.Get(Strings.CliTooOld) + "\n" + stderr);
                    return;
                }

                // 3. Stale-resume retry - silent failure of a --resume invocation
                //    means the session ID is no longer valid in the CLI's store.
                //    Clear it and retry once as a fresh chat.
                if (resumeAttempted && !isResumeRetry
                    && process.ExitCode != 0 && fullResponse.Length == 0)
                {
                    Logger.Warn("Resume failed (exit " + process.ExitCode +
                                "), retrying as fresh chat. stderr: " + stderr);
                    _sessionId = null;
                    var prefix = Strings.Get(Strings.StaleResume) + "\n\n";
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
                        : Strings.Get(Strings.ProcessExitCode, process.ExitCode));
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
                    if (obj.TryGetValue("model", out var modelObj) && modelObj is string model && !string.IsNullOrWhiteSpace(model))
                        _lastSeenModel = model;
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
                    var usage = UsageFromResult(obj, _lastSeenModel);
                    if (usage != null) ReportActivity(ChatActivityEvent.ForUsage(usage));
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
                                ReportToolResult(block);
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

                            if (currentBlockType == "tool_use")
                            {
                                ReportActivity(ChatActivityEvent.Started(
                                    block.TryGetValue("id", out var toolId) ? toolId as string : null,
                                    ToolCallDescriber.ForCliTool(
                                        block.TryGetValue("name", out var toolName) ? toolName as string : null,
                                        block.TryGetValue("input", out var toolInput) ? toolInput as Dictionary<string, object> : null)));
                            }
                            else if (currentBlockType == "text")
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

        /// <summary>
        /// The per-turn system prompt (before the office / project-folder blocks): where the
        /// command list is, the live port, the model context, and the RULES block in
        /// <paramref name="language"/>. Rebuilt every turn, so a language switch in Settings
        /// applies from the next message on — also in a resumed session.
        /// </summary>
        internal static string BuildTurnPrompt(string systemPromptPath, int httpPort, string context, string language)
        {
            var sb = new StringBuilder();
            sb.Append("You are inside Revit. Read " + systemPromptPath +
                " for available commands. Always use curl to interact with Revit. " +
                "After a visual change, run /screenshot and Read the returned PNG path to verify the result.");
            sb.AppendLine();
            sb.AppendLine(ArgsRule);
            // Heals resumed sessions whose history mentions a dead port (e.g. after a
            // fallback-port instance closed): the live port always wins.
            sb.Append(BuildPortDirective(httpPort));

            if (!string.IsNullOrEmpty(context))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine(context);
                sb.AppendLine();
                sb.Append("Use the context above to answer. If the user has elements selected, take them into account.");
            }

            sb.AppendLine();
            sb.AppendLine();
            sb.Append(BuildBehaviourRules(language).TrimEnd());
            return sb.ToString();
        }

        // The CLI's message for a rejected option value:
        // "option '--permission-mode <mode>' argument 'dontAsk' is invalid".
        internal static bool IsPermissionModeRejected(string stderr)
        {
            return !string.IsNullOrEmpty(stderr)
                && stderr.IndexOf("--permission-mode", StringComparison.Ordinal) >= 0
                && stderr.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A tool_result block from the CLI: finishes the matching step (UI-only).
        private void ReportToolResult(Dictionary<string, object> block)
        {
            object id, content, isError;
            block.TryGetValue("tool_use_id", out id);
            block.TryGetValue("content", out content);
            block.TryGetValue("is_error", out isError);
            ReportActivity(ChatActivityEvent.Finished(id as string,
                ToolCallDescriber.ErrorLine(ToolCallDescriber.ResultText(content), isError is bool b && b)));
        }

        internal static string BuildPortDirective(int httpPort)
        {
            return "The VibeModel server is at " + RevitHttpServer.BaseUrl(httpPort) +
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
                    RevitHttpServer.BaseUrl(_httpPort) + "/health");
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

        internal static string EscapeArg(string arg)
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
