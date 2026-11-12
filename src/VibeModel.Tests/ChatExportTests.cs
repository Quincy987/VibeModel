using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude.Commands;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The chat export: steps recorded per turn (activity model → stored message → JSON and back),
    /// the markdown per chat (stored steps, the debug-log fallback, nothing recorded), the secret
    /// scrubbing, and the writer's single file / zip.
    /// </summary>
    public class ChatExportTests : IDisposable
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc);
        private readonly string _dir;

        public ChatExportTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VibeModelExportTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static ChatSession SessionWithSteps()
        {
            var s = new ChatSession { Id = "20261112-130000-abcdef12", Project = "Kade demo", Backend = "claude-cli", CreatedUtc = T0, UpdatedUtc = T0.AddMinutes(1) };
            s.Messages.Add(new ChatSessionMessage { Role = ChatSession.RoleUser, Content = "Hoeveel wanden?", TimestampUtc = T0 });
            s.Messages.Add(new ChatSessionMessage
            {
                Role = ChatSession.RoleAssistant,
                Content = "Er staan **12** wanden.",
                TimestampUtc = T0.AddSeconds(30),
                Model = "claude-sonnet-5-5",
                DurationMs = 30000,
                Usage = new ChatSessionUsage { InputTokens = 50000, OutputTokens = 800, ContextTokens = 50000 },
                Steps = new List<ChatSessionStep>
                {
                    new ChatSessionStep { Command = "filter", Args = "walls", Ok = true, DurationMs = 120 },
                    new ChatSessionStep { Command = "wall", Args = "0 0 5000 0 --type Nope", Ok = false, Error = "Wall type 'Nope' not found", DurationMs = 80 },
                    new ChatSessionStep { Command = "exec", Args = "// Doel: tel wanden\nsb.AppendLine(\"x\");", Ok = true, DurationMs = 1500 }
                }
            });
            s.Title = "Hoeveel wanden?";
            return s;
        }

        // --- recording ------------------------------------------------------------------

        [Fact]
        public void TurnActivity_KeepsFullArgsAndDurations()
        {
            var turn = new TurnActivity(T0);
            var call = ToolCallDescriber.ForRevitTool("revit_exec", "// Doel: test\n" + new string('x', 100));
            turn.Apply(ChatActivityEvent.Started("1", call), T0.AddSeconds(1));
            turn.Apply(ChatActivityEvent.Finished("1", null), T0.AddSeconds(3));
            turn.Apply(ChatActivityEvent.Started("2", ToolCallDescriber.ForRevitTool("revit_wall", "0 0 5000 0")), T0.AddSeconds(4));
            turn.Apply(ChatActivityEvent.Finished("2", "Wall type not found"), T0.AddSeconds(4.5));
            turn.Finish(T0.AddSeconds(5));

            var steps = ChatSessionStep.FromTurn(turn);
            Assert.Equal(2, steps.Count);
            Assert.StartsWith("// Doel: test\nxxx", steps[0].Args);         // full, line break kept (the detail is 60 chars)
            Assert.True(steps[0].Ok);
            Assert.Equal(2000, steps[0].DurationMs);
            Assert.False(steps[1].Ok);
            Assert.Equal("Wall type not found", steps[1].Error);
            Assert.Equal(500, steps[1].DurationMs);
        }

        [Fact]
        public void CliCurl_FullArgsAreTheDecodedArgs_OrTheWholeCommand()
        {
            var simple = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s -H \"X-VibeModel-Token: $VIBEMODEL_TOKEN\" \"http://127.0.0.1:18884/wall?args=0%200%205000%200\"" }
            });
            Assert.Equal("0 0 5000 0", simple.FullArgs);

            var encoded = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object>
            {
                { "command", "curl -s -G \"http://127.0.0.1:18884/exec\" -H \"X-VibeModel-Token: abcdef0123456789abcdef\" --data-urlencode \"args=sb.AppendLine(doc.Title);\"" }
            });
            Assert.Contains("--data-urlencode \"args=sb.AppendLine(doc.Title);\"", encoded.FullArgs);
            Assert.DoesNotContain("abcdef0123456789abcdef", encoded.FullArgs);
            Assert.Contains("X-VibeModel-Token: <token>", encoded.FullArgs);
        }

        [Fact]
        public void CliCurl_SeveralCurlsInOneCall_KeepTheWholeCommand()
        {
            var cmd = "curl -s \"http://127.0.0.1:18884/info\" && curl -s \"http://127.0.0.1:18884/wall?args=0%200%205000%200\"";
            var call = ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object> { { "command", cmd } });
            Assert.Equal("info", call.Command);
            Assert.Equal(cmd, call.FullArgs);
        }

        [Fact]
        public void CliCurl_InALoopOrPipe_KeepsTheWholeCommand()
        {
            // Live: the stored args of this call were just "$c".
            var loop = "for c in walls floors; do curl -s -H \"X-VibeModel-Token: $T\" \"http://127.0.0.1:18884/list?args=$c\"; done";
            Assert.Equal(loop, ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object> { { "command", loop } }).FullArgs);
            var pipe = "curl -s \"http://127.0.0.1:18884/list?args=walls\" | tail -5";
            Assert.Equal(pipe, ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object> { { "command", pipe } }).FullArgs);
            var preview = "curl -s \"http://127.0.0.1:18884/wall?args=0%200%205000%200&preview=1\"";   // & inside the URL is fine
            Assert.Equal("0 0 5000 0", ToolCallDescriber.ForCliTool("Bash", new Dictionary<string, object> { { "command", preview } }).FullArgs);
        }

        [Fact]
        public void StepStillRunning_IsStoredAsNotFinished()
        {
            var turn = new TurnActivity(T0);
            turn.Apply(ChatActivityEvent.Started("1", ToolCallDescriber.ForRevitTool("revit_info", "")), T0);
            var steps = ChatSessionStep.FromTurn(turn);                      // an error arrived before the turn finished
            Assert.False(steps[0].Ok);
            Assert.Equal("not finished", steps[0].Error);
            Assert.Null(steps[0].DurationMs);
        }

        [Fact]
        public void FullArgs_AreCapped()
        {
            var full = ToolCallDescriber.Full(new string('a', 5000));
            Assert.StartsWith(new string('a', ToolCallDescriber.MaxFullArgsLength), full);
            Assert.EndsWith("… (+1000 tekens)", full);
        }

        [Fact]
        public void Store_RoundTripsTheTurnRecord_AndReadsOldFiles()
        {
            var store = new ChatSessionStore(_dir);
            var s = SessionWithSteps();
            store.SaveSession(s);
            var loaded = store.LoadSession(s.Id);
            var reply = loaded.Messages[1];
            Assert.Equal(3, reply.Steps.Count);
            Assert.Equal("wall", reply.Steps[1].Command);
            Assert.False(reply.Steps[1].Ok);
            Assert.Equal("Wall type 'Nope' not found", reply.Steps[1].Error);
            Assert.Equal("// Doel: tel wanden\nsb.AppendLine(\"x\");", reply.Steps[2].Args);
            Assert.Equal(1500, reply.Steps[2].DurationMs);
            Assert.Equal(50000, reply.Usage.InputTokens);
            Assert.Null(reply.Usage.CostUsd);

            // A reported cost (the CLI's total_cost_usd) survives the JSON number round trip.
            s.Messages[1].Usage.CostUsd = 0.0123;
            store.SaveSession(s);
            Assert.Equal(0.0123, store.LoadSession(s.Id).Messages[1].Usage.CostUsd.Value, 6);
            Assert.Equal("claude-sonnet-5-5", reply.Model);
            Assert.Equal(30000, reply.DurationMs);
            Assert.Null(loaded.Messages[0].Steps);                      // the question carries none

            // A file from an older build: no steps/usage/model keys at all.
            File.WriteAllText(Path.Combine(_dir, "20260903-000528-123df2fb.json"),
                "{\"id\":\"20260903-000528-123df2fb\",\"title\":\"oud\",\"project\":\"\",\"backend\":\"anthropic\"," +
                "\"createdUtc\":\"2026-09-03T00:05:28.0000000Z\",\"updatedUtc\":\"2026-09-03T00:06:00.0000000Z\"," +
                "\"messages\":[{\"role\":\"user\",\"content\":\"hoi\",\"timestampUtc\":\"2026-09-03T00:05:28.0000000Z\"}," +
                "{\"role\":\"assistant\",\"content\":\"hallo\",\"timestampUtc\":\"2026-09-03T00:06:00.0000000Z\"}]}");
            var old = store.LoadSession("20260903-000528-123df2fb");
            Assert.Equal("hallo", old.Messages[1].Content);
            Assert.Null(old.Messages[1].Steps);
            Assert.Null(old.Messages[1].Usage);
            Assert.Null(old.Messages[1].Model);

            Assert.Equal(2, store.LoadSessions(0).Count);
            Assert.Single(store.LoadSessions(1));
        }

        [Fact]
        public void Store_WithoutTurnRecord_WritesTheOldShape()
        {
            var store = new ChatSessionStore(_dir);
            var s = ChatSession.Start("P", "anthropic");
            s.Append(ChatSession.RoleUser, "vraag");
            s.Append(ChatSession.RoleAssistant, "antwoord");
            store.SaveSession(s);
            var json = File.ReadAllText(Path.Combine(_dir, s.Id + ".json"));
            Assert.DoesNotContain("steps", json);
            Assert.DoesNotContain("usage", json);
        }

        // --- the document -----------------------------------------------------------------

        [Fact]
        public void Render_WithStoredSteps_HasQuestionCommandsAnswerAndTokens()
        {
            ChatExportStats stats;
            var md = ChatExportDocument.Render(SessionWithSteps(), null, Strings.Dutch, out stats);
            Assert.Contains("# Hoeveel wanden?", md);
            Assert.Contains("Project: Kade demo", md);
            Assert.Contains("model: claude-sonnet-5-5", md);
            Assert.Contains("**Vraag**", md);
            Assert.Contains("Hoeveel wanden?", md);
            Assert.Contains("**Commands** (3, 1 mislukt)", md);
            Assert.Contains("| 1 | filter | walls | ✓ | 0.1 s |", md);
            Assert.Contains("| 2 | wall | 0 0 5000 0 --type Nope | ✗ Wall type 'Nope' not found | 0.1 s |", md);
            Assert.Contains("```csharp\n// Doel: tel wanden\nsb.AppendLine(\"x\");\n```", md.Replace("\r\n", "\n"));
            Assert.Contains("**Antwoord** (30 s)", md);
            Assert.Contains("Er staan **12** wanden.", md);
            Assert.Contains("50.8k tokens · gesprekslengte 50.0k tokens · ≈ €", md);
            Assert.Contains("≈ €", md);                                   // sonnet is priced
            Assert.Equal(1, stats.Turns);
            Assert.Equal(3, stats.Commands);
            Assert.Equal(1, stats.Failed);
            Assert.Equal(0, stats.ChatsWithoutRecord);
        }

        [Fact]
        public void Render_WithoutSteps_UsesTheDebugLogBetweenQuestionAndAnswer()
        {
            var s = SessionWithSteps();
            s.Messages[1].Steps = null;
            s.Messages[1].Usage = null;
            var qLocal = T0.ToLocalTime();
            var log = ChatExportLog.Parse(qLocal.Date, new[]
            {
                "[" + qLocal.AddSeconds(-60).ToString("HH:mm:ss.fff") + "] [INFO] Executing: info",          // before: not this turn
                "[" + qLocal.AddSeconds(5).ToString("HH:mm:ss.fff") + "] [INFO] Executing: filter walls",
                "[" + qLocal.AddSeconds(6).ToString("HH:mm:ss.fff") + "] [INFO] Registered command: wall",   // not a command line
                "[" + qLocal.AddSeconds(10).ToString("HH:mm:ss.fff") + "] [INFO] Executing: wall 0 0 5000 0",
                "[" + qLocal.AddSeconds(12).ToString("HH:mm:ss.fff") + "] [INFO] Executing batch: 4 commands (atomic)",
                "[" + qLocal.AddSeconds(90).ToString("HH:mm:ss.fff") + "] [INFO] Executing: later",          // after the answer
            });
            Func<DateTime, DateTime, LogLookup> lookup = (from, to) => new LogLookup
            {
                Available = true,
                Commands = log.Where(c => c.LocalTime >= from.ToLocalTime() && c.LocalTime <= to.ToLocalTime()).ToList()
            };

            ChatExportStats stats;
            var md = ChatExportDocument.Render(s, lookup, Strings.Dutch, out stats);
            Assert.Contains("**Commands** (3) — _uit de debuglog — indicatief", md);
            Assert.Contains("batch: 4 commands (atomic)", md);
            Assert.Contains("filter walls", md);
            Assert.Contains("wall 0 0 5000 0", md);
            Assert.DoesNotContain("Executing: later", md);
            Assert.DoesNotContain("` later", md);
            Assert.DoesNotContain("` info", md);
            Assert.Equal(0, stats.Commands);                                  // exact and indicative never summed
            Assert.Equal(3, stats.LogCommands);
            Assert.Equal("~3", stats.CommandsText());
            Assert.Contains("commands: ~3", md);
            Assert.Equal(1, stats.ChatsFromLog);
            Assert.Equal(0, stats.ChatsWithoutRecord);
        }

        [Fact]
        public void Render_NoAnswer_LogWindowStopsAtTheNextQuestion()
        {
            var s = SessionWithSteps();
            s.Messages.RemoveAt(1);                                           // turn 1 got no answer
            s.Messages.Add(new ChatSessionMessage { Role = ChatSession.RoleUser, Content = "Tweede", TimestampUtc = T0.AddMinutes(2) });
            var windows = new List<Tuple<DateTime, DateTime>>();
            ChatExportStats stats;
            ChatExportDocument.Render(s, (from, to) => { windows.Add(Tuple.Create(from, to)); return new LogLookup(); }, Strings.Dutch, out stats);
            Assert.Equal(T0.AddMinutes(2), windows[0].Item2);                 // not into turn 2
            Assert.Equal(T0.AddMinutes(32), windows[1].Item2);                // last turn: 30 min
        }

        [Fact]
        public void Render_NothingRecorded_SaysSo()
        {
            var s = SessionWithSteps();
            s.Messages[1].Steps = null;
            ChatExportStats stats;
            var md = ChatExportDocument.Render(s, (a, b) => new LogLookup(), Strings.Dutch, out stats);
            Assert.Contains("geen commands vastgelegd — chat van vóór deze versie, debuglog stond uit", md);
            Assert.Equal(1, stats.ChatsWithoutRecord);
            var en = ChatExportDocument.Render(s, (a, b) => new LogLookup(), Strings.English, out stats);
            Assert.Contains("**Question**", en);
            Assert.Contains("no commands recorded", en);
        }

        [Fact]
        public void Render_EscapesPipesInTheTable()
        {
            var s = SessionWithSteps();
            s.Messages[1].Steps = new List<ChatSessionStep> { new ChatSessionStep { Command = "filter", Args = "walls Mark contains a|b", Ok = true } };
            ChatExportStats stats;
            var md = ChatExportDocument.Render(s, null, Strings.Dutch, out stats);
            Assert.Contains("walls Mark contains a\\|b", md);
        }

        // --- secrets ---------------------------------------------------------------------

        [Fact]
        public void Scrub_RemovesTokensAndKeys_KeepsPaths()
        {
            var text = "curl -H \"X-VibeModel-Token: 9f8e7d6c5b4a39281706f5e4d3c2b1a0\" http://127.0.0.1:18884/info\n" +
                       "set VIBEMODEL_TOKEN=9f8e7d6c5b4a39281706f5e4d3c2b1a0 & echo\n" +
                       "ANTHROPIC_API_KEY=sk-ant-api03-AbCdEf_123-xyz\n" +
                       "key sk-ant-api03-ZZZZZZZZ in text\n" +
                       "{\"port\":18884,\"pid\":1,\"token\":\"deadbeefdeadbeef\"}\n" +
                       "C:\\Users\\jjansen\\Documents\\Kade.rvt\n" +
                       "curl -H \"X-VibeModel-Token: $VIBEMODEL_TOKEN\" stays readable";
            var s = ChatExportRedaction.Scrub(text, "extra-secret-1234");
            Assert.DoesNotContain("9f8e7d6c5b4a39281706f5e4d3c2b1a0", s);
            Assert.DoesNotContain("AbCdEf_123", s);
            Assert.DoesNotContain("ZZZZZZZZ", s);
            Assert.DoesNotContain("deadbeefdeadbeef", s);
            Assert.Contains("X-VibeModel-Token: <token>", s);
            Assert.Contains("VIBEMODEL_TOKEN=<token>", s);
            Assert.Contains("ANTHROPIC_API_KEY=sk-ant-…", s);
            Assert.Contains("\"token\":\"<token>\"", s);
            Assert.Contains("C:\\Users\\jjansen\\Documents\\Kade.rvt", s);  // paths stay
            Assert.Contains("X-VibeModel-Token: $VIBEMODEL_TOKEN", s);       // a variable reference is no secret
            Assert.Equal("a <token> b", ChatExportRedaction.Scrub("a extra-secret-1234 b", "extra-secret-1234"));
        }

        [Fact]
        public void Render_ScrubsTheAnswerOfAnOldChat()
        {
            var s = SessionWithSteps();
            s.Messages[1].Content = "De token is X-VibeModel-Token: 0123456789abcdef0123";
            ChatExportStats stats;
            var md = ChatExportDocument.Render(s, null, Strings.Dutch, out stats);
            Assert.DoesNotContain("0123456789abcdef0123", md);
        }

        // --- the writer ------------------------------------------------------------------

        [Fact]
        public void Writer_FewChats_OneMarkdownFile()
        {
            var outDir = Path.Combine(_dir, "exports");
            var r = ChatExportWriter.Export(new List<ChatSession> { SessionWithSteps() }, null, outDir, Strings.Dutch, new DateTime(2026, 11, 12, 15, 0, 0));
            Assert.False(r.IsZip);
            Assert.Equal(Path.Combine(outDir, "chats-20261112-150000.md"), r.Path);
            var text = File.ReadAllText(r.Path);
            Assert.StartsWith("# VibeModel-chatexport", text);
            Assert.Contains("# Hoeveel wanden?", text);
            Assert.Equal(3, r.Stats.Commands);

            var again = ChatExportWriter.Export(new List<ChatSession> { SessionWithSteps() }, null, outDir, Strings.Dutch, new DateTime(2026, 11, 12, 15, 0, 0));
            Assert.Equal(Path.Combine(outDir, "chats-20261112-150000 (2).md"), again.Path);
        }

        [Fact]
        public void Writer_ManyChats_ZipWithIndexAndReadme()
        {
            var sessions = Enumerable.Range(0, 7).Select(i =>
            {
                var s = SessionWithSteps();
                s.Id = "id" + i;
                if (i == 3) s.Title = "Hoeveel wanden?";                     // same title → unique entry names
                return s;
            }).ToList();
            var outDir = Path.Combine(_dir, "exports");
            var r = ChatExportWriter.Export(sessions, Path.Combine(_dir, "nologs"), outDir, Strings.English, new DateTime(2026, 11, 12, 15, 0, 0));
            Assert.True(r.IsZip);
            Assert.EndsWith(".zip", r.Path);
            Assert.False(File.Exists(r.Path + ".part"));
            using (var zip = ZipFile.OpenRead(r.Path))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert.Contains("README.md", names);
                Assert.Contains("INDEX.md", names);
                Assert.Equal(7, names.Count(n => n.StartsWith("chats/")));
                Assert.Equal(7, names.Distinct().Count(n => n.StartsWith("chats/")));
                string index;
                using (var rd = new StreamReader(zip.GetEntry("INDEX.md").Open())) index = rd.ReadToEnd();
                Assert.Contains("| date | chat | project | turns | commands | failed | tokens |", index);
                Assert.Contains("Kade demo", index);
            }
            Assert.Equal(7, r.Stats.Chats);
            Assert.Equal(21, r.Stats.Commands);
        }

        [Fact]
        public void Slug_IsFileSafe()
        {
            Assert.Equal("maak-een-muur-van-5-m", ChatExportWriter.Slug("Maak een muur van 5 m?"));
            Assert.Equal("coordinaten", ChatExportWriter.Slug("Coördinaten"));
            Assert.Equal("chat", ChatExportWriter.Slug("???"));
        }

        // --- the command -----------------------------------------------------------------

        [Fact]
        public void ExportCount_Parses()
        {
            int max;
            Assert.True(ChatLogCommand.TryParseExportCount("", out max)); Assert.Equal(20, max);
            Assert.True(ChatLogCommand.TryParseExportCount("5", out max)); Assert.Equal(5, max);
            Assert.True(ChatLogCommand.TryParseExportCount("all", out max)); Assert.Equal(0, max);
            Assert.False(ChatLogCommand.TryParseExportCount("veel", out max));
            Assert.False(ChatLogCommand.TryParseExportCount("0", out max));
        }

        [Fact]
        public void Describe_NamesPathCountsAndTheSecretRule()
        {
            var text = ChatLogCommand.Describe(new ChatExportResult
            {
                Path = @"C:\x\chats-1.zip",
                IsZip = true,
                Stats = new ChatExportStats { Chats = 12, Turns = 30, Commands = 80, Failed = 4, ChatsWithoutRecord = 9, ChatsFromLog = 2 },
                Elapsed = TimeSpan.FromMilliseconds(420)
            });
            Assert.Contains("Exported 12 chats (30 turns, 80 commands, 4 failed) to a zip in 420 ms:", text);
            Assert.Contains("(1 turn, ", ChatLogCommand.Describe(new ChatExportResult { Path = "x", Stats = new ChatExportStats { Chats = 1, Turns = 1 } }));
            Assert.Contains(@"C:\x\chats-1.zip", text);
            Assert.Contains("2 older chats take their commands from the debug log", text);
            Assert.Contains("9 chats have no commands recorded", text);
            Assert.Contains("No server token or API key", text);
            Assert.StartsWith("ERROR: chat export failed: boom", ChatLogCommand.Describe(new ChatExportResult { Error = "boom" }));
            Assert.Equal("none", ChatLogCommand.Describe(new ChatExportResult { Error = "none", NothingToExport = true }));
            Assert.Contains("(1 turn, 4 + ~7 commands,", ChatLogCommand.Describe(new ChatExportResult
            {
                Path = "x", Stats = new ChatExportStats { Chats = 2, Turns = 1, Commands = 4, LogCommands = 7 }
            }));
        }
    }
}
