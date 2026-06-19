using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Chat
{
    public class AnthropicDirectBackend : IChatBackend
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const int MaxTokens = 8192;
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

        public bool IsAvailable
        {
            get
            {
                var key = SettingsManager.GetApiKey();
                return !string.IsNullOrWhiteSpace(key);
            }
        }

        public string StatusMessage
        {
            get
            {
                return IsAvailable
                    ? "Anthropic API (direct)"
                    : "API key not configured. Click Settings to add your Anthropic API key.";
            }
        }

        public AnthropicDirectBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
        {
            _commands = commands;
            _httpPort = httpPort;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public void SendMessage(
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            var apiKey = SettingsManager.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError("API key not configured. Click Settings to add your Anthropic API key.");
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
                    RunAgenticLoop(apiKey, prompt, onToken, onComplete, onError, _internalCts.Token);
                }
                catch (OperationCanceledException)
                {
                    onComplete("[Cancelled]");
                }
                catch (Exception ex)
                {
                    Logger.Error("AnthropicDirectBackend error", ex);
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
            string apiKey,
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken ct)
        {
            // Gather context and prepend to first user message
            var context = GatherModelContext();
            var userContent = string.IsNullOrEmpty(context) ? prompt : context + "\n\n" + prompt;

            // Add user message to conversation
            _conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", userContent }
            });

            var fullResponse = new StringBuilder();
            bool lastWasText = false;

            for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                var requestBody = BuildRequestBody(apiKey);
                var response = StreamRequest(apiKey, requestBody, ct);

                // Parse the streamed response
                var assistantContent = new List<Dictionary<string, object>>();
                string stopReason = null;

                foreach (var sseEvent in response)
                {
                    ct.ThrowIfCancellationRequested();

                    if (sseEvent.EventType == "content_block_start")
                    {
                        var contentBlock = GetDict(sseEvent.Data, "content_block");
                        if (contentBlock != null)
                        {
                            var blockType = GetString(contentBlock, "type");
                            if (blockType == "text")
                            {
                                assistantContent.Add(new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", "" }
                                });
                            }
                            else if (blockType == "tool_use")
                            {
                                assistantContent.Add(new Dictionary<string, object>
                                {
                                    { "type", "tool_use" },
                                    { "id", GetString(contentBlock, "id") },
                                    { "name", GetString(contentBlock, "name") },
                                    { "input", new Dictionary<string, object>() }
                                });
                            }
                        }
                    }
                    else if (sseEvent.EventType == "content_block_delta")
                    {
                        var delta = GetDict(sseEvent.Data, "delta");
                        if (delta != null)
                        {
                            var deltaType = GetString(delta, "type");
                            if (deltaType == "text_delta")
                            {
                                var text = GetString(delta, "text");
                                if (text != null && assistantContent.Count > 0)
                                {
                                    var lastBlock = assistantContent[assistantContent.Count - 1];
                                    if (GetString(lastBlock, "type") == "text")
                                    {
                                        // Separator between text blocks from different iterations
                                        if (!lastWasText && fullResponse.Length > 0)
                                        {
                                            fullResponse.Append("\n\n");
                                            onToken("\n\n");
                                        }
                                        lastWasText = true;

                                        lastBlock["text"] = (string)lastBlock["text"] + text;
                                        fullResponse.Append(text);
                                        onToken(text);
                                    }
                                }
                            }
                            else if (deltaType == "input_json_delta")
                            {
                                // Accumulate partial JSON for tool input
                                var partialJson = GetString(delta, "partial_json");
                                if (partialJson != null && assistantContent.Count > 0)
                                {
                                    var lastBlock = assistantContent[assistantContent.Count - 1];
                                    if (GetString(lastBlock, "type") == "tool_use")
                                    {
                                        if (!lastBlock.ContainsKey("_partial_json"))
                                            lastBlock["_partial_json"] = "";
                                        lastBlock["_partial_json"] = (string)lastBlock["_partial_json"] + partialJson;
                                    }
                                }
                            }
                        }
                    }
                    else if (sseEvent.EventType == "message_delta")
                    {
                        var delta = GetDict(sseEvent.Data, "delta");
                        if (delta != null)
                        {
                            stopReason = GetString(delta, "stop_reason");
                        }
                    }
                    else if (sseEvent.EventType == "error")
                    {
                        var error = GetDict(sseEvent.Data, "error");
                        var msg = error != null ? GetString(error, "message") : "Unknown API error";
                        onError(msg ?? "Unknown API error");
                        return;
                    }
                }

                // Parse accumulated partial JSON for tool_use blocks
                foreach (var block in assistantContent)
                {
                    if (GetString(block, "type") == "tool_use" && block.ContainsKey("_partial_json"))
                    {
                        var partialJson = (string)block["_partial_json"];
                        block.Remove("_partial_json");
                        try
                        {
                            var parsed = _json.Deserialize<Dictionary<string, object>>(partialJson);
                            if (parsed != null)
                                block["input"] = parsed;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Failed to parse tool input JSON: " + partialJson, ex);
                        }
                    }
                }

                // Add assistant message to conversation
                _conversationHistory.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", assistantContent.Select(b =>
                    {
                        // Return a clean copy without internal tracking fields
                        var clean = new Dictionary<string, object>(b);
                        clean.Remove("_partial_json");
                        return clean;
                    }).ToArray() }
                });

                // If stop reason is end_turn (or not tool_use), we're done
                if (stopReason != "tool_use")
                {
                    onComplete(fullResponse.ToString());
                    return;
                }

                // Execute tools and add results
                lastWasText = false;
                var toolResults = new List<Dictionary<string, object>>();
                bool addedImage = false;
                foreach (var block in assistantContent)
                {
                    if (GetString(block, "type") == "tool_use")
                    {
                        var toolName = GetString(block, "name");
                        var toolId = GetString(block, "id");
                        var input = block.ContainsKey("input") ? block["input"] as Dictionary<string, object> : null;

                        // Show tool activity BEFORE running it, so the user sees what's
                        // happening during the (blocking) call rather than after. UI-only —
                        // not added to _conversationHistory.
                        var toolLabel = "\n\n" + StatusVerbs.Describe(toolName) + "\n\n";
                        onToken(toolLabel);
                        fullResponse.Append(toolLabel);

                        var result = ExecuteTool(toolName, input);

                        // Screenshot results carry an image; everything else is a plain string.
                        var content = BuildToolResultContent(toolName, result);
                        if (content is object[]) addedImage = true;

                        toolResults.Add(new Dictionary<string, object>
                        {
                            { "type", "tool_result" },
                            { "tool_use_id", toolId },
                            { "content", content }
                        });
                    }
                }

                // Keep only the newest screenshot as an actual image — drop older base64
                // blocks to text so we don't re-ship every image on every turn.
                if (addedImage) DropOldScreenshotImages();

                _conversationHistory.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", toolResults.ToArray() }
                });
            }

            // If we exhausted iterations
            onComplete(fullResponse.ToString());
        }

        private string ExecuteTool(string toolName, Dictionary<string, object> input)
        {
            // Map tool name back to command name (revit_info -> info)
            var commandName = toolName.StartsWith("revit_") ? toolName.Substring(6) : toolName;

            // Extract args from input
            var args = "";
            if (input != null)
            {
                object argsObj;
                if (input.TryGetValue("args", out argsObj) && argsObj != null)
                    args = argsObj.ToString();
            }

            // Call the command via HTTP to the local server (this ensures it runs on the Revit thread)
            try
            {
                // Request JSON so the model reads structured fields ({ok,data} / {ok,error}).
                // Skip screenshot: it returns a text "Path:" line that BuildToolResultContent
                // parses to inline the image.
                var url = "http://localhost:" + _httpPort + "/" + commandName;
                var query = new List<string>();
                if (!string.IsNullOrEmpty(args))
                    query.Add("args=" + Uri.EscapeDataString(args));
                if (commandName != "screenshot")
                    query.Add("format=json");
                if (query.Count > 0)
                    url += "?" + string.Join("&", query);

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

        // Builds the tool_result content. Default is the plain string (unchanged behavior).
        // The screenshot tool returns an image-bearing path: read + base64 the PNG and emit a
        // mixed text+image content array so the model can SEE the view.
        private object BuildToolResultContent(string toolName, string result)
        {
            if (toolName == "revit_screenshot" && result != null && !result.StartsWith("ERROR"))
            {
                var path = ExtractPath(result);
                if (path != null && File.Exists(path))
                {
                    try
                    {
                        var b64 = Convert.ToBase64String(File.ReadAllBytes(path));
                        return new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "type", "text" },
                                { "text", result }
                            },
                            new Dictionary<string, object>
                            {
                                { "type", "image" },
                                { "source", new Dictionary<string, object>
                                    {
                                        { "type", "base64" },
                                        { "media_type", "image/png" },
                                        { "data", b64 }
                                    }
                                }
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Screenshot inline failed", ex);
                        return result + "\n[Could not attach image: " + ex.Message + "]";
                    }
                }
            }
            return result; // default: plain string, unchanged
        }

        // Parses the "Path:" line out of a screenshot result. Text-contract bridge until
        // Plan 03 (structured I/O) exposes the path as a field.
        private static string ExtractPath(string result)
        {
            foreach (var line in result.Split('\n'))
                if (line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(5).Trim();
            return null;
        }

        // Replaces image blocks in older tool_results with a short text note, so only the
        // most recent screenshot keeps its (large) base64 payload in the re-sent history.
        private void DropOldScreenshotImages()
        {
            foreach (var msg in _conversationHistory)
            {
                if (!msg.TryGetValue("content", out var contentObj) || !(contentObj is object[] blocks))
                    continue;

                foreach (var b in blocks)
                {
                    if (!(b is Dictionary<string, object> block)) continue;
                    if (!(block.TryGetValue("type", out var bt) && (bt as string) == "tool_result")) continue;
                    if (!(block.TryGetValue("content", out var trContent) && trContent is object[] trBlocks)) continue;

                    bool hasImage = false;
                    string text = null;
                    foreach (var ob in trBlocks)
                    {
                        if (!(ob is Dictionary<string, object> ib) || !ib.TryGetValue("type", out var it)) continue;
                        var its = it as string;
                        if (its == "image") hasImage = true;
                        else if (its == "text" && ib.TryGetValue("text", out var tv)) text = tv as string;
                    }

                    if (hasImage)
                        block["content"] = (text ?? "[screenshot]") +
                                           "\n[Earlier screenshot image dropped to save context.]";
                }
            }
        }

        private Dictionary<string, object> BuildRequestBody(string apiKey)
        {
            var tools = ToolDefinitionBuilder.Build(_commands, ToolFormat.Anthropic);
            var systemPrompt = BuildSystemPrompt();

            var body = new Dictionary<string, object>
            {
                { "model", SettingsManager.GetModel() },
                { "max_tokens", MaxTokens },
                { "stream", true },
                { "system", systemPrompt },
                { "messages", _conversationHistory.ToArray() },
                { "tools", tools }
            };

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
            sb.AppendLine("- When a task involves multiple steps, outline the full sequence up front so the user can approve or adjust before you proceed.");
            sb.AppendLine("- Prefer concrete Revit operations over abstract explanations. If a question can be answered by querying the model, query it rather than speculating.");
            sb.AppendLine("- When something fails, explain what went wrong in plain terms and suggest an alternative approach.");
            sb.AppendLine("- Stay within the boundaries of what VibeModel commands can do. If a request falls outside available commands, say so honestly and suggest a workaround.");
            sb.AppendLine();
            sb.AppendLine("TIPS:");
            sb.AppendLine("- Tool results are JSON: {\"ok\":true,\"data\":{...}} on success (some commands return {\"ok\":true,\"text\":\"...\"}), or {\"ok\":false,\"error\":{\"code\",\"message\",\"suggestion\"}} on failure — read the fields, and on an error follow the suggestion to self-correct. (revit_screenshot is the exception: it returns text plus an image.)");
            sb.AppendLine("- All dimensions are in millimeters (mm).");
            sb.AppendLine("- Always check revit_info first to understand the current document.");
            sb.AppendLine("- Use revit_selected to inspect what the user has selected.");
            sb.AppendLine("- You can SEE the model with revit_screenshot. Decide on your own when looking helps — you do not need to be asked. Take one after creating/moving/deleting visible geometry to verify it looks right, when the user asks how something looks or where things are, or when a spatial/layout decision needs visual context.");
            sb.AppendLine("- Do NOT screenshot for pure data queries (counts, parameters, IDs) or when nothing changed visually — it wastes time and tokens. Use judgement.");
            sb.AppendLine("- For complex operations, use revit_exec to run arbitrary C# code against the Revit API.");
            return sb.ToString();
        }

        private List<SseEvent> StreamRequest(string apiKey, Dictionary<string, object> body, CancellationToken ct)
        {
            var jsonBody = _json.Serialize(body);
            Logger.Info("Sending API request: " + jsonBody.Substring(0, Math.Min(500, jsonBody.Length)) + "...");

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            var httpResponse = _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).Result;

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = httpResponse.Content.ReadAsStringAsync().Result;
                Logger.Error("API error " + (int)httpResponse.StatusCode + ": " + errorBody);

                // Try to parse error message
                try
                {
                    var errorObj = _json.Deserialize<Dictionary<string, object>>(errorBody);
                    if (errorObj != null)
                    {
                        var error = errorObj.ContainsKey("error") ? errorObj["error"] as Dictionary<string, object> : null;
                        if (error != null && error.ContainsKey("message"))
                            throw new Exception("API error: " + error["message"]);
                    }
                }
                catch (Exception ex) when (!(ex.Message.StartsWith("API error:")))
                {
                    // Failed to parse, use raw
                }

                throw new Exception("API error " + (int)httpResponse.StatusCode + ": " + errorBody);
            }

            // Parse SSE stream
            var events = new List<SseEvent>();
            var stream = httpResponse.Content.ReadAsStreamAsync().Result;
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                string currentEvent = null;
                var dataBuilder = new StringBuilder();

                while ((line = reader.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested();

                    if (line.StartsWith("event: "))
                    {
                        currentEvent = line.Substring(7).Trim();
                    }
                    else if (line.StartsWith("data: "))
                    {
                        dataBuilder.Append(line.Substring(6));
                    }
                    else if (string.IsNullOrEmpty(line))
                    {
                        // End of event
                        if (currentEvent != null && dataBuilder.Length > 0)
                        {
                            var dataStr = dataBuilder.ToString().Trim();
                            if (dataStr == "[DONE]")
                                break;

                            try
                            {
                                var data = _json.Deserialize<Dictionary<string, object>>(dataStr);
                                events.Add(new SseEvent { EventType = currentEvent, Data = data });
                            }
                            catch (Exception ex)
                            {
                                Logger.Error("Failed to parse SSE data: " + dataStr, ex);
                            }
                        }
                        currentEvent = null;
                        dataBuilder.Clear();
                    }
                }
            }

            return events;
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
            Logger.Info("Direct backend session reset");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
            _httpClient?.Dispose();
        }

        // --- Helpers ---

        private static Dictionary<string, object> GetDict(Dictionary<string, object> obj, string key)
        {
            object val;
            if (obj != null && obj.TryGetValue(key, out val))
                return val as Dictionary<string, object>;
            return null;
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            object val;
            if (obj != null && obj.TryGetValue(key, out val))
                return val as string;
            return null;
        }

        private class SseEvent
        {
            public string EventType;
            public Dictionary<string, object> Data;
        }
    }
}
