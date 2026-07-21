using System;
using System.IO;
using System.Linq;
using VibeModel.Services.Chat;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Headless tests for the chat-session persistence layer (no Revit, no UI).
    /// Each test gets its own temp directory so runs never collide.
    /// </summary>
    public class ChatSessionStoreTests : IDisposable
    {
        private readonly string _dir;

        public ChatSessionStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VibeModelChatTests-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static ChatSession NewSession(string project = "ProjectA", string userText = "Hello world")
        {
            var session = ChatSession.Start(project, "anthropic");
            session.Append(ChatSession.RoleUser, userText);
            session.Append(ChatSession.RoleAssistant, "Hi there!");
            return session;
        }

        [Fact]
        public void SaveListLoad_RoundTrips()
        {
            var store = new ChatSessionStore(_dir);
            var session = NewSession();
            session.Append(ChatSession.RoleSystem, "Error: something");
            store.SaveSession(session);

            var list = store.ListSessions();
            Assert.Single(list);
            Assert.Equal(session.Id, list[0].Id);
            Assert.Equal("Hello world", list[0].Title);
            Assert.Equal("ProjectA", list[0].Project);
            Assert.Equal("anthropic", list[0].Backend);
            Assert.Equal(3, list[0].MessageCount);

            var loaded = store.LoadSession(session.Id);
            Assert.NotNull(loaded);
            Assert.Equal(session.Id, loaded.Id);
            Assert.Equal(session.Title, loaded.Title);
            Assert.Equal(session.Project, loaded.Project);
            Assert.Equal(session.Backend, loaded.Backend);
            Assert.Equal(session.CreatedUtc, loaded.CreatedUtc);
            Assert.Equal(session.UpdatedUtc, loaded.UpdatedUtc);
            Assert.Equal(3, loaded.Messages.Count);
            Assert.Equal(ChatSession.RoleUser, loaded.Messages[0].Role);
            Assert.Equal("Hello world", loaded.Messages[0].Content);
            Assert.Equal(ChatSession.RoleAssistant, loaded.Messages[1].Role);
            Assert.Equal("Hi there!", loaded.Messages[1].Content);
            Assert.Equal(ChatSession.RoleSystem, loaded.Messages[2].Role);
            Assert.Equal(session.Messages[0].TimestampUtc, loaded.Messages[0].TimestampUtc);
        }

        [Fact]
        public void Save_OverwritesSameFile_AndBumpsUpdated()
        {
            var store = new ChatSessionStore(_dir);
            var session = NewSession();
            store.SaveSession(session);

            session.Append(ChatSession.RoleUser, "Second question");
            session.Append(ChatSession.RoleAssistant, "Second answer");
            store.SaveSession(session);

            var list = store.ListSessions();
            Assert.Single(list);
            Assert.Equal(4, list[0].MessageCount);
        }

        [Fact]
        public void Title_IsSingleLine_TruncatedWithEllipsis()
        {
            var longText = "line one\r\nline two\t" + new string('x', 100);
            var title = ChatSession.MakeTitle(longText);

            Assert.DoesNotContain("\n", title);
            Assert.DoesNotContain("\r", title);
            Assert.DoesNotContain("\t", title);
            Assert.True(title.Length <= 63, "title too long: " + title.Length);
            Assert.EndsWith("...", title);
            Assert.StartsWith("line one line two", title);

            // Short messages pass through untouched
            Assert.Equal("Hello", ChatSession.MakeTitle("Hello"));
            // Whitespace-only falls back
            Assert.Equal("Untitled chat", ChatSession.MakeTitle("   \n  "));
        }

        [Fact]
        public void Title_ComesFromFirstUserMessage()
        {
            var session = ChatSession.Start("P", "local");
            session.Append(ChatSession.RoleSystem, "welcome noise");
            session.Append(ChatSession.RoleUser, "Actual question");
            session.Append(ChatSession.RoleUser, "Follow-up");
            Assert.Equal("Actual question", session.Title);
        }

        [Fact]
        public void EmptySession_IsNotSaved()
        {
            var store = new ChatSessionStore(_dir);

            var noUser = ChatSession.Start("ProjectA", "anthropic");
            noUser.Append(ChatSession.RoleSystem, "welcome");
            noUser.Append(ChatSession.RoleAssistant, "stray");
            store.SaveSession(noUser);
            store.SaveSession(null);

            Assert.Empty(store.ListSessions());
            Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.json").Length > 0);
        }

        [Fact]
        public void Prune_KeepsOnlyMostRecent()
        {
            var store = new ChatSessionStore(_dir, maxSessions: 3);
            var baseTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

            var ids = new string[5];
            for (int i = 0; i < 5; i++)
            {
                var session = NewSession(userText: "Chat number " + i);
                session.UpdatedUtc = baseTime.AddMinutes(i);
                store.SaveSession(session);
                ids[i] = session.Id;
            }

            var list = store.ListSessions();
            Assert.Equal(3, list.Count);
            // Newest three survive, oldest two are gone
            Assert.Equal(new[] { ids[4], ids[3], ids[2] }, list.Select(m => m.Id).ToArray());
            Assert.Null(store.LoadSession(ids[0]));
            Assert.Null(store.LoadSession(ids[1]));
        }

        [Fact]
        public void CorruptFile_IsSkippedNotFatal()
        {
            var store = new ChatSessionStore(_dir);
            var good = NewSession();
            store.SaveSession(good);

            File.WriteAllText(Path.Combine(_dir, "garbage.json"), "this is not json {{{");
            File.WriteAllText(Path.Combine(_dir, "wrongshape.json"), "[1, 2, 3]");

            var list = store.ListSessions();
            Assert.Single(list);
            Assert.Equal(good.Id, list[0].Id);
        }

        [Fact]
        public void LoadSession_MissingOrInvalidId_ReturnsNull()
        {
            var store = new ChatSessionStore(_dir);
            Assert.Null(store.LoadSession("does-not-exist"));
            Assert.Null(store.LoadSession(null));
            Assert.Null(store.LoadSession("bad/path\\id"));
        }

        [Fact]
        public void DeleteSession_RemovesFile()
        {
            var store = new ChatSessionStore(_dir);
            var session = NewSession();
            store.SaveSession(session);
            Assert.Single(store.ListSessions());

            store.DeleteSession(session.Id);
            Assert.Empty(store.ListSessions());
            Assert.Null(store.LoadSession(session.Id));

            // Deleting again (or nonsense) is a no-op, never a throw
            store.DeleteSession(session.Id);
            store.DeleteSession(null);
        }

        [Fact]
        public void ListSessions_SortsNewestFirst()
        {
            var store = new ChatSessionStore(_dir);
            var baseTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

            var older = NewSession(userText: "older");
            older.UpdatedUtc = baseTime;
            store.SaveSession(older);

            var newer = NewSession(userText: "newer");
            newer.UpdatedUtc = baseTime.AddHours(1);
            store.SaveSession(newer);

            var list = store.ListSessions();
            Assert.Equal(new[] { newer.Id, older.Id }, list.Select(m => m.Id).ToArray());
        }
    }
}
