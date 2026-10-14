using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Every call VibeModel makes to the Anthropic Messages API goes out through here: the
    /// configured endpoint (<see cref="SettingsManager.GetApiBaseUrl"/>), Windows' own proxy with
    /// the user's Windows credentials (office proxies, NTLM/Kerberos included), TLS 1.2 or newer,
    /// and certificate validation against the Windows store — so a TLS-inspecting proxy whose root
    /// IT installed just works, and validation is never switched off.
    /// </summary>
    public static class AnthropicHttp
    {
        public const string ApiVersion = "2023-06-01";

        static AnthropicHttp()
        {
            EnsureTls12(ServicePointManager.SecurityProtocol, p => ServicePointManager.SecurityProtocol = p);
        }

        /// <summary>
        /// The setting is process-wide (Revit and every other add-in share it). "SystemDefault" lets
        /// Windows pick (TLS 1.2 and 1.3) and is left alone — OR-ing TLS 1.2 into it would pin the whole
        /// process to TLS 1.2 only. Only an explicit list that lacks TLS 1.2 gets it added.
        /// </summary>
        internal static void EnsureTls12(SecurityProtocolType current, Action<SecurityProtocolType> set)
        {
            if (current == SecurityProtocolType.SystemDefault) return;
            if ((current & SecurityProtocolType.Tls12) == SecurityProtocolType.Tls12) return;
            set(current | SecurityProtocolType.Tls12);
        }

        /// <summary>The Messages URL in use (managed.json → settings.json → api.anthropic.com).</summary>
        public static string MessagesUrl => SettingsManager.MessagesUrl(SettingsManager.GetApiBaseUrl());

        /// <summary>
        /// A handler that goes through the Windows proxy, authenticating to the PROXY as the signed-in
        /// user. The API server itself never gets Windows credentials (UseDefaultCredentials off).
        /// </summary>
        public static HttpClientHandler CreateHandler()
        {
            var proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultCredentials;
            return new HttpClientHandler
            {
                UseProxy = true,
                Proxy = proxy,
                UseDefaultCredentials = false
            };
        }

        public static HttpClient CreateClient(TimeSpan timeout)
        {
            return new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = timeout };
        }

        /// <summary>A POST to the Messages endpoint with the key and the API version.</summary>
        public static HttpRequestMessage NewMessagesRequest(string apiKey, string jsonBody)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            AddHeaders(request, apiKey);
            return request;
        }

        internal static void AddHeaders(HttpRequestMessage request, string apiKey)
        {
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
        }

        /// <summary>The Models endpoint next to a Messages URL (".../v1/messages" -> ".../v1/models").</summary>
        public static string ModelsUrl(string messagesUrl) =>
            messagesUrl.Substring(0, messagesUrl.Length - "/messages".Length) + "/models";

        /// <summary>
        /// The proxy Windows uses for the API host, or null when it goes direct. Also handed to the
        /// Claude Code CLI (HTTPS_PROXY), which does not read Windows' proxy settings itself.
        /// </summary>
        public static Uri SystemProxyFor(string url)
        {
            try
            {
                var target = new Uri(url);
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null || proxy.IsBypassed(target)) return null;
                var via = proxy.GetProxy(target);
                return via == null || via == target ? null : via;
            }
            catch
            {
                return null;
            }
        }

        // --- Key test (wizard and Settings) ----------------------------------------------

        public enum KeyTestOutcome { Ok, InvalidKey, NoAccess, ModelUnknown, Network, Other }

        public sealed class KeyTestResult
        {
            public KeyTestOutcome Outcome;
            public int Status;
            public string Detail;
            public bool Ok => Outcome == KeyTestOutcome.Ok;
        }

        /// <summary>
        /// Checks a key without spending a token: GET /v1/models/{model} answers 200 for a valid key
        /// and an available model, 401 for a wrong key, 403 for a key without access, 404 for an
        /// unknown model. A network failure (no route, proxy refused, TLS) is reported as such, with
        /// the innermost exception text. <paramref name="baseUrl"/>: test another endpoint than the
        /// saved one (Settings validates what is typed); null = the configured one.
        /// </summary>
        public static KeyTestResult TestKey(string apiKey, string model, TimeSpan timeout, string baseUrl = null,
            CancellationToken ct = default(CancellationToken))
        {
            var modelsUrl = ModelsUrl(baseUrl == null ? MessagesUrl : SettingsManager.MessagesUrl(baseUrl)) +
                            "/" + Uri.EscapeDataString(model ?? "");
            try
            {
                using (var client = CreateClient(timeout))
                using (var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl))
                {
                    AddHeaders(request, apiKey);
                    using (var response = client.SendAsync(request, ct).GetAwaiter().GetResult())
                    {
                        var status = (int)response.StatusCode;
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        var message = ErrorMessage(body);
                        switch (status)
                        {
                            case 200: return new KeyTestResult { Outcome = KeyTestOutcome.Ok, Status = status, Detail = model };
                            case 401: return new KeyTestResult { Outcome = KeyTestOutcome.InvalidKey, Status = status, Detail = message };
                            case 403: return new KeyTestResult { Outcome = KeyTestOutcome.NoAccess, Status = status, Detail = message };
                            case 404: return new KeyTestResult { Outcome = KeyTestOutcome.ModelUnknown, Status = status, Detail = message };
                            default: return new KeyTestResult { Outcome = KeyTestOutcome.Other, Status = status, Detail = message ?? Truncate(body, 200) };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new KeyTestResult { Outcome = KeyTestOutcome.Network, Detail = Innermost(ex) };
            }
        }

        /// <summary>The API's own error message from an error body, or null.</summary>
        public static string ErrorMessage(string body)
        {
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(body ?? "") as Dictionary<string, object>;
                if (root != null && root.TryGetValue("error", out var e) && e is Dictionary<string, object> error &&
                    error.TryGetValue("message", out var m) && m is string text)
                    return text;
            }
            catch
            {
                // Not JSON (a proxy's HTML error page): the caller shows the status instead.
            }
            return null;
        }

        /// <summary>
        /// The innermost exception message — "The remote name could not be resolved", not "One or more
        /// errors occurred". An HttpClient timeout surfaces as "A task was canceled"; that is said as
        /// what it is.
        /// </summary>
        public static string Innermost(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e is TaskCanceledException ? "no answer in time (timed out)" : e.Message;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
