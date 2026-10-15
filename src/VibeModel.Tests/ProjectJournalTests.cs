using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VibeModel.Services.Chat;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 08 (project part): the journal line format, the X-VibeModel-Turn header, [bron: …]
    /// parsing, reading back (today / date / last n) and rotation at the size limit.
    /// </summary>
    public class ProjectJournalTests : IDisposable
    {
        private readonly string _dir;
        private static readonly DateTime When = new DateTime(2026, 10, 12, 14, 3, 12);
        private static readonly ChatTurnRef Turn = new ChatTurnRef { Id = "t3f9a2c", Question = "maak een muur van 5 m" };

        public ProjectJournalTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-journal-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // --- Line format -----------------------------------------------------------------

        [Fact]
        public void CommandLine_Format()
        {
            var line = ProjectJournal.FormatCommandLine(When, "wall", "0 0 5000 0 3000",
                new List<long> { 1543814 }, new List<long> { 1543700, 1543701 }, new List<long>(), Turn);

            Assert.Equal("- 2026-10-12 14:03:12 · wall 0 0 5000 0 3000 · created 1543814 · modified 1543700 1543701 · " +
                         "chat t3f9a2c \"maak een muur van 5 m\"", line);
        }

        [Fact]
        public void CommandLine_WithoutTurn_AndDeleted()
        {
            var line = ProjectJournal.FormatCommandLine(When, "delete", "12345 12346",
                new List<long>(), null, new List<long> { 12345, 12346 }, null);
            Assert.Equal("- 2026-10-12 14:03:12 · delete 12345 12346 · deleted 12345 12346", line);
        }

        [Fact]
        public void CommandLine_ManyIds_AreCapped()
        {
            var ids = Enumerable.Range(1, 50).Select(i => (long)i).ToList();
            var line = ProjectJournal.FormatCommandLine(When, "exec", "x", ids, null, null, null);
            Assert.Contains("created 1 2 3", line);
            Assert.Contains(" 20 +30 more", line);
            Assert.DoesNotContain(" 21 ", line);
        }

        [Fact]
        public void CommandLine_LongMultilineArgs_StayOneShortLine()
        {
            var code = "var a = 1;\r\nvar b = 2;\n" + new string('x', 500);
            var line = ProjectJournal.FormatCommandLine(When, "exec", code, new List<long> { 7 }, null, null, null);
            Assert.DoesNotContain("\n", line);
            Assert.Contains("exec var a = 1; ⏎ var b = 2; ⏎ xxx", line);
            Assert.Contains("…", line);
            Assert.True(line.Length < ProjectJournal.MaxArgsChars + 60);
        }

        [Fact]
        public void CommandLine_ExportNamesTheWrittenFiles()
        {
            var line = ProjectJournal.FormatCommandLine(When, "export", "pdf 0001", null, null, null, Turn,
                new List<string> { @"C:\proj\export\0001 - Situatie.pdf", @"C:\proj\export\0001 - Situatie.log" });
            Assert.Equal("- 2026-10-12 14:03:12 · export pdf 0001 · wrote 0001 - Situatie.pdf; 0001 - Situatie.log · " +
                         "chat t3f9a2c \"maak een muur van 5 m\"", line);
        }

        [Fact]
        public void ExportCommand_JournalFiles_ReadsThePathsFromItsResult()
        {
            var data = new Dictionary<string, object>
            {
                { "files", new List<object>
                    {
                        new Dictionary<string, object> { { "path", @"C:\proj\export\a.pdf" }, { "sizeBytes", 10L } },
                        new Dictionary<string, object> { { "path", @"C:\proj\export\b.pdf" }, { "sizeBytes", 20L } }
                    } }
            };
            var files = new VibeModel.Services.Claude.Commands.ExportCommand()
                .JournalFiles(VibeModel.Services.Claude.CommandResult.Ok("EXPORT", data));
            Assert.Equal(new[] { @"C:\proj\export\a.pdf", @"C:\proj\export\b.pdf" }, files);
            Assert.Empty(new VibeModel.Services.Claude.Commands.ExportCommand()
                .JournalFiles(VibeModel.Services.Claude.CommandResult.Ok("nothing")));
        }

        [Fact]
        public void BatchText_JoinsTheCommands()
        {
            var text = ProjectJournal.BatchText(new[]
            {
                new KeyValuePair<string, string>("wall", "0 0 5000 0"),
                new KeyValuePair<string, string>("info", "")
            });
            Assert.Equal("batch: wall 0 0 5000 0; info", text);
        }

        [Fact]
        public void TurnLine_WithSources_None_Cancelled()
        {
            Assert.Equal("- 2026-10-12 14:03:12 · chat t3f9a2c \"maak een muur van 5 m\" · sources: DEMO001- DO.pdf p.3; bestek §4.2",
                ProjectJournal.FormatTurnLine(When, Turn, new List<string> { "DEMO001- DO.pdf p.3", "bestek §4.2" }, false));
            Assert.EndsWith("· sources: none", ProjectJournal.FormatTurnLine(When, Turn, new List<string>(), false));
            Assert.EndsWith("· cancelled", ProjectJournal.FormatTurnLine(When, Turn, null, true));
        }

        [Fact]
        public void Quote_InQuestion_DoesNotBreakTheLine()
        {
            var line = ProjectJournal.FormatTurnLine(When, new ChatTurnRef { Id = "t1", Question = "wat is \"fase 2\"?" }, null, false);
            Assert.Contains("chat t1 \"wat is 'fase 2'?\"", line);
        }

        // --- Sources ---------------------------------------------------------------------

        [Fact]
        public void ParseSources_FindsBronMarkers_Distinct_InOrder()
        {
            var answer = "Fase 2 [bron: DEMO001- DO.pdf p.3]. Zie ook [Bron:bestek §4.2] en nogmaals [bron: DEMO001- DO.pdf p.3]." +
                         " English [source: memo.txt] [bronnen: leidraad 4.1]. Not a marker: [bronze] [bron:] [bron: open";
            Assert.Equal(new[] { "DEMO001- DO.pdf p.3", "bestek §4.2", "memo.txt", "leidraad 4.1" }, ProjectJournal.ParseSources(answer));
            Assert.Empty(ProjectJournal.ParseSources("geen bronnen"));
            Assert.Empty(ProjectJournal.ParseSources(null));
        }

        // --- Turn header -----------------------------------------------------------------

        [Fact]
        public void TurnHeader_RoundTrips_NonAsciiQuestion()
        {
            var turn = new ChatTurnRef { Id = "t3f9a2c", Question = "onthoud dat nieuwe doorsnedes fase 2 krijgen — één regel" };
            var header = turn.ToHeader();
            Assert.True(header.All(c => c < 128), header);   // HTTP header-safe
            var back = ChatTurnRef.FromHeader(header);
            Assert.Equal(turn.Id, back.Id);
            Assert.Equal(turn.Question, back.Question);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("bad\"id question")]
        public void TurnHeader_Malformed_IsIgnored(string value)
        {
            Assert.Null(ChatTurnRef.FromHeader(value));
        }

        [Fact]
        public void TurnHeader_LongQuestion_IsCut()
        {
            var back = ChatTurnRef.FromHeader("t1 " + Uri.EscapeDataString(new string('a', 500)));
            Assert.Equal(ProjectJournal.QuestionChars, back.Question.Length);
        }

        // --- Reading back ----------------------------------------------------------------

        private static readonly string[] Lines =
        {
            "# VibeModel journal — DEMO001",
            "",
            "- 2026-10-11 09:00:00 · wall 0 0 1 0 · created 1",
            "- 2026-10-12 14:03:12 · wall 0 0 5000 0 · created 2",
            "- 2026-10-12 14:03:25 · chat t1 \"maak een muur\" · sources: none",
        };

        [Fact]
        public void Query_Today_Date_Last()
        {
            var today = new DateTime(2026, 10, 12);
            Assert.Equal(2, ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("today", today, out _)).Count);
            Assert.Equal(2, ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("vandaag", today, out _)).Count);
            Assert.Single(ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("2026-10-11", today, out _)));
            Assert.Single(ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("gisteren", today, out _)));
            Assert.Single(ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("11-10-2026", today, out _)));
            var last2 = ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("--last 2", today, out _));
            Assert.Equal(new[] { Lines[3], Lines[4] }, last2);
            Assert.Equal(3, ProjectJournal.Filter(Lines, ProjectJournal.ParseQuery("", today, out _)).Count);   // header never counts
            Assert.Null(ProjectJournal.ParseQuery("morgen?", today, out var error));
            Assert.Contains("journal [today", error);
        }

        // --- Writing + rotation ----------------------------------------------------------

        private string Journal => Path.Combine(_dir, ProjectJournal.FileName);

        [Fact]
        public void AppendNow_WritesHeaderOnce_ThenLines()
        {
            ProjectJournal.AppendNow(_dir, "DEMO001", new[] { "- a" }, When);
            ProjectJournal.AppendNow(_dir, "DEMO001", new[] { "- b", "- c" }, When);

            var text = File.ReadAllText(Journal, Encoding.UTF8);
            Assert.Equal("# VibeModel journal — DEMO001\r\n\r\n- a\r\n- b\r\n- c\r\n", text);
        }

        [Fact]
        public void AppendNow_RotatesAtTheLimit_ToMonthFile_ThenNumbered()
        {
            ProjectJournal.AppendNow(_dir, "P", new[] { "- " + new string('x', 200) }, When, rotateBytes: 100);
            ProjectJournal.AppendNow(_dir, "P", new[] { "- second" }, When, rotateBytes: 100);    // over the limit: rotates first
            ProjectJournal.AppendNow(_dir, "P", new[] { "- " + new string('y', 200) }, When, rotateBytes: 100);   // still small: appends
            ProjectJournal.AppendNow(_dir, "P", new[] { "- fourth" }, When, rotateBytes: 100);    // over again: name taken, so -2
            ProjectJournal.AppendNow(_dir, "P", new[] { "- fifth" }, When, rotateBytes: 100);

            var month = Path.Combine(_dir, "journal-202610.md");
            var month2 = Path.Combine(_dir, "journal-202610-2.md");
            Assert.Contains(new string('x', 200), File.ReadAllText(month));
            Assert.DoesNotContain("- second", File.ReadAllText(month));
            Assert.Contains("- second", File.ReadAllText(month2));
            Assert.Contains(new string('y', 200), File.ReadAllText(month2));
            Assert.Equal("# VibeModel journal — P\r\n\r\n- fourth\r\n- fifth\r\n", File.ReadAllText(Journal));
        }

        [Fact]
        public void RotationTarget_SkipsTakenNames()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(@"C:\j", "journal-202610.md"),
                Path.Combine(@"C:\j", "journal-202610-2.md")
            };
            Assert.Equal(Path.Combine(@"C:\j", "journal-202610-3.md"), ProjectJournal.RotationTarget(@"C:\j", When, taken.Contains));
        }

        [Fact]
        public void RotatedCandidates_ForADate_AreThatMonthAndTheNext()
        {
            Directory.CreateDirectory(_dir);
            foreach (var n in new[] { "journal-202609.md", "journal-202610.md", "journal-202610-2.md", "journal-202611.md", "journal-202612.md" })
                File.WriteAllText(Path.Combine(_dir, n), "");
            var names = ProjectJournal.RotatedCandidates(_dir, "2026-10-12").Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "journal-202610-2.md", "journal-202610.md", "journal-202611.md" }, names);
        }
    }
}
