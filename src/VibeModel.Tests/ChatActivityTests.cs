using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.UI;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Pins the chat pane's per-turn step model (TurnActivity) and the tool-call / tool-result
    /// parsing that feeds it (ToolCallDescriber): steps add/complete/fail, the cap at 8, the
    /// split between intermediate notes and the answer, and the collapse summary.
    /// </summary>
    public class ChatActivityTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

        private static ChatActivityEvent Start(string id, string command, string detail = "", string echo = null) =>
            ChatActivityEvent.Started(id, new ToolCallInfo(command, detail), echo);

        private static ChatActivityEvent Done(string id, string error = null) =>
            ChatActivityEvent.Finished(id, error);

        // --- TurnActivity -------------------------------------------------------------

        [Fact]
        public void NewTurn_IsThinking_UntilSomethingHappens()
        {
            var turn = new TurnActivity(T0);
            Assert.True(turn.IsThinking);
            Assert.False(turn.HasSteps);

            turn.AppendText("Hallo", T0.AddSeconds(2));
            Assert.False(turn.IsThinking);
            Assert.Equal("Hallo", turn.LiveText);
        }

        [Fact]
        public void Steps_Add_Complete_Fail()
        {
            var turn = new TurnActivity(T0);
            turn.Apply(Start("a", "wall", "0 0 8000 0"), T0.AddSeconds(1));
            Assert.True(turn.HasRunningStep);
            Assert.False(turn.IsThinking);

            turn.Apply(Done("a"), T0.AddSeconds(2));
            turn.Apply(Start("b", "get", "999"), T0.AddSeconds(3));
            turn.Apply(Done("b", "Element 999 not found"), T0.AddSeconds(4));

            var steps = turn.Steps.ToList();
            Assert.Equal(2, steps.Count);
            Assert.Equal(TurnActivity.StepState.Done, steps[0].State);
            Assert.Equal("0 0 8000 0", steps[0].Detail);
            Assert.Equal(TurnActivity.StepState.Failed, steps[1].State);
            Assert.Equal("Element 999 not found", steps[1].ErrorLine);
            Assert.Equal(1, turn.FailedCount);
            // Between steps the model is thinking again.
            Assert.True(turn.IsThinking);
        }

        [Fact]
        public void DuplicateStart_AndUnknownFinish_AreIgnored()
        {
            var turn = new TurnActivity(T0);
            turn.Apply(Start("a", "wall"), T0);
            turn.Apply(Start("a", "wall"), T0);
            turn.Apply(Done("zzz", "boom"), T0);
            Assert.Equal(1, turn.StepCount);
            Assert.Equal(TurnActivity.StepState.Running, turn.Steps.Single().State);
        }

        [Fact]
        public void TextBeforeToolCall_BecomesNote_TextAfterLastCall_IsAnswer()
        {
            var turn = new TurnActivity(T0);
            turn.AppendText("Ik maak eerst de wand.", T0);
            turn.Apply(Start("a", "wall", "0 0 8000 0"), T0);
            turn.Apply(Done("a"), T0);
            turn.AppendText("\n\nNu de screenshot.", T0);
            turn.Apply(Start("b", "screenshot"), T0);
            turn.Apply(Done("b"), T0);
            turn.AppendText("\n\nDe wand staat er.", T0);
            turn.Finish(T0.AddSeconds(48));

            Assert.Equal("De wand staat er.", turn.Answer);
            var items = turn.Items;
            Assert.Equal(4, items.Count);
            Assert.Equal("Ik maak eerst de wand.", items[0].Note);
            Assert.True(items[1].IsStep);
            Assert.Equal("Nu de screenshot.", items[2].Note);
            Assert.True(items[3].IsStep);
        }

        [Fact]
        public void EchoedStatusLine_IsHiddenFromDisplay()
        {
            // Anthropic/Local stream "\n\nCreating wall…\n\n" right after the ToolStarted event;
            // the step row replaces it in the display (it still lands in the stored transcript).
            var turn = new TurnActivity(T0);
            const string echo = "\n\nCreating wall…\n\n";
            turn.Apply(Start("a", "wall", "0 0 1 0", echo), T0);
            turn.AppendText(echo, T0);
            turn.Apply(Done("a"), T0);
            turn.AppendText("\n\n", T0);
            turn.AppendText("Klaar.", T0);
            turn.Finish(T0);

            Assert.Equal("Klaar.", turn.Answer);
            Assert.Single(turn.Items); // just the step, no note made of the echo
        }

        [Fact]
        public void EchoIsOnlySkippedOnce_AndOnlyWhenItMatches()
        {
            var turn = new TurnActivity(T0);
            turn.Apply(Start("a", "wall", "", "\n\nCreating wall…\n\n"), T0);
            turn.AppendText("something else", T0);
            Assert.Equal("something else", turn.LiveText);
            turn.AppendText("\n\nCreating wall…\n\n", T0); // no longer pending → kept
            Assert.Contains("Creating wall", turn.LiveText);
        }

        [Fact]
        public void TurnWithoutTools_HasNoSteps_AndWholeTextIsAnswer()
        {
            var turn = new TurnActivity(T0);
            turn.AppendText("Er is een document open: ", T0);
            turn.AppendText("Project1.", T0);
            turn.Finish(T0.AddSeconds(3));

            Assert.False(turn.HasSteps);
            Assert.Empty(turn.Items);
            Assert.Equal("Er is een document open: Project1.", turn.Answer);
        }

        [Fact]
        public void EndingRightAfterAStep_PromotesTheLastNoteToAnswer()
        {
            var turn = new TurnActivity(T0);
            turn.AppendText("Ik verwijder de wand.", T0);
            turn.Apply(Start("a", "delete", "123"), T0);
            turn.Apply(Done("a"), T0);
            turn.Finish(T0);

            Assert.Equal("Ik verwijder de wand.", turn.Answer);
            Assert.Single(turn.Items);
            Assert.True(turn.Items[0].IsStep);
        }

        [Fact]
        public void EmptyStream_UsesFallbackText()
        {
            var turn = new TurnActivity(T0);
            turn.Finish(T0, fallbackText: "full text from onComplete");
            Assert.Equal("full text from onComplete", turn.Answer);
        }

        [Fact]
        public void Finish_CompletesOrCancelsRunningSteps()
        {
            var done = new TurnActivity(T0);
            done.Apply(Start("a", "wall"), T0);
            done.Finish(T0);
            Assert.Equal(TurnActivity.StepState.Done, done.Steps.Single().State);

            var cancelled = new TurnActivity(T0);
            cancelled.Apply(Start("a", "wall"), T0);
            cancelled.Finish(T0, cancelled: true);
            Assert.Equal(TurnActivity.StepState.Cancelled, cancelled.Steps.Single().State);
        }

        [Fact]
        public void AfterFinish_FurtherInputIsIgnored()
        {
            var turn = new TurnActivity(T0);
            turn.Finish(T0.AddSeconds(5));
            turn.AppendText("late", T0.AddSeconds(9));
            turn.Apply(Start("x", "wall"), T0.AddSeconds(9));
            Assert.Equal(0, turn.StepCount);
            Assert.Equal(TimeSpan.FromSeconds(5), turn.Elapsed(T0.AddSeconds(100)));
        }

        [Fact]
        public void LiveView_CapsAtEightSteps_WithHiddenCount()
        {
            var turn = new TurnActivity(T0);
            for (var i = 1; i <= 11; i++)
            {
                turn.AppendText("note " + i, T0);
                turn.Apply(Start("s" + i, "wall", i.ToString()), T0);
                turn.Apply(Done("s" + i), T0);
            }

            int hidden;
            var live = turn.VisibleItems(false, out hidden);
            Assert.Equal(3, hidden);
            Assert.Equal(8, live.Count(i => i.IsStep));
            // Starts at the 4th step; the note in front of it belongs to the hidden part.
            Assert.True(live[0].IsStep);
            Assert.Equal("4", live[0].Step.Detail);
            Assert.Equal("11", live.Last().Step.Detail);

            var all = turn.VisibleItems(true, out hidden);
            Assert.Equal(0, hidden);
            Assert.Equal(22, all.Count);
        }

        [Fact]
        public void LiveView_EightOrFewerSteps_ShowsEverything()
        {
            var turn = new TurnActivity(T0);
            for (var i = 1; i <= 8; i++)
                turn.Apply(Start("s" + i, "wall"), T0);
            int hidden;
            Assert.Equal(8, turn.VisibleItems(false, out hidden).Count);
            Assert.Equal(0, hidden);
        }

        [Fact]
        public void Stall_After60sWithoutProgress_ResetByAnyEvent()
        {
            var turn = new TurnActivity(T0);
            Assert.False(turn.IsStalled(T0.AddSeconds(59)));
            Assert.True(turn.IsStalled(T0.AddSeconds(60)));

            turn.Apply(Start("a", "screenshot"), T0.AddSeconds(61));
            Assert.False(turn.IsStalled(T0.AddSeconds(100)));
            Assert.True(turn.IsStalled(T0.AddSeconds(121)));

            turn.Finish(T0.AddSeconds(130));
            Assert.False(turn.IsStalled(T0.AddSeconds(500)));
        }

        // --- Summary / clock strings ----------------------------------------------------

        [Fact]
        public void Summary_Dutch_And_English()
        {
            // One language source (Settings -> Taal / Language); the tests pass it explicitly.
            Assert.Equal("7 stappen · 48 s", UiText.StepSummary(7, 0, TimeSpan.FromSeconds(48), "nl"));
            Assert.Equal("1 stap · 1 mislukt · 1 min 12 s", UiText.StepSummary(1, 1, TimeSpan.FromSeconds(72), "nl"));
            Assert.Equal("+3 eerdere stappen", UiText.EarlierSteps(3, "nl"));
            Assert.Equal("+1 eerdere stap", UiText.EarlierSteps(1, "nl"));
            Assert.Equal("Wand maken…", UiText.StepLabel("wall", "nl"));
            Assert.Equal("Zichtbaarheid onderzoeken…", UiText.StepLabel("whyhidden", "nl"));
            Assert.Equal("whyhidden2…", UiText.StepLabel("whyhidden2", "nl"));
            Assert.Equal("Bestand lezen…", UiText.StepLabel("@read", "nl"));
            Assert.Equal("WebFetch…", UiText.StepLabel("@WebFetch", "nl"));

            Assert.Equal("2 steps · 5 s", UiText.StepSummary(2, 0, TimeSpan.FromSeconds(5), "en"));
            Assert.Equal("1 step · 2 failed · 5 s", UiText.StepSummary(1, 2, TimeSpan.FromSeconds(5), "en"));
            Assert.Equal("Creating wall…", UiText.StepLabel("wall", "en"));
            Assert.Equal("Reading file…", UiText.StepLabel("@read", "en"));
            Assert.Equal("whyhidden2…", UiText.StepLabel("whyhidden2", "en"));
        }

        [Fact]
        public void StepLabels_AreTheStatusVerbs()
        {
            // One label table: a step row and the status line of the same command read the same.
            foreach (var lang in new[] { "nl", "en" })
                foreach (var cmd in new[] { "wall", "delete", "color", "whyhidden", "readdoc", "check" })
                    Assert.Equal(StatusVerbs.Describe(cmd, lang), UiText.StepLabel(cmd, lang));
        }

        [Fact]
        public void Clock_IsMinutesSeconds()
        {
            Assert.Equal("00:00", UiText.Clock(TimeSpan.Zero));
            Assert.Equal("00:48", UiText.Clock(TimeSpan.FromSeconds(48.9)));
            Assert.Equal("01:05", UiText.Clock(TimeSpan.FromSeconds(65)));
        }

        // --- ToolCallDescriber ----------------------------------------------------------

        [Fact]
        public void RevitTool_StripsPrefix_AndShortensArgs()
        {
            var call = ToolCallDescriber.ForRevitTool("revit_wall", "0 0   8000\n0");
            Assert.Equal("wall", call.Command);
            Assert.Equal("0 0 8000 0", call.Detail);

            var longArgs = ToolCallDescriber.ForRevitTool("revit_exec", new string('x', 200));
            Assert.True(longArgs.Detail.Length <= ToolCallDescriber.MaxDetailLength);
            Assert.EndsWith("…", longArgs.Detail);
        }

        [Fact]
        public void RevitTool_Batch_ShowsFirstLinePlusCount()
        {
            var call = ToolCallDescriber.ForRevitTool("revit_batch", "wall 0 0 5000 0\nwall 5000 0 5000 5000\nwall 5000 5000 0 5000");
            Assert.Equal("batch", call.Command);
            Assert.Equal("wall 0 0 5000 0 (+2)", call.Detail);
        }

        [Fact]
        public void CliBash_CurlToOurServer_BecomesTheVibeModelCommand()
        {
            var call = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s \"http://127.0.0.1:18884/wall?args=0%200%208000%200%203000\"" },
                { "description", "Create wall" }
            });
            Assert.Equal("wall", call.Command);
            Assert.Equal("0 0 8000 0 3000", call.Detail);

            var plus = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s \"http://localhost:18885/list?args=walls&format=json\"" }
            });
            Assert.Equal("list", plus.Command);
            Assert.Equal("walls", plus.Detail);

            var noArgs = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s http://127.0.0.1:18884/screenshot" }
            });
            Assert.Equal("screenshot", noArgs.Command);
            Assert.Equal("", noArgs.Detail);

            var plusSign = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s \"http://127.0.0.1:18884/set?args=123+Comments+Hello\"" }
            });
            Assert.Equal("123 Comments Hello", plusSign.Detail);
        }

        [Fact]
        public void CliPowerShell_CurlExe_IsRecognizedToo()
        {
            var call = ToolCallDescriber.ForCliTool("PowerShell", new Dictionary<string, object>
            {
                { "command", "curl.exe -s \"http://127.0.0.1:18884/screenshot?args=1024\"" }
            });
            Assert.Equal("screenshot", call.Command);
            Assert.Equal("1024", call.Detail);
        }

        [Fact]
        public void CliBash_BatchPost_ReadsTheBody()
        {
            var call = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s -X POST http://127.0.0.1:18884/batch -d \"wall 0 0 5000 0\nwall 5000 0 5000 5000\"" }
            });
            Assert.Equal("batch", call.Command);
            Assert.Equal("wall 0 0 5000 0 (+1)", call.Detail);
        }

        [Fact]
        public void CliBash_OtherCommand_UsesDescription()
        {
            var call = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "ls -la C:/temp" },
                { "description", "List temp folder" }
            });
            Assert.Equal("@shell", call.Command);
            Assert.Equal("List temp folder", call.Detail);
        }

        [Fact]
        public void CliRead_ImageVersusFile()
        {
            var image = ToolCallDescriber.ForCliTool("Read", new Dictionary<string, object>
            {
                { "file_path", @"C:\Users\x\AppData\Local\VibeModel\screenshots\shot_1.png" }
            });
            Assert.Equal("@image", image.Command);
            Assert.Equal("shot_1.png", image.Detail);

            var file = ToolCallDescriber.ForCliTool("Read", new Dictionary<string, object>
            {
                { "file_path", "C:/Users/x/AppData/Local/VibeModel/system-prompt-18884.txt" }
            });
            Assert.Equal("@read", file.Command);
            Assert.Equal("system-prompt-18884.txt", file.Detail);
        }

        [Theory]
        [InlineData("{\"ok\":true,\"data\":{\"id\":5}}", false, null)]
        [InlineData("{\"ok\":false,\"error\":{\"code\":\"x\",\"message\":\"Element 999 not found\\nmore\"}}", false, "Element 999 not found")]
        [InlineData("ERROR: Level 'L9' not found.\nHint: use levels", false, "Level 'L9' not found.")]
        [InlineData("Created wall 12345\n-- model #48 | view: L1 | selected: 0", false, null)]
        [InlineData("curl: (7) Failed to connect to 127.0.0.1 port 18884", false, "curl: (7) Failed to connect to 127.0.0.1 port 18884")]
        [InlineData("Exit code 1\nsomething broke", true, "Exit code 1")]
        [InlineData("", false, null)]
        public void ErrorLine_Classifies(string result, bool isError, string expected)
        {
            Assert.Equal(expected, ToolCallDescriber.ErrorLine(result, isError));
        }

        [Fact]
        public void ErrorLine_Batch_OnlyRolledBackFails()
        {
            const string partial = "{\"ok\":false,\"atomic\":false,\"rolledBack\":false,\"results\":[" +
                "{\"command\":\"wall\",\"args\":\"\",\"result\":\"ERROR: bad args\"}]}";
            Assert.Null(ToolCallDescriber.ErrorLine(partial));

            const string rolled = "{\"ok\":false,\"atomic\":true,\"rolledBack\":true,\"results\":[" +
                "{\"command\":\"wall\",\"args\":\"1\",\"result\":{\"ok\":true}}," +
                "{\"command\":\"wall\",\"args\":\"\",\"result\":{\"ok\":false,\"error\":{\"message\":\"bad args\"}}}]}";
            Assert.Equal("bad args", ToolCallDescriber.ErrorLine(rolled));
        }

        [Fact]
        public void ResultText_StringOrTextBlocks()
        {
            Assert.Equal("plain", ToolCallDescriber.ResultText("plain"));
            var blocks = new object[]
            {
                new Dictionary<string, object> { { "type", "text" }, { "text", "one" } },
                new Dictionary<string, object> { { "type", "image" } },
                new Dictionary<string, object> { { "type", "text" }, { "text", "two" } },
            };
            Assert.Equal("one\ntwo", ToolCallDescriber.ResultText(blocks));
            Assert.Equal("", ToolCallDescriber.ResultText(null));
        }
    }
}
