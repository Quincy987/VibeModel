using System;
using System.Collections.Generic;
using System.Threading;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Pins the transcript-replay rules of ChatBackendBase.RestoreHistory:
    /// system messages skipped, leading assistant dropped, consecutive same-role
    /// turns merged, and a trailing user turn padded so roles always alternate.
    /// </summary>
    public class ChatBackendRestoreTests
    {
        private sealed class FakeBackend : ChatBackendBase
        {
            public FakeBackend() : base(new Dictionary<string, IClaudeCommand>(), 0) { }

            public override bool IsAvailable => true;
            public override string StatusMessage => "";

            public override void SendMessage(
                string prompt,
                Action<string> onToken,
                Action<string> onComplete,
                Action<string> onError,
                CancellationToken cancellationToken)
            {
            }

            public IReadOnlyList<Dictionary<string, object>> History => _conversationHistory;
        }

        private static ChatSessionMessage Msg(string role, string content)
        {
            return new ChatSessionMessage { Role = role, Content = content, TimestampUtc = DateTime.UtcNow };
        }

        [Fact]
        public void Restore_SkipsSystem_DropsLeadingAssistant_Merges()
        {
            var backend = new FakeBackend();
            backend.RestoreHistory(new[]
            {
                Msg(ChatSession.RoleSystem, "welcome noise"),
                Msg(ChatSession.RoleAssistant, "stray greeting before any user turn"),
                Msg(ChatSession.RoleUser, "first"),
                Msg(ChatSession.RoleUser, "second"),
                Msg(ChatSession.RoleAssistant, "reply"),
                Msg(ChatSession.RoleSystem, "Error: transient"),
                Msg(ChatSession.RoleUser, "third"),
                Msg(ChatSession.RoleAssistant, "final reply"),
            });

            Assert.Equal(4, backend.History.Count);
            Assert.Equal("user", backend.History[0]["role"]);
            Assert.Equal("first\n\nsecond", backend.History[0]["content"]);
            Assert.Equal("assistant", backend.History[1]["role"]);
            Assert.Equal("reply", backend.History[1]["content"]);
            Assert.Equal("user", backend.History[2]["role"]);
            Assert.Equal("third", backend.History[2]["content"]);
            Assert.Equal("assistant", backend.History[3]["role"]);
            Assert.Equal("final reply", backend.History[3]["content"]);
        }

        [Fact]
        public void Restore_PadsTrailingUserTurn()
        {
            var backend = new FakeBackend();
            backend.RestoreHistory(new[]
            {
                Msg(ChatSession.RoleUser, "question"),
                Msg(ChatSession.RoleAssistant, "answer"),
                Msg(ChatSession.RoleUser, "follow-up that errored"),
            });

            Assert.Equal(4, backend.History.Count);
            Assert.Equal("user", backend.History[2]["role"]);
            Assert.Equal("assistant", backend.History[3]["role"]);
            Assert.False(string.IsNullOrEmpty((string)backend.History[3]["content"]));
        }

        [Fact]
        public void Restore_NullOrEmpty_LeavesHistoryEmpty()
        {
            var backend = new FakeBackend();
            backend.RestoreHistory(null);
            Assert.Empty(backend.History);

            backend.RestoreHistory(new ChatSessionMessage[0]);
            Assert.Empty(backend.History);

            // Restore replaces any prior state
            backend.RestoreHistory(new[] { Msg(ChatSession.RoleUser, "hi"), Msg(ChatSession.RoleAssistant, "yo") });
            Assert.Equal(2, backend.History.Count);
            backend.RestoreHistory(new ChatSessionMessage[0]);
            Assert.Empty(backend.History);
        }
    }
}
