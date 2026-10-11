using System;
using System.IO;
using System.Linq;
using System.Threading;
using VibeModel.Services.Standards;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>The office family library index: scan, search ranking, persistence, unreachable roots.</summary>
    public class LibraryIndexTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vm-lib-" + Guid.NewGuid().ToString("N"));

        public LibraryIndexTests()
        {
            Touch("Sheet piles", "EX_SheetPile_AZ12.rfa");
            Touch("Sheet piles", "EX_SheetPile_AZ12.0001.rfa");   // Revit backup: ignored
            Touch("Piles", "EX_Pile concrete.rfa");
            Touch("Piles", "NLRS_Pile steel tube.rfa");
            Touch(Path.Combine("Piles", "Symbols"), "EX_Pile symbol.rfa");
            Touch("Misc", "readme.txt");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Touch(string folder, string file)
        {
            var dir = Path.Combine(_root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), "");
        }

        private LibraryIndex Build() =>
            LibraryIndex.Build(new[] { _root, Path.Combine(_root, "does-not-exist") }, new[] { "EX_", "NLRS_" }, CancellationToken.None);

        [Fact]
        public void Build_IndexesFamilies_SkipsBackupsAndOtherFiles()
        {
            var idx = Build();
            Assert.Equal(4, idx.Entries.Count);
            Assert.DoesNotContain(idx.Entries, e => e.Name.Contains(".0001"));
            Assert.Single(idx.UnreachableRoots);
            Assert.Equal("NLRS_", idx.Entries.Single(e => e.Name.StartsWith("NLRS_")).Prefix);
            Assert.Equal(Path.Combine("Piles", "Symbols"), idx.Entries.Single(e => e.Name == "EX_Pile symbol").Folder);
        }

        [Fact]
        public void Search_AllTermsMustMatch_NameHitsFirst()
        {
            var idx = Build();
            var hits = idx.Search("pile");
            Assert.Equal(4, hits.Count); // every family has "pile" in its name
            Assert.Equal("EX_Pile symbol", idx.Search("symbols").Single().Name); // folder-only hit
            Assert.Equal("EX_Pile symbol", idx.Search("pile symbol").Single().Name);
            Assert.Equal("EX_SheetPile_AZ12", idx.Search("sheet piles az12").Single().Name); // folder + name terms
            Assert.Empty(idx.Search("bridge"));
            Assert.Empty(idx.Search("   "));
        }

        [Fact]
        public void SaveAndLoad_RoundTrip()
        {
            var idx = Build();
            var file = Path.Combine(_root, "_cache", "library-index.json");
            idx.Save(file);
            var back = LibraryIndex.Load(file);
            Assert.Equal(idx.Entries.Count, back.Entries.Count);
            Assert.Equal(idx.Entries.Select(e => e.Path).OrderBy(p => p), back.Entries.Select(e => e.Path).OrderBy(p => p));
            Assert.True(Math.Abs((back.BuiltUtc - idx.BuiltUtc).TotalSeconds) < 1);
            Assert.Null(LibraryIndex.Load(file + ".missing"));
        }

        [Theory]
        [InlineData("Family.0001", true)]
        [InlineData("Family.v2", false)]
        [InlineData("Family 0001", false)]
        [InlineData("Family.12345", false)]
        public void BackupNames(string name, bool expected) => Assert.Equal(expected, LibraryIndex.IsBackupName(name));

        [Fact]
        public void PathProbe_ReportsExistence()
        {
            var file = Directory.GetFiles(_root, "*.rfa", SearchOption.AllDirectories).First();
            Assert.True(PathProbe.FileExists(file));
            Assert.False(PathProbe.FileExists(file + ".nope"));
            Assert.False(PathProbe.FileExists(""));
        }
    }
}
