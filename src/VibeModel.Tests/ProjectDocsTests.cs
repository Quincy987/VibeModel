using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 10 project folder: resolution (model-folder default vs configured override), the
    /// root boundary check (the real risk), name lookup, the shared manifest and the caps.
    /// </summary>
    public class ProjectDocsTests : IDisposable
    {
        private readonly string _base;
        private readonly string _root;

        public ProjectDocsTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "vm-projectdocs-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_base, "docs");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, true); } catch { }
        }

        private string Touch(string relative, string content = "x")
        {
            var full = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return full;
        }

        // --- Resolve ---------------------------------------------------------------------

        [Fact]
        public void Resolve_NoOverride_UsesTheModelsFolder()
        {
            var f = ProjectDocs.Resolve(null, @"C:\Projects\DEMO001\model.rvt");
            Assert.Equal(ProjectFolderSource.ModelFolder, f.Source);
            Assert.Equal(@"C:\Projects\DEMO001", f.Path);
            Assert.Equal("(map van het model)", f.SourceLabel);
        }

        [Fact]
        public void Resolve_Override_WinsOverTheModelFolder()
        {
            var f = ProjectDocs.Resolve(@" D:\Docs\DEMO ", @"C:\Projects\DEMO001\model.rvt");
            Assert.Equal(ProjectFolderSource.Configured, f.Source);
            Assert.Equal(@"D:\Docs\DEMO", f.Path);
            Assert.Equal("(ingesteld)", f.SourceLabel);
        }

        [Fact]
        public void Resolve_UncModel_UsesItsShareFolder()
        {
            var f = ProjectDocs.Resolve("", @"\\server\projects\DEMO\central.rvt");
            Assert.Equal(ProjectFolderSource.ModelFolder, f.Source);
            Assert.Equal(@"\\server\projects\DEMO", f.Path);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("BIM 360://Project/Model.rvt")]
        [InlineData("Autodesk Docs://Project/Model.rvt")]
        [InlineData("model.rvt")]
        public void Resolve_NoLocalPath_HasNoDefault(string pathName)
        {
            var f = ProjectDocs.Resolve(null, pathName);
            Assert.False(f.IsSet);
            Assert.Equal(ProjectFolderSource.None, f.Source);
        }

        [Fact]
        public void StaleConfiguredFolder_DegradesToNotConfigured()
        {
            var gone = Path.Combine(_base, "moved-away");
            Directory.CreateDirectory(gone);
            Assert.Equal(gone, SettingsManager.LiveFolderOrNull(gone));
            Directory.Delete(gone);
            Assert.Null(SettingsManager.LiveFolderOrNull(gone));
            // ...so resolution falls back to the model folder rather than erroring.
            var f = ProjectDocs.Resolve(SettingsManager.LiveFolderOrNull(gone), @"C:\P\m.rvt");
            Assert.Equal(ProjectFolderSource.ModelFolder, f.Source);
        }

        [Fact]
        public void ProjectKey_IsTheAttachmentStoreKey()
        {
            Assert.Equal(AttachmentStore.SanitizeName("DEMO 001: rakdeel B"), ProjectDocs.ProjectKey("DEMO 001: rakdeel B"));
        }

        // --- Boundary --------------------------------------------------------------------

        [Theory]
        [InlineData(@"..\secret.txt")]
        [InlineData(@"sub\..\..\secret.txt")]
        [InlineData(@"../secret.txt")]
        [InlineData(@"..")]
        [InlineData(@"...\x")]
        public void Boundary_RejectsTraversal(string name)
        {
            Assert.False(ProjectDocs.TryResolveInside(_root, name, out var full, out var err));
            Assert.Null(full);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Theory]
        [InlineData(@"C:\Windows\win.ini")]
        [InlineData(@"\Windows\win.ini")]
        [InlineData(@"\\server\share\x.txt")]
        [InlineData(@"C:memo.txt")]
        [InlineData(@"memo.txt:hidden")]
        public void Boundary_RejectsAbsoluteAndDriveRelative(string name)
        {
            Assert.False(ProjectDocs.TryResolveInside(_root, name, out _, out _));
        }

        [Fact]
        public void Boundary_RejectsAnAbsolutePathIntoTheRootItself()
        {
            // Even pointing at a real file inside the root: names are relative, full stop.
            var inside = Touch("memo.txt");
            Assert.False(ProjectDocs.TryResolveInside(_root, inside, out _, out _));
        }

        [Fact]
        public void Boundary_PrefixSiblingIsOutside()
        {
            // "docs-x" shares the "docs" prefix but is not inside it.
            var sibling = Path.Combine(_base, "docs-x", "a.txt");
            Assert.False(ProjectDocs.IsUnderRoot(_root, sibling));
            Assert.False(ProjectDocs.TryResolveInside(_root, @"..\docs-x\a.txt", out _, out _));
            Assert.True(ProjectDocs.IsUnderRoot(_root, Path.Combine(_root, "a.txt")));
        }

        [Fact]
        public void Boundary_RootItselfIsNotInside()
        {
            Assert.False(ProjectDocs.TryResolveInside(_root, ".", out _, out _));
            Assert.False(ProjectDocs.IsUnderRoot(_root, _root));
        }

        [Fact]
        public void Boundary_CaseTricksStayInsideOrOutsideCorrectly()
        {
            Assert.True(ProjectDocs.TryResolveInside(_root.ToUpperInvariant(), @"MEMO.TXT", out var full, out _));
            Assert.True(ProjectDocs.IsUnderRoot(_root.ToLowerInvariant(), full));
            Assert.False(ProjectDocs.TryResolveInside(_root, @"SUB\..\..\DOCS-X\a.txt", out _, out _));
        }

        [Fact]
        public void Boundary_AcceptsNestedNamesAndForwardSlashes()
        {
            Assert.True(ProjectDocs.TryResolveInside(_root, "survey/levels.txt", out var full, out _));
            Assert.Equal(Path.Combine(_root, "survey", "levels.txt"), full);
            Assert.True(ProjectDocs.TryResolveInside(_root + "\\", "\"memo.txt\"", out full, out _));
            Assert.Equal(Path.Combine(_root, "memo.txt"), full);
        }

        [Fact]
        public void AttachmentStore_StillUsesTheSharedBoundary()
        {
            // Source already inside the store is returned unchanged; a sibling is copied.
            var store = Path.Combine(_base, "attachments");
            var inside = Path.Combine(store, "p", "a.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(inside));
            File.WriteAllText(inside, "a");
            Assert.Equal(inside, AttachmentStore.StoreFile(inside, "p", store));

            var sibling = Path.Combine(_base, "attachments-x", "b.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sibling));
            File.WriteAllText(sibling, "b");
            Assert.NotEqual(sibling, AttachmentStore.StoreFile(sibling, "p", store));
        }

        // --- Listing ---------------------------------------------------------------------

        [Fact]
        public void List_OneLevelDeep_SkipsRevitFilesAndFolders()
        {
            Touch("memo.txt");
            Touch("DEMO001- DO.pdf");
            Touch("model.rvt");
            Touch("model.0001.rvt");
            Touch("lib.rfa");
            Touch("~$memo.docx");
            Touch(@"survey\levels.csv");
            Touch(@"survey\deep\too-deep.txt");
            Touch(@"model_backup\model.0002.rvt");
            Touch(@"scan Support\points.rcs");

            var listing = ProjectDocs.List(_root);
            var names = listing.Entries.Select(e => e.RelativePath).ToList();
            Assert.Equal(new[] { "memo.txt", "DEMO001- DO.pdf", @"survey\levels.csv" }, names);
            Assert.Equal(new[] { "survey" }, listing.Subfolders);
            Assert.Equal("pdf", listing.Entries[1].Tag);
            Assert.Equal("text", listing.Entries[0].Tag);
        }

        [Fact]
        public void List_Subfolder_IsRelativeToTheRoot()
        {
            Touch(@"survey\levels.csv");
            Touch(@"survey\deep\a.txt");
            var listing = ProjectDocs.List(_root, Path.Combine(_root, "survey"));
            Assert.Equal(new[] { @"survey\levels.csv", @"survey\deep\a.txt" },
                listing.Entries.Select(e => e.RelativePath).ToArray());
        }

        [Fact]
        public void TryList_MissingFolder_SaysSo()
        {
            Assert.Null(ProjectDocs.TryList(Path.Combine(_base, "nope"), null, out var err));
            Assert.Contains("does not exist", err);
        }

        [Fact]
        public void Tag_OtherKindsShowTheirExtension()
        {
            Assert.Equal("docx", new ProjectDocEntry { RelativePath = "Memo.DOCX", Kind = AttachmentKind.Other }.Tag);
            Assert.Equal("file", new ProjectDocEntry { RelativePath = "README", Kind = AttachmentKind.Other }.Tag);
            Assert.Equal("image", new ProjectDocEntry { RelativePath = "a.png", Kind = AttachmentKind.Image }.Tag);
        }

        // --- Manifest --------------------------------------------------------------------

        [Fact]
        public void Manifest_NamesTheFolderAndItsSource()
        {
            Touch("memo.txt", "hello");
            var folder = ProjectDocs.Resolve(null, Path.Combine(_root, "m.rvt"));
            var text = ProjectDocs.BuildManifest(folder, ProjectDocs.List(_root), ProjectDocs.MaxManifestEntries);
            var first = text.Split('\n')[0].TrimEnd('\r');
            Assert.Equal("Project folder: " + _root + "  (map van het model)", first);
            Assert.Contains("memo.txt", text);
            Assert.Contains("[text]", text);
            Assert.Contains("5 B", text);
        }

        [Fact]
        public void Manifest_CapsAt200WithACount()
        {
            for (int i = 0; i < 230; i++) Touch("f" + i.ToString("000") + ".txt");
            var folder = new ProjectFolder { Path = _root, Source = ProjectFolderSource.Configured };
            var text = ProjectDocs.BuildManifest(folder, ProjectDocs.List(_root), ProjectDocs.MaxManifestEntries);
            Assert.Contains("(230 files, 0 subfolders)", text);
            Assert.Contains("f199.txt", text);
            Assert.DoesNotContain("f200.txt", text);
            Assert.Contains("and 30 more", text);
            Assert.Contains("(ingesteld)", text);
        }

        [Fact]
        public void PromptBlock_SharesTheManifestAndCarriesTheInstruction()
        {
            Touch("memo.txt");
            var folder = new ProjectFolder { Path = _root, Source = ProjectFolderSource.ModelFolder };
            var listing = ProjectDocs.List(_root);
            var block = ProjectDocs.BuildPromptBlock(folder, listing, null, false);
            Assert.Contains(ProjectDocs.BuildManifest(folder, listing, ProjectDocs.MaxPromptEntries), block);
            Assert.Contains("readdoc <name> when one is relevant", block);
            Assert.Contains("don't guess at their contents", block);
            Assert.Contains("default: the folder the open model is in", block);
            Assert.DoesNotContain("Read the file directly", block);
            Assert.Contains("Read the file directly", ProjectDocs.BuildPromptBlock(folder, listing, null, true));
        }

        [Fact]
        public void PromptBlock_EmptyWithoutFolder_AndNamesAnUnlistableOne()
        {
            Assert.Equal("", ProjectDocs.BuildPromptBlock(ProjectFolder.None, null, null, false));
            var folder = new ProjectFolder { Path = @"K:\offline", Source = ProjectFolderSource.Configured };
            var block = ProjectDocs.BuildPromptBlock(folder, null, "the folder did not answer", false);
            Assert.Contains(@"K:\offline  (ingesteld)", block);
            Assert.Contains("could not be listed: the folder did not answer", block);
        }

        // --- Name resolution -------------------------------------------------------------

        [Fact]
        public void Find_CaseInsensitive_ExactThenPrefix_AmbiguousListsMatches()
        {
            Touch("Bestek rev C.pdf");
            Touch("bestek bijlage.txt");
            Touch("memo.txt");
            Touch(@"survey\memo.txt");
            var entries = ProjectDocs.List(_root).Entries;

            Assert.Equal("Bestek rev C.pdf", ProjectDocs.Find(entries, "BESTEK REV C.PDF", out _).RelativePath);
            Assert.Equal("memo.txt", ProjectDocs.Find(entries, "MEMO.txt", out _).RelativePath); // relative path beats file name
            Assert.Equal(@"survey\memo.txt", ProjectDocs.Find(entries, "survey/MEMO.TXT", out _).RelativePath);
            Assert.Equal("Bestek rev C.pdf", ProjectDocs.Find(entries, "bestek r", out _).RelativePath);

            Assert.Null(ProjectDocs.Find(entries, "bestek", out var matches));
            Assert.Equal(2, matches.Count);

            Assert.Null(ProjectDocs.Find(entries, "nothing-like-it", out matches));
            Assert.Empty(matches);
        }

        // --- Content ---------------------------------------------------------------------

        [Fact]
        public void CapText_GiantCsvTruncatesWithTheAttachmentNotice()
        {
            var big = new string('a', AttachmentFormatting.MaxInlineTextChars + 10);
            var capped = ProjectDocs.CapText(big, @"C:\p\giant.csv", out var truncated);
            Assert.True(truncated);
            Assert.StartsWith(new string('a', 100), capped);
            Assert.EndsWith(AttachmentFormatting.TruncationNotice(@"C:\p\giant.csv"), capped);
            Assert.Equal("small", ProjectDocs.CapText("small", "x", out truncated));
            Assert.False(truncated);
        }

        [Fact]
        public void ReadDocxText_ReturnsParagraphText()
        {
            var path = Path.Combine(_root, "memo.docx");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var part = zip.CreateEntry("word/document.xml");
                using (var w = new StreamWriter(part.Open(), Encoding.UTF8))
                    w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                            "<w:p><w:r><w:t>Veiligheids</w:t></w:r><w:r><w:t>constructie</w:t></w:r></w:p>" +
                            "<w:p><w:r><w:t>rak</w:t><w:tab/><w:t>B</w:t></w:r></w:p></w:body></w:document>");
            }
            Assert.Equal("Veiligheidsconstructie\nrak\tB\n", ProjectDocs.ReadDocxText(path));
        }

        [Fact]
        public void CrossesReparsePoint_FalseForPlainFiles()
        {
            var f = Touch(@"sub\a.txt");
            Assert.False(ProjectDocs.CrossesReparsePoint(_root, f));
        }
    }
}
