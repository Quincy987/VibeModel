using System;
using System.Collections.Generic;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// A persisted chat conversation: metadata plus the ordered message transcript.
    /// Pure data + title derivation — no UI, no file I/O (that's ChatSessionStore).
    /// </summary>
    public class ChatSession
    {
        public const string RoleUser = "user";
        public const string RoleAssistant = "assistant";
        public const string RoleSystem = "system";

        private const int MaxTitleLength = 60;

        public string Id { get; set; }
        public string Title { get; set; }
        public string Project { get; set; }
        public string Backend { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<ChatSessionMessage> Messages { get; private set; }

        public ChatSession()
        {
            Title = "";
            Project = "";
            Backend = "";
            Messages = new List<ChatSessionMessage>();
        }

        /// <summary>Create a fresh session stamped with the current project and backend.</summary>
        public static ChatSession Start(string project, string backend)
        {
            var now = DateTime.UtcNow;
            return new ChatSession
            {
                Id = now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Project = project ?? "",
                Backend = backend ?? "",
                CreatedUtc = now,
                UpdatedUtc = now
            };
        }

        /// <summary>Sessions with no user message are never persisted (empty-session rule).</summary>
        public bool HasUserMessages
        {
            get
            {
                foreach (var m in Messages)
                    if (m.Role == RoleUser)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// Append a message and bump UpdatedUtc. The first user message becomes the title.
        /// </summary>
        public void Append(string role, string content)
        {
            Messages.Add(new ChatSessionMessage
            {
                Role = role,
                Content = content ?? "",
                TimestampUtc = DateTime.UtcNow
            });
            if (role == RoleUser && string.IsNullOrEmpty(Title))
                Title = MakeTitle(content);
            UpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>First-user-message title: single line, capped at 60 chars with ellipsis.</summary>
        public static string MakeTitle(string firstUserMessage)
        {
            if (string.IsNullOrWhiteSpace(firstUserMessage))
                return "Untitled chat";

            var oneLine = firstUserMessage.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            while (oneLine.Contains("  "))
                oneLine = oneLine.Replace("  ", " ");
            oneLine = oneLine.Trim();

            if (oneLine.Length <= MaxTitleLength)
                return oneLine;
            return oneLine.Substring(0, MaxTitleLength).TrimEnd() + "...";
        }
    }

    public class ChatSessionMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    /// <summary>Lightweight listing entry for the history browser (no message bodies).</summary>
    public class ChatSessionMeta
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Project { get; set; }
        public string Backend { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int MessageCount { get; set; }
    }
}
