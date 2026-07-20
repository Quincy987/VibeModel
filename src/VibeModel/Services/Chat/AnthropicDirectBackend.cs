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
    public class AnthropicDirectBackend : ChatBackendBase
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const int MaxTokens = 8192;
        private const int MaxToolLoopIterations = 25;

        // Attachment size guards. The API caps requests at ~32MB; base64 inflates bytes
        // by 4/3, so the practical raw-file ceiling for a PDF is ~22MB (the plan's 30MB
        // would sail past the request limit once encoded). Images above the reject cap
        // are absurd; ones between the downscale threshold and the cap get re-encoded.
        internal const long MaxPdfBytes = 22L * 1024 * 1024;
        internal const long MaxImageBytes = 20L * 1024 * 1024;
        // Budget for summed base64 chars across all attachment blocks kept in history.
        internal const int MaxAttachmentPayloadChars = 25 * 1024 * 1024;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // One entry per image/document block shipped in a user message, so an over-budget
        // history can collapse the oldest payloads to text stubs (blocks are located by
        // reference inside their owning message's content array).
        internal class SentAttachmentRecord
        {
            public Dictionary<string, object> Owner;   // history message holding the block
            public Dictionary<string, object> Block;   // the image/document block itself
            public string Name;
            public string Path;
            public int PayloadChars;
            public bool Dropped;
        }

        private readonly List<SentAttachmentRecord> _sentAttachments = new List<SentAttachmentRecord>();

        public override bool IsAvailable
        {
            get
            {
                var key = SettingsManager.GetApiKey();
                return !string.IsNullOrWhiteSpace(key);
            }
        }

        public override string StatusMessage
        {
            get
            {
                return IsAvailable
                    ? "Anthropic API (direct)"
                    : "API key not configured. Click Settings to add your Anthropic API key.";
            }
        }

        protected override string ResetLogLabel => "Direct backend";

        public AnthropicDirectBackend(IReadOnlyDictionary<string, IClaudeCommand> commands, int httpPort)
            : base(commands, httpPort)
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public override void SendMessage(
            string prompt,
            IReadOnlyList<ChatAttachment> attachments,
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

            // Fail fast with a clear message before shipping a request the API would reject.
            var sizeError = ValidateAttachmentSizes(attachments);
            if (sizeError != null)
            {
                onError(sizeError);
                return;
            }

            RunOnBackgroundThread(
                ct => RunAgenticLoop(apiKey, prompt, attachments, onToken, onComplete, onError, ct),
                onComplete, onError, cancellationToken, "AnthropicDirectBackend error");
        }

        private void RunAgenticLoop(
            string apiKey,
            string prompt,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken ct)
        {
            // Gather context and prepend to first user message
            var context = GatherModelContext();
            var baseText = string.IsNullOrEmpty(context) ? prompt : context + "\n\n" + prompt;

            // Add user message to conversation. Text-only messages keep the plain-string
            // content path unchanged; attachments switch to a content-block array.
            Dictionary<string, object> userMessage;
            if (attachments == null || attachments.Count == 0)
            {
                userMessage = new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", baseText }
                };
            }
            else
            {
                var records = new List<SentAttachmentRecord>();
                var blocks = BuildUserContentBlocks(baseText, attachments, records);

                // Attachments persist in history for the whole session (that is the point —
                // follow-up questions keep working). Only when a NEW attachment would push
                // the summed base64 payload over budget do the OLDEST ones collapse to a
                // text stub, mirroring DropOldScreenshotImages.
                int incomingChars = 0;
                foreach (var r in records) incomingChars += r.PayloadChars;
                TrimAttachmentPayload(_sentAttachments, incomingChars, MaxAttachmentPayloadChars);

                userMessage = new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", blocks }
                };
                foreach (var r in records)
                {
                    r.Owner = userMessage;
                    _sentAttachments.Add(r);
                }
            }
            _conversationHistory.Add(userMessage);

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

        // Extracts args from the tool_use input Dictionary, then delegates to the shared
        // HTTP executor. (BuildToolResultContent handles inlining the screenshot image.)
        private string ExecuteTool(string toolName, Dictionary<string, object> input)
        {
            var args = "";
            if (input != null)
            {
                object argsObj;
                if (input.TryGetValue("args", out argsObj) && argsObj != null)
                    args = argsObj.ToString();
            }
            return ExecuteTool(toolName, args);
        }

        // --- Attachment content building -------------------------------------------

        /// <summary>
        /// Pre-send size validation. Returns a user-facing error string, or null if all
        /// attachments are within limits.
        /// </summary>
        internal static string ValidateAttachmentSizes(IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null)
                return null;

            foreach (var a in attachments)
            {
                if (a.Kind == AttachmentKind.Pdf && a.SizeBytes > MaxPdfBytes)
                    return a.FileName + " is " + ChatAttachment.FormatSize(a.SizeBytes) +
                           " — PDFs over ~22 MB exceed the API's ~32 MB request limit once base64-encoded. " +
                           "Try splitting the document into parts.";

                if (a.Kind == AttachmentKind.Image && a.SizeBytes > MaxImageBytes)
                    return a.FileName + " is " + ChatAttachment.FormatSize(a.SizeBytes) +
                           " — images over " + ChatAttachment.FormatSize(MaxImageBytes) +
                           " can't be sent. Try exporting it at a lower resolution.";
            }
            return null;
        }

        /// <summary>
        /// Builds the content-block array for a user message with attachments:
        /// one text block (context + prompt + inlined text files + notes), then one
        /// image/document block per image/PDF. Adds a SentAttachmentRecord per
        /// payload block; the caller sets Owner once the history message exists.
        /// Read failures degrade to text notes instead of failing the message.
        /// </summary>
        internal static object[] BuildUserContentBlocks(
            string baseText,
            IReadOnlyList<ChatAttachment> attachments,
            List<SentAttachmentRecord> records)
        {
            var textSb = new StringBuilder(baseText ?? "");
            var mediaBlocks = new List<object>();

            foreach (var a in attachments)
            {
                switch (a.Kind)
                {
                    case AttachmentKind.Image:
                        try
                        {
                            string mediaType;
                            var bytes = ImageResizer.LoadImageBytesForApi(a.StoredPath, a.MimeType, out mediaType);
                            AddMediaBlock(mediaBlocks, records, a, "image", mediaType, bytes);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Failed to read image attachment: " + a.StoredPath, ex);
                            AppendSection(textSb, "[Attached image " + a.FileName + " could not be read: " + ex.Message + "]");
                        }
                        break;

                    case AttachmentKind.Pdf:
                        try
                        {
                            var bytes = File.ReadAllBytes(a.StoredPath);
                            AddMediaBlock(mediaBlocks, records, a, "document", "application/pdf", bytes);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Failed to read PDF attachment: " + a.StoredPath, ex);
                            AppendSection(textSb, "[Attached PDF " + a.FileName + " could not be read: " + ex.Message + "]");
                        }
                        break;

                    case AttachmentKind.Text:
                        AppendSection(textSb, AttachmentFormatting.BuildTextFence(a));
                        break;

                    default:
                        AppendSection(textSb, AttachmentFormatting.BuildUnreadableNote(a));
                        break;
                }
            }

            var blocks = new List<object>();
            // The API rejects empty text blocks — skip it when there is genuinely no text
            // (e.g. an image attached with no message and no model context).
            if (textSb.Length > 0)
                blocks.Add(new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", textSb.ToString() }
                });
            blocks.AddRange(mediaBlocks);
            return blocks.ToArray();
        }

        private static void AddMediaBlock(
            List<object> mediaBlocks,
            List<SentAttachmentRecord> records,
            ChatAttachment a,
            string blockType,
            string mediaType,
            byte[] bytes)
        {
            var b64 = Convert.ToBase64String(bytes);
            var block = new Dictionary<string, object>
            {
                { "type", blockType },
                { "source", new Dictionary<string, object>
                    {
                        { "type", "base64" },
                        { "media_type", mediaType },
                        { "data", b64 }
                    }
                }
            };
            mediaBlocks.Add(block);
            records.Add(new SentAttachmentRecord
            {
                Block = block,
                Name = a.FileName,
                Path = a.StoredPath,
                PayloadChars = b64.Length
            });
        }

        private static void AppendSection(StringBuilder sb, string section)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }
            sb.Append(section);
        }

        /// <summary>
        /// Drops the OLDEST still-active attachment payloads until the incoming payload
        /// fits the budget alongside what remains. Dropped blocks are replaced in place
        /// (inside their owning message's content array) with a text stub naming the
        /// stored path, so the model can ask for a re-attach.
        /// </summary>
        internal static void TrimAttachmentPayload(
            List<SentAttachmentRecord> sent, int incomingPayloadChars, int budgetChars)
        {
            int active = 0;
            foreach (var r in sent)
                if (!r.Dropped) active += r.PayloadChars;

            int idx = 0;
            while (active + incomingPayloadChars > budgetChars && idx < sent.Count)
            {
                var r = sent[idx++];
                if (r.Dropped) continue;
                DropRecord(r);
                active -= r.PayloadChars;
            }
        }

        private static void DropRecord(SentAttachmentRecord r)
        {
            r.Dropped = true;
            var contentArr = r.Owner != null && r.Owner.TryGetValue("content", out var c)
                ? c as object[]
                : null;
            if (contentArr == null) return;

            for (int i = 0; i < contentArr.Length; i++)
            {
                if (ReferenceEquals(contentArr[i], r.Block))
                {
                    contentArr[i] = new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", "[Attachment " + r.Name + " dropped from context to fit limits — stored at " +
                                  r.Path + ", re-attach to discuss again]" }
                    };
                    break;
                }
            }
        }

        public override void ResetSession()
        {
            base.ResetSession();
            _sentAttachments.Clear();
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

        // Anthropic-specific behavior tail: has the "outline the full sequence up front"
        // bullet and the fuller "query it" / "suggest a workaround" wording.
        protected override void AppendBehaviorTail(StringBuilder sb)
        {
            sb.AppendLine("- When a task involves multiple steps, outline the full sequence up front so the user can approve or adjust before you proceed.");
            sb.AppendLine("- Prefer concrete Revit operations over abstract explanations. If a question can be answered by querying the model, query it rather than speculating.");
            sb.AppendLine("- When something fails, explain what went wrong in plain terms and suggest an alternative approach.");
            sb.AppendLine("- Stay within the boundaries of what VibeModel commands can do. If a request falls outside available commands, say so honestly and suggest a workaround.");
        }

        protected override void AppendJsonTip(StringBuilder sb)
        {
            sb.AppendLine("- Tool results are JSON: {\"ok\":true,\"data\":{...}} on success (some commands return {\"ok\":true,\"text\":\"...\"}), or {\"ok\":false,\"error\":{\"code\",\"message\",\"suggestion\"}} on failure — read the fields, and on an error follow the suggestion to self-correct. (revit_screenshot is the exception: it returns text plus an image.)");
        }

        // Anthropic is vision-capable: it inlines screenshots as images (see BuildToolResultContent).
        protected override void AppendTipsTail(StringBuilder sb)
        {
            sb.AppendLine("- You can SEE the model with revit_screenshot. Decide on your own when looking helps — you do not need to be asked. Take one after creating/moving/deleting visible geometry to verify it looks right, when the user asks how something looks or where things are, or when a spatial/layout decision needs visual context.");
            sb.AppendLine("- Do NOT screenshot for pure data queries (counts, parameters, IDs) or when nothing changed visually — it wastes time and tokens. Use judgement.");
            sb.AppendLine("- For complex operations, use revit_exec to run arbitrary C# code against the Revit API.");
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
