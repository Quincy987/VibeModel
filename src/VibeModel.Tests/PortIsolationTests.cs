using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The per-port isolation contract that keeps chat working when multiple Revit
    /// instances run (fallback ports 18885+): the prompt file is keyed by port, its
    /// content references only the live port, and the discovery file lets external
    /// clients find the right port. Guards against the shared-file staleness bug
    /// where a second instance overwrote system-prompt.txt with a dead port.
    /// </summary>
    public class SystemPromptPortTests
    {
        // Real command set via the headless reflection scan (same approach as
        // CommandRegistryTests) — Name/Description/Usage are pure properties.
        private static readonly IReadOnlyDictionary<string, IClaudeCommand> Commands =
            new ClaudeCommandRegistry().GetCommands();

        [Fact]
        public void PromptPath_ContainsThePort()
        {
            Assert.EndsWith("system-prompt-18885.txt",
                ClaudeCodeBackend.GetSystemPromptPath(18885));
        }

        [Fact]
        public void PromptPaths_DifferPerPort()
        {
            Assert.NotEqual(
                ClaudeCodeBackend.GetSystemPromptPath(18884),
                ClaudeCodeBackend.GetSystemPromptPath(18885));
        }

        [Fact]
        public void PromptText_ReferencesOnlyTheGivenPort()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(Commands, 18885, null);
            Assert.Contains("http://127.0.0.1:18885", text);
            Assert.DoesNotContain("127.0.0.1:18884", text);
        }

        // The prompt must name the loopback IP, not "localhost": the CLI child curls this
        // URL, and localhost resolves to ::1 first while the server binds IPv4 only.
        [Fact]
        public void PromptText_UsesLoopbackIpNotLocalhost()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(Commands, 18884, null);
            Assert.Contains("http://127.0.0.1:18884", text);
            Assert.DoesNotContain("http://localhost:", text);
        }

        [Fact]
        public void PromptText_WithToken_IncludesAuthHeaderInstruction()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(Commands, 18884, "secret");
            Assert.Contains(RevitHttpServer.TokenHeader, text);
        }

        [Fact]
        public void PromptText_WithoutToken_OmitsAuthInstruction()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(Commands, 18884, null);
            Assert.DoesNotContain(RevitHttpServer.TokenHeader, text);
        }

        [Fact]
        public void PortDirective_NamesTheLivePortAndOverridesHistory()
        {
            var directive = ClaudeCodeBackend.BuildPortDirective(18886);
            Assert.Contains("http://127.0.0.1:18886", directive);
            Assert.DoesNotContain("localhost", directive);
            Assert.Contains("ONLY correct port", directive);
        }
    }

    /// <summary>
    /// In-process callers (and the CLI child) must reach the server by literal IPv4
    /// loopback: the listener binds IPAddress.Loopback, while "localhost" resolves to
    /// ::1 first — a dropped IPv6 attempt stalls the caller for its whole timeout.
    /// </summary>
    public class LoopbackAddressTests
    {
        [Fact]
        public void BaseUrl_IsLoopbackIpWithPort()
        {
            Assert.Equal("http://127.0.0.1:18885", RevitHttpServer.BaseUrl(18885));
        }

        [Fact]
        public void BaseUrl_NeverUsesLocalhost()
        {
            Assert.DoesNotContain("localhost", RevitHttpServer.BaseUrl(18884));
        }
    }

    public class ServerDiscoveryTests
    {
        [Fact]
        public void FilePath_IsPerPort()
        {
            Assert.EndsWith("18885.json", ServerDiscovery.GetFilePath(18885));
            Assert.NotEqual(
                ServerDiscovery.GetFilePath(18884),
                ServerDiscovery.GetFilePath(18885));
        }

        [Fact]
        public void Json_RoundTripsPortPidAndTimestamp()
        {
            var started = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
            var json = ServerDiscovery.BuildJson(18885, 1234, started);

            var obj = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            Assert.Equal(18885, Convert.ToInt32(obj["port"]));
            Assert.Equal(1234, Convert.ToInt32(obj["pid"]));
            Assert.StartsWith("2026-07-20T12:00:00", (string)obj["startedUtc"]);
        }
    }
}
