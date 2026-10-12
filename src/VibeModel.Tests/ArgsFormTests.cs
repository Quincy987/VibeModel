using System.Linq;
using System.Text.RegularExpressions;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Arguments belong in ?args= (or a POST /batch body), never in the URL path: the prompt
    /// file the Claude CLI reads only shows that form, every turn prompt repeats the rule, and
    /// the server still runs a path-form call ("/wall%200%200%205000%200") as a safety net.
    /// </summary>
    public class ArgsFormTests
    {
        // ---- Server: args in the path ----------------------------------------------

        [Theory]
        [InlineData("wall%200%200%205000%200", "wall", "0 0 5000 0")]
        [InlineData("wall 0 0 5000 0", "wall", "0 0 5000 0")]
        [InlineData("wall/0%200%205000%200", "wall", "0 0 5000 0")]
        [InlineData("delete%2012345%2012346", "delete", "12345 12346")]
        [InlineData("color%2012345%20255%200%200", "color", "12345 255 0 0")]
        [InlineData("set%20123%20Comments%20a+b", "set", "123 Comments a+b")]
        [InlineData("exec%20sb.AppendLine(doc.Title);", "exec", "sb.AppendLine(doc.Title);")]
        public void PathWithArgs_SplitsIntoCommandAndArgs(string path, string command, string args)
        {
            Assert.True(RevitHttpServer.TrySplitPathArgs(path, out var c, out var a));
            Assert.Equal(command, c);
            Assert.Equal(args, a);
        }

        [Theory]
        [InlineData("info")]
        [InlineData("wall")]
        [InlineData("batch")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("%20wall")]          // nothing before the separator
        [InlineData("wa%3Fll%200")]      // "wa?ll" is no command name
        [InlineData("wall%2")]           // broken escape → left alone
        public void PlainOrUnrecognisablePath_IsLeftAlone(string path)
        {
            Assert.False(RevitHttpServer.TrySplitPathArgs(path, out _, out _));
        }

        // ---- Prompt file -----------------------------------------------------------

        private static string PromptFile() =>
            ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, null);

        [Fact]
        public void PromptFile_StatesTheArgsRule()
        {
            var text = PromptFile();
            Assert.Contains(ClaudeCodeBackend.ArgsRule, text);
            Assert.Contains("never in the path", ClaudeCodeBackend.ArgsRule);
        }

        [Fact]
        public void PromptFile_EveryUrlPathIsABareCommandName()
        {
            // Every URL in the file: /<command> then end-of-URL or ?args= — never "/wall 0 0".
            var urls = Regex.Matches(PromptFile(), @"http://127\.0\.0\.1:18884(/[^""'\s]*)")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(urls);
            foreach (var url in urls)
                Assert.Matches(@"^/[a-z0-9_]+(\?args=\S+)?$", url);
        }

        [Fact]
        public void PromptFile_CommandsWithArgsUseTheArgsQuery()
        {
            var text = PromptFile();
            Assert.Contains("curl -s \"http://127.0.0.1:18884/wall?args=ARGS\"   ARGS: <x1> <y1> <x2> <y2>", text);
            Assert.Contains("curl -s \"http://127.0.0.1:18884/delete?args=ARGS\"   ARGS: <id>", text);
            Assert.Contains("curl -s \"http://127.0.0.1:18884/info\"", text);
            Assert.Contains("/wall?args=0%200%206000%200", text);
            Assert.Contains("POST \"http://127.0.0.1:18884/batch\"", text);
            // The old form, grammar straight after the slash, is gone.
            Assert.DoesNotContain("18884/wall <", text);
            Assert.DoesNotContain("18884/delete <", text);
        }

        [Theory]
        [InlineData("wall", "wall <x1> <y1> <x2> <y2> [height]", "<x1> <y1> <x2> <y2> [height]")]
        [InlineData("info", "info", "")]
        [InlineData("info", "", "")]
        [InlineData("color", "color <elementId> <R> <G> <B> | color <id> reset", "<elementId> <R> <G> <B> | <id> reset")]
        [InlineData("view", "view plan|3d [levelName]", "plan|3d [levelName]")]
        [InlineData("dimension", "dimension <id> [--dir x|y]  |  dimension --levels [<x>]", "<id> [--dir x|y] | --levels [<x>]")]
        [InlineData("colorsplash", "colorsplash <category> | colorsplash", "<category> | (none)")]
        public void ArgsGrammar_StripsTheCommandName(string name, string usage, string expected)
        {
            Assert.Equal(expected, ClaudeCodeBackend.ArgsGrammar(name, usage));
        }

        // ---- Turn prompt -----------------------------------------------------------

        [Fact]
        public void TurnPrompt_RepeatsTheArgsRule()
        {
            var prompt = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl");
            Assert.Contains(ClaudeCodeBackend.ArgsRule, prompt);
        }
    }
}
