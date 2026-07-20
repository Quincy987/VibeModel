using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Chat
{
    public class LocalLlmBackend : ChatBackendBase
    {
        private const int MaxTokens = 4096;
        private const int MaxToolLoopIterations = 25;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private bool? _cachedAvailable;

        public override bool IsAvailable
        {
            get
            {
                // Only cache positive results — if server was down, re-probe
                // so we detect it coming online without requiring a backend switch
                if (_cachedAvailable == true)
                    return true;

                var result = ProbeServer();
                if (result)
                    _cachedAvailable = true;
                return result;
            }
        }

        public override string StatusMessage
        {
            get
            {
                var endpoint = SettingsManager.GetLocalLlmEndpoint();
                if (string.IsNullOrWhiteSpace(endpoint))
                    return "Local LLM endpoint not configured. Click Settings to set it up.";

                return IsAvailable
                    ? "Local LLM (" + endpoint + ")"
                    : "Local LLM not reachable at " + endpoint + ". Is the server running?";
            }
        }

        private bool ProbeServer()
        {
            var endpoint = SettingsManager.GetLocalLlmEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            try
            {
                using (var probe = new HttpClient())
                {
                    probe.Timeout = TimeSpan.FromSeconds(3);
                    var baseUri = endpoint.TrimEnd('/');

                    try
                    {
                        var r = probe.GetAsync(baseUri + "/health").Result;
                        if (r.IsSuccessStatusCode) return true;
                    }
                    catch { }

                    try
                    {
                        var r = probe.GetAsync(baseUri + "/v1/models").Result;
                        if (r.IsSuccessStatusCode) return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        public LocalLlmBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
            : this(commands, httpPort, null)
        {
        }

        public LocalLlmBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort, HttpClient httpClient)
            : base(commands, httpPort)
        {
            var timeoutSeconds = SettingsManager.GetLocalLlmTimeout();
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        protected override string ResetLogLabel => "Local LLM backend";

        public override void SendMessage(
            string prompt,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            var endpoint = SettingsManager.GetLocalLlmEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                onError("Local LLM endpoint not configured. Click Settings to set it up.");
                return;
            }

            RunOnBackgroundThread(
                ct => RunAgenticLoop(endpoint, prompt, attachments, onToken, onComplete, onError, ct),
                onComplete, onError, cancellationToken, "LocalLlmBackend error");
        }

        private void RunAgenticLoop(
            string endpoint,
            string prompt,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken ct)
        {
            // Gather Revit context
            var context = GatherModelContext();
            var userContent = string.IsNullOrEmpty(context) ? prompt : context + "\n\n" + prompt;
            userContent = AppendAttachments(userContent, attachments);

            _conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", userContent }
            });

            var fullResponse = new StringBuilder();
            bool lastWasText = false;
            bool toolUseEnabled = SettingsManager.GetLocalLlmToolUse();

            for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                var requestBody = BuildRequestBody(toolUseEnabled);
                var apiUrl = endpoint.TrimEnd('/') + "/v1/chat/completions";

                var assistantMessage = new Dictionary<string, object> { { "role", "assistant" } };
                string assistantTextContent = "";
                var toolCalls = new List<Dictionary<string, object>>();
                string finishReason = null;

                // Stream the response
                var jsonBody = _json.Serialize(requestBody);
                Logger.Info("LocalLLM request: " + jsonBody.Substring(0, Math.Min(500, jsonBody.Length)) + "...");

                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var httpResponse = _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).Result;

                if (!httpResponse.IsSuccessStatusCode)
                {
                    var errorBody = httpResponse.Content.ReadAsStringAsync().Result;
                    Logger.Error("LocalLLM API error " + (int)httpResponse.StatusCode + ": " + errorBody);
                    onError("Local LLM error " + (int)httpResponse.StatusCode + ": " + errorBody);
                    return;
                }

                var stream = httpResponse.Content.ReadAsStreamAsync().Result;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    // Accumulate partial tool call data keyed by index
                    var toolCallPartials = new Dictionary<int, Dictionary<string, string>>();

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!line.StartsWith("data: "))
                            continue;

                        var data = line.Substring(6).Trim();
                        if (data == "[DONE]")
                            break;

                        Dictionary<string, object> chunk;
                        try
                        {
                            chunk = _json.Deserialize<Dictionary<string, object>>(data);
                        }
                        catch
                        {
                            continue;
                        }

                        if (chunk == null) continue;

                        // Extract choices[0]
                        var choicesRaw = chunk.ContainsKey("choices") ? chunk["choices"] : null;
                        var choices = ToObjectArray(choicesRaw);
                        if (choices == null || choices.Length == 0) continue;
                        var choice = choices[0] as Dictionary<string, object>;
                        if (choice == null) continue;

                        // Check finish_reason
                        if (choice.ContainsKey("finish_reason") && choice["finish_reason"] is string fr && !string.IsNullOrEmpty(fr))
                            finishReason = fr;

                        var delta = choice.ContainsKey("delta") ? choice["delta"] as Dictionary<string, object> : null;
                        if (delta == null) continue;

                        // Text content
                        if (delta.ContainsKey("content") && delta["content"] is string textDelta && !string.IsNullOrEmpty(textDelta))
                        {
                            if (!lastWasText && fullResponse.Length > 0)
                            {
                                fullResponse.Append("\n\n");
                                onToken("\n\n");
                            }
                            lastWasText = true;
                            assistantTextContent += textDelta;
                            fullResponse.Append(textDelta);
                            onToken(textDelta);
                        }

                        // Tool calls (streamed as deltas)
                        if (delta.ContainsKey("tool_calls"))
                        {
                            var tcArray = ToObjectArray(delta["tool_calls"]);
                            if (tcArray != null)
                            {
                                foreach (var tcObj in tcArray)
                                {
                                    var tc = tcObj as Dictionary<string, object>;
                                    if (tc == null) continue;

                                    int index = 0;
                                    if (tc.ContainsKey("index"))
                                    {
                                        var idxVal = tc["index"];
                                        if (idxVal is int i) index = i;
                                        else int.TryParse(idxVal?.ToString(), out index);
                                    }

                                    if (!toolCallPartials.ContainsKey(index))
                                        toolCallPartials[index] = new Dictionary<string, string>
                                        {
                                            { "id", "" }, { "name", "" }, { "arguments", "" }
                                        };

                                    var partial = toolCallPartials[index];

                                    if (tc.ContainsKey("id") && tc["id"] is string id)
                                        partial["id"] = id;

                                    var fn = tc.ContainsKey("function") ? tc["function"] as Dictionary<string, object> : null;
                                    if (fn != null)
                                    {
                                        if (fn.ContainsKey("name") && fn["name"] is string name)
                                            partial["name"] = name;
                                        if (fn.ContainsKey("arguments") && fn["arguments"] is string args)
                                            partial["arguments"] += args;
                                    }
                                }
                            }
                        }
                    }

                    // Build final tool_calls list from partials
                    foreach (var kvp in toolCallPartials.OrderBy(k => k.Key))
                    {
                        var p = kvp.Value;
                        toolCalls.Add(new Dictionary<string, object>
                        {
                            { "id", p["id"] },
                            { "type", "function" },
                            { "function", new Dictionary<string, object>
                                {
                                    { "name", p["name"] },
                                    { "arguments", p["arguments"] }
                                }
                            }
                        });
                    }
                }

                // Build assistant message for history
                // OpenAI spec: content should be null when only tool_calls are present
                assistantMessage["content"] = string.IsNullOrEmpty(assistantTextContent) && toolCalls.Count > 0
                    ? null
                    : (object)assistantTextContent;
                if (toolCalls.Count > 0)
                    assistantMessage["tool_calls"] = toolCalls.ToArray();
                _conversationHistory.Add(assistantMessage);

                // If no tool calls, we're done
                if (finishReason != "tool_calls" || toolCalls.Count == 0)
                {
                    onComplete(fullResponse.ToString());
                    return;
                }

                // Execute tool calls
                lastWasText = false;
                foreach (var tc in toolCalls)
                {
                    var fn = tc["function"] as Dictionary<string, object>;
                    var toolName = fn["name"] as string ?? "";
                    var toolId = tc["id"] as string ?? "";
                    var argsJson = fn["arguments"] as string ?? "{}";

                    // Parse args
                    string argsValue = "";
                    try
                    {
                        var parsed = _json.Deserialize<Dictionary<string, object>>(argsJson);
                        if (parsed != null && parsed.ContainsKey("args"))
                            argsValue = parsed["args"]?.ToString() ?? "";
                    }
                    catch
                    {
                        argsValue = argsJson;
                    }

                    // Show tool activity BEFORE running it, so the user sees what's happening
                    // during the (blocking) call rather than after. UI-only — not added to history.
                    var toolLabel = "\n\n" + StatusVerbs.Describe(toolName) + "\n\n";
                    onToken(toolLabel);
                    fullResponse.Append(toolLabel);

                    var result = ExecuteTool(toolName, argsValue);

                    // Add tool result to history (OpenAI format)
                    _conversationHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "tool" },
                        { "tool_call_id", toolId },
                        { "content", result }
                    });
                }
            }

            // Exhausted iterations
            onComplete(fullResponse.ToString());
        }

        /// <summary>
        /// Text attachments inline as fenced blocks (same format as the Anthropic
        /// backend); images/PDFs/other get a note — most local models aren't
        /// vision-capable, so no base64 wiring here (future enhancement, see
        /// AppendTipsTail note about screenshots).
        /// </summary>
        internal static string AppendAttachments(
            string userContent, IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return userContent;

            var sb = new StringBuilder(userContent ?? "");
            foreach (var a in attachments)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }
                if (a.Kind == AttachmentKind.Text)
                    sb.Append(AttachmentFormatting.BuildTextFence(a));
                else
                    sb.Append("[Attached " + a.FileName + " (" + a.Kind + ") — this backend cannot read this format; stored at " +
                              a.StoredPath + "]");
            }
            return sb.ToString();
        }

        private static object[] ToObjectArray(object raw)
        {
            if (raw is object[] arr) return arr;
            if (raw is System.Collections.ArrayList al) return al.ToArray();
            return null;
        }

        private Dictionary<string, object> BuildRequestBody(bool includeTools)
        {
            var systemPrompt = BuildSystemPrompt();

            // Build messages array: system message first, then conversation
            var messages = new List<Dictionary<string, object>>();
            messages.Add(new Dictionary<string, object>
            {
                { "role", "system" },
                { "content", systemPrompt }
            });
            messages.AddRange(_conversationHistory);

            var model = SettingsManager.GetLocalLlmModel();

            var body = new Dictionary<string, object>
            {
                { "model", string.IsNullOrWhiteSpace(model) ? "local-model" : model },
                { "max_tokens", MaxTokens },
                { "stream", true },
                { "messages", messages.ToArray() }
            };

            if (includeTools)
            {
                var tools = ToolDefinitionBuilder.Build(_commands, ToolFormat.OpenAI);
                if (tools.Length > 0)
                    body["tools"] = tools;
            }

            return body;
        }

        // Local-specific behavior tail. NOTE: the "Prefer concrete" and "Stay within" bullets
        // are intentionally kept terser than Anthropic's (no "query it"/"suggest a workaround"
        // trailing sentences) — local models tend to follow shorter instructions better, so this
        // wording is preserved rather than reconciled toward the fuller Anthropic phrasing.
        protected override void AppendBehaviorTail(StringBuilder sb)
        {
            sb.AppendLine("- Prefer concrete Revit operations over abstract explanations.");
            sb.AppendLine("- When something fails, explain what went wrong in plain terms and suggest an alternative approach.");
            sb.AppendLine("- Stay within the boundaries of what VibeModel commands can do.");
        }

        protected override void AppendJsonTip(StringBuilder sb)
        {
            sb.AppendLine("- Tool results are JSON: {\"ok\":true,\"data\":{...}} on success or {\"ok\":false,\"error\":{\"code\",\"message\",\"suggestion\"}} on failure. Read the fields; on an error, follow the suggestion.");
        }

        protected override void AppendTipsTail(StringBuilder sb)
        {
            // NOTE: revit_screenshot returns a PNG path as text. Unlike AnthropicDirectBackend,
            // this backend does NOT inline the image — most local models aren't vision-capable.
            // The model only sees the path string. (Vision wiring for llava-class models is a
            // future enhancement.)
            sb.AppendLine("- revit_screenshot saves a PNG of the active view and returns its path (the image itself can't be viewed here).");
        }
    }
}
