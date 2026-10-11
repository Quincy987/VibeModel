using System.Collections.Generic;
using System.IO;
using System.Linq;
using VibeModel.Services.Standards;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Chapter splitting of extracted manual text (plan 11 §4.0) and the distiller's source selection.</summary>
    public class ChapterSplitterTests
    {
        private static readonly List<string> Manual = new List<string>
        {
            // p1: title page
            "Revit modelling manual\nversion 1.0",
            // p2: table of contents — must not produce chapters
            "Contents\n1 Introduction ........ 3\n1.1 Purpose ........ 3\n2 Setting up ........ 4\n2.1 Template ........ 4\n2.2 Families ........ 5\n3 Phasing ........ 6\n3.1 Phase table ........ 6",
            // p3
            "1 Introduction\nThis manual describes how we model.\n1.1 Purpose\nOne file per project part.\n2 Setting up\n2.1 Template\nStart from the office template.",
            // p4: a numbered sentence and a list item that are not headings
            "2.2 Families\nUse the library first.\n3 lagen beton worden apart gemodelleerd.\n1 regel per object.",
            // p5
            "3 Phasing\n3.1 Phase table\nNew work goes in phase 2.\n3.1.1 Temporary works\nCreated and demolished in phase 2"
        };

        [Fact]
        public void Split_FindsRealHeadings_SkipsTocAndSentences()
        {
            var ch = ChapterSplitter.Split(Manual);
            var numbers = ch.Select(c => c.Number).ToList();
            Assert.Equal(new[] { "0", "1", "1.1", "2", "2.1", "2.2", "3", "3.1", "3.1.1" }, numbers.ToArray());

            var families = ch.Single(c => c.Number == "2.2");
            Assert.Equal("Families", families.Title);
            Assert.Equal(4, families.PageFrom);
            Assert.Contains("3 lagen beton", families.Text);   // stayed body text
            Assert.Contains("1 regel per object", families.Text);

            var front = ch.First();
            Assert.Contains("Contents", front.Text);            // TOC kept as front matter text
        }

        [Fact]
        public void GetChapterText_IncludesSubchapters()
        {
            var ch = ChapterSplitter.Split(Manual);
            var text = ChapterSplitter.GetChapterText(ch, "3.1", out var label);
            Assert.Contains("New work goes in phase 2", text);
            Assert.Contains("Temporary works", text);
            Assert.StartsWith("3.1 Phase table", label);

            Assert.NotNull(ChapterSplitter.GetChapterText(ch, "families", out _)); // by title
            Assert.Null(ChapterSplitter.GetChapterText(ch, "9.9", out _));
        }

        [Theory]
        [InlineData(null, new[] { 1 }, true)]
        [InlineData(null, new[] { 1, 1 }, true)]
        [InlineData(null, new[] { 4 }, false)]
        [InlineData(new[] { 2, 3 }, new[] { 2, 3, 1 }, true)]   // first child
        [InlineData(new[] { 2, 3 }, new[] { 2, 4 }, true)]      // next sibling
        [InlineData(new[] { 2, 3, 3 }, new[] { 3 }, true)]      // next top level
        [InlineData(new[] { 2, 3 }, new[] { 2, 5 }, true)]      // one missing heading tolerated
        [InlineData(new[] { 2, 3 }, new[] { 2, 7 }, false)]
        [InlineData(new[] { 2, 3 }, new[] { 1 }, false)]        // backwards
        [InlineData(new[] { 2, 3 }, new[] { 2, 3, 2 }, false)]  // child must start at 1
        public void IsSuccessor(int[] prev, int[] next, bool expected)
        {
            Assert.Equal(expected, ChapterSplitter.IsSuccessor(prev, next));
        }

        [Fact]
        public void TextSource_SplitsOnFormFeeds()
        {
            var file = Path.Combine(Path.GetTempPath(), "vm-manual-" + System.Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(file, "1 Intro\nhello\f2 Rules\nworld");
                var pages = DocumentIndexService.ExtractPages(file, out var error);
                Assert.Null(error);
                Assert.Equal(2, pages.Count);
                var hash1 = DocumentIndexService.FileHash(file);
                Assert.Equal(hash1, DocumentIndexService.FileHash(file));
                File.AppendAllText(file, "!");
                Assert.NotEqual(hash1, DocumentIndexService.FileHash(file));
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void IndexedDocument_RoundTripsThroughJson()
        {
            var doc = new IndexedDocument { File = @"C:\x\Manual v1.pdf", Hash = "abc", PageCount = 5, Chapters = ChapterSplitter.Split(Manual) };
            var back = IndexedDocument.FromJson(PackJson.Parse(PackJson.Serializer.Serialize(doc.ToJson())));
            Assert.Equal("Manual v1", back.Name);
            Assert.Equal(doc.Chapters.Count, back.Chapters.Count);
            Assert.Equal("Families", back.Chapters.Single(c => c.Number == "2.2").Title);
            Assert.Same(back, DocumentIndexService.FindDocument(new[] { back }, "manual"));
            Assert.Null(DocumentIndexService.FindDocument(new[] { back }, "other"));
        }

        [Fact]
        public void Distiller_SelectsDeclaredChaptersAndTheirChildren()
        {
            var doc = new IndexedDocument { File = "Manual.pdf", Chapters = ChapterSplitter.Split(Manual) };
            var source = StandardsDistiller.BuildSource(new[] { doc }, new[] { "1.1", "3" }, 100000, out var used);
            Assert.Equal(new[] { "Manual 1.1", "Manual 3", "Manual 3.1", "Manual 3.1.1" }, used.ToArray());
            Assert.Contains("One file per project part", source);
            Assert.DoesNotContain("Use the library first", source);

            StandardsDistiller.BuildSource(new[] { doc }, new[] { "1.1", "3" }, 60, out var capped);
            Assert.True(capped.Count < used.Count);
        }
    }
}
