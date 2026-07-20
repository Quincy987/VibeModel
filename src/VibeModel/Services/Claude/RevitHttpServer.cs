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
    ///   POST /batch               → body = one command per line
    ///   GET /health               → "OK"
    /// </summary>
    public class RevitHttpServer : IDisposable
    {
        private const int BasePort = 18884;
        private const int MaxPortRetries = 5;

        // Optional shared-secret gate. Read once at startup from the VIBEMODEL_TOKEN env var.
        // Null/empty => auth disabled and the server behaves exactly as before (fully open).
        // When set, every request except /health must present a matching X-VibeModel-Token header.
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

            var token = Environment.GetEnvironmentVariable(TokenEnvVar);
            _authToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
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

                    Logger.Info("HTTP server started on http://127.0.0.1:" + port
                              + (_authToken != null ? " (token auth ENABLED)" : " (open, no token)"));
                    ServerDiscovery.Write(port);
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
                    var request = ReadHttpRequest(stream);
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

        private HttpRequest ReadHttpRequest(NetworkStream stream)
        {
            // Read headers — accumulate until we find \r\n\r\n
            var headerBuilder = new StringBuilder();
            var buffer = new byte[4096];
            int headerEnd = -1;
            byte[] overflow = null;
            int overflowLength = 0;

            while (headerEnd < 0)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) return null;

                headerBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                var headerStr = headerBuilder.ToString();
                headerEnd = headerStr.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                if (headerEnd >= 0)
                {
                    // Everything after \r\n\r\n is body overflow
                    int bodyStart = headerEnd + 4;
                    var fullBytes = Encoding.UTF8.GetBytes(headerStr);
                    var bodyOverflowStr = headerStr.Substring(bodyStart);
                    overflow = Encoding.UTF8.GetBytes(bodyOverflowStr);
                    overflowLength = overflow.Length;
                }

                if (headerBuilder.Length > 65536)
                    return null; // Headers too large, reject
            }

            var raw = headerBuilder.ToString();
            var headerSection = raw.Substring(0, headerEnd);
            var lines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            // Parse request line: "GET /command?args=foo HTTP/1.1"
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var method = requestLine[0];
            var rawPath = requestLine[1];

            // Capture Accept (used to decide text vs JSON response format) and the optional
            // auth token header (used by the shared-secret gate when a token is configured).
            string accept = null;
            string token = null;
            foreach (var line in lines)
            {
                if (accept == null && line.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase))
                {
                    accept = line.Substring(7).Trim();
                }
                else if (token == null && line.StartsWith(TokenHeader + ":", StringComparison.OrdinalIgnoreCase))
                {
                    token = line.Substring(TokenHeader.Length + 1).Trim();
                }
            }

            // Find Content-Length for POST body
            string body = null;
            if (method == "POST")
            {
                int contentLength = 0;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring(15).Trim(), out contentLength);
                    }
                }

                if (contentLength > 0 && contentLength <= 1048576) // 1MB max body
                {
                    // Start with any overflow from header read
                    var bodyBytes = new byte[contentLength];
                    int bodyRead = 0;

                    if (overflow != null && overflowLength > 0)
                    {
                        int toCopy = Math.Min(overflowLength, contentLength);
                        Array.Copy(overflow, 0, bodyBytes, 0, toCopy);
                        bodyRead = toCopy;
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
                else if (overflow != null && overflowLength > 0)
                {
                    // No Content-Length but some body data arrived
                    body = Encoding.UTF8.GetString(overflow, 0, overflowLength);
                }
            }

            return new HttpRequest(method, rawPath, body, accept, token);
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

            // Response format: ?format=json or an explicit Accept: application/json.
            // Curl's default Accept (*/*) must NOT trigger JSON — text stays the default.
            bool wantsJson = QueryHasFormatJson(rawQuery)
                          || (request.Accept != null &&
                              request.Accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0);
            var fmt = wantsJson ? ResponseFormat.Json : ResponseFormat.Text;

            // Route
            // /health (and the bare root) stays unauthenticated so liveness probes keep
            // working regardless of whether a token is configured.
            if (string.IsNullOrEmpty(path) || path == "health")
            {
                return new HttpResponse(200, "OK");
            }

            // Shared-secret gate: when a token is configured, every non-health request must
            // present a matching X-VibeModel-Token header. No token configured => open (unchanged).
            if (_authToken != null && !FixedTimeEquals(_authToken, request.Token))
            {
                return Unauthorized(fmt);
            }

            if (path == "batch" && request.Method == "POST")
            {
                return HandleBatch(request.Body, rawQuery, fmt);
            }

            // Single command — use POST body as args if no query args
            var args = queryArgs;
            if (string.IsNullOrEmpty(args) && request.Method == "POST" && !string.IsNullOrEmpty(request.Body))
            {
                args = request.Body.TrimEnd('\r', '\n');
            }

            var result = _commandHandler.EnqueueAndWait(path, args, fmt);
            return new HttpResponse(200, result,
                fmt == ResponseFormat.Json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
        }

        // Builds a 401 response in the requested format. JSON uses the standard error envelope
        // ({"ok":false,"error":{code,message,suggestion}}); text mode uses the "ERROR:" convention.
        private HttpResponse Unauthorized(ResponseFormat fmt)
        {
            const string message = "Missing or invalid token.";
            const string suggestion = "Send the configured token in the " + TokenHeader + " request header.";

            if (fmt == ResponseFormat.Json)
            {
                var body = CommandResult.Error("unauthorized", message, suggestion)
                    .Render(ResponseFormat.Json, new JavaScriptSerializer());
                return new HttpResponse(401, body, "application/json; charset=utf-8");
            }

            return new HttpResponse(401, "ERROR: " + message + "\nHint: " + suggestion);
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

        private HttpResponse HandleBatch(string body, string rawQuery, ResponseFormat fmt)
        {
            var parsed = BatchParser.Parse(body, rawQuery);
            if (!parsed.IsValid)
                return new HttpResponse(400, parsed.Error);

            // One request → one TransactionGroup → one undo entry.
            var result = _commandHandler.EnqueueBatchAndWait(parsed.Commands, parsed.Atomic, fmt);
            return new HttpResponse(200, result,
                fmt == ResponseFormat.Json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
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

        private class HttpRequest
        {
            public string Method { get; }
            public string Path { get; }
            public string Body { get; }
            public string Accept { get; }
            public string Token { get; }

            public HttpRequest(string method, string path, string body, string accept = null, string token = null)
            {
                Method = method;
                Path = path;
                Body = body;
                Accept = accept;
                Token = token;
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
