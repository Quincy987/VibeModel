using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CSharp;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 17: the loop-check instrumentation of exec snippets and the clock behind it. The
    /// scanner must touch only real loops with braced bodies — never text in strings, chars or
    /// comments — and leave a snippet without loops byte-identical.
    /// </summary>
    public class ExecDeadlineTests
    {
        private static string Instrument(string code) => ExecDeadline.Instrument(code, out _, out _);

        [Fact]
        public void NoLoops_SameString()
        {
            const string code = "sb.AppendLine(doc.Title);";
            Assert.Same(code, ExecDeadline.Instrument(code, out var n, out var b));
            Assert.Equal(0, n);
            Assert.Equal(0, b);
        }

        [Theory]
        [InlineData("while (true) { x++; }", "while (true) { __vm.Tick(); x++; }")]
        [InlineData("for(;;){x++;}", "for(;;){ __vm.Tick();x++;}")]
        [InlineData("foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall))) { n++; }",
                    "foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall))) { __vm.Tick(); n++; }")]
        [InlineData("do { i++; } while (i < 10);", "do { __vm.Tick(); i++; } while (i < 10);")]
        [InlineData("for (;;) /* c */ { }", "for (;;) /* c */ { __vm.Tick(); }")]
        [InlineData("while (a)\r\n{\r\n  b();\r\n}", "while (a)\r\n{ __vm.Tick();\r\n  b();\r\n}")]
        [InlineData("xs.ForEach(x => { while (a) { } });", "xs.ForEach(x => { while (a) { __vm.Tick(); } });")]
        public void BracedLoops_GetATick(string code, string expected)
        {
            Assert.Equal(expected, Instrument(code));
        }

        [Fact]
        public void NestedLoops_BothChecked()
        {
            var result = ExecDeadline.Instrument("for (int i = 0; i < n; i++) { for (int j = 0; j < m; j++) { k++; } }", out var n, out _);
            Assert.Equal(2, n);
            Assert.Equal("for (int i = 0; i < n; i++) { __vm.Tick(); for (int j = 0; j < m; j++) { __vm.Tick(); k++; } }", result);
        }

        [Fact]
        public void BracelessLoop_IsCounted_NotTouched()
        {
            const string code = "foreach (var x in xs) sb.AppendLine(x);";
            Assert.Same(code, ExecDeadline.Instrument(code, out var n, out var b));
            Assert.Equal(0, n);
            Assert.Equal(1, b);
        }

        [Fact]
        public void EmptyLoopAndDoWhileTail_AreNotBraceless()
        {
            ExecDeadline.Instrument("for (i = 0; i < 3; i++); do { } while (x);", out var n, out var b);
            Assert.Equal(1, n);
            Assert.Equal(0, b);
        }

        [Theory]
        [InlineData("sb.AppendLine(\"while (true) { }\");")]
        [InlineData("sb.AppendLine(\"a \\\" for (;;) { \\\" b\");")]
        [InlineData("var s = @\"line \"\" for (;;) {\r\n while (x) { \";")]
        [InlineData("var s = $\"{(x ? \"a\" : \"b\")} for (;;) {{ }}\";")]
        [InlineData("var s = $@\"{n} while (a) {{ }}\";")]
        [InlineData("// while (true) {\r\nx++;")]
        [InlineData("/* for (;;) { */ x++;")]
        [InlineData("var c = '{'; var q = '\"'; var b = '\\'';")]
        [InlineData("format(x); var whileX = 1; doSomething(); undo(); var @for = 2;")]
        public void TextThatIsNoLoop_IsUntouched(string code)
        {
            Assert.Same(code, Instrument(code));
        }

        [Fact]
        public void LoopAfterAnInterpolatedString_StillChecked()
        {
            Assert.Equal("var s = $\"{list.Count} {{x}}\"; while (a) { __vm.Tick(); }",
                Instrument("var s = $\"{list.Count} {{x}}\"; while (a) { }"));
        }

        [Fact]
        public void InterpolationHoleWithAString_DoesNotDerailTheScanner()
        {
            Assert.Equal("var s = $\"{(ok ? \"}\" : \"{\")}\"; for (;;) { __vm.Tick(); }",
                Instrument("var s = $\"{(ok ? \"}\" : \"{\")}\"; for (;;) { }"));
        }

        [Fact]
        public void Clock_FiresOnceExpired_AndStaysFired()
        {
            var clock = ExecClock.Expired();
            Assert.Throws<ExecDeadlineException>(() => clock.Tick());
            Assert.True(clock.Fired);
            Assert.Throws<ExecDeadlineException>(() => clock.Tick());
        }

        [Fact]
        public void Clock_WithinBudget_DoesNothing()
        {
            var clock = new ExecClock(30);
            for (int i = 0; i < 1000; i++) clock.Tick();
            Assert.False(clock.Fired);
        }

        [Theory]
        [InlineData(null, 25)]
        [InlineData(60, 60)]
        [InlineData(0, 1)]
        [InlineData(1000, 300)]
        public void Budget_DefaultAndClamp(int? requested, int expected)
        {
            Assert.Equal(expected, ExecDeadline.BudgetSeconds(requested));
        }

        [Fact]
        public void Messages()
        {
            Assert.Equal("exec stopped after 25 s at a loop check (nothing kept).", ExecDeadline.StoppedMessage(25, false));
            Assert.Contains("were kept", ExecDeadline.StoppedMessage(25, true));
            Assert.Equal("narrow the collector or raise ?timeout=<s> (max 300).", ExecDeadline.StoppedHint);
        }

        [Fact]
        public void HttpWait_CoversTheBudget()
        {
            var usual = TimeSpan.FromSeconds(30);
            Assert.Equal(TimeSpan.FromSeconds(35), ExecDeadline.Wait(usual, true, null));
            Assert.Equal(TimeSpan.FromSeconds(70), ExecDeadline.Wait(usual, true, 60));
            Assert.Equal(TimeSpan.FromSeconds(310), ExecDeadline.Wait(usual, true, 900));
            Assert.Equal(usual, ExecDeadline.Wait(usual, false, 60));
            // A batch's own, longer wait is never shortened.
            Assert.Equal(TimeSpan.FromSeconds(300), ExecDeadline.Wait(TimeSpan.FromSeconds(300), true, null));
        }

        [Theory]
        [InlineData("timeout=60", 60)]
        [InlineData("args=x&timeout=120&format=json", 120)]
        [InlineData("timeout=abc", null)]
        [InlineData("timeout=0", null)]
        [InlineData("args=timeout%3D5", null)]
        public void Timeout_FromQuery(string query, int? expected)
        {
            Assert.Equal(expected, RevitHttpServer.ParseRunOptions(query).TimeoutSeconds);
        }

        [Fact]
        public void PromptFile_ExplainsTheTimeout()
        {
            var text = VibeModel.Services.Chat.ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, null);
            Assert.Contains(VibeModel.Services.Chat.ClaudeCodeBackend.ExecTimeoutRule, text);
            Assert.Contains(VibeModel.Services.Chat.ClaudeCodeBackend.ApiLookupRule, text);
            Assert.Contains("/apilookup?args=ARGS", text);
        }

        /// <summary>
        /// End to end without Revit: an instrumented endless loop, compiled the way exec compiles
        /// (CodeDom), stops at its deadline.
        /// </summary>
        [Fact]
        public void InstrumentedEndlessLoop_Compiles_AndStops()
        {
            var body = Instrument("int n = 0; while (true) { n++; }");
            var source = "public static class T { public static int Run(VibeModel.Services.Claude.ExecClock __vm) { " + body + " } }";
            CompilerResults results;
            using (var provider = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters { GenerateInMemory = true };
                parameters.ReferencedAssemblies.Add("System.dll");
                parameters.ReferencedAssemblies.Add(typeof(ExecClock).Assembly.Location);
                results = provider.CompileAssemblyFromSource(parameters, source);
            }
            Assert.False(results.Errors.HasErrors, results.Errors.Count > 0 ? results.Errors[0].ErrorText : "");

            var run = results.CompiledAssembly.GetType("T").GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            var clock = new ExecClock(1);
            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<TargetInvocationException>(() => run.Invoke(null, new object[] { clock }));
            sw.Stop();
            Assert.IsType<ExecDeadlineException>(ex.InnerException);
            Assert.True(clock.Fired);
            Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 5);
        }
    }
}
