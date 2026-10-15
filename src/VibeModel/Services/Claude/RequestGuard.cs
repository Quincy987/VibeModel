using System;
using System.Collections.Generic;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Refuses requests a web browser made. The server binds loopback only, but a page the user
    /// has open can still reach it: <c>&lt;img src="http://127.0.0.1:18884/exec?args=…"&gt;</c> is a
    /// GET no browser blocks, and a hostile domain that resolves to 127.0.0.1 (DNS rebinding) is
    /// even same-origin and can read the answers. Browsers always mark such requests — an
    /// <c>Origin</c> header, <c>Sec-Fetch-*</c> headers, and the page's own name in <c>Host</c> —
    /// while curl, the chat backends (WebClient / HttpWebRequest) and the Claude Code CLI send
    /// none of them and address the server as 127.0.0.1 / localhost. Pure (headers in, verdict
    /// out) so it is unit-tested without a socket.
    /// </summary>
    internal static class RequestGuard
    {
        /// <summary>Null when the request may proceed, else the reason it was refused.</summary>
        internal static string Check(IDictionary<string, string> headers, int port)
        {
            string value;

            // Browsers add Origin to every CORS and POST request (and to cross-origin fetches);
            // nothing that legitimately drives this server sends it.
            if (TryGet(headers, "Origin", out value))
                return "Requests from a web page are refused (Origin: " + Shorten(value) + ").";

            // Fetch metadata (every current browser): "none" = typed in the address bar,
            // "same-origin" = the server's own page (it has none; DNS rebinding is caught by Host).
            string site = null;
            if (TryGet(headers, "Sec-Fetch-Site", out site))
            {
                var s = site.Trim().ToLowerInvariant();
                if (s != "none" && s != "same-origin")
                    return "Requests from a web page are refused (Sec-Fetch-Site: " + Shorten(site) + ").";
            }
            if (TryGet(headers, "Sec-Fetch-Mode", out value) && site == null)
            {
                var m = value.Trim().ToLowerInvariant();
                if (m == "no-cors" || m == "cors" || m == "navigate" || m == "websocket")
                    return "Requests from a web page are refused (Sec-Fetch-Mode: " + Shorten(value) + ").";
            }

            // DNS rebinding: the browser sends the attacker's host name. A missing Host is fine
            // (HTTP/1.0 clients); browsers always send one.
            if (TryGet(headers, "Host", out value) && !IsLoopbackHost(value, port))
                return "Host '" + Shorten(value) + "' is not this server - address it as 127.0.0.1:" + port + ".";

            return null;
        }

        /// <summary>127.0.0.1, localhost or [::1], bare or with this server's port.</summary>
        internal static bool IsLoopbackHost(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            var h = host.Trim().ToLowerInvariant();

            string name = h, portPart = null;
            if (h.StartsWith("["))
            {
                int close = h.IndexOf(']');
                if (close < 0) return false;
                name = h.Substring(0, close + 1);
                var rest = h.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':') return false;
                    portPart = rest.Substring(1);
                }
            }
            else
            {
                int colon = h.IndexOf(':');
                if (colon >= 0)
                {
                    name = h.Substring(0, colon);
                    portPart = h.Substring(colon + 1);
                }
            }

            if (name != "127.0.0.1" && name != "localhost" && name != "[::1]") return false;
            return portPart == null || portPart == port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryGet(IDictionary<string, string> headers, string name, out string value)
        {
            value = null;
            return headers != null && headers.TryGetValue(name, out value) && value != null;
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }
    }
}
