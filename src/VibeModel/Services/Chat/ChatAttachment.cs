using System;
using System.IO;

namespace VibeModel.Services.Chat
{
    public enum AttachmentKind
    {
        Image,
        Pdf,
        Text,
        Other
    }

    /// <summary>
    /// A file the user attached to a chat message. StoredPath points at the persisted
    /// per-project copy (see AttachmentStore); it is null while the file is still
    /// pending in the UI and set before the message is handed to a backend.
    /// </summary>
    public class ChatAttachment
    {
        public string FileName { get; set; }
        public string StoredPath { get; set; }
        public AttachmentKind Kind { get; set; }
        public string MimeType { get; set; }
        public long SizeBytes { get; set; }

        public static ChatAttachment FromFile(string path)
        {
            var info = new FileInfo(path);
            var ext = info.Extension;
            return new ChatAttachment
            {
                FileName = info.Name,
                StoredPath = null,
                Kind = ClassifyExtension(ext),
                MimeType = MimeTypeFor(ext),
                SizeBytes = info.Exists ? info.Length : 0
            };
        }

        internal static AttachmentKind ClassifyExtension(string extension)
        {
            switch ((extension ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "png":
                case "jpg":
                case "jpeg":
                case "gif":
                case "webp":
                    return AttachmentKind.Image;
                case "pdf":
                    return AttachmentKind.Pdf;
                case "txt":
                case "md":
                case "csv":
                case "json":
                case "xml":
                case "log":
                    return AttachmentKind.Text;
                default:
                    return AttachmentKind.Other;
            }
        }

        internal static string MimeTypeFor(string extension)
        {
            switch ((extension ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "png": return "image/png";
                case "jpg":
                case "jpeg": return "image/jpeg";
                case "gif": return "image/gif";
                case "webp": return "image/webp";
                case "pdf": return "application/pdf";
                case "txt": return "text/plain";
                case "md": return "text/markdown";
                case "csv": return "text/csv";
                case "json": return "application/json";
                case "xml": return "application/xml";
                case "log": return "text/plain";
                default: return "application/octet-stream";
            }
        }

        public string FormatSize()
        {
            return FormatSize(SizeBytes);
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.#") + " GB";
            if (bytes >= 1024L * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            if (bytes >= 1024)
                return (bytes / 1024.0).ToString("0.#") + " KB";
            return bytes + " B";
        }
    }
}
