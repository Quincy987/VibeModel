using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Shared plumbing for the chat backends. Holds only genuinely-common code:
    /// live model-context gathering, HTTP tool execution, the background-send guard,
    /// and CTS-based lifecycle. The agentic loops themselves stay in the subclasses —
    /// they legitimately differ (SSE streaming, NDJSON, child process, vision inlining).
    /// </summary>
    public abstract class ChatBackendBase : IChatBackend
    {
        protected readonly int _httpPort;
        protected readonly IReadOnlyDictionary<string, IClaudeCommand> _commands;
        protected readonly object _lock = new object();

        // Shared secret for the local Revit HTTP server. Null unless VIBEMODEL_TOKEN is set —
        // when present, in-app tool/context calls must carry the X-VibeModel-Token header.
        protected readonly string _authToken;

        // Assigned by subclasses that talk over HTTP (Anthropic, Local). ClaudeCode leaves
        // it null — it drives a child process — and base Dispose is null-safe.
        protected HttpClient _httpClient;

        // Conversation history for the API-style backends. ClaudeCode does not use it
        // (session continuity there is the CLI's own --resume session id).
        protected List<Dictionary<string, object>> _conversationHistory = new List<Dictionary<string, object>>();

        protected bool _isSending;
        protected bool _disposed;
        protected CancellationTokenSource _internalCts;

        protected ChatBackendBase(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            _commands = commands;
            _httpPort = httpPort;

            var token = Environment.GetEnvironmentVariable(RevitHttpServer.TokenEnvVar);
            _authToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        /// <summary>
        /// A WebClient for the local Revit server, UTF-8 and carrying the auth token header when
        /// one is configured. Centralizes the header so every in-app call stays authenticated.
        /// </summary>
        protected WebClient CreateRevitClient()
        {
            var client = new WebClient { Encoding = Encoding.UTF8 };
            if (_authToken != null)
                client.Headers[RevitHttpServer.TokenHeader] = _authToken;
            return client;
        }

        public abstract bool IsAvailable { get; }
        public abstract string StatusMessage { get; }

        public abstract void SendMessage(
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gather live Revit context for the turn in ONE round-trip: /context composes
        /// info + active view + selection server-side (see ContextCommand), replacing three
        /// serial calls across the ExternalEvent boundary. Lowers time-to-first-token.
        /// </summary>
        protected string GatherModelContext()
        {
            try
            {
                using (var client = CreateRevitClient())
                {
                    var context = client.DownloadString("http://localhost:" + _httpPort + "/context");
                    return (context ?? string.Empty).Trim();
                }
            }
            catch (Exception ex)
            {
                Logger.Info("GatherModelContext failed — " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Runs a tool by calling the matching VibeModel command over the local HTTP server
        /// (which marshals it onto the Revit thread). Callers pass the already-resolved args
        /// string — Anthropic extracts it from the tool_use Dictionary, Local from the JSON args.
        /// </summary>
        protected string ExecuteTool(string toolName, string args)
        {
            // Map tool name back to command name (revit_info -> info)
            var commandName = toolName.StartsWith("revit_") ? toolName.Substring(6) : toolName;

            try
            {
                // Request JSON so the model reads structured fields ({ok,data} / {ok,error}).
                // Skip screenshot: it returns a text "Path:" line that the caller inlines as an image.
                var url = "http://localhost:" + _httpPort + "/" + commandName;
                var query = new List<string>();
                if (!string.IsNullOrEmpty(args))
                    query.Add("args=" + Uri.EscapeDataString(args));
                if (commandName != "screenshot")
                    query.Add("format=json");
                if (query.Count > 0)
                    url += "?" + string.Join("&", query);

                using (var client = CreateRevitClient())
                {
                    var result = client.DownloadString(url);
                    Logger.Info("Tool " + toolName + " result: " + (result.Length > 200 ? result.Substring(0, 200) + "..." : result));
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Tool execution failed: " + toolName, ex);
                return "ERROR: Failed to execute " + commandName + ": " + ex.Message;
            }
        }

        /// <summary>
        /// Shared background-send scaffold for the CTS-based backends: enforce the
        /// single-in-flight guard, set up a linked cancellation source, run the supplied
        /// agentic loop on a pool thread, and clean up. Cancellation surfaces as
        /// onComplete("[Cancelled]"); other failures as onError(message).
        /// </summary>
        protected void RunOnBackgroundThread(
            Action<CancellationToken> run,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken,
            string errorLogTag)
        {
            lock (_lock)
            {
                if (_isSending)
                {
                    onError("A message is already being processed. Please wait or cancel first.");
                    return;
                }
                _isSending = true;
                _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            var token = _internalCts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    run(token);
                }
                catch (OperationCanceledException)
                {
                    onComplete("[Cancelled]");
                }
                catch (Exception ex)
                {
                    Logger.Error(errorLogTag, ex);
                    onError(ex.Message);
                }
                finally
                {
                    lock (_lock)
                    {
                        _isSending = false;
                        _internalCts?.Dispose();
                        _internalCts = null;
                    }
                }
            });
        }

        // --- System prompt scaffold (shared by the API-style backends) -------------
        // Template method: the identical intro / BEHAVIOR header / common bullets /
        // TIPS header / mm+info+selected tips live here; each backend fills the hooks
        // with its own (intentionally different) behavior tail, JSON tip, and vision lines.
        // ClaudeCode does not use this — it writes a file-based system prompt instead.

        protected string BuildSystemPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are inside Autodesk Revit via the VibeModel add-in.");
            sb.AppendLine("You can control Revit by calling the provided tools. Each tool maps to a VibeModel command.");
            sb.AppendLine();
            sb.AppendLine("BEHAVIOR:");
            sb.AppendLine("- You are a senior Revit modeling assistant. Speak in clear, professional language using standard AEC/BIM terminology.");
            sb.AppendLine("- Keep responses concise — prefer short, direct answers with exact values, element IDs, and parameter names.");
            sb.AppendLine("- Adapt your detail level to the user: give brief answers to experienced users, add context when a question suggests less familiarity.");
            sb.AppendLine("- When you complete a task, suggest 1-2 logical next steps based on the current model context. Keep suggestions brief and at the end of your response.");
            sb.AppendLine("- Always confirm destructive operations (delete, overwrite) before executing.");
            AppendBehaviorTail(sb);
            sb.AppendLine();
            sb.AppendLine("TIPS:");
            AppendJsonTip(sb);
            sb.AppendLine("- All dimensions are in millimeters (mm).");
            sb.AppendLine("- Always check revit_info first to understand the current document.");
            sb.AppendLine("- Use revit_selected to inspect what the user has selected.");
            AppendTipsTail(sb);
            return sb.ToString();
        }

        // Backend-specific behavior bullets that follow the common ones (order-preserving).
        protected virtual void AppendBehaviorTail(StringBuilder sb) { }

        // Backend-specific "Tool results are JSON: ..." tip (first tip under TIPS).
        protected virtual void AppendJsonTip(StringBuilder sb) { }

        // Backend-specific trailing tips (vision / screenshot / exec lines).
        protected virtual void AppendTipsTail(StringBuilder sb) { }

        // --- Lifecycle -------------------------------------------------------------

        public virtual void Cancel()
        {
            lock (_lock)
            {
                _internalCts?.Cancel();
            }
        }

        // Log label used by the default ResetSession. Overridden per backend.
        protected virtual string ResetLogLabel => "Backend";

        public virtual void ResetSession()
        {
            _conversationHistory.Clear();
            Logger.Info(ResetLogLabel + " session reset");
        }

        /// <summary>
        /// Default restore for the API-style backends: replay the transcript's
        /// user/assistant text into _conversationHistory. Consecutive same-role
        /// messages are merged and a leading assistant turn is dropped so the
        /// rebuilt history is always a valid alternating conversation.
        /// </summary>
        public virtual void RestoreHistory(IEnumerable<ChatSessionMessage> messages)
        {
            _conversationHistory.Clear();
            if (messages == null)
                return;

            string lastRole = null;
            foreach (var m in messages)
            {
                if (m == null || string.IsNullOrEmpty(m.Content))
                    continue;
                if (m.Role != ChatSession.RoleUser && m.Role != ChatSession.RoleAssistant)
                    continue;
                if (_conversationHistory.Count == 0 && m.Role != ChatSession.RoleUser)
                    continue; // conversations must open with a user turn

                if (m.Role == lastRole)
                {
                    var prev = _conversationHistory[_conversationHistory.Count - 1];
                    prev["content"] = (string)prev["content"] + "\n\n" + m.Content;
                }
                else
                {
                    _conversationHistory.Add(new Dictionary<string, object>
                    {
                        { "role", m.Role },
                        { "content", m.Content }
                    });
                    lastRole = m.Role;
                }
            }
            Logger.Info(ResetLogLabel + " restored " + _conversationHistory.Count + " turn(s) from saved session");
        }

        public virtual void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
            _httpClient?.Dispose();
        }
    }
}
