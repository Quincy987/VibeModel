using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.Services.Claude.Commands;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 08 (project part): the memory store (parse, dedupe, forget, clear, the bounded
    /// prompt block), where project data lives, and the switch that turns it all off.
    /// </summary>
    public class ProjectMemoryTests : IDisposable
    {
        private readonly string _dir;
        private static readonly DateTime Day = new DateTime(2026, 10, 12, 14, 3, 12);

        public ProjectMemoryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-memory-" + Guid.NewGuid().ToString("N"), "VibeModel");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_dir), true); } catch { }
        }

        private string FileText => File.ReadAllText(Path.Combine(_dir, ProjectMemory.FileName), Encoding.UTF8);

        private ProjectMemory.RememberResult Remember(string text, DateTime? when = null)
        {
            var r = ProjectMemory.Remember(_dir, "DEMO001", text, when ?? Day, "nl", out var error);
            Assert.Null(error);
            return r;
        }

        // --- Parse / render ----------------------------------------------------------------

        [Fact]
        public void Parse_ReadsDatedAndUndatedBullets_IgnoresProse()
        {
            var text = "# Projectgeheugen — DEMO001\r\n\r\nFeiten die VibeModel onthoudt.\r\n\r\n" +
                       "- 2026-10-01 — deksteen = NLCS Prefab Beton hoek\r\n" +
                       "* handgeschreven feit zonder datum\n" +
                       "- 2026-10-12 - doorsnedes krijgen fase 2. Bouwfase\n" +
                       "-\n" +
                       "gewone tekst\n";
            var entries = ProjectMemory.Parse(text);

            Assert.Equal(3, entries.Count);
            Assert.Equal("2026-10-01", entries[0].Date);
            Assert.Equal("deksteen = NLCS Prefab Beton hoek", entries[0].Text);
            Assert.Null(entries[1].Date);
            Assert.Equal("handgeschreven feit zonder datum", entries[1].Text);
            Assert.Equal("doorsnedes krijgen fase 2. Bouwfase", entries[2].Text);
        }

        [Fact]
        public void Render_ThenParse_RoundTrips()
        {
            var entries = new List<MemoryEntry>
            {
                new MemoryEntry { Date = "2026-10-12", Text = "Een collega wil de 3D naast de noordelijke op blad 0031" },
                new MemoryEntry { Date = null, Text = "zonder datum" }
            };
            var text = ProjectMemory.Render(entries, "DEMO001", "nl");

            Assert.StartsWith("# Projectgeheugen — DEMO001", text);
            Assert.Contains("- 2026-10-12 — Een collega wil de 3D naast de noordelijke op blad 0031\r\n", text);
            var back = ProjectMemory.Parse(text);
            Assert.Equal(entries.Select(e => e.Display), back.Select(e => e.Display));
            Assert.StartsWith("# Project memory", ProjectMemory.Render(entries, "X", "en"));
        }

        // --- Remember / dedupe ---------------------------------------------------------------

        [Fact]
        public void Remember_CreatesFileWithHeader_AndAppendsDatedLines()
        {
            var first = Remember("doorsnedes krijgen fase 2. Bouwfase");
            var second = Remember("deksteen = NLCS Prefab Beton hoek", Day.AddDays(1));

            Assert.False(first.Duplicate);
            Assert.Equal(0, first.Index);
            Assert.Equal(1, second.Index);
            var text = FileText;
            Assert.StartsWith("# Projectgeheugen — DEMO001", text);
            Assert.Contains("- 2026-10-12 — doorsnedes krijgen fase 2. Bouwfase\r\n", text);
            Assert.Contains("- 2026-10-13 — deksteen = NLCS Prefab Beton hoek\r\n", text);
        }

        [Theory]
        [InlineData("Doorsnedes krijgen fase 2. Bouwfase")]
        [InlineData("  doorsnedes   krijgen fase 2. bouwfase.  ")]
        [InlineData("\"doorsnedes krijgen fase 2. Bouwfase\"")]
        [InlineData("doorsnedes krijgen\nfase 2. Bouwfase!")]
        public void Remember_SameFactNormalized_IsNotSavedTwice(string variant)
        {
            Remember("doorsnedes krijgen fase 2. Bouwfase");
            var before = FileText;

            var again = Remember(variant, Day.AddDays(3));

            Assert.True(again.Duplicate);
            Assert.Equal(0, again.Index);
            Assert.Equal(before, FileText);
        }

        [Fact]
        public void Remember_DifferentFact_IsNotADuplicate()
        {
            Remember("doorsnedes krijgen fase 2. Bouwfase");
            Assert.False(Remember("doorsnedes krijgen fase 3. Sloop").Duplicate);
        }

        [Fact]
        public void Remember_EmptyText_IsRefused()
        {
            Assert.Null(ProjectMemory.Remember(_dir, "P", "  \"\" ", Day, "nl", out var error));
            Assert.NotNull(error);
        }

        [Fact]
        public void Remember_HandEditedFileWithoutFinalNewline_StaysOneFactPerLine()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, ProjectMemory.FileName), "# mine\n\n- eigen feit", new UTF8Encoding(false));

            Remember("nieuw feit");

            var entries = ProjectMemory.Parse(FileText);
            Assert.Equal(new[] { "eigen feit", "nieuw feit" }, entries.Select(e => e.Text));
            Assert.StartsWith("# mine", FileText);
        }

        [Fact]
        public void CleanFact_OneLineAndCapped()
        {
            Assert.Equal("a b", ProjectMemory.CleanFact("  a\r\n  b "));
            var longFact = ProjectMemory.CleanFact(new string('x', 1000));
            Assert.Equal(ProjectMemory.MaxFactChars, longFact.Length);
            Assert.EndsWith("…", longFact);
        }

        // --- Forget / clear ------------------------------------------------------------------

        private void Seed()
        {
            Remember("deksteen = NLCS Prefab Beton hoek");
            Remember("doorsnedes krijgen fase 2. Bouwfase");
            Remember("Een collega wil de 3D naast de noordelijke op blad 0031");
        }

        [Fact]
        public void Forget_ByNumber_RemovesThatFact()
        {
            Seed();
            var removed = ProjectMemory.Forget(_dir, "DEMO001", "2", "nl", out var error, out var failed);

            Assert.Null(error);
            Assert.False(failed);
            Assert.Equal("doorsnedes krijgen fase 2. Bouwfase", Assert.Single(removed).Text);
            Assert.Equal(new[] { "deksteen = NLCS Prefab Beton hoek", "Een collega wil de 3D naast de noordelijke op blad 0031" },
                ProjectMemory.Parse(FileText).Select(e => e.Text));
        }

        [Theory]
        [InlineData("Doorsnedes krijgen fase 2. bouwfase.")]   // the text, normalized
        [InlineData("noordelijke")]                              // a unique fragment
        [InlineData("#1")]
        public void Forget_ByTextOrFragment(string arg)
        {
            Seed();
            var removed = ProjectMemory.Forget(_dir, "DEMO001", arg, "nl", out var error, out _);
            Assert.Null(error);
            Assert.Single(removed);
            Assert.Equal(2, ProjectMemory.Parse(FileText).Count);
        }

        [Theory]
        [InlineData("fase")]   // in two facts (#2 and #4)
        [InlineData("e")]      // matches every fact
        [InlineData("9")]      // out of range
        [InlineData("nergens")]
        [InlineData("")]
        public void Forget_AmbiguousOrUnknown_RemovesNothing(string arg)
        {
            Seed();
            Remember("fase 4 voor tijdelijke werken");
            var before = FileText;

            var removed = ProjectMemory.Forget(_dir, "DEMO001", arg, "nl", out var error, out var failed);

            Assert.Null(removed);
            Assert.False(failed);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Equal(before, FileText);
        }

        [Fact]
        public void Forget_Ambiguous_ListsTheNumbers()
        {
            Seed();
            Remember("fase 4 voor tijdelijke werken");
            ProjectMemory.Forget(_dir, "DEMO001", "fase", "nl", out var error, out _);
            Assert.Contains("#2", error);
            Assert.Contains("#4", error);
        }

        [Fact]
        public void Clear_KeepsHeader_DropsFacts()
        {
            Seed();
            var dropped = ProjectMemory.Clear(_dir, "DEMO001", "nl", out var error);
            Assert.Null(error);
            Assert.Equal(3, dropped.Count);
            Assert.Empty(ProjectMemory.Parse(FileText));
            Assert.StartsWith("# Projectgeheugen", FileText);
        }

        [Fact]
        public void Load_NoFileYet_IsEmptyNotAnError()
        {
            var entries = ProjectMemory.Load(_dir, out var error);
            Assert.Null(error);
            Assert.Empty(entries);
        }

        // --- Prompt block --------------------------------------------------------------------

        private static ProjectDataLocation Loc => new ProjectDataLocation
        {
            Directory = @"C:\P\VibeModel",
            Folder = new ProjectFolder { Path = @"C:\P", Source = ProjectFolderSource.ModelFolder }
        };

        [Fact]
        public void PromptBlock_ListsFactsNumbered_NewestLast_WithTheRule()
        {
            var entries = new List<MemoryEntry>
            {
                new MemoryEntry { Date = "2026-10-01", Text = "deksteen = NLCS Prefab Beton hoek" },
                new MemoryEntry { Date = "2026-10-12", Text = "doorsnedes krijgen fase 2. Bouwfase" }
            };
            var block = ProjectMemory.BuildPromptBlock(entries, Loc, null);

            Assert.StartsWith("PROJECT MEMORY (", block);
            Assert.Contains(@"C:\P\VibeModel\memory.md (map van het model)", block);
            Assert.True(block.IndexOf("1. 2026-10-01 — deksteen", StringComparison.Ordinal) <
                        block.IndexOf("2. 2026-10-12 — doorsnedes", StringComparison.Ordinal));
            Assert.Contains("save it with `remember` (one line, their words)", block);
            Assert.Contains("read PROJECT MEMORY before answering 'last time' questions", block);
            Assert.DoesNotContain("older", block);
        }

        [Fact]
        public void PromptBlock_Empty_SaysSoAndStillCarriesTheRule()
        {
            var block = ProjectMemory.BuildPromptBlock(new List<MemoryEntry>(), Loc, null);
            Assert.Contains("(nothing saved yet)", block);
            Assert.Contains(ProjectMemory.PromptRule, block);
        }

        [Fact]
        public void PromptBlock_Unreadable_NamesTheReason()
        {
            var block = ProjectMemory.BuildPromptBlock(null, Loc, "the folder did not answer within 3 s");
            Assert.Contains("(could not be read: the folder did not answer within 3 s)", block);
        }

        [Fact]
        public void PromptBlock_IsBoundedTo2kB_KeepsNewest_CountsOlder()
        {
            var entries = Enumerable.Range(1, 200)
                .Select(i => new MemoryEntry { Date = "2026-10-12", Text = "feit nummer " + i + " met wat extra tekst erbij é" })
                .ToList();
            var block = ProjectMemory.BuildPromptBlock(entries, Loc, null);

            Assert.True(Encoding.UTF8.GetByteCount(block) <= ProjectMemory.MaxPromptBytes,
                "block is " + Encoding.UTF8.GetByteCount(block) + " bytes");
            Assert.Contains("200. 2026-10-12 — feit nummer 200 ", block);   // newest kept
            Assert.DoesNotContain("1. 2026-10-12 — feit nummer 1 ", block);  // oldest cut
            var older = System.Text.RegularExpressions.Regex.Match(block, @"\(\+(\d+) older — run memory to see all\)");
            Assert.True(older.Success);
            int shown = System.Text.RegularExpressions.Regex.Matches(block, @"^\d+\. ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
            Assert.Equal(200, shown + int.Parse(older.Groups[1].Value));
            Assert.True(block.IndexOf("older", StringComparison.Ordinal) < block.IndexOf("200. ", StringComparison.Ordinal));
        }

        // --- Location + switch ---------------------------------------------------------------

        [Fact]
        public void Locate_ProjectFolder_UsesItsVibeModelSubfolder()
        {
            var loc = ProjectData.Locate(new ProjectFolder { Path = @"K:\Proj\DEMO", Source = ProjectFolderSource.Configured }, "DEMO001");
            Assert.Equal(@"K:\Proj\DEMO\VibeModel", loc.Directory);
            Assert.False(loc.IsFallback);
            Assert.Equal("(ingesteld)", loc.SourceLabel);
        }

        [Fact]
        public void Locate_NoFolder_FallsBackToLocalPerTitle()
        {
            var loc = ProjectData.Locate(ProjectFolder.None, "Project1: unsaved", @"C:\Local\projects");
            Assert.True(loc.IsFallback);
            Assert.Equal(@"C:\Local\projects\Project1_ unsaved", loc.Directory);
            Assert.Null(ProjectData.Locate(ProjectFolder.None, "  "));
        }

        [Fact]
        public void DocsListing_SkipsVibeModelsOwnFolder()
        {
            Assert.True(ProjectDocs.IsSkippedFolder("VibeModel"));
            Assert.False(ProjectDocs.IsSkippedFolder("survey"));
        }

        [Fact]
        public void SwitchOff_NoMemoryBlock_NoMemoryCommandsOffered()
        {
            try
            {
                ProjectData.EnabledOverride = false;
                Assert.Equal("", ProjectMemory.BuildPromptBlockForActiveDocument());
                Assert.False(ProjectMemoryCommands.IsOffered(new RememberCommand()));
                Assert.False(ProjectMemoryCommands.IsOffered(new JournalCommand()));
                Assert.True(ProjectMemoryCommands.IsOffered(new DocsCommand()));
                Assert.Null(ProjectJournal.BeginTurn("vraag"));

                ProjectData.EnabledOverride = true;
                Assert.True(ProjectMemoryCommands.IsOffered(new MemoryCommand()));
                Assert.True(ProjectMemoryCommands.IsOffered(new ForgetCommand()));
            }
            finally
            {
                ProjectData.EnabledOverride = null;
            }
        }

        [Fact]
        public void MemoryCommands_AreNotModifications()
        {
            // No transaction, no undo step, and a batch of only these stays read-only.
            foreach (IClaudeCommand c in new IClaudeCommand[] { new RememberCommand(), new MemoryCommand(), new ForgetCommand(), new JournalCommand() })
                Assert.False(c is IModificationCommand, c.Name);
        }
    }
}
