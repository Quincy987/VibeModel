using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Minimal HTTP/1.1 server using TcpListener (no admin rights required).
    /// Listens on 127.0.0.1 only — not accessible from network.
    ///
    /// Supports:
    ///   GET /command              → execute "command" with no args
    ///   GET /command?args=x+y+z   → execute "command" with args "x y z"
    ///   GET /command%20x%20y      → same (safety net: args in the path instead of ?args=)
    ///   POST /batch               → body = one command per line
    ///   GET /health               → "OK"
    /// </summary>
    public class RevitHttpServer : IDisposable
    {
        private const int BasePort = 18884;
        private const int MaxPortRetries = 5;

        // Loopback host for clients INSIDE this process (and for the CLI child we spawn).
        // Must stay a literal IPv4 address: Start() below binds IPAddress.Loopback (IPv4
        // only), while "localhost" resolves to ::1 first on Windows. Where an IPv6 connect
        // attempt is silently dropped instead of refused (some endpoint-protection setups),
        // a request to "localhost" burns its whole timeout and the caller wrongly concludes
        // the server is down. See ClaudeCodeBackend.IsServerHealthy.
        internal const string LoopbackHost = "127.0.0.1";

        /// <summary>
        /// Base URL ("http://127.0.0.1:&lt;port&gt;") for callers inside this process and for
        /// the CLI child we spawn. Use this instead of hand-building the URL so the host
        /// can't drift back to "localhost" at a single call site — see <see cref="LoopbackHost"/>.
        /// </summary>
        internal static string BaseUrl(int port)
        {
            return "http://" + LoopbackHost + ":" + port;
        }

        // Shared-secret gate, decided once at startup by ServerAuth: the VIBEMODEL_TOKEN env var
        // when set, else a random per-session token (published in the servers file), else — only
        // with OpenServer = true — no token at all. When a token is in force, every request except
        // /health must present it in the X-VibeModel-Token header.
        internal const string TokenEnvVar = "VIBEMODEL_TOKEN";
        internal const string TokenHeader = "X-VibeModel-Token";
        private readonly string _authToken;

        private TcpListener _listener;
        private Thread _listenThread;
        private volatile bool _running;
        private readonly RevitCommandHandler _commandHandler;

        public int ActivePort { get; private set; }
        public bool IsRunning => _running;

        public RevitHttpServer(RevitCommandHandler commandHandler)
        {
            _commandHandler = commandHandler;
            _authToken = ServerAuth.Initialize();
        }

        public bool Start()
        {
            for (int i = 0; i < MaxPortRetries; i++)
            {
                int port = BasePort + i;
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Start();
                    ActivePort = port;
                    _running = true;

                    _listenThread = new Thread(ListenLoop)
                    {
                        IsBackground = true,
                        Name = "VibeModel-HTTP"
                    };
                    _listenThread.Start();

                    Logger.Info("HTTP server started on http://127.0.0.1:" + port + " - " + ServerAuth.Describe(port));
                    if (_authToken == null)
                        Logger.Warn("HTTP server is OPEN (OpenServer = true): any local process can run commands without a token");
                    ServerDiscovery.Write(port, _authToken);
                    return true;
                }
                catch (SocketException)
                {
                    Logger.Warn("Port " + port + " in use, trying next...");
                    try { _listener.Stop(); } catch { }
                    _listener = null;
                }
            }

            Logger.Error("Failed to start HTTP server on ports " + BasePort + "-" + (BasePort + MaxPortRetries - 1));
            return false;
        }

        public void Stop()
        {
            if (ActivePort != 0)
                ServerDiscovery.Delete(ActivePort);

            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch { }

            if (_listenThread != null && _listenThread.IsAlive)
            {
                _listenThread.Join(3000);
            }

            Logger.Info("HTTP server stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) when (!_running)
                {
                    // Expected when stopping
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        Logger.Error("Accept error", ex);
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 35000; // 30s command timeout + 5s buffer

                using (var stream = client.GetStream())
                {
                    var request = ReadHttpRequest(stream, bytes => stream.Write(bytes, 0, bytes.Length));
                    if (request == null)
                    {
                        SendResponse(stream, 400, "Bad Request");
                        return;
                    }

                    // A batch executes as one request and uses a size-scaled timeout (up to ~5 min).
                    // Give the response socket headroom to match (batch timeout 300s + buffer).
                    if (request.Method == "POST" &&
                        request.Path.TrimStart('/').StartsWith("batch"))
                    {
                        client.SendTimeout = 305000;
                    }

                    var response = ProcessRequest(request);
                    SendResponse(stream, response.StatusCode, response.Body, response.ContentType);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Client handler error", ex);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        internal const int MaxHeaderBytes = 65536;
        internal const int MaxBodyBytes = 1048576; // 1 MB

        /// <summary>
        /// Reads one request as BYTES: the head up to the blank line, then exactly Content-Length
        /// body bytes, and only then decodes the body as UTF-8 — once. (Decoding every 4 KB read
        /// separately broke a °, m² or ë that straddled a read boundary, and re-encoding the
        /// decoded overflow changed the byte count.) <paramref name="interim"/> receives
        /// "100 Continue" when the client waits for it (curl does for bodies over 1 KB). Null when
        /// the connection closed early or the head is malformed or too large.
        /// </summary>
        internal static HttpRequest ReadHttpRequest(Stream stream, Action<byte[]> interim = null)
        {
            var head = new MemoryStream();
            var buffer = new byte[4096];
            int headerEnd = -1;
            byte[] pending = null; // body bytes that arrived together with the head

            while (headerEnd < 0)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) return null;
                head.Write(buffer, 0, bytesRead);

                var all = head.GetBuffer();
                int length = (int)head.Length;
                // The terminator may straddle two reads: search from 3 bytes before this one.
                headerEnd = IndexOfHeaderEnd(all, Math.Max(0, length - bytesRead - 3), length);
                if (headerEnd >= 0)
                {
                    int bodyStart = headerEnd + 4;
                    pending = new byte[length - bodyStart];
                    Array.Copy(all, bodyStart, pending, 0, pending.Length);
                }
                else if (length > MaxHeaderBytes)
                {
                    return null; // Headers too large, reject
                }
            }

            var headerSection = Encoding.UTF8.GetString(head.GetBuffer(), 0, headerEnd);
            var lines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            // Parse request line: "GET /command?args=foo HTTP/1.1"
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var method = requestLine[0];
            var rawPath = requestLine[1];

            // Every header, first occurrence wins: Accept (text vs JSON), the token, the chat turn
            // (journal), Content-Length / Expect, and the browser markers RequestGuard checks.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                var name = lines[i].Substring(0, colon).Trim();
                if (!headers.ContainsKey(name))
                    headers[name] = lines[i].Substring(colon + 1).Trim();
            }

            string body = null;
            if (method == "POST")
            {
                int contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var lengthHeader))
                    int.TryParse(lengthHeader, out contentLength);

                if (contentLength > 0 && contentLength <= MaxBodyBytes)
                {
                    var bodyBytes = new byte[contentLength];
                    int bodyRead = Math.Min(pending.Length, contentLength);
                    Array.Copy(pending, 0, bodyBytes, 0, bodyRead);

                    if (bodyRead < contentLength && interim != null &&
                        headers.TryGetValue("Expect", out var expect) &&
                        expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    {
                        interim(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                    }

                    // Read remaining body bytes with blocking reads
                    while (bodyRead < contentLength)
                    {
                        int read = stream.Read(bodyBytes, bodyRead, contentLength - bodyRead);
                        if (read == 0) break; // Connection closed
                        bodyRead += read;
                    }

                    body = Encoding.UTF8.GetString(bodyBytes, 0, bodyRead);
                }
                else if (contentLength == 0 && pending.Length > 0)
                {
                    // No Content-Length but some body data arrived
                    body = Encoding.UTF8.GetString(pending);
                }
            }

            headers.TryGetValue("Accept", out var accept);
            headers.TryGetValue(TokenHeader, out var token);
            // Project journal: which chat turn sent this (id + url-encoded question).
            headers.TryGetValue(Chat.ProjectJournal.TurnHeader, out var turn);
            return new HttpRequest(method, rawPath, body, accept, token, headers) { Turn = Chat.ChatTurnRef.FromHeader(turn) };
        }

        private static int IndexOfHeaderEnd(byte[] data, int from, int length)
        {
            for (int i = from; i + 3 < length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private HttpResponse ProcessRequest(HttpRequest request)
        {
            // Parse path and query string
            var path = request.Path;
            var queryArgs = "";
            var rawQuery = "";

            int qmark = path.IndexOf('?');
            if (qmark >= 0)
            {
                rawQuery = path.Substring(qmark + 1);
                path = path.Substring(0, qmark);
                queryArgs = ParseQueryArgs(rawQuery);
            }

            // Strip leading slash
            if (path.StartsWith("/"))
                path = path.Substring(1);

            // Safety net: "/wall%200%200%205000%200" (args in the path) runs as "wall" with
            // args "0 0 5000 0" instead of failing with "Unknown command 'wall%200…'".
            string pathArgs;
            if (TrySplitPathArgs(path, out var pathCommand, out pathArgs))
            {
                Logger.Info("Args in the URL path - treating '" + pathCommand + "' + '" + pathArgs + "' as ?args=");
                path = pathCommand;
                queryArgs = string.IsNullOrEmpty(queryArgs) ? pathArgs : pathArgs + " " + queryArgs;
            }

            // Response format: ?format=json or an explicit Accept: application/json.
            // Curl's default Accept (*/*) must NOT trigger JSON — text stays the default.
            bool wantsJson = QueryHasFormatJson(rawQuery)
                          || (request.Accept != null &&
                              request.Accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0);
            var fmt = wantsJson ? ResponseFormat.Json : ResponseFormat.Text;

            // Browser / DNS-rebinding gate, before anything else — /health included: a page that
            // can probe which ports answer is a page that can enumerate running Revit instances.
            var refused = RequestGuard.Check(request.Headers, ActivePort);
            if (refused != null)
            {
                Logger.Warn("Refused request to /" + path + ": " + refused);
                return Forbidden(fmt, refused);
            }

            // Route
            // /health (and the bare root) stays unauthenticated so liveness probes keep
            // working regardless of whether a token is configured.
            if (string.IsNullOrEmpty(path) || path == "health")
            {
                return new HttpResponse(200, "OK");
            }

            // Shared-secret gate: when a token is in force (the default), every non-health request
            // must present a matching X-VibeModel-Token header. Open server (opt-in) => no check.
            if (_authToken != null && !FixedTimeEquals(_authToken, request.Token))
            {
                return Unauthorized(fmt);
            }

            if (path == "batch" && request.Method == "POST")
            {
                return HandleBatch(request.Body, rawQuery, fmt, request.Turn);
            }

            // Single command — use POST body as args if no query args
            var args = queryArgs;
            if (string.IsNullOrEmpty(args) && request.Method == "POST" && !string.IsNullOrEmpty(request.Body))
            {
                args = request.Body.TrimEnd('\r', '\n');
            }

            var result = _commandHandler.EnqueueAndWait(path, args, fmt, request.Turn);
            return new HttpResponse(200, result,
                fmt == ResponseFormat.Json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
        }

        // Builds a 401 response in the requested format. JSON uses the standard error envelope
        // ({"ok":false,"error":{code,message,suggestion}}); text mode uses the "ERROR:" convention.
        private HttpResponse Unauthorized(ResponseFormat fmt)
        {
            const string message = "Missing or invalid token.";
            var suggestion = "Send the server's token in the " + TokenHeader + " request header. " +
                (ServerAuth.Mode == ServerAuthMode.EnvironmentToken
                    ? "It is the value of " + TokenEnvVar + " in the environment Revit was started from."
                    : "This session's token is the \"token\" field of " + ServerDiscovery.GetFilePath(ActivePort) + ".");
            return ErrorResponse(401, fmt, "unauthorized", message, suggestion);
        }

        // 403 for a request a web page made (Origin / Sec-Fetch-*) or that names another host.
        private HttpResponse Forbidden(ResponseFormat fmt, string reason)
        {
            const string suggestion = "Only local tools may drive this server: call it from curl, a script or the chat pane, " +
                                      "without browser headers, at http://127.0.0.1:<port>.";
            return ErrorResponse(403, fmt, "forbidden", reason, suggestion);
        }

        // JSON uses the standard error envelope ({"ok":false,"error":{code,message,suggestion}});
        // text mode uses the "ERROR:" convention with a Hint line.
        private static HttpResponse ErrorResponse(int status, ResponseFormat fmt, string code, string message, string suggestion)
        {
            if (fmt == ResponseFormat.Json)
            {
                var body = CommandResult.Error(code, message, suggestion)
                    .Render(ResponseFormat.Json, new JavaScriptSerializer());
                return new HttpResponse(status, body, "application/json; charset=utf-8");
            }

            return new HttpResponse(status, "ERROR: " + message + "\nHint: " + suggestion);
        }

        // Length-independent, short-circuit-free comparison to avoid leaking the token via timing.
        private static bool FixedTimeEquals(string expected, string actual)
        {
            if (expected == null || actual == null)
                return false;

            var a = Encoding.UTF8.GetBytes(expected);
            var b = Encoding.UTF8.GetBytes(actual);

            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i < b.Length ? i : 0];

            return diff == 0;
        }

        // Scans the query string for an explicit format=json pair (ignored by ParseQueryArgs).
        internal static bool QueryHasFormatJson(string query)
        {
            if (string.IsNullOrEmpty(query)) return false;
            foreach (var param in query.Split('&'))
            {
                var kv = param.Split(new[] { '=' }, 2);
                if (kv.Length == 2 &&
                    kv[0].Equals("format", StringComparison.OrdinalIgnoreCase) &&
                    kv[1].Equals("json", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private HttpResponse HandleBatch(string body, string rawQuery, ResponseFormat fmt, Chat.ChatTurnRef turn)
        {
            var parsed = BatchParser.Parse(body, rawQuery);
            if (!parsed.IsValid)
                return new HttpResponse(400, parsed.Error);

            // One request → one TransactionGroup → one undo entry.
            var result = _commandHandler.EnqueueBatchAndWait(parsed.Commands, parsed.Atomic, fmt, turn);
            return new HttpResponse(200, result,
                fmt == ResponseFormat.Json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
        }

        /// <summary>
        /// Recognises a request path that carries the arguments itself — "wall%200%200%205000%200",
        /// "wall 0 0", "wall/0 0 5000 0": the first segment (up to the first space or '/', after
        /// percent-decoding) is the command, the rest is the args, verbatim. False for a plain
        /// command path ("info", "wall"), which is routed exactly as before.
        /// </summary>
        internal static bool TrySplitPathArgs(string path, out string command, out string args)
        {
            command = null;
            args = null;
            if (string.IsNullOrEmpty(path)) return false;

            string decoded;
            try { decoded = Uri.UnescapeDataString(path); }
            catch (UriFormatException) { return false; }

            int cut = -1;
            for (int i = 0; i < decoded.Length; i++)
            {
                if (char.IsWhiteSpace(decoded[i]) || decoded[i] == '/') { cut = i; break; }
            }
            if (cut <= 0) return false;

            var name = decoded.Substring(0, cut);
            foreach (var c in name)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;

            command = name;
            args = decoded.Substring(cut + 1).Trim();
            return true;
        }

        internal static string ParseQueryArgs(string query)
        {
            // Parse "args=hello+world&other=x" → "hello world"
            foreach (var param in query.Split('&'))
            {
                var kv = param.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && kv[0] == "args")
                {
                    return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                }
            }
            return "";
        }

        private void SendResponse(NetworkStream stream, int statusCode, string body,
            string contentType = "text/plain; charset=utf-8")
        {
            string statusText;
            switch (statusCode)
            {
                case 200: statusText = "OK"; break;
                case 401: statusText = "Unauthorized"; break;
                case 403: statusText = "Forbidden"; break;
                default: statusText = "Bad Request"; break;
            }
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");

            var header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n"
                       + "Content-Type: " + contentType + "\r\n"
                       + "Content-Length: " + bodyBytes.Length + "\r\n"
                       + "Connection: close\r\n"
                       + "\r\n";

            var headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0)
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        internal class HttpRequest
        {
            public string Method { get; }
            public string Path { get; }
            public string Body { get; }
            public string Accept { get; }
            public string Token { get; }
            public IDictionary<string, string> Headers { get; }
            public Chat.ChatTurnRef Turn { get; set; }

            public HttpRequest(string method, string path, string body, string accept = null, string token = null,
                IDictionary<string, string> headers = null)
            {
                Method = method;
                Path = path;
                Body = body;
                Accept = accept;
                Token = token;
                Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private class HttpResponse
        {
            public int StatusCode { get; }
            public string Body { get; }
            public string ContentType { get; }

            public HttpResponse(int statusCode, string body,
                string contentType = "text/plain; charset=utf-8")
            {
                StatusCode = statusCode;
                Body = body;
                ContentType = contentType;
            }
        }
    }
}
