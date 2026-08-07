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
    /// The chat-attachment contract: classification by extension, per-project disk
    /// persistence (sanitize/dedupe), backend prompt/content building for all three
    /// backends, size guards, and the history payload-trimming that keeps old
    /// attachments from blowing the API request limit.
    /// </summary>
    public class ChatAttachmentClassificationTests
    {
        [Theory]
        [InlineData("png", AttachmentKind.Image)]
        [InlineData(".PNG", AttachmentKind.Image)]
        [InlineData("jpg", AttachmentKind.Image)]
        [InlineData("jpeg", AttachmentKind.Image)]
        [InlineData("gif", AttachmentKind.Image)]
        [InlineData("webp", AttachmentKind.Image)]
        [InlineData("pdf", AttachmentKind.Pdf)]
        [InlineData("txt", AttachmentKind.Text)]
        [InlineData("md", AttachmentKind.Text)]
        [InlineData("csv", AttachmentKind.Text)]
        [InlineData("json", AttachmentKind.Text)]
        [InlineData("xml", AttachmentKind.Text)]
        [InlineData("log", AttachmentKind.Text)]
        [InlineData("docx", AttachmentKind.Other)]
        [InlineData("rvt", AttachmentKind.Other)]
        [InlineData("", AttachmentKind.Other)]
        public void ClassifyExtension_MapsKnownKinds(string ext, AttachmentKind expected)
        {
            Assert.Equal(expected, ChatAttachment.ClassifyExtension(ext));
        }

        [Theory]
        [InlineData("pdf", "application/pdf")]
        [InlineData(".jpg", "image/jpeg")]
        [InlineData("png", "image/png")]
        [InlineData("docx", "application/octet-stream")]
        public void MimeTypeFor_MapsKnownTypes(string ext, string expected)
        {
            Assert.Equal(expected, ChatAttachment.MimeTypeFor(ext));
        }

        [Fact]
        public void FromFile_PopulatesNameKindAndSize()
        {
            var path = Path.Combine(Path.GetTempPath(), "vm-att-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(path, new byte[1234]);
            try
            {
                var att = ChatAttachment.FromFile(path);
                Assert.Equal(Path.GetFileName(path), att.FileName);
                Assert.Equal(AttachmentKind.Pdf, att.Kind);
                Assert.Equal("application/pdf", att.MimeType);
                Assert.Equal(1234, att.SizeBytes);
                Assert.Null(att.StoredPath);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(512, "512 B")]
        [InlineData(2048, "2 KB")]
        [InlineData(2516582, "2.4 MB")]
        public void FormatSize_HumanReadable(long bytes, string expected)
        {
            Assert.Equal(expected, ChatAttachment.FormatSize(bytes));
        }
    }

    public class AttachmentStoreTests : IDisposable
    {
        private readonly string _root;

        public AttachmentStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vm-store-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Theory]
        [InlineData("My Project", "My Project")]
        [InlineData("Spec: Rev/A", "Spec_ Rev_A")]
        [InlineData("", "default")]
        [InlineData("   ", "default")]
        [InlineData(null, "default")]
        [InlineData("NUL", "default")]
        [InlineData("con.rvt", "default")]
        [InlineData("COM1", "default")]
        public void SanitizeName_ProducesSafeFolderNames(string input, string expected)
        {
            Assert.Equal(expected, AttachmentStore.SanitizeName(input));
        }

        [Fact]
        public void SanitizeName_CapsLength()
        {
            Assert.Equal(100, AttachmentStore.SanitizeName(new string('a', 300)).Length);
        }

        [Fact]
        public void StoreFile_CopiesIntoProjectFolder()
        {
            var source = Path.Combine(Path.GetTempPath(), "vm-src-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(source, "spec content");
            try
            {
                var stored = AttachmentStore.StoreFile(source, "Prinsengracht", _root);

                Assert.True(File.Exists(stored));
                Assert.Equal("spec content", File.ReadAllText(stored));
                Assert.StartsWith(Path.Combine(_root, "Prinsengracht"), stored);
                Assert.True(File.Exists(source)); // copy, not move
            }
            finally { File.Delete(source); }
        }

        [Fact]
        public void StoreFile_DedupesCollidingNames()
        {
            var source = Path.Combine(Path.GetTempPath(), "vm-dup-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(source, "v1");
            try
            {
                var first = AttachmentStore.StoreFile(source, "proj", _root);
                File.WriteAllText(source, "v2");
                var second = AttachmentStore.StoreFile(source, "proj", _root);
                var third = AttachmentStore.StoreFile(source, "proj", _root);

                Assert.NotEqual(first, second);
                Assert.NotEqual(second, third);
                Assert.Contains(" (2)", second);
                Assert.Contains(" (3)", third);
                Assert.Equal("v1", File.ReadAllText(first));
                Assert.Equal("v2", File.ReadAllText(second));
            }
            finally { File.Delete(source); }
        }

        [Fact]
        public void StoreFile_AlreadyInStore_ReturnsUnchangedWithoutCopy()
        {
            var folder = Path.Combine(_root, "proj");
            Directory.CreateDirectory(folder);
            var inStore = Path.Combine(folder, "pasted-20260721-120000.png");
            File.WriteAllBytes(inStore, new byte[] { 1, 2, 3 });

            var result = AttachmentStore.StoreFile(inStore, "proj", _root);

            Assert.Equal(inStore, result);
            Assert.Single(Directory.GetFiles(folder));
        }

        [Fact]
        public void StoreFile_SiblingFolderWithRootPrefix_IsStillCopied()
        {
            // A folder named "<root>-other" must not false-match the "already in
            // store" check (separator-boundary comparison).
            var sibling = _root + "-other";
            Directory.CreateDirectory(sibling);
            var source = Path.Combine(sibling, "spec.txt");
            File.WriteAllText(source, "outside store");
            try
            {
                var stored = AttachmentStore.StoreFile(source, "proj", _root);

                Assert.NotEqual(source, stored);
                Assert.StartsWith(Path.Combine(_root, "proj"), stored);
                Assert.True(File.Exists(stored));
            }
            finally { Directory.Delete(sibling, true); }
        }

        [Fact]
        public void DedupePath_ReturnsOriginalWhenFree()
        {
            Directory.CreateDirectory(_root);
            Assert.Equal(Path.Combine(_root, "a.pdf"), AttachmentStore.DedupePath(_root, "a.pdf"));
        }
    }

    public class ClaudeCodePromptTests
    {
        [Fact]
        public void NullOrEmptyAttachments_LeavePromptUnchanged()
        {
            Assert.Equal("hello", ClaudeCodeBackend.BuildPromptWithAttachments("hello", null));
            Assert.Equal("hello", ClaudeCodeBackend.BuildPromptWithAttachments("hello", new List<ChatAttachment>()));
        }

        [Fact]
        public void Attachments_PrependReadInstructionAndPaths()
        {
            var atts = new List<ChatAttachment>
            {
                new ChatAttachment { FileName = "spec.pdf", StoredPath = @"C:\store\proj\spec.pdf" },
                new ChatAttachment { FileName = "plan.png", StoredPath = @"C:\store\proj\plan.png" }
            };
            var prompt = ClaudeCodeBackend.BuildPromptWithAttachments("check the spec", atts);

            Assert.Contains("read them with the Read tool", prompt);
            Assert.Contains(@"C:\store\proj\spec.pdf", prompt);
            Assert.Contains(@"C:\store\proj\plan.png", prompt);
            Assert.EndsWith("check the spec", prompt);
        }
    }

    public class LocalLlmAttachmentTests : IDisposable
    {
        private readonly string _dir;

        public LocalLlmAttachmentTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-local-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void TextAttachment_InlinesFencedContent()
        {
            var path = Path.Combine(_dir, "notes.md");
            File.WriteAllText(path, "wall height 3000mm");
            var atts = new List<ChatAttachment>
            {
                new ChatAttachment { FileName = "notes.md", StoredPath = path, Kind = AttachmentKind.Text }
            };

            var result = LocalLlmBackend.AppendAttachments("prompt", atts);

            Assert.StartsWith("prompt", result);
            Assert.Contains("--- Attached file: notes.md ---", result);
            Assert.Contains("wall height 3000mm", result);
        }

        [Fact]
        public void NonTextAttachment_GetsUnreadableNoteWithPath()
        {
            var atts = new List<ChatAttachment>
            {
                new ChatAttachment { FileName = "spec.pdf", StoredPath = @"C:\s\spec.pdf", Kind = AttachmentKind.Pdf }
            };

            var result = LocalLlmBackend.AppendAttachments("prompt", atts);

            Assert.Contains("cannot read this format", result);
            Assert.Contains(@"C:\s\spec.pdf", result);
        }

        [Fact]
        public void NoAttachments_ReturnsContentUnchanged()
        {
            Assert.Equal("prompt", LocalLlmBackend.AppendAttachments("prompt", null));
        }
    }

    public class AnthropicAttachmentTests : IDisposable
    {
        private readonly string _dir;

        public AnthropicAttachmentTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-anthropic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private ChatAttachment MakeFile(string name, AttachmentKind kind, string mime, byte[] bytes)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, bytes);
            return new ChatAttachment
            {
                FileName = name,
                StoredPath = path,
                Kind = kind,
                MimeType = mime,
                SizeBytes = bytes.Length
            };
        }

        // --- Size guards ---

        [Fact]
        public void ValidateSizes_NullAndSmallFiles_PassThrough()
        {
            Assert.Null(AnthropicDirectBackend.ValidateAttachmentSizes(null));
            var ok = new List<ChatAttachment>
            {
                new ChatAttachment { FileName = "a.pdf", Kind = AttachmentKind.Pdf, SizeBytes = 5 * 1024 * 1024 },
                new ChatAttachment { FileName = "b.png", Kind = AttachmentKind.Image, SizeBytes = 5 * 1024 * 1024 }
            };
            Assert.Null(AnthropicDirectBackend.ValidateAttachmentSizes(ok));
        }

        [Fact]
        public void ValidateSizes_OversizedPdf_ReturnsClearError()
        {
            var atts = new List<ChatAttachment>
            {
                new ChatAttachment { FileName = "big.pdf", Kind = AttachmentKind.Pdf,
                    SizeBytes = AnthropicDirectBackend.MaxPdfBytes + 1 }
            };
            var error = AnthropicDirectBackend.ValidateAttachmentSizes(atts);
            Assert.NotNull(error);
            Assert.Contains("big.pdf", error);
            Assert.Contains("request limit", error);
        }

        [Fact]
        public void ValidateSizes_OversizedImage_ReturnsClearError()
        {
            var atts = new List<ChatAttachment>
            {
                new ChatAttachment { FileName = "huge.png", Kind = AttachmentKind.Image,
                    SizeBytes = AnthropicDirectBackend.MaxImageBytes + 1 }
            };
            var error = AnthropicDirectBackend.ValidateAttachmentSizes(atts);
            Assert.NotNull(error);
            Assert.Contains("huge.png", error);
        }

        // --- Content block building ---

        [Fact]
        public void Build_PdfAndImage_ProduceTypedBase64Blocks()
        {
            // Image bytes are deliberately not a decodable image: ImageResizer falls
            // back to the original bytes, which keeps this test deterministic.
            var pdf = MakeFile("spec.pdf", AttachmentKind.Pdf, "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 fake"));
            var img = MakeFile("plan.png", AttachmentKind.Image, "image/png", new byte[] { 1, 2, 3, 4 });
            var records = new List<AnthropicDirectBackend.SentAttachmentRecord>();

            var blocks = AnthropicDirectBackend.BuildUserContentBlocks(
                "context + prompt", new List<ChatAttachment> { pdf, img }, records);

            Assert.Equal(3, blocks.Length); // text, document, image
            var text = (Dictionary<string, object>)blocks[0];
            Assert.Equal("text", text["type"]);
            Assert.Equal("context + prompt", text["text"]);

            var doc = (Dictionary<string, object>)blocks[1];
            Assert.Equal("document", doc["type"]);
            var docSource = (Dictionary<string, object>)doc["source"];
            Assert.Equal("base64", docSource["type"]);
            Assert.Equal("application/pdf", docSource["media_type"]);
            Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.4 fake")), docSource["data"]);

            var image = (Dictionary<string, object>)blocks[2];
            Assert.Equal("image", image["type"]);
            var imgSource = (Dictionary<string, object>)image["source"];
            Assert.Equal("image/png", imgSource["media_type"]);

            Assert.Equal(2, records.Count);
            Assert.All(records, r => Assert.True(r.PayloadChars > 0));
            Assert.All(records, r => Assert.False(r.Dropped));
        }

        [Fact]
        public void Build_TextAttachment_InlinesIntoTextBlock()
        {
            var txt = MakeFile("notes.txt", AttachmentKind.Text, "text/plain", Encoding.UTF8.GetBytes("floor at +2400"));
            var records = new List<AnthropicDirectBackend.SentAttachmentRecord>();

            var blocks = AnthropicDirectBackend.BuildUserContentBlocks(
                "prompt", new List<ChatAttachment> { txt }, records);

            Assert.Single(blocks);
            var text = (string)((Dictionary<string, object>)blocks[0])["text"];
            Assert.Contains("prompt", text);
            Assert.Contains("--- Attached file: notes.txt ---", text);
            Assert.Contains("floor at +2400", text);
            Assert.Empty(records); // inline text carries no droppable payload
        }

        [Fact]
        public void Build_OtherKind_GetsUnreadableNote()
        {
            var other = MakeFile("model.rvt", AttachmentKind.Other, "application/octet-stream", new byte[] { 9 });
            var records = new List<AnthropicDirectBackend.SentAttachmentRecord>();

            var blocks = AnthropicDirectBackend.BuildUserContentBlocks(
                "prompt", new List<ChatAttachment> { other }, records);

            var text = (string)((Dictionary<string, object>)blocks[0])["text"];
            Assert.Contains("model.rvt", text);
            Assert.Contains("format not directly readable", text);
        }

        [Fact]
        public void Build_EmptyTextWithImageOnly_OmitsEmptyTextBlock()
        {
            var img = MakeFile("shot.png", AttachmentKind.Image, "image/png", new byte[] { 1 });
            var records = new List<AnthropicDirectBackend.SentAttachmentRecord>();

            var blocks = AnthropicDirectBackend.BuildUserContentBlocks(
                "", new List<ChatAttachment> { img }, records);

            Assert.Single(blocks);
            Assert.Equal("image", ((Dictionary<string, object>)blocks[0])["type"]);
        }

        [Fact]
        public void Build_MissingFile_DegradesToTextNote()
        {
            var gone = new ChatAttachment
            {
                FileName = "gone.pdf",
                StoredPath = Path.Combine(_dir, "does-not-exist.pdf"),
                Kind = AttachmentKind.Pdf,
                MimeType = "application/pdf"
            };
            var records = new List<AnthropicDirectBackend.SentAttachmentRecord>();

            var blocks = AnthropicDirectBackend.BuildUserContentBlocks(
                "prompt", new List<ChatAttachment> { gone }, records);

            Assert.Single(blocks); // just the text block — no document block
            var text = (string)((Dictionary<string, object>)blocks[0])["text"];
            Assert.Contains("could not be read", text);
            Assert.Empty(records);
        }

        // --- History payload trimming ---

        private static AnthropicDirectBackend.SentAttachmentRecord MakeSentRecord(string name, int payloadChars)
        {
            var block = new Dictionary<string, object>
            {
                { "type", "document" },
                { "source", new Dictionary<string, object> { { "data", new string('A', payloadChars) } } }
            };
            var owner = new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", "msg" } }, block } }
            };
            return new AnthropicDirectBackend.SentAttachmentRecord
            {
                Owner = owner,
                Block = block,
                Name = name,
                Path = @"C:\store\" + name,
                PayloadChars = payloadChars
            };
        }

        [Fact]
        public void Trim_UnderBudget_DropsNothing()
        {
            var sent = new List<AnthropicDirectBackend.SentAttachmentRecord>
            {
                MakeSentRecord("a.pdf", 100), MakeSentRecord("b.pdf", 100)
            };

            AnthropicDirectBackend.TrimAttachmentPayload(sent, 100, 1000);

            Assert.All(sent, r => Assert.False(r.Dropped));
        }

        [Fact]
        public void Trim_OverBudget_DropsOldestFirstAndStubsTheBlock()
        {
            var oldest = MakeSentRecord("oldest.pdf", 400);
            var newer = MakeSentRecord("newer.pdf", 400);
            var sent = new List<AnthropicDirectBackend.SentAttachmentRecord> { oldest, newer };

            // incoming 400 + active 800 > budget 1000 → drop oldest (400) → 800 fits
            AnthropicDirectBackend.TrimAttachmentPayload(sent, 400, 1000);

            Assert.True(oldest.Dropped);
            Assert.False(newer.Dropped);

            // The oldest block was replaced in place with a text stub naming the stored path
            var content = (object[])oldest.Owner["content"];
            var stub = (Dictionary<string, object>)content[1];
            Assert.Equal("text", stub["type"]);
            var stubText = (string)stub["text"];
            Assert.Contains("oldest.pdf", stubText);
            Assert.Contains(@"C:\store\oldest.pdf", stubText);
            Assert.Contains("re-attach", stubText);

            // The newer block is untouched
            var newerContent = (object[])newer.Owner["content"];
            Assert.Equal("document", ((Dictionary<string, object>)newerContent[1])["type"]);
        }

        [Fact]
        public void Trim_AlreadyDroppedRecords_AreSkippedNotDoubleCounted()
        {
            var dropped = MakeSentRecord("gone.pdf", 500);
            dropped.Dropped = true;
            var active = MakeSentRecord("live.pdf", 300);
            var sent = new List<AnthropicDirectBackend.SentAttachmentRecord> { dropped, active };

            // active 300 + incoming 300 < budget 1000 → nothing more drops
            AnthropicDirectBackend.TrimAttachmentPayload(sent, 300, 1000);

            Assert.False(active.Dropped);
        }
    }
}
