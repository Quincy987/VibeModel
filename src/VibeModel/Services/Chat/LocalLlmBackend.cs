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
    public class LocalLlmBackend : IChatBackend
    {
        private const int MaxTokens = 4096;
        private const int MaxToolLoopIterations = 25;

        private readonly int _httpPort;
        private readonly IReadOnlyDictionary<string, IClaudeCommand> _commands;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly object _lock = new object();
        private readonly HttpClient _httpClient;

        private List<Dictionary<string, object>> _conversationHistory = new List<Dictionary<string, object>>();
        private bool _isSending;
        private bool _disposed;
        private CancellationTokenSource _internalCts;
        private bool? _cachedAvailable;

        public bool IsAvailable
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

        public string StatusMessage
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
        {
            _commands = commands;
            _httpPort = httpPort;

            var timeoutSeconds = SettingsManager.GetLocalLlmTimeout();
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        public void SendMessage(
            string prompt,
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

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RunAgenticLoop(endpoint, prompt, onToken, onComplete, onError, _internalCts.Token);
                }
                catch (OperationCanceledException)
                {
                    onComplete("[Cancelled]");
                }
                catch (Exception ex)
                {
                    Logger.Error("LocalLlmBackend error", ex);
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

        private void RunAgenticLoop(
            string endpoint,
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken ct)
        {
            // Gather Revit context
            var context = GatherModelContext();
            var userContent = string.IsNullOrEmpty(context) ? prompt : context + "\n\n" + prompt;

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

        private string ExecuteTool(string toolName, string args)
        {
            var commandName = toolName.StartsWith("revit_") ? toolName.Substring(6) : toolName;

            try
            {
                var url = "http://localhost:" + _httpPort + "/" + commandName;
                if (!string.IsNullOrEmpty(args))
                    url += "?args=" + Uri.EscapeDataString(args);

                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
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

        private string BuildSystemPrompt()
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
            sb.AppendLine("- Prefer concrete Revit operations over abstract explanations.");
            sb.AppendLine("- When something fails, explain what went wrong in plain terms and suggest an alternative approach.");
            sb.AppendLine("- Stay within the boundaries of what VibeModel commands can do.");
            sb.AppendLine();
            sb.AppendLine("TIPS:");
            sb.AppendLine("- All dimensions are in millimeters (mm).");
            sb.AppendLine("- Always check revit_info first to understand the current document.");
            sb.AppendLine("- Use revit_selected to inspect what the user has selected.");
            return sb.ToString();
        }

        private string GatherModelContext()
        {
            // Single round-trip: /context composes info + active view + selection server-side,
            // replacing three serial calls across the ExternalEvent boundary (lowers TTFT).
            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
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

        public void Cancel()
        {
            lock (_lock)
            {
                _internalCts?.Cancel();
            }
        }

        public void ResetSession()
        {
            _conversationHistory.Clear();
            Logger.Info("Local LLM backend session reset");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
            _httpClient?.Dispose();
        }
    }
}
