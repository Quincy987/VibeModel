using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VibeModel.Services.Chat
{
    public enum ChatActivityKind
    {
        ToolStarted,
        ToolFinished
    }

    /// <summary>
    /// A structured progress event from a backend, next to the text stream: "the model now runs
    /// this tool" / "that tool finished (or failed)". UI-only — it never touches the stored chat
    /// history or anything sent back to the model; the onToken stream is unchanged.
    /// </summary>
    public sealed class ChatActivityEvent
    {
        public ChatActivityKind Kind { get; set; }
        public string ToolId { get; set; }

        /// <summary>VibeModel command ("wall") or a CLI tool ("@read", "@image", "@shell", "@Grep").</summary>
        public string Command { get; set; }

        /// <summary>The command's short args, one line ("0 0 8000 0").</summary>
        public string Detail { get; set; }

        /// <summary>ToolFinished: the first error line, or null when it succeeded.</summary>
        public string ErrorLine { get; set; }

        /// <summary>
        /// ToolStarted: the exact onToken delta the backend streams right after this event (its
        /// UI-only status line, e.g. "\n\nCreating wall…\n\n"). The pane hides it because the step
        /// row replaces it; it still goes into the stored transcript exactly as before.
        /// </summary>
        public string EchoText { get; set; }

        public static ChatActivityEvent Started(string toolId, ToolCallInfo call, string echoText = null) =>
            new ChatActivityEvent
            {
                Kind = ChatActivityKind.ToolStarted,
                ToolId = toolId,
                Command = call.Command,
                Detail = call.Detail,
                EchoText = echoText
            };

        public static ChatActivityEvent Finished(string toolId, string errorLine) =>
            new ChatActivityEvent { Kind = ChatActivityKind.ToolFinished, ToolId = toolId, ErrorLine = errorLine };
    }

    /// <summary>A backend that reports tool activity. The pane sets the sink before each turn.</summary>
    public interface IChatActivitySource
    {
        /// <summary>Called on the backend's worker thread; null = nobody listens.</summary>
        Action<ChatActivityEvent> ActivitySink { get; set; }
    }

    public struct ToolCallInfo
    {
        public string Command;
        public string Detail;

        public ToolCallInfo(string command, string detail)
        {
            Command = command;
            Detail = detail;
        }
    }

    /// <summary>
    /// Turns a raw tool call into a step (command + short args) and a raw tool result into its
    /// first error line. Pure string work, shared by all three backends.
    /// </summary>
    public static class ToolCallDescriber
    {
        public const int MaxDetailLength = 60;
        public const int MaxErrorLength = 140;

        private static readonly Regex CurlUrl = new Regex(
            @"https?://(?:127\.0\.0\.1|localhost|\[::1\]):\d+/(?<cmd>[A-Za-z]+)(?<query>\?[^""'\s]*)?",
            RegexOptions.Compiled);

        // -d "..." / --data '...' / --data-binary "..." — the POST body of a batch.
        private static readonly Regex CurlData = new Regex(
            @"(?:-d|--data(?:-binary|-raw)?)\s+(?:""(?<b>(?:[^""\\]|\\.)*)""|'(?<b>[^']*)')",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        /// <summary>Anthropic / Local: tool "revit_wall" with an args string.</summary>
        public static ToolCallInfo ForRevitTool(string toolName, string args)
        {
            var command = (toolName ?? "").StartsWith("revit_") ? toolName.Substring(6) : (toolName ?? "");
            if (command.Equals("batch", StringComparison.OrdinalIgnoreCase))
                return new ToolCallInfo(command, BatchDetail(args));
            return new ToolCallInfo(command, Shorten(args));
        }

        /// <summary>
        /// Claude CLI: its own tools. A Bash call that curls our server becomes that VibeModel
        /// command (the model talks to Revit through curl); Read becomes "read"/"look at image".
        /// </summary>
        public static ToolCallInfo ForCliTool(string toolName, IDictionary<string, object> input)
        {
            var name = toolName ?? "";
            // The CLI shells out through Bash or (on Windows) PowerShell — both curl the server.
            if (name == "Bash" || name == "PowerShell")
            {
                var command = GetString(input, "command") ?? "";
                var match = CurlUrl.Match(command);
                if (match.Success)
                {
                    var cmd = match.Groups["cmd"].Value.ToLowerInvariant();
                    string detail;
                    if (cmd == "batch")
                    {
                        var data = CurlData.Match(command);
                        detail = data.Success ? BatchDetail(data.Groups["b"].Value.Replace("\\\"", "\"")) : "";
                    }
                    else
                    {
                        detail = Shorten(QueryArgs(match.Groups["query"].Value));
                    }
                    return new ToolCallInfo(cmd, detail);
                }
                var description = GetString(input, "description");
                return new ToolCallInfo("@shell", Shorten(string.IsNullOrWhiteSpace(description) ? command : description));
            }

            if (name == "Read")
            {
                var path = GetString(input, "file_path") ?? "";
                var file = FileName(path);
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                var isImage = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp";
                return new ToolCallInfo(isImage ? "@image" : "@read", Shorten(file));
            }

            // Any other CLI tool: its name plus the first string input as the detail.
            var first = input?.Values.OfType<string>().FirstOrDefault();
            return new ToolCallInfo("@" + name, Shorten(first));
        }

        /// <summary>
        /// The first error line of a tool result, or null when it looks successful. Recognizes the
        /// JSON envelope ({"ok":false,"error":{"message":…}}), text-mode "ERROR: …", curl's own
        /// failures and the CLI's is_error flag. A batch that partly failed is not a failed step.
        /// </summary>
        public static string ErrorLine(string result, bool isError = false)
        {
            var text = (result ?? "").Trim();

            if (text.StartsWith("{"))
            {
                try
                {
                    var obj = Json.DeserializeObject(text) as Dictionary<string, object>;
                    if (obj != null)
                    {
                        object ok;
                        var failed = obj.TryGetValue("ok", out ok) && ok is bool okBool && !okBool;
                        if (failed && obj.ContainsKey("results"))
                        {
                            // A resilient batch keeps what succeeded: only a rolled-back batch failed.
                            object rolled;
                            failed = obj.TryGetValue("rolledBack", out rolled) && rolled is bool r && r;
                            if (failed) return Cap(FirstBatchError(obj["results"]) ?? "Batch rolled back");
                        }
                        if (failed)
                        {
                            object err;
                            string message = null;
                            if (obj.TryGetValue("error", out err) && err is Dictionary<string, object> errDict)
                                message = GetString(errDict, "message");
                            return Cap(FirstLine(message) ?? "Error");
                        }
                        if (!isError) return null;
                    }
                }
                catch
                {
                    // Not JSON after all — fall through to the text rules.
                }
            }

            var firstLine = FirstLine(text);
            if (firstLine != null)
            {
                if (firstLine.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = firstLine.Substring(5).TrimStart(':', ' ').Trim();
                    return Cap(rest.Length > 0 ? rest : firstLine);
                }
                if (firstLine.StartsWith("curl: (", StringComparison.Ordinal))
                    return Cap(firstLine);
            }

            if (isError)
                return Cap(firstLine ?? "Error");
            return null;
        }

        /// <summary>A CLI tool_result's content: a string, or an array of {type:text,text} blocks.</summary>
        public static string ResultText(object content)
        {
            if (content is string s) return s;
            var items = content as object[] ?? (content as System.Collections.ArrayList)?.ToArray();
            if (items == null) return "";
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                if (item is Dictionary<string, object> block && GetString(block, "type") == "text")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(GetString(block, "text"));
                }
            }
            return sb.ToString();
        }

        /// <summary>Collapses whitespace to one line and caps it with an ellipsis.</summary>
        public static string Shorten(string text, int max = MaxDetailLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var one = Regex.Replace(text, @"\s+", " ").Trim();
            return one.Length <= max ? one : one.Substring(0, max - 1).TrimEnd() + "…";
        }

        // The first failing sub-result of a JSON batch: results[i].result is either an
        // {ok:false,error:{message}} envelope or an "ERROR: …" string.
        private static string FirstBatchError(object results)
        {
            var items = results as object[] ?? (results as System.Collections.ArrayList)?.ToArray();
            if (items == null) return null;
            foreach (var item in items)
            {
                var entry = item as Dictionary<string, object>;
                object result;
                if (entry == null || !entry.TryGetValue("result", out result)) continue;
                var line = result is string s
                    ? ErrorLine(s)
                    : ErrorLine(Json.Serialize(result));
                if (line != null) return line;
            }
            return null;
        }

        private static string BatchDetail(string body)
        {
            var lines = (body ?? "").Replace("\\n", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#"))
                .ToList();
            if (lines.Count == 0) return "";
            var first = Shorten(lines[0], MaxDetailLength - 6);
            return lines.Count == 1 ? first : first + " (+" + (lines.Count - 1) + ")";
        }

        private static string QueryArgs(string query)
        {
            if (string.IsNullOrEmpty(query)) return "";
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                if (pair.StartsWith("args=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = pair.Substring(5).Replace('+', ' ');
                    try { return Uri.UnescapeDataString(raw); }
                    catch { return raw; }
                }
            }
            return "";
        }

        private static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return cut >= 0 ? path.Substring(cut + 1) : path;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t;
            }
            return null;
        }

        private static string Cap(string line) => Shorten(line, MaxErrorLength);

        private static string GetString(IDictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) ? value as string : null;
        }
    }

    /// <summary>
    /// The state of one chat turn as the pane draws it: the steps (tool calls) the model ran,
    /// the text it wrote between them (muted notes), and the text after the last tool call (the
    /// answer). WPF-free; the pane feeds it tokens and activity events on the UI thread.
    /// </summary>
    public sealed class TurnActivity
    {
        public const int MaxVisibleSteps = 8;
        public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(60);

        public enum StepState { Running, Done, Failed, Cancelled }

        public sealed class Step
        {
            public string Id { get; internal set; }
            public string Command { get; internal set; }
            public string Detail { get; internal set; }
            public StepState State { get; internal set; }
            public string ErrorLine { get; internal set; }
        }

        /// <summary>One row of the block: either a step or a note (intermediate text).</summary>
        public sealed class Item
        {
            public Step Step { get; internal set; }
            public string Note { get; internal set; }
            public bool IsStep => Step != null;
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly StringBuilder _segment = new StringBuilder();
        private string _pendingEcho;

        public TurnActivity(DateTime startedUtc)
        {
            StartedUtc = startedUtc;
            LastProgressUtc = startedUtc;
        }

        public DateTime StartedUtc { get; }
        public DateTime LastProgressUtc { get; private set; }
        public DateTime? FinishedUtc { get; private set; }
        public bool IsFinished => FinishedUtc.HasValue;

        /// <summary>Set by <see cref="Finish"/>: the text to render as the assistant's answer.</summary>
        public string Answer { get; private set; } = "";

        public IReadOnlyList<Item> Items => _items;
        public IEnumerable<Step> Steps => _items.Where(i => i.IsStep).Select(i => i.Step);
        public int StepCount => _items.Count(i => i.IsStep);
        public int FailedCount => Steps.Count(s => s.State == StepState.Failed);
        public bool HasSteps => _items.Any(i => i.IsStep);
        public bool HasRunningStep => Steps.Any(s => s.State == StepState.Running);

        /// <summary>Text streamed since the last tool call — the answer so far, if no tool follows.</summary>
        public string LiveText => _segment.ToString().Trim();

        /// <summary>Nothing to show yet, or the model is between steps: "Aan het denken…".</summary>
        public bool IsThinking => !IsFinished && !HasRunningStep && LiveText.Length == 0;

        public TimeSpan Elapsed(DateTime nowUtc) => (FinishedUtc ?? nowUtc) - StartedUtc;

        /// <summary>No token or event for <see cref="StallThreshold"/> while the turn still runs.</summary>
        public bool IsStalled(DateTime nowUtc) => !IsFinished && nowUtc - LastProgressUtc >= StallThreshold;

        public void AppendText(string delta, DateTime nowUtc)
        {
            if (IsFinished || string.IsNullOrEmpty(delta)) return;
            LastProgressUtc = nowUtc;

            // The backend's own status line for the step just started: the step row replaces it.
            if (_pendingEcho != null && delta == _pendingEcho)
            {
                _pendingEcho = null;
                return;
            }
            _pendingEcho = null;
            _segment.Append(delta);
        }

        public void Apply(ChatActivityEvent e, DateTime nowUtc)
        {
            if (IsFinished || e == null) return;
            LastProgressUtc = nowUtc;

            if (e.Kind == ChatActivityKind.ToolStarted)
            {
                if (e.ToolId != null && FindStep(e.ToolId) != null) return;

                // Text written before this tool call was an intermediate note, not the answer.
                FlushSegmentAsNote();
                _items.Add(new Item
                {
                    Step = new Step
                    {
                        Id = e.ToolId,
                        Command = e.Command ?? "",
                        Detail = e.Detail ?? "",
                        State = StepState.Running
                    }
                });
                _pendingEcho = string.IsNullOrEmpty(e.EchoText) ? null : e.EchoText;
            }
            else
            {
                var step = FindStep(e.ToolId)
                    // No id (shouldn't happen): finish the oldest running step.
                    ?? (e.ToolId == null ? Steps.FirstOrDefault(s => s.State == StepState.Running) : null);
                if (step == null || step.State != StepState.Running) return;
                step.State = string.IsNullOrEmpty(e.ErrorLine) ? StepState.Done : StepState.Failed;
                step.ErrorLine = e.ErrorLine;
            }
        }

        /// <summary>
        /// Ends the turn. Steps still running become Done (or Cancelled). The answer is the text
        /// after the last tool call; when the model ended with a step without writing anything
        /// after it, its last note is promoted to the answer so the turn never ends silently.
        /// </summary>
        public void Finish(DateTime nowUtc, bool cancelled = false, string fallbackText = null)
        {
            if (IsFinished) return;
            FinishedUtc = nowUtc;
            foreach (var s in Steps.Where(s => s.State == StepState.Running))
                s.State = cancelled ? StepState.Cancelled : StepState.Done;

            var answer = LiveText;
            _segment.Clear();
            if (answer.Length == 0)
            {
                var lastNote = _items.FindLastIndex(i => !i.IsStep);
                if (lastNote >= 0)
                {
                    answer = _items[lastNote].Note;
                    _items.RemoveAt(lastNote);
                }
            }
            if (answer.Length == 0 && !HasSteps && !string.IsNullOrWhiteSpace(fallbackText))
                answer = fallbackText.Trim();
            Answer = answer;
        }

        /// <summary>
        /// The rows to draw. Live (collapsed=false, expanded=false): the last
        /// <see cref="MaxVisibleSteps"/> steps with the notes between them; <paramref name="hiddenSteps"/>
        /// counts the steps above. Expanded: everything.
        /// </summary>
        public IReadOnlyList<Item> VisibleItems(bool showAll, out int hiddenSteps)
        {
            hiddenSteps = 0;
            var total = StepCount;
            if (showAll || total <= MaxVisibleSteps)
                return _items.ToList();

            hiddenSteps = total - MaxVisibleSteps;
            var seen = 0;
            var start = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                if (!_items[i].IsStep) continue;
                if (seen == hiddenSteps) { start = i; break; }
                seen++;
            }
            return _items.Skip(start).ToList();
        }

        private void FlushSegmentAsNote()
        {
            var note = LiveText;
            _segment.Clear();
            if (note.Length > 0)
                _items.Add(new Item { Note = note });
        }

        private Step FindStep(string id) =>
            id == null ? null : Steps.FirstOrDefault(s => s.Id == id);
    }
}
