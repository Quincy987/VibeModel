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

        /// <summary>
        /// Append an assistant answer with what the turn ran: its steps, usage, model and duration
        /// (all optional — null leaves the message exactly as <see cref="Append(string,string)"/> does).
        /// Only <see cref="ChatSessionMessage.Content"/> ever goes back to a model or the History view.
        /// </summary>
        public void Append(string role, string content, List<ChatSessionStep> steps, ChatSessionUsage usage,
            string model, long? durationMs)
        {
            Append(role, content);
            var m = Messages[Messages.Count - 1];
            m.Steps = steps;
            m.Usage = usage;
            m.Model = string.IsNullOrWhiteSpace(model) ? null : model;
            m.DurationMs = durationMs;
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

        // Additive, assistant messages from 1.2.18 on (null in older chats and on other roles):
        // what the turn ran, for the chat export. Never sent to a model.

        /// <summary>The turn's tool calls in order; null = not recorded (an older chat).</summary>
        public List<ChatSessionStep> Steps { get; set; }
        public ChatSessionUsage Usage { get; set; }
        public string Model { get; set; }
        public long? DurationMs { get; set; }
    }

    /// <summary>One tool call of a turn as stored: command, full args (capped, scrubbed), outcome, duration.</summary>
    public class ChatSessionStep
    {
        public string Command { get; set; }
        public string Args { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }
        public long? DurationMs { get; set; }

        /// <summary>The steps of a finished turn as they are stored (a cancelled step is not ok).</summary>
        public static List<ChatSessionStep> FromTurn(TurnActivity turn)
        {
            var steps = new List<ChatSessionStep>();
            if (turn == null) return steps;
            foreach (var s in turn.Steps)
            {
                steps.Add(new ChatSessionStep
                {
                    Command = s.Command ?? "",
                    Args = s.FullArgs ?? s.Detail ?? "",
                    Ok = s.State == TurnActivity.StepState.Done,
                    Error = s.State == TurnActivity.StepState.Failed ? s.ErrorLine
                        : s.State == TurnActivity.StepState.Cancelled ? "cancelled" : null,
                    DurationMs = s.DurationMs
                });
            }
            return steps;
        }
    }

    /// <summary>A turn's token counts as stored (the pane's cost line comes from these).</summary>
    public class ChatSessionUsage
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long ContextTokens { get; set; }
        public double? CostUsd { get; set; }

        public static ChatSessionUsage From(TurnUsage usage)
        {
            if (usage == null || usage.IsEmpty) return null;
            return new ChatSessionUsage
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CacheReadTokens = usage.CacheReadTokens,
                CacheWriteTokens = usage.CacheWriteTokens,
                ContextTokens = usage.ContextTokens,
                CostUsd = usage.ReportedCostUsd
            };
        }

        /// <summary>Back to a <see cref="TurnUsage"/> so the pane's own wording and pricing apply.</summary>
        public TurnUsage ToTurnUsage(string model) => new TurnUsage
        {
            Model = model,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            CacheReadTokens = CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens,
            ContextTokens = ContextTokens,
            ReportedCostUsd = CostUsd
        };
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
