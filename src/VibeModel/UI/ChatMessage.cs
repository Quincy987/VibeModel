using System;
using System.Collections.Generic;
using VibeModel.Services.Chat;

namespace VibeModel.UI
{
    public enum ChatRole
    {
        User,
        Assistant,
        System
    }

    public class ChatMessage
    {
        public ChatRole Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }

        // Files the user attached to this message (null/empty for most messages).
        // The bubble renderer shows these as a chip line under the text.
        public IReadOnlyList<ChatAttachment> Attachments { get; set; }

        public ChatMessage(ChatRole role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = DateTime.Now;
        }
    }
}
