using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The browser gate: a web page's request carries Origin / Sec-Fetch-* / its own Host;
    /// curl, WebClient and the CLI carry none of those and address 127.0.0.1 or localhost.
    /// </summary>
    public class RequestGuardTests
    {
        private const int Port = 18884;

        private static Dictionary<string, string> Headers(params string[] pairs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        [Fact]
        public void Curl_NoHeadersButHost_IsAllowed()
        {
            Assert.Null(RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "User-Agent", "curl/8.4.0", "Accept", "*/*"), Port));
            Assert.Null(RequestGuard.Check(Headers("Host", "localhost:18884"), Port));
        }

        [Fact]
        public void WebClient_InPane_IsAllowed()
        {
            // What System.Net.WebClient sends from inside Revit: Host, token, Accept, Connection.
            Assert.Null(RequestGuard.Check(Headers(
                "Host", "127.0.0.1:18885", "X-VibeModel-Token", "abc", "Accept", "application/json", "Connection", "Keep-Alive"), 18885));
        }

        [Fact]
        public void NoHeadersAtAll_IsAllowed()
        {
            Assert.Null(RequestGuard.Check(Headers(), Port));
            Assert.Null(RequestGuard.Check(null, Port));
        }

        [Fact]
        public void Origin_IsRefused_EvenWhenLoopback()
        {
            var reason = RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "Origin", "http://evil.example"), Port);
            Assert.NotNull(reason);
            Assert.Contains("evil.example", reason);
            // A page served from the loopback address itself is still a page.
            Assert.NotNull(RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "Origin", "http://127.0.0.1:18884"), Port));
            Assert.NotNull(RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "Origin", "null"), Port));
        }

        [Theory]
        [InlineData("cross-site")]
        [InlineData("same-site")]
        [InlineData("Cross-Site")]
        public void SecFetchSite_NotNoneOrSameOrigin_IsRefused(string site)
        {
            Assert.NotNull(RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "Sec-Fetch-Site", site, "Sec-Fetch-Mode", "no-cors"), Port));
        }

        [Theory]
        [InlineData("none")]
        [InlineData("same-origin")]
        public void SecFetchSite_NoneOrSameOrigin_IsAllowed(string site)
        {
            // Typed into the address bar (none): the user's own doing, a GET of a read command.
            Assert.Null(RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "Sec-Fetch-Site", site, "Sec-Fetch-Mode", "navigate"), Port));
        }

        [Theory]
        [InlineData("no-cors")]
        [InlineData("cors")]
        [InlineData("navigate")]
        public void SecFetchMode_WithoutSite_IsRefused(string mode)
        {
            Assert.NotNull(RequestGuard.Check(Headers("Host", "127.0.0.1:18884", "Sec-Fetch-Mode", mode), Port));
        }

        [Theory]
        [InlineData("evil.example:18884")]
        [InlineData("evil.example")]
        [InlineData("127.0.0.1.evil.example:18884")]
        [InlineData("127.0.0.1:18885")]   // another instance's port: not this server
        [InlineData("localhost:80")]
        [InlineData("192.168.1.10:18884")]
        [InlineData("[::1]:18885")]
        public void ForeignHost_IsRefused(string host)
        {
            var reason = RequestGuard.Check(Headers("Host", host), Port);
            Assert.NotNull(reason);
            Assert.Contains("127.0.0.1:18884", reason);
        }

        [Theory]
        [InlineData("127.0.0.1:18884")]
        [InlineData("127.0.0.1")]
        [InlineData("localhost:18884")]
        [InlineData("localhost")]
        [InlineData("LOCALHOST:18884")]
        [InlineData("[::1]:18884")]
        [InlineData("[::1]")]
        [InlineData(" 127.0.0.1:18884 ")]
        public void LoopbackHost_IsAllowed(string host)
        {
            Assert.True(RequestGuard.IsLoopbackHost(host, Port), host);
            Assert.Null(RequestGuard.Check(Headers("Host", host), Port));
        }

        [Fact]
        public void HostCheck_AppliesToHealthToo()
        {
            // The guard knows no paths: whoever calls it for /health gets the same verdict.
            Assert.NotNull(RequestGuard.Check(Headers("Host", "evil.example:18884"), Port));
        }

        [Fact]
        public void BadIpv6Host_IsRefused()
        {
            Assert.False(RequestGuard.IsLoopbackHost("[::1", Port));
            Assert.False(RequestGuard.IsLoopbackHost("[::1]x", Port));
            Assert.False(RequestGuard.IsLoopbackHost("", Port));
        }
    }

    /// <summary>The token decision and the servers file that publishes it.</summary>
    public class ServerAuthTests
    {
        [Fact]
        public void ExplicitEnvToken_Wins()
        {
            ServerAuth.Resolve("  secret-1 ", openServer: true, generate: () => "generated", out var token, out var mode);
            Assert.Equal("secret-1", token);
            Assert.Equal(ServerAuthMode.EnvironmentToken, mode);
        }

        [Fact]
        public void OpenServer_WithoutEnvToken_HasNoToken()
        {
            ServerAuth.Resolve("", openServer: true, generate: () => "generated", out var token, out var mode);
            Assert.Null(token);
            Assert.Equal(ServerAuthMode.Open, mode);
        }

        [Fact]
        public void Default_GeneratesASessionToken()
        {
            ServerAuth.Resolve(null, openServer: false, generate: () => "generated", out var token, out var mode);
            Assert.Equal("generated", token);
            Assert.Equal(ServerAuthMode.SessionToken, mode);
        }

        [Fact]
        public void GeneratedToken_Is256BitsHex_AndUnique()
        {
            var a = ServerAuth.GenerateToken();
            var b = ServerAuth.GenerateToken();
            Assert.Equal(64, a.Length);
            Assert.Matches("^[0-9a-f]{64}$", a);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void ServersFile_CarriesTheToken()
        {
            var started = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
            var json = ServerDiscovery.BuildJson(18884, 4242, started, "tok-123");
            var obj = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            Assert.Equal("tok-123", (string)obj["token"]);
            Assert.Equal(18884, Convert.ToInt32(obj["port"]));
            Assert.Equal(4242, Convert.ToInt32(obj["pid"]));
        }

        [Fact]
        public void ServersFile_OpenServer_HasNoTokenField()
        {
            var json = ServerDiscovery.BuildJson(18884, 4242, DateTime.UtcNow, null);
            var obj = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            Assert.False(obj.ContainsKey("token"));
        }

        [Fact]
        public void Describe_NamesTheMode()
        {
            var token = ServerAuth.Token; var mode = ServerAuth.Mode;
            try
            {
                ServerAuth.SetForTests("x", ServerAuthMode.SessionToken);
                Assert.Contains("18884.json", ServerAuth.Describe(18884));
                ServerAuth.SetForTests("x", ServerAuthMode.EnvironmentToken);
                Assert.Contains("VIBEMODEL_TOKEN", ServerAuth.Describe(18884));
                ServerAuth.SetForTests(null, ServerAuthMode.Open);
                Assert.StartsWith("OPEN", ServerAuth.Describe(18884));
            }
            finally { ServerAuth.SetForTests(token, mode); }
        }
    }

    /// <summary>
    /// The request reader works on bytes: a multi-byte character across a read boundary and
    /// a header terminator across two reads both survive; Content-Length is honoured.
    /// </summary>
    public class HttpRequestReaderTests
    {
        // Hands out at most `chunk` bytes per Read, like a socket that delivers in pieces.
        private sealed class ChunkedStream : MemoryStream
        {
            private readonly int _chunk;
            public ChunkedStream(byte[] data, int chunk) : base(data) { _chunk = chunk; }
            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, _chunk));
        }

        private static byte[] Request(string method, string path, string body, params string[] extraHeaders)
        {
            var bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append(method + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:18884\r\nUser-Agent: curl/8.4.0\r\nAccept: */*\r\n");
            foreach (var h in extraHeaders) sb.Append(h + "\r\n");
            if (body != null) sb.Append("Content-Length: " + bodyBytes.Length + "\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            var all = new byte[head.Length + bodyBytes.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(bodyBytes, 0, all, head.Length, bodyBytes.Length);
            return all;
        }

        [Fact]
        public void BatchBody_Over4KB_WithMultiByteCharAcrossTheReadBoundary_RoundTrips()
        {
            // Lines with ° and m² such that the body runs well past the first 4096-byte read.
            var body = new StringBuilder();
            for (int i = 0; i < 120; i++)
                body.Append("set 1543814 Comments hoek 30° oppervlak 12 m² lijn ").Append(i).Append('\n');
            var text = body.ToString();
            var bytes = Request("POST", "/batch", text);
            Assert.True(bytes.Length > 5000);

            // Make sure a multi-byte character really straddles offset 4096: shift with padding
            // in a header until byte 4095 is the lead byte of a 2-byte sequence (a line is ~54
            // bytes with two such characters, so at most ~40 shifts are needed).
            var padded = bytes;
            for (int pad = 0; pad < 64; pad++)
            {
                padded = Request("POST", "/batch", text, "X-Pad: " + new string('p', pad));
                if ((padded[4095] & 0xE0) == 0xC0) break; // lead byte of a 2-byte UTF-8 sequence
            }
            Assert.Equal(0xC0, padded[4095] & 0xE0);

            var request = RevitHttpServer.ReadHttpRequest(new MemoryStream(padded));
            Assert.NotNull(request);
            Assert.Equal("POST", request.Method);
            Assert.Equal(text, request.Body);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), Encoding.UTF8.GetByteCount(request.Body));
        }

        [Fact]
        public void Body_ArrivingOneByteAtATime_RoundTrips()
        {
            var text = "wall 0 0 5000 0 3000\nset 1 Comments ë°² done\n";
            var request = RevitHttpServer.ReadHttpRequest(new ChunkedStream(Request("POST", "/batch", text), 1));
            Assert.NotNull(request);
            Assert.Equal(text, request.Body);
        }

        [Fact]
        public void HeaderTerminator_AcrossTwoReads_IsFound()
        {
            var bytes = Request("GET", "/info?args=x", null);
            int blank = Array.IndexOf(bytes, (byte)'\r', bytes.Length - 8); // somewhere in the final \r\n\r\n
            foreach (var chunk in new[] { blank - 1, blank, blank + 1, blank + 2, blank + 3 })
            {
                var request = RevitHttpServer.ReadHttpRequest(new ChunkedStream(bytes, Math.Max(1, chunk)));
                Assert.NotNull(request);
                Assert.Equal("/info?args=x", request.Path);
                Assert.Null(request.Body);
            }
        }

        [Fact]
        public void Headers_AreParsedCaseInsensitively_FirstWins()
        {
            var bytes = Request("GET", "/info", null, "x-vibemodel-token: t1", "X-VibeModel-Token: t2", "Origin: http://evil.example");
            var request = RevitHttpServer.ReadHttpRequest(new MemoryStream(bytes));
            Assert.Equal("t1", request.Token);
            Assert.Equal("*/*", request.Accept);
            Assert.Equal("http://evil.example", request.Headers["origin"]);
            Assert.Equal("127.0.0.1:18884", request.Headers["Host"]);
        }

        [Fact]
        public void ExpectContinue_IsAnsweredBeforeTheBodyIsRead()
        {
            // curl sends "Expect: 100-continue" for bodies over 1 KB and waits ~1 s for it.
            var text = new string('a', 2000) + "\n";
            var bytes = Request("POST", "/batch", text, "Expect: 100-continue");
            int headEnd = Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            var sent = new List<string>();
            // Hand the server only the head first: the body must not be required before 100 Continue.
            var request = RevitHttpServer.ReadHttpRequest(new ChunkedStream(bytes, headEnd), b => sent.Add(Encoding.ASCII.GetString(b)));
            Assert.Equal(text, request.Body);
            Assert.Single(sent);
            Assert.StartsWith("HTTP/1.1 100 Continue\r\n\r\n", sent[0]);
        }

        [Fact]
        public void ContentLength_Bounds_TheBody()
        {
            var bytes = Request("POST", "/batch", "info\n");
            var longer = new byte[bytes.Length + 10];
            Buffer.BlockCopy(bytes, 0, longer, 0, bytes.Length);
            for (int i = bytes.Length; i < longer.Length; i++) longer[i] = (byte)'z';
            var request = RevitHttpServer.ReadHttpRequest(new MemoryStream(longer));
            Assert.Equal("info\n", request.Body);
        }

        [Fact]
        public void ClosedConnection_OrGarbage_IsNull()
        {
            Assert.Null(RevitHttpServer.ReadHttpRequest(new MemoryStream(new byte[0])));
            Assert.Null(RevitHttpServer.ReadHttpRequest(new MemoryStream(Encoding.ASCII.GetBytes("GET\r\n\r\n"))));
            Assert.Null(RevitHttpServer.ReadHttpRequest(new MemoryStream(Encoding.ASCII.GetBytes(new string('h', RevitHttpServer.MaxHeaderBytes + 10)))));
        }
    }

    /// <summary>managed.json under %ProgramData% is only IT's when its ACL says so.</summary>
    public class ManagedSettingsTrustTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settings;

        public ManagedSettingsTrustTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-managed-trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settings = Path.Combine(_dir, "settings.json");
            SettingsManager.OverridePathForTests(_settings);
            ManagedSettings.OverrideSearchPathsForTests(Path.Combine(_dir, "managed.json"), Path.Combine(_dir, "user", "managed.json"));
        }

        public void Dispose()
        {
            ManagedSettings.OverrideTrustCheckForTests(null);
            ManagedSettings.OverrideSearchPathsForTests(null);
            SettingsManager.OverridePathForTests(null);
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void UntrustedMachineFile_IsIgnored_AndNamed()
        {
            File.WriteAllText(Path.Combine(_dir, "managed.json"), "{\"OpenServer\":true,\"Model\":\"claude-opus-5-5\"}");
            ManagedSettings.OverrideTrustCheckForTests(p => p.EndsWith("user\\managed.json") ? null : "the file is writable by BUILTIN\\Users");

            Assert.Null(ManagedSettings.FilePath);
            Assert.False(ManagedSettings.IsManaged(ManagedSettings.OpenServer));
            Assert.False(SettingsManager.GetOpenServer());
            Assert.Equal(Path.Combine(_dir, "managed.json"), ManagedSettings.UntrustedFile);
            Assert.Contains("Users", ManagedSettings.UntrustedReason);
        }

        [Fact]
        public void UntrustedMachineFile_FallsThroughToTheUserFile()
        {
            File.WriteAllText(Path.Combine(_dir, "managed.json"), "{\"Model\":\"from-machine\"}");
            Directory.CreateDirectory(Path.Combine(_dir, "user"));
            File.WriteAllText(Path.Combine(_dir, "user", "managed.json"), "{\"Model\":\"from-user\"}");
            ManagedSettings.OverrideTrustCheckForTests(p => p.EndsWith("user\\managed.json") ? null : "not owned by Administrators");

            Assert.Equal(Path.Combine(_dir, "user", "managed.json"), ManagedSettings.FilePath);
            Assert.Equal("from-user", SettingsManager.GetModel());
            Assert.NotNull(ManagedSettings.UntrustedFile);
        }

        [Fact]
        public void TrustedFile_IsUsed_OpenServerManagedWins()
        {
            File.WriteAllText(Path.Combine(_dir, "managed.json"), "{\"OpenServer\":false}");
            ManagedSettings.OverrideTrustCheckForTests(p => null);
            SettingsManager.SetValueForTests(ManagedSettings.OpenServer, true);

            Assert.NotNull(ManagedSettings.FilePath);
            Assert.Null(ManagedSettings.UntrustedFile);
            Assert.True(ManagedSettings.IsManaged(ManagedSettings.OpenServer));
            Assert.False(SettingsManager.GetOpenServer()); // IT said false; the user's true loses
        }

        [Fact]
        public void OpenServer_DefaultsFalse_SettingsTrueOpensIt()
        {
            ManagedSettings.OverrideTrustCheckForTests(p => null);
            Assert.False(SettingsManager.GetOpenServer());
            SettingsManager.SetValueForTests(ManagedSettings.OpenServer, true);
            Assert.True(SettingsManager.GetOpenServer());
            SettingsManager.SetValueForTests(ManagedSettings.OpenServer, "false");
            Assert.False(SettingsManager.GetOpenServer());
        }

        [Fact]
        public void TheRealSearchPaths_StartWithProgramData()
        {
            ManagedSettings.OverrideSearchPathsForTests(null);
            var first = ManagedSettings.SearchPaths[0];
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), first, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("VibeModel\\managed.json", first);
        }
    }

    /// <summary>The ACL decision itself, on in-memory security descriptors.</summary>
    public class FileTrustTests
    {
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier System = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        private static readonly SecurityIdentifier AuthUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        private static FileSecurity Security(SecurityIdentifier owner, params Tuple<SecurityIdentifier, FileSystemRights, AccessControlType>[] rules)
        {
            var s = new FileSecurity();
            s.SetOwner(owner);
            foreach (var r in rules) s.AddAccessRule(new FileSystemAccessRule(r.Item1, r.Item2, r.Item3));
            return s;
        }

        private static Tuple<SecurityIdentifier, FileSystemRights, AccessControlType> Allow(SecurityIdentifier sid, FileSystemRights rights) =>
            Tuple.Create(sid, rights, AccessControlType.Allow);

        [Fact]
        public void AdminOwned_UsersReadOnly_IsTrusted()
        {
            var s = Security(Admins, Allow(Admins, FileSystemRights.FullControl), Allow(System, FileSystemRights.FullControl),
                Allow(Users, FileSystemRights.ReadAndExecute));
            Assert.Null(FileTrust.Problem(s, "the file"));
        }

        [Fact]
        public void SystemOwned_IsTrusted()
        {
            var s = Security(System, Allow(System, FileSystemRights.FullControl), Allow(Users, FileSystemRights.Read));
            Assert.Null(FileTrust.Problem(s, "the file"));
        }

        [Fact]
        public void UserOwned_IsNotTrusted()
        {
            var me = WindowsIdentity.GetCurrent().User;
            var s = Security(me, Allow(Admins, FileSystemRights.FullControl), Allow(Users, FileSystemRights.Read));
            var problem = FileTrust.Problem(s, "the file");
            Assert.NotNull(problem);
            Assert.Contains("not owned by Administrators or SYSTEM", problem);
        }

        [Theory]
        [InlineData(FileSystemRights.Write)]
        [InlineData(FileSystemRights.Modify)]
        [InlineData(FileSystemRights.FullControl)]
        [InlineData(FileSystemRights.Delete)]
        [InlineData(FileSystemRights.CreateFiles)]
        [InlineData(FileSystemRights.AppendData)]
        [InlineData(FileSystemRights.ChangePermissions)]
        [InlineData(FileSystemRights.TakeOwnership)]
        public void AdminOwned_ButUsersCanWrite_IsNotTrusted(FileSystemRights rights)
        {
            var s = Security(Admins, Allow(Admins, FileSystemRights.FullControl), Allow(Users, rights));
            var problem = FileTrust.Problem(s, "its folder");
            Assert.NotNull(problem);
            Assert.StartsWith("its folder is writable by", problem);
        }

        [Fact]
        public void AuthenticatedUsersWrite_IsNotTrusted_DenyIsIgnored()
        {
            Assert.NotNull(FileTrust.Problem(Security(Admins, Allow(AuthUsers, FileSystemRights.Write)), "the file"));
            // A Deny for Users does not make the file writable; only Allow ACEs count.
            var denied = Security(Admins, Allow(Admins, FileSystemRights.FullControl),
                Tuple.Create(Users, FileSystemRights.Write, AccessControlType.Deny));
            Assert.Null(FileTrust.Problem(denied, "the file"));
        }

        [Fact]
        public void ARealUserCreatedFile_IsNotTrusted()
        {
            // The scenario the check exists for: this (non-elevated) test creates a file; it is
            // owned by the current user, so it must not pass.
            var path = Path.Combine(Path.GetTempPath(), "vm-trust-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{}");
            try
            {
                var problem = FileTrust.Check(path);
                Assert.NotNull(problem);
            }
            finally { File.Delete(path); }
        }
    }
}
