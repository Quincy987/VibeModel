using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The direct API backend end to end against a local mock of the Messages API (ApiBaseUrl):
    /// real HttpClient, real SSE parsing, real tool loop. Revit is not there — tool calls go to a
    /// closed port and come back as errors, which is fine: what is checked is what the backend sends.
    /// </summary>
    [Collection("ProcessSettings")]
    public class DirectBackendStreamTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _envKey;
        private readonly MockMessagesApi _api;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public DirectBackendStreamTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-direct-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test-0000000000000000");
            SettingsManager.OverridePathForTests(Path.Combine(_dir, "settings.json"));
            ManagedSettings.OverrideSearchPathsForTests(Path.Combine(_dir, "managed.json"));
            _api = new MockMessagesApi();
            SettingsManager.SetApiBaseUrl("http://127.0.0.1:" + _api.Port);
        }

        public void Dispose()
        {
            _api.Dispose();
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _envKey);
            SettingsManager.OverridePathForTests(null);
            ManagedSettings.OverrideSearchPathsForTests(null);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string Sse(params string[] events) => string.Concat(events);

        private static string Ev(string type, object data) =>
            "event: " + type + "\ndata: " + Json.Serialize(data) + "\n\n";

        // A response that thinks (empty text, a signature), then calls revit_info.
        private static string ThinkThenTool(string signature) => Sse(
            Ev("message_start", new { type = "message_start", message = new { model = "claude-sonnet-5-5", usage = new { input_tokens = 1000, output_tokens = 1 } } }),
            Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } }),
            Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = signature } }),
            Ev("content_block_stop", new { type = "content_block_stop", index = 0 }),
            Ev("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = "toolu_1", name = "revit_info", input = new { } } }),
            Ev("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = "{\"args\":\"\"}" } }),
            Ev("content_block_stop", new { type = "content_block_stop", index = 1 }),
            Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 40 } }),
            Ev("message_stop", new { type = "message_stop" }));

        private static string Answer(string text) => Sse(
            Ev("message_start", new { type = "message_start", message = new { model = "claude-sonnet-5-5", usage = new { input_tokens = 1200, output_tokens = 1 } } }),
            Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }),
            Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = text } }),
            Ev("content_block_stop", new { type = "content_block_stop", index = 0 }),
            Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 10 } }),
            Ev("message_stop", new { type = "message_stop" }));

        private (string answer, string error, List<TurnUsage> usage) RunTurn(AnthropicDirectBackend backend, string question)
        {
            string answer = null, error = null;
            var usage = new List<TurnUsage>();
            var done = new ManualResetEventSlim();
            backend.ActivitySink = e => { if (e.Kind == ChatActivityKind.Usage) lock (usage) usage.Add(e.Usage); };
            backend.SendMessage(question, null, _ => { },
                a => { answer = a; done.Set(); },
                e => { error = e; done.Set(); },
                CancellationToken.None);
            Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "turn did not finish");
            return (answer, error, usage);
        }

        // JavaScriptSerializer hands arrays back as ArrayList (typed target) or object[] (untyped).
        private static object[] Items(object value) =>
            value as object[] ?? (value as System.Collections.ArrayList)?.ToArray() ?? new object[0];

        private static List<Dictionary<string, object>> Messages(string body) =>
            Items(Json.Deserialize<Dictionary<string, object>>(body)["messages"]).Cast<Dictionary<string, object>>().ToList();

        private static object[] Content(Dictionary<string, object> message) => Items(message["content"]);

        [Fact]
        public void ThinkingBlock_IsSentBackWithTheToolResult_SystemPromptFrozen_UsageSummed()
        {
            _api.Enqueue(MockMessagesApi.Ok(ThinkThenTool("sig-abc")));
            _api.Enqueue(MockMessagesApi.Ok(Answer("Klaar.")));
            var backend = new AnthropicDirectBackend(new ClaudeCommandRegistry().GetCommands(), 1);

            var (answer, error, usage) = RunTurn(backend, "Wat zit er in dit model?");

            Assert.Null(error);
            Assert.Contains("Klaar.", answer);
            Assert.Equal(2, _api.Requests.Count);

            var first = Json.Deserialize<Dictionary<string, object>>(_api.Requests[0]);
            var second = Json.Deserialize<Dictionary<string, object>>(_api.Requests[1]);
            Assert.Equal("claude-sonnet-5-5", first["model"]);
            Assert.Equal(first["system"], second["system"]);            // frozen for the turn

            var assistant = Messages(_api.Requests[1])[1];
            Assert.Equal("assistant", assistant["role"]);
            var blocks = Content(assistant).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal("thinking", blocks[0]["type"]);
            Assert.Equal("sig-abc", blocks[0]["signature"]);              // replayed unchanged
            Assert.Equal("tool_use", blocks[1]["type"]);
            Assert.DoesNotContain(blocks, b => (string)b["type"] == "text"); // no empty text block

            var toolResult = (Dictionary<string, object>)Content(Messages(_api.Requests[1])[2])[0];
            Assert.Equal("tool_result", toolResult["type"]);
            Assert.Equal("toolu_1", toolResult["tool_use_id"]);

            var last = usage.Last();
            Assert.Equal(2200, last.InputTokens);
            Assert.Equal(50, last.OutputTokens);
            Assert.Equal("claude-sonnet-5-5", last.Model);
        }

        [Fact]
        public void ThinkingRefusedByTheApi_IsRemovedAndTheRequestRetriedOnce()
        {
            _api.Enqueue(MockMessagesApi.Ok(ThinkThenTool("sig-stale")));
            _api.Enqueue(MockMessagesApi.Error(400,
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"messages.1.content.0: Invalid `signature` in `thinking` block.\"}}"));
            _api.Enqueue(MockMessagesApi.Ok(Answer("Toch gelukt.")));
            var backend = new AnthropicDirectBackend(new ClaudeCommandRegistry().GetCommands(), 1);

            var (answer, error, _) = RunTurn(backend, "Test");

            Assert.Null(error);
            Assert.Contains("Toch gelukt.", answer);
            Assert.Equal(3, _api.Requests.Count);
            var retried = Content(Messages(_api.Requests[2])[1]).Cast<Dictionary<string, object>>().ToList();
            Assert.DoesNotContain(retried, b => (string)b["type"] == "thinking");
            Assert.Contains(retried, b => (string)b["type"] == "tool_use");
        }

        [Fact]
        public void Refusal_IsExplained_NotAnEmptyAnswer()
        {
            _api.Enqueue(MockMessagesApi.Ok(Sse(
                Ev("message_start", new { type = "message_start", message = new { model = "claude-sonnet-5-5", usage = new { input_tokens = 10, output_tokens = 0 } } }),
                Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = "refusal" }, usage = new { output_tokens = 0 } }),
                Ev("message_stop", new { type = "message_stop" }))));
            var backend = new AnthropicDirectBackend(new ClaudeCommandRegistry().GetCommands(), 1);

            var (_, error, _) = RunTurn(backend, "x");

            Assert.Equal(Strings.Get(Strings.ApiRefusal), error);
        }

        [Fact]
        public void SecondTurn_WithAChangedHistory_SendsNoStaleThinking()
        {
            // Turn 1 thinks + calls a tool. The system prompt carries live facts and is rebuilt per
            // turn — when it differs, the thinking of earlier turns must not be sent again.
            _api.Enqueue(MockMessagesApi.Ok(ThinkThenTool("sig-1")));
            _api.Enqueue(MockMessagesApi.Ok(Answer("Een.")));
            _api.Enqueue(MockMessagesApi.Ok(Answer("Twee.")));
            var system = "system prompt, turn 1";
            var backend = new AnthropicDirectBackend(new ClaudeCommandRegistry().GetCommands(), 1)
            {
                SystemPromptSource = () => system
            };

            RunTurn(backend, "Eerste vraag");
            system = "system prompt, turn 2 (the active view changed)";
            var (answer, error, _) = RunTurn(backend, "Second question");
            Assert.Null(error);
            Assert.Contains("Twee.", answer);

            var third = _api.Requests[2];
            Assert.DoesNotContain("sig-1", third);
            Assert.Contains("toolu_1", third);                  // the tool call itself stays
        }

        /// <summary>A one-connection-at-a-time HTTP server answering queued responses and recording bodies.</summary>
        private sealed class MockMessagesApi : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Queue<string> _responses = new Queue<string>();
            private readonly Thread _thread;
            private volatile bool _stop;
            public readonly List<string> Requests = new List<string>();
            public int Port { get; }

            public MockMessagesApi()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            public static string Ok(string sse) =>
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n" + sse;

            public static string Error(int status, string json) =>
                "HTTP/1.1 " + status + " Bad Request\r\nContent-Type: application/json\r\nContent-Length: " +
                Encoding.UTF8.GetByteCount(json) + "\r\nConnection: close\r\n\r\n" + json;

            public void Enqueue(string response) { lock (_responses) _responses.Enqueue(response); }

            private void Serve()
            {
                while (!_stop)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { return; }
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var headers = ReadHeaders(stream);
                        if (headers == null) continue;
                        if (headers.IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                            stream.Write(cont, 0, cont.Length);
                        }
                        var length = 0;
                        foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                length = int.Parse(line.Substring(15).Trim());
                        var body = new byte[length];
                        var read = 0;
                        while (read < length)
                        {
                            var n = stream.Read(body, read, length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        lock (Requests) Requests.Add(Encoding.UTF8.GetString(body, 0, read));
                        string response;
                        lock (_responses)
                            response = _responses.Count > 0 ? _responses.Dequeue() : Error(500, "{\"error\":{\"message\":\"no response queued\"}}");
                        var bytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
            }

            private static string ReadHeaders(NetworkStream stream)
            {
                var sb = new StringBuilder();
                var buf = new byte[1];
                while (stream.Read(buf, 0, 1) == 1)
                {
                    sb.Append((char)buf[0]);
                    if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") return sb.ToString();
                }
                return null;
            }

            public void Dispose()
            {
                _stop = true;
                _listener.Stop();
            }
        }
    }
}
