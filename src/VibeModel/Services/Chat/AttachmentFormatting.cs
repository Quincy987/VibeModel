using System;
using System.IO;
using System.Text;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Shared attachment-to-prompt formatting for the API-style backends: fenced
    /// inline text files (with a truncation cap so a giant CSV can't blow the
    /// context window) and the not-readable note for unsupported formats.
    /// </summary>
    internal static class AttachmentFormatting
    {
        // ~100k tokens worth of characters — large enough for any real spec text,
        // small enough to always fit alongside the rest of the conversation.
        internal const int MaxInlineTextChars = 400000;

        /// <summary>
        /// Renders a Text attachment as a fenced block the model can read inline.
        /// Read failures degrade to a note instead of failing the whole message.
        /// </summary>
        internal static string BuildTextFence(ChatAttachment attachment)
        {
            string content;
            try
            {
                content = File.ReadAllText(attachment.StoredPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read text attachment: " + attachment.StoredPath, ex);
                return "[Attached file " + attachment.FileName + " could not be read: " + ex.Message + "]";
            }

            bool truncated = content.Length > MaxInlineTextChars;
            if (truncated)
                content = content.Substring(0, MaxInlineTextChars);

            var sb = new StringBuilder();
            sb.AppendLine("--- Attached file: " + attachment.FileName + " ---");
            sb.AppendLine("```");
            sb.AppendLine(content);
            sb.AppendLine("```");
            if (truncated)
                sb.AppendLine("[File truncated at " + MaxInlineTextChars + " characters — full copy at " +
                              attachment.StoredPath + "]");
            return sb.ToString();
        }

        internal static string BuildUnreadableNote(ChatAttachment attachment)
        {
            return "[Attached file " + attachment.FileName + " stored at " + attachment.StoredPath +
                   " — format not directly readable]";
        }
    }
}
