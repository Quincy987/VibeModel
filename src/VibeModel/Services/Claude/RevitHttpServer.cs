using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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

        private TcpListener _listener;
        private Thread _listenThread;
        private volatile bool _running;
        private readonly RevitCommandHandler _commandHandler;

        public int ActivePort { get; private set; }
        public bool IsRunning => _running;

        public RevitHttpServer(RevitCommandHandler commandHandler)
        {
            _commandHandler = commandHandler;
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

                    Logger.Info("HTTP server started on http://127.0.0.1:" + port);
                    return true;
                }
                catch (SocketException)
                {
                    Logger.Warn("Port " + port + " in use, trying next...");
                    _listener = null;
                }
            }

            Logger.Error("Failed to start HTTP server on ports " + BasePort + "-" + (BasePort + MaxPortRetries - 1));
            return false;
        }

        public void Stop()
        {
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

                    var response = ProcessRequest(request);
                    SendResponse(stream, response.StatusCode, response.Body);
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
            var buffer = new byte[8192];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return null;

            var raw = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            // Parse request line: "GET /command?args=foo HTTP/1.1"
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var method = requestLine[0];
            var rawPath = requestLine[1];

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

                // Find body after double CRLF
                int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    body = raw.Substring(headerEnd + 4);

                    // If we haven't received the full body yet, keep reading
                    while (body.Length < contentLength && stream.DataAvailable)
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                            body += Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    }
                }
            }

            return new HttpRequest(method, rawPath, body);
        }

        private HttpResponse ProcessRequest(HttpRequest request)
        {
            // Parse path and query string
            var path = request.Path;
            var queryArgs = "";

            int qmark = path.IndexOf('?');
            if (qmark >= 0)
            {
                var query = path.Substring(qmark + 1);
                path = path.Substring(0, qmark);
                queryArgs = ParseQueryArgs(query);
            }

            // Strip leading slash
            if (path.StartsWith("/"))
                path = path.Substring(1);

            // Route
            if (string.IsNullOrEmpty(path) || path == "health")
            {
                return new HttpResponse(200, "OK");
            }

            if (path == "batch" && request.Method == "POST")
            {
                return HandleBatch(request.Body);
            }

            // Single command
            var result = _commandHandler.EnqueueAndWait(path, queryArgs);
            return new HttpResponse(200, result);
        }

        private HttpResponse HandleBatch(string body)
        {
            if (string.IsNullOrEmpty(body))
                return new HttpResponse(400, "ERROR: Empty batch body");

            var lines = body.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0];
                var args = parts.Length > 1 ? parts[1] : "";

                sb.AppendLine(">>> " + trimmed);
                var result = _commandHandler.EnqueueAndWait(cmd, args);
                sb.AppendLine(result);
                sb.AppendLine();
            }

            return new HttpResponse(200, sb.ToString());
        }

        private string ParseQueryArgs(string query)
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

        private void SendResponse(NetworkStream stream, int statusCode, string body)
        {
            var statusText = statusCode == 200 ? "OK" : "Bad Request";
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");

            var header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n"
                       + "Content-Type: text/plain; charset=utf-8\r\n"
                       + "Content-Length: " + bodyBytes.Length + "\r\n"
                       + "Connection: close\r\n"
                       + "Access-Control-Allow-Origin: *\r\n"
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

            public HttpRequest(string method, string path, string body)
            {
                Method = method;
                Path = path;
                Body = body;
            }
        }

        private class HttpResponse
        {
            public int StatusCode { get; }
            public string Body { get; }

            public HttpResponse(int statusCode, string body)
            {
                StatusCode = statusCode;
                Body = body;
            }
        }
    }
}
