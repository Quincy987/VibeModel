using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat.QuickReplies
{
    /// <summary>
    /// The cheap model behind the quick replies: one blocking call, system prompt + user input in,
    /// the JSON object of <see cref="QuickReplyService.SchemaJson"/> out (or null). Runs on a pool
    /// thread; must honour the token (the caller cancels after <see cref="Timeout"/>).
    /// </summary>
    public interface ISuggestionModel
    {
        string Name { get; }
        TimeSpan Timeout { get; }
        string Complete(string systemPrompt, string userInput, CancellationToken ct);
    }

    /// <summary>Which quick-reply model goes with the active chat backend (null: no chips).</summary>
    public static class SuggestionModels
    {
        // "These replies are worthless if they arrive after the user has already started typing."
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
        // The CLI spends 3–5 s starting up before the model call (~2.5 s) even begins — measured
        // 5.1–7.4 s per call on 2026-10-12 — so 8 s would drop a large share of the answers.
        // A late set is still discarded once the user types, so the longer budget costs nothing.
        public static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(12);

        public const string HaikuModel = "claude-haiku-5-5";

        private static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// <summary>
        /// POST a JSON body (the caller's token sets the deadline); the response text, or null on an
        /// HTTP error — logged once here for every transport.
        /// </summary>
        internal static string PostJson(string url, string body, IDictionary<string, string> headers, string label,
            CancellationToken ct, out int status)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (headers != null)
                foreach (var h in headers) request.Headers.Add(h.Key, h.Value);
            var response = Http.SendAsync(request, ct).GetAwaiter().GetResult();
            status = (int)response.StatusCode;
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode) return text;
            Logger.Info("Quick replies: " + label + " HTTP " + status + " - " + (text.Length > 300 ? text.Substring(0, 300) : text));
            return null;
        }

        public static ISuggestionModel For(IChatBackend backend)
        {
            switch (backend)
            {
                case AnthropicDirectBackend _:
                    return new AnthropicSuggestionModel();
                case ClaudeCodeBackend cli:
                    return string.IsNullOrEmpty(cli.CliPath) || ClaudeCliSuggestionModel.Unsupported
                        ? null
                        : new ClaudeCliSuggestionModel(cli.CliPath);
                case LocalLlmBackend _:
                    // Chat-only (no tool use): the model cannot act in Revit, so no chip could keep its promise.
                    return SettingsManager.GetLocalLlmToolUse() ? new LocalSuggestionModel() : null;
                default:
                    return null;
            }
        }
    }

    /// <summary>Anthropic Messages API, Haiku, the schema as a forced tool.</summary>
    public sealed class AnthropicSuggestionModel : ISuggestionModel
    {
        private const string ToolName = "quick_replies";

        public string Name => "Anthropic API (" + SuggestionModels.HaikuModel + ")";
        public TimeSpan Timeout => SuggestionModels.DefaultTimeout;

        public string Complete(string systemPrompt, string userInput, CancellationToken ct)
        {
            var apiKey = SettingsManager.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var text = SuggestionModels.PostJson("https://api.anthropic.com/v1/messages",
                json.Serialize(BuildRequest(systemPrompt, userInput, json)),
                new Dictionary<string, string> { { "x-api-key", apiKey }, { "anthropic-version", "2023-06-01" } },
                "Anthropic API", ct, out _);
            return text == null ? null : ToolInput(text, json);
        }

        internal static Dictionary<string, object> BuildRequest(string systemPrompt, string userInput, JavaScriptSerializer json)
        {
            return new Dictionary<string, object>
            {
                { "model", SuggestionModels.HaikuModel },
                { "max_tokens", 1024 },
                { "system", systemPrompt },
                { "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", userInput } } } },
                { "tools", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "name", ToolName },
                            { "description", "The one-tap answers to show under VibeModel's answer (or none)." },
                            { "input_schema", json.DeserializeObject(QuickReplyService.SchemaJson) }
                        }
                    }
                },
                { "tool_choice", new Dictionary<string, object> { { "type", "tool" }, { "name", ToolName } } }
            };
        }

        /// <summary>The forced tool call's input, re-serialized; null when there is none.</summary>
        internal static string ToolInput(string responseJson, JavaScriptSerializer json)
        {
            var root = json.DeserializeObject(responseJson) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("content", out var content) || !(content is object[] blocks)) return null;
            foreach (var block in blocks.OfType<Dictionary<string, object>>())
                if (block.TryGetValue("type", out var t) && (t as string) == "tool_use" &&
                    block.TryGetValue("input", out var input) && input is Dictionary<string, object>)
                    return json.Serialize(input);
            return null;
        }
    }

    /// <summary>
    /// The Claude Code CLI as a one-shot structured call: Haiku, no tools, no session file, no
    /// MCP servers, low effort, its own system prompt (none of Claude Code's), the input on stdin.
    /// A CLI that does not know --json-schema / --tools is remembered for the session and skipped.
    /// </summary>
    public sealed class ClaudeCliSuggestionModel : ISuggestionModel
    {
        private readonly string _cli;
        private static readonly object Sync = new object();
        // Optional speed-up flags an older CLI may reject — dropped one at a time, the call still works.
        private static readonly List<string> OptionalFlags = new List<string> { "--effort low", "--strict-mcp-config" };
        private static bool _loggedUnsupported;

        /// <summary>True once the CLI rejected a flag the call cannot work without.</summary>
        public static bool Unsupported { get; private set; }

        public ClaudeCliSuggestionModel(string cli)
        {
            _cli = cli;
        }

        public string Name => "Claude Code CLI (haiku)";
        public TimeSpan Timeout => SuggestionModels.CliTimeout;

        internal static string BuildArguments(string systemPrompt, IEnumerable<string> optionalFlags)
        {
            var sb = new StringBuilder("-p --model haiku --output-format json --no-session-persistence");
            sb.Append(" --json-schema ").Append(ClaudeCodeBackend.EscapeArg(QuickReplyService.SchemaJson));
            sb.Append(" --tools \"\"");
            foreach (var flag in optionalFlags) sb.Append(' ').Append(flag);
            sb.Append(" --system-prompt ").Append(ClaudeCodeBackend.EscapeArg(systemPrompt));
            return sb.ToString();
        }

        public string Complete(string systemPrompt, string userInput, CancellationToken ct)
        {
            List<string> flags;
            lock (Sync) flags = OptionalFlags.ToList();

            // Outside any repository, so no project CLAUDE.md rides along.
            var workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel", "quickreplies");
            Directory.CreateDirectory(workDir);

            var psi = new ProcessStartInfo
            {
                FileName = _cli,
                Arguments = BuildArguments(systemPrompt, flags),
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var p = Process.Start(psi))
            {
                if (p == null) return null;
                using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    try
                    {
                        var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                        stdin.Write(userInput);
                        stdin.Close();
                    }
                    catch (IOException)
                    {
                        // The CLI already exited (e.g. on an unknown option) — its stderr says why.
                    }
                    p.WaitForExit();
                    ct.ThrowIfCancellationRequested();
                    var stdout = outTask.Result;
                    var stderr = errTask.Result;
                    if (p.ExitCode != 0)
                    {
                        HandleRejectedOption(stderr);
                        Logger.Info("Quick replies: CLI exit " + p.ExitCode + " - " + Short(stderr));
                        return null;
                    }
                    var json = QuickReplyService.ParseCliOutput(stdout);
                    if (json == null)
                        Logger.Info("Quick replies: CLI output had no structured result - " + Short(stdout));
                    return json;
                }
            }
        }

        /// <summary>"error: unknown option '--effort'" → "--effort"; null when stderr is about something else.</summary>
        internal static string RejectedOption(string stderr)
        {
            var m = System.Text.RegularExpressions.Regex.Match(stderr ?? "", @"unknown option '([^']+)'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Split('=')[0] : null;
        }

        // An optional speed-up flag is dropped and the next call works; a required one → no quick replies this session.
        private static void HandleRejectedOption(string stderr)
        {
            var option = RejectedOption(stderr);
            if (option == null) return;
            lock (Sync)
            {
                var optional = OptionalFlags.FirstOrDefault(f => f.Split(' ')[0] == option);
                if (optional != null)
                {
                    OptionalFlags.Remove(optional);
                    Logger.Info("Quick replies: this CLI does not know " + optional + " - dropped");
                    return;
                }
                Unsupported = true;
                if (_loggedUnsupported) return;
                _loggedUnsupported = true;
                Logger.Info("Quick replies: this Claude Code CLI has no structured output (" + Short(stderr) +
                            ") - no quick replies this session; update with: claude update");
            }
        }

        private static string Short(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }

    /// <summary>The local OpenAI-compatible server: JSON mode, the schema spelled out in the prompt.</summary>
    public sealed class LocalSuggestionModel : ISuggestionModel
    {
        // A server that rejected response_format is asked without it from then on.
        private static bool _noJsonMode;

        public string Name => "local LLM";
        public TimeSpan Timeout => SuggestionModels.DefaultTimeout;

        public string Complete(string systemPrompt, string userInput, CancellationToken ct)
        {
            var endpoint = SettingsManager.GetLocalLlmEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            var url = endpoint.TrimEnd('/') + "/v1/chat/completions";
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            var text = SuggestionModels.PostJson(url, json.Serialize(BuildRequest(systemPrompt, userInput, !_noJsonMode)),
                null, "local LLM", ct, out var status);
            // A server that does not know response_format answers 400 or 422.
            if (text == null && (status == 400 || status == 422) && !_noJsonMode)
            {
                _noJsonMode = true;
                Logger.Info("Quick replies: local server rejected response_format - retrying without JSON mode");
                text = SuggestionModels.PostJson(url, json.Serialize(BuildRequest(systemPrompt, userInput, false)),
                    null, "local LLM", ct, out status);
            }
            if (text == null) return null;

            return QuickReplyService.ExtractJsonObject(MessageContent(text, json));
        }

        /// <summary>choices[0].message.content of an OpenAI-style response, or null.</summary>
        internal static string MessageContent(string responseJson, JavaScriptSerializer json)
        {
            var root = json.DeserializeObject(responseJson) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("choices", out var choices) || !(choices is object[] arr) || arr.Length == 0) return null;
            if (!(arr[0] is Dictionary<string, object> first) || !first.TryGetValue("message", out var m) ||
                !(m is Dictionary<string, object> message)) return null;
            return message.TryGetValue("content", out var c) ? c as string : null;
        }

        internal static Dictionary<string, object> BuildRequest(string systemPrompt, string userInput, bool jsonMode)
        {
            var model = SettingsManager.GetLocalLlmModel();
            var body = new Dictionary<string, object>
            {
                { "model", string.IsNullOrWhiteSpace(model) ? "local-model" : model },
                { "max_tokens", 1024 },
                { "stream", false },
                { "messages", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "role", "system" },
                            { "content", systemPrompt + "\n\n# Output\n\nReply with ONLY a JSON object matching this schema, nothing else:\n" + QuickReplyService.SchemaJson }
                        },
                        new Dictionary<string, object> { { "role", "user" }, { "content", userInput } }
                    }
                }
            };
            if (jsonMode) body["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };
            return body;
        }
    }
}
