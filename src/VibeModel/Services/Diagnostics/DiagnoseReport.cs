using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Diagnostics
{
    /// <summary>
    /// The support report behind the pane's Diagnose button and the `diagnose` command: what
    /// VibeModel sees of this computer — versions, signature, backend and key source, where it
    /// looked for Claude Code, the proxy, whether the API host answers (and who issued the
    /// certificate it presented: a TLS-inspecting proxy shows up here), recent warnings and
    /// errors, and the settings. Every secret is masked. Network and process probes run in
    /// parallel under one deadline (<see cref="Deadline"/>), so the report never hangs Revit.
    /// </summary>
    public static class DiagnoseReport
    {
        public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        // Set by VibeModelApp at startup and on every backend switch.
        public static string RevitVersion { get; set; }
        public static int HttpPort { get; set; }
        public static bool FileFallback { get; set; }
        public static IChatBackend Backend { get; set; }

        private static readonly string[] SecretWords = { "key", "token", "secret", "password", "protected" };

        public static string Build()
        {
            // Slow parts first, in parallel.
            var api = Task.Run(() => ProbeHost(AnthropicHttp.ModelsUrl(AnthropicHttp.MessagesUrl)));
            var claudeAi = Task.Run(() => ProbeHost("https://claude.ai/"));
            var cli = Task.Run(() =>
            {
                var path = ClaudeCodeBackend.LocateCli(out var version);
                return path == null ? null : path + " (" + version + ")";
            });
            var signature = Task.Run(() => SignatureOf(typeof(DiagnoseReport).Assembly.Location));
            var deadline = DateTime.UtcNow + Deadline;

            var sb = new StringBuilder();
            sb.AppendLine("VibeModel diagnosis - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " (local time)");
            sb.AppendLine();

            sb.AppendLine("== VibeModel");
            var asm = typeof(DiagnoseReport).Assembly;
            sb.AppendLine("Version: " + asm.GetName().Version);
            sb.AppendLine("Add-in: " + asm.Location);
            sb.AppendLine("Signature: " + Await(signature, deadline, "(check timed out)"));
            sb.AppendLine("Revit: " + (RevitVersion ?? "?") + " · .NET " + Environment.Version + " · Windows " + Environment.OSVersion.Version);
            sb.AppendLine("HTTP server: " + (FileFallback ? "not running - file-based fallback (C:\\RevitClaudeLink)" : "port " + HttpPort));
            if (!FileFallback)
                sb.AppendLine("Server access: " + ServerAuth.Describe(HttpPort) + "; browser requests (Origin / Sec-Fetch / foreign Host) refused");
            var approval = SettingsManager.GetChangeApproval(out var approvalSource);
            sb.AppendLine("Change approval: " + ChangeApprovalPolicy.Describe(approval, approvalSource, ChangeApprovalPolicy.DontAskThisSession));
            sb.AppendLine("Debug log: " + (Logger.DebugEnabled ? "on" : "off (warnings and errors only)") + " - " + Logger.LogDirectory);
            sb.AppendLine();

            sb.AppendLine("== Chat backend");
            var managedPath = ManagedSettings.FilePath;
            sb.AppendLine("Preferred: " + SettingsManager.GetPreferredBackend() + Managed(ManagedSettings.PreferredBackend));
            if (Backend is ChatBackendBase b)
                sb.AppendLine("In use: " + b.DisplayName + " · " + (b.ModelName ?? "default model") + " · " +
                              (Backend.IsAvailable ? "ready" : "NOT ready - " + Backend.StatusMessage));
            else
                sb.AppendLine("In use: " + (Backend == null ? "none yet" : Backend.GetType().Name));
            var key = SettingsManager.GetApiKey(out var keySource);
            sb.AppendLine("API key: " + (key == null ? "none" : SettingsManager.MaskKey(key) + " (from " + KeySourceText(keySource) + ")"));
            sb.AppendLine("API endpoint: " + AnthropicHttp.MessagesUrl + Managed(ManagedSettings.ApiBaseUrl));
            sb.AppendLine("API model: " + SettingsManager.GetModel() + Managed(ManagedSettings.Model));
            var cliResult = Await(cli, deadline, null);
            sb.AppendLine("Claude Code: " + (cliResult ?? "not found"));
            foreach (var line in ClaudeCodeBackend.LastSearch)
                sb.AppendLine("  checked: " + line);
            sb.AppendLine("managed.json: " + (managedPath ?? "none (looked in " + string.Join(", ", ManagedSettings.SearchPaths) + ")"));
            if (ManagedSettings.UntrustedFile != null)
                sb.AppendLine("  IGNORED " + ManagedSettings.UntrustedFile + ": " + ManagedSettings.UntrustedReason);
            if (managedPath != null)
                foreach (var k in ManagedSettings.Keys.Where(ManagedSettings.IsManaged))
                {
                    ManagedSettings.TryGet(k, out var v);
                    sb.AppendLine("  " + k + " = " + MaskIfSecret(k, v));
                }
            sb.AppendLine();

            AppendUpdates(sb);
            sb.AppendLine();

            sb.AppendLine("== Network");
            var proxy = AnthropicHttp.SystemProxyFor(AnthropicHttp.MessagesUrl);
            sb.AppendLine("Windows proxy for the API: " + (proxy == null ? "none (direct)" : proxy.GetLeftPart(UriPartial.Authority)));
            foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "NODE_EXTRA_CA_CERTS" })
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v)) sb.AppendLine(name + ": " + v);
            }
            sb.AppendLine(Await(api, deadline, "API host: check timed out after " + Deadline.TotalSeconds + " s"));
            sb.AppendLine(Await(claudeAi, deadline, "claude.ai: check timed out after " + Deadline.TotalSeconds + " s"));
            sb.AppendLine();

            sb.AppendLine("== Recent warnings and errors (this Revit session)");
            var recent = Logger.Recent();
            if (recent.Count == 0) sb.AppendLine("none");
            foreach (var line in recent.Skip(Math.Max(0, recent.Count - 20)))
                sb.AppendLine(MaskSecretsInText(line.Split('\n')[0]));
            sb.AppendLine();

            sb.AppendLine("== Settings (" + SettingsManager.FilePath + ", secrets masked)");
            foreach (var kv in SettingsManager.Load().OrderBy(k => k.Key))
                sb.AppendLine(kv.Key + " = " + MaskIfSecret(kv.Key, kv.Value));
            return sb.ToString();
        }

        // In-app updates (plan 19): what runs, from where, what the loader starts next, what is installed.
        private static void AppendUpdates(StringBuilder sb)
        {
            sb.AppendLine("== Updates");
            try
            {
                var engine = Update.UpdateService.Engine;
                var status = engine.Status;
                sb.AppendLine("Loader: " + Update.LoaderInfo.Describe() +
                              (Update.LoaderInfo.Rule != null ? " - picked the add-in by rule " + Update.LoaderInfo.Rule : ""));
                sb.AppendLine("Running from: " + Update.UpdateService.DescribeRunningFrom());
                var current = engine.Layout.ReadCurrent();
                sb.AppendLine("current.txt: " + (current ?? "none") + " (" + engine.Layout.CurrentFile + ")");
                var installed = engine.Layout.Installed();
                var running = engine.RunningFolder;
                sb.AppendLine("Versions installed: " + (installed.Count == 0 ? "none" : string.Join(", ", installed.Select(kv =>
                    kv.Key + (string.Equals(kv.Key, running, StringComparison.OrdinalIgnoreCase) ? " (running)" : "")))));
                sb.AppendLine("Source: " + engine.Source.Describe() + Managed(ManagedSettings.UpdateSource));
                sb.AppendLine("Updates: " + (SettingsManager.IsUpdateDisabled() ? "switched off by IT (DisableUpdates)" :
                              "automatic check " + (SettingsManager.GetUpdateCheck() ? "on" : "off")));
                var last = SettingsManager.GetUpdateLastChecked();
                sb.AppendLine("Last check: " + (last.HasValue ? last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " - " + SettingsManager.GetUpdateLastResult() : "never"));
                sb.AppendLine("State now: " + Update.UpdateDecision.Describe(status));
                if (status.LastError != null) sb.AppendLine("Last check/action failed: " + status.LastError);
                if (status.Note != null) sb.AppendLine("Note: " + status.Note);
            }
            catch (Exception ex)
            {
                sb.AppendLine("could not read the update state: " + ex.Message);
            }
        }

        private static string Managed(string key) => ManagedSettings.IsManaged(key) ? " (managed by IT)" : "";

        private static string KeySourceText(ApiKeySource source)
        {
            switch (source)
            {
                case ApiKeySource.Managed: return "managed.json";
                case ApiKeySource.Environment: return "ANTHROPIC_API_KEY";
                case ApiKeySource.Settings: return "settings, DPAPI-encrypted";
                default: return "nowhere";
            }
        }

        // The task's text, or the fallback when the shared deadline passed or the task failed.
        private static string Await(Task<string> task, DateTime deadline, string fallback)
        {
            var left = deadline - DateTime.UtcNow;
            try
            {
                return task.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero) ? task.Result : fallback;
            }
            catch (Exception ex)
            {
                return (fallback ?? "failed") + " - " + AnthropicHttp.Innermost(ex);
            }
        }

        /// <summary>A value as the report prints it: secrets masked, nested objects summarized.</summary>
        internal static string MaskIfSecret(string key, object value)
        {
            if (value == null) return "(null)";
            if (value is Dictionary<string, object> d) return "{" + d.Count + " entries}";
            if (value is object[] a) return "[" + a.Length + " items]";
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (IsSecretName(key))
                return text.Length == 0 ? "(empty)" : key.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "(encrypted, " + text.Length + " chars)"
                    : SettingsManager.MaskKey(text);
            return text;
        }

        internal static bool IsSecretName(string key) =>
            SecretWords.Any(w => (key ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Masks anything that looks like an Anthropic key inside free text (log lines).</summary>
        internal static string MaskSecretsInText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(text, @"sk-ant-[A-Za-z0-9_\-]{8,}", m => SettingsManager.MaskKey(m.Value));
        }

        // --- Network probe -------------------------------------------------------------------

        /// <summary>
        /// One line: DNS, then an HTTPS GET through the same proxy path the chat uses. Any HTTP
        /// status means the host is reachable (401/404 without a key is the expected answer). The
        /// certificate's issuer is captured without touching validation.
        /// </summary>
        internal static string ProbeHost(string url)
        {
            var uri = new Uri(url);
            var sb = new StringBuilder(uri.Host + ": ");
            try
            {
                var dns = Task.Run(() => Dns.GetHostAddresses(uri.Host));
                sb.Append(dns.Wait(ProbeTimeout) ? "DNS " + string.Join(",", dns.Result.Take(2).Select(a => a.ToString())) : "DNS timed out");
            }
            catch (Exception ex)
            {
                sb.Append("DNS failed (" + AnthropicHttp.Innermost(ex) + ")");
            }

            string issuer = null;
            var started = DateTime.UtcNow;
            try
            {
                var handler = AnthropicHttp.CreateHandler();
                handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                {
                    issuer = cert?.Issuer;
                    return errors == SslPolicyErrors.None; // never weakens validation
                };
                using (var client = new HttpClient(handler, true) { Timeout = ProbeTimeout })
                using (var response = client.GetAsync(uri).GetAwaiter().GetResult())
                {
                    var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    sb.Append(" · HTTPS reachable (HTTP " + (int)response.StatusCode + ", " + ms + " ms)");
                }
            }
            catch (Exception ex)
            {
                sb.Append(" · HTTPS FAILED: " + AnthropicHttp.Innermost(ex));
            }
            if (issuer != null) sb.Append(" · certificate issued by " + issuer);
            return sb.ToString();
        }

        // --- Authenticode --------------------------------------------------------------------

        /// <summary>"valid - CN=…", "signed by CN=…, not trusted on this computer (0x…)" or "not signed".</summary>
        internal static string SignatureOf(string path)
        {
            string signer;
            try
            {
                signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).GetNameInfo(X509NameType.SimpleName, false);
            }
            catch
            {
                return "not signed";
            }
            var status = WinTrust.Verify(path);
            return status == 0
                ? "valid - " + signer
                : "signed by " + signer + ", not trusted on this computer (0x" + status.ToString("X8") + ")";
        }

        private static class WinTrust
        {
            private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct FileInfo
            {
                public uint cbStruct;
                public string pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct TrustData
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
            private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

            /// <summary>0 = signed and trusted; otherwise the HRESULT (e.g. 0x800B0109 untrusted root).</summary>
            public static uint Verify(string path)
            {
                var file = new FileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo)), pcwszFilePath = path };
                var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
                var dataPtr = IntPtr.Zero;
                try
                {
                    Marshal.StructureToPtr(file, filePtr, false);
                    var data = new TrustData
                    {
                        cbStruct = (uint)Marshal.SizeOf(typeof(TrustData)),
                        dwUIChoice = 2,            // WTD_UI_NONE
                        fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                        dwUnionChoice = 1,         // WTD_CHOICE_FILE
                        pFile = filePtr,
                        dwProvFlags = 0x1000       // WTD_CACHE_ONLY_URL_RETRIEVAL: never goes online
                    };
                    dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustData)));
                    Marshal.StructureToPtr(data, dataPtr, false);
                    return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);
                }
                finally
                {
                    if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                    Marshal.DestroyStructure(filePtr, typeof(FileInfo));
                    Marshal.FreeHGlobal(filePtr);
                }
            }
        }

        /// <summary>Writes the report to %LOCALAPPDATA%\VibeModel\logs\diagnose-yyyyMMdd-HHmm.txt and returns the path.</summary>
        public static string Save(string report)
        {
            Directory.CreateDirectory(Logger.LogDirectory);
            var path = Path.Combine(Logger.LogDirectory, "diagnose-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            return path;
        }
    }
}
