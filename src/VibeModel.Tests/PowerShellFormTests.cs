using System;
using System.IO;
using System.Linq;
using System.Text;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Phase 1, step 2: on PowerShell the CLI sends code through a file in its scratch folder, as a
    /// raw POST body. The form the prompt teaches must reach exec intact — quotes, newlines, a
    /// byte-order mark from Set-Content -Encoding utf8 — and the rules must say it.
    /// </summary>
    public class PowerShellFormTests
    {
        private const string Snippet =
            "// Doel: muren tellen\n" +
            "var n = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();\n" +
            "sb.AppendLine(\"walls: \" + n + \" \\\"quoted\\\" 'single'\");\n";

        private static byte[] Request(string method, string target, byte[] body)
        {
            var head = method + " " + target + " HTTP/1.1\r\nHost: 127.0.0.1:18884\r\nUser-Agent: curl/8.4.0\r\n" +
                       "X-VibeModel-Token: t\r\n" +
                       (body != null ? "Content-Type: application/x-www-form-urlencoded\r\nContent-Length: " + body.Length + "\r\n" : "") + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(head).ToList();
            if (body != null) bytes.AddRange(body);
            return bytes.ToArray();
        }

        // What Windows PowerShell 5.1's Set-Content -Encoding utf8 writes: a BOM, then the text.
        private static byte[] SetContentUtf8(string text) =>
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r\n"))).ToArray();

        [Fact]
        public void TaughtForm_RawPostOfTheFile_ReachesExecIntact_WithoutTheBom()
        {
            // curl.exe -s -X POST "<url>/exec?readonly=1" --data-binary "@$f"
            var raw = Request("POST", "/exec?readonly=1", SetContentUtf8(Snippet));
            var req = RevitHttpServer.ReadHttpRequest(new MemoryStream(raw));
            Assert.NotNull(req);
            var args = RevitHttpServer.CommandArgs(req, RevitHttpServer.ParseQueryArgs("readonly=1"));
            Assert.Equal(Snippet.Replace("\n", "\r\n").TrimEnd('\r', '\n'), args);
            Assert.NotEqual('\uFEFF', args[0]);
            Assert.True(RevitHttpServer.ParseRunOptions("readonly=1").ReadOnly);
        }

        [Fact]
        public void TwentyKilobyteSnippet_PassesAsABody()
        {
            var big = string.Concat(Enumerable.Repeat(Snippet, 20000 / Snippet.Length + 1));
            Assert.True(big.Length >= 20000);
            var req = RevitHttpServer.ReadHttpRequest(new MemoryStream(Request("POST", "/exec", SetContentUtf8(big))));
            Assert.Equal(big.Replace("\n", "\r\n").TrimEnd('\r', '\n'), RevitHttpServer.CommandArgs(req, ""));
        }

        [Fact]
        public void UrlEncodedFile_TheBomIsDroppedToo()
        {
            // curl -G --data-urlencode "args@file" (the Bash / older form): %EF%BB%BF + the encoded snippet.
            var query = "args=" + Uri.EscapeDataString("\uFEFF" + Snippet);
            Assert.Equal(Snippet, RevitHttpServer.CommandArgs(new RevitHttpServer.HttpRequest("GET", "/exec?" + query, null),
                RevitHttpServer.ParseQueryArgs(query)));
        }

        [Fact]
        public void ABatchBody_LosesItsBom()
        {
            Assert.Equal("wall 0 0 5000 0", RevitHttpServer.StripBom("\uFEFFwall 0 0 5000 0"));
            Assert.Equal("", RevitHttpServer.StripBom(""));
            Assert.Null(RevitHttpServer.StripBom(null));
        }

        [Fact]
        public void PromptFile_TeachesTheFileForm_NoPipes_ScratchFolder_C5()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, "t");
            Assert.Contains(ClaudeCodeBackend.PowerShellRule, text);
            Assert.Contains(ClaudeCodeBackend.ExecDialectRule, text);
            var rule = ClaudeCodeBackend.PowerShellRule;
            Assert.Contains("--data-binary \"@$f\"", rule);
            Assert.Contains("$env:" + ScratchFolder.EnvVar, rule);
            Assert.Contains("Select-Object", rule);
            Assert.Contains("Set-Content -Encoding utf8", rule);
            Assert.Contains("no Python", rule);
            Assert.Contains("never below 40", rule);
            // The live pane turn left the token out when the example said "<token header>".
            Assert.Contains("-H \"X-VibeModel-Token: $env:VIBEMODEL_TOKEN\"", rule);
            Assert.Contains("X-VibeModel-Token: $env:VIBEMODEL_TOKEN", ClaudeCodeBackend.PowerShellTurnRule);
            // The taught form is a raw POST, never --data-urlencode without -G (a form body exec never decodes).
            Assert.DoesNotContain("--data-urlencode \"args@", rule);
            Assert.Contains("C# 5", text);
        }

        [Fact]
        public void OpenServer_RulesCarryNoTokenHeader()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, null);
            Assert.DoesNotContain("X-VibeModel-Token", text);
            Assert.Contains("--data-binary \"@$f\"", text);
            var turn = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x.txt", 18884, "", "nl", tokenRequired: false);
            Assert.DoesNotContain("X-VibeModel-Token", turn);
        }

        [Fact]
        public void TurnPrompt_RepeatsThePowerShellLine()
        {
            var prompt = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl");
            Assert.Contains(ClaudeCodeBackend.PowerShellTurnRule, prompt);
        }

        // ---------------------------------------------------------------- the scratch folder

        [Fact]
        public void ScratchKey_IsSortableAndUnique()
        {
            var a = ScratchFolder.NewKey(new DateTime(2026, 11, 26, 10, 15, 0), 4711, 1);
            var b = ScratchFolder.NewKey(new DateTime(2026, 11, 26, 10, 15, 0), 4711, 2);
            Assert.Equal("20261126-101500-4711-1", a);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Prune_RemovesOnlyFoldersOlderThanAWeek_AndNeverTheCurrentOne()
        {
            var root = Path.Combine(Path.GetTempPath(), "vm-scratch-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var old = ScratchFolder.Ensure(root, "old");
                var recent = ScratchFolder.Ensure(root, "recent");
                var current = ScratchFolder.Ensure(root, "current");
                File.WriteAllText(Path.Combine(old, "q1.cs"), "x");
                File.WriteAllText(Path.Combine(current, "q1.cs"), "x");
                var longAgo = DateTime.UtcNow.AddDays(-10);
                foreach (var dir in new[] { old, current })
                {
                    File.SetLastWriteTimeUtc(Path.Combine(dir, "q1.cs"), longAgo);
                    Directory.SetLastWriteTimeUtc(dir, longAgo);
                }
                var removed = ScratchFolder.Prune(root, DateTime.UtcNow, keep: current);
                Assert.Equal(1, removed);
                Assert.False(Directory.Exists(old));
                Assert.True(Directory.Exists(recent));
                Assert.True(Directory.Exists(current));
                Assert.Equal(0, ScratchFolder.Prune(Path.Combine(root, "missing"), DateTime.UtcNow));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
