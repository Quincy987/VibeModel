using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// File-based persistence for chat sessions: one JSON file per session under
    /// %LOCALAPPDATA%\VibeModel\chats\. Every operation is defensive — a failed
    /// load/save logs and degrades silently so persistence can never break chat.
    /// </summary>
    public class ChatSessionStore
    {
        public const int DefaultMaxSessions = 200;

        private static ChatSessionStore _default;

        /// <summary>Store rooted at %LOCALAPPDATA%\VibeModel\chats (the production location).</summary>
        public static ChatSessionStore Default
        {
            get
            {
                if (_default == null)
                    _default = new ChatSessionStore(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VibeModel", "chats"));
                return _default;
            }
        }

        private readonly string _dir;
        private readonly int _maxSessions;
        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public ChatSessionStore(string directory, int maxSessions = DefaultMaxSessions)
        {
            _dir = directory;
            _maxSessions = Math.Max(1, maxSessions);
        }

        /// <summary>
        /// Persist a session, overwriting its existing file. Sessions without any
        /// user message are not saved (avoids empty-session litter).
        /// </summary>
        public void SaveSession(ChatSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.Id) || !session.HasUserMessages)
                return;

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_dir);

                    var messages = new List<object>();
                    foreach (var m in session.Messages)
                    {
                        messages.Add(new Dictionary<string, object>
                        {
                            { "role", m.Role ?? "" },
                            { "content", m.Content ?? "" },
                            { "timestampUtc", ToIso(m.TimestampUtc) }
                        });
                    }

                    var payload = new Dictionary<string, object>
                    {
                        { "id", session.Id },
                        { "title", session.Title ?? "" },
                        { "project", session.Project ?? "" },
                        { "backend", session.Backend ?? "" },
                        { "createdUtc", ToIso(session.CreatedUtc) },
                        { "updatedUtc", ToIso(session.UpdatedUtc) },
                        { "messages", messages }
                    };

                    File.WriteAllText(PathFor(session.Id), _json.Serialize(payload));
                    PruneLocked();
                }
                catch (Exception ex)
                {
                    Logger.Error("ChatSessionStore: failed to save session " + session.Id, ex);
                }
            }
        }

        /// <summary>Load a full session by id. Returns null if missing or unreadable.</summary>
        public ChatSession LoadSession(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;

            lock (_lock)
            {
                return LoadFile(PathFor(id));
            }
        }

        /// <summary>
        /// List all saved sessions as lightweight metadata, newest first.
        /// Corrupt/unreadable files are skipped, never fatal.
        /// </summary>
        public List<ChatSessionMeta> ListSessions()
        {
            var result = new List<ChatSessionMeta>();
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_dir))
                        return result;

                    foreach (var file in Directory.GetFiles(_dir, "*.json"))
                    {
                        var s = LoadFile(file);
                        if (s == null)
                            continue;
                        result.Add(new ChatSessionMeta
                        {
                            Id = s.Id,
                            Title = s.Title,
                            Project = s.Project,
                            Backend = s.Backend,
                            UpdatedUtc = s.UpdatedUtc,
                            MessageCount = s.Messages.Count
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("ChatSessionStore: failed to list sessions", ex);
                }
            }
            result.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
            return result;
        }

        public void DeleteSession(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return;

            lock (_lock)
            {
                try
                {
                    var path = PathFor(id);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Logger.Error("ChatSessionStore: failed to delete session " + id, ex);
                }
            }
        }

        private string PathFor(string id)
        {
            return Path.Combine(_dir, id + ".json");
        }

        /// <summary>Keep only the most recent _maxSessions files (by session UpdatedUtc).</summary>
        private void PruneLocked()
        {
            var files = Directory.GetFiles(_dir, "*.json");
            if (files.Length <= _maxSessions)
                return;

            var ordered = files
                .Select(f =>
                {
                    var s = LoadFile(f);
                    // Unparsable files sort by file time so they age out too.
                    var updated = s != null ? s.UpdatedUtc : File.GetLastWriteTimeUtc(f);
                    return new { File = f, Updated = updated };
                })
                .OrderBy(x => x.Updated)
                .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var excess = files.Length - _maxSessions;
            for (int i = 0; i < excess; i++)
            {
                try
                {
                    File.Delete(ordered[i].File);
                }
                catch (Exception ex)
                {
                    Logger.Error("ChatSessionStore: failed to prune " + ordered[i].File, ex);
                }
            }
        }

        private ChatSession LoadFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                var dto = _json.Deserialize<SessionDto>(File.ReadAllText(path));
                if (dto == null || string.IsNullOrEmpty(dto.id))
                    return null;

                var session = new ChatSession
                {
                    Id = dto.id,
                    Title = dto.title ?? "",
                    Project = dto.project ?? "",
                    Backend = dto.backend ?? "",
                    CreatedUtc = ParseIso(dto.createdUtc),
                    UpdatedUtc = ParseIso(dto.updatedUtc)
                };

                if (dto.messages != null)
                {
                    foreach (var m in dto.messages)
                    {
                        if (m == null)
                            continue;
                        session.Messages.Add(new ChatSessionMessage
                        {
                            Role = m.role ?? "",
                            Content = m.content ?? "",
                            TimestampUtc = ParseIso(m.timestampUtc)
                        });
                    }
                }
                return session;
            }
            catch (Exception ex)
            {
                Logger.Warn("ChatSessionStore: skipping unreadable session file "
                    + Path.GetFileName(path) + " - " + ex.Message);
                return null;
            }
        }

        private static string ToIso(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Unspecified)
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseIso(string value)
        {
            DateTime dt;
            if (!string.IsNullOrEmpty(value)
                && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return DateTime.MinValue;
        }

        // JSON shape on disk. JavaScriptSerializer binds by exact member name,
        // hence the lowercase fields matching the serialized keys.
        private class SessionDto
        {
            public string id { get; set; }
            public string title { get; set; }
            public string project { get; set; }
            public string backend { get; set; }
            public string createdUtc { get; set; }
            public string updatedUtc { get; set; }
            public List<MessageDto> messages { get; set; }
        }

        private class MessageDto
        {
            public string role { get; set; }
            public string content { get; set; }
            public string timestampUtc { get; set; }
        }
    }
}
