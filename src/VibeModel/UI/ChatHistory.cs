using System;
using System.Collections.Generic;
using System.Text;

namespace VibeModel.UI
{
    /// <summary>
    /// Thread-safe static store of chat messages.
    /// Written to by ChatPane, read by ChatLogCommand over HTTP.
    /// </summary>
    public static class ChatHistory
    {
        private static readonly object Lock = new object();
        private static readonly List<ChatHistoryEntry> Entries = new List<ChatHistoryEntry>();
        private const int MaxEntries = 100;

        public static void Add(string role, string content)
        {
            lock (Lock)
            {
                if (Entries.Count >= MaxEntries)
                    Entries.RemoveAt(0);
                Entries.Add(new ChatHistoryEntry(role, content, DateTime.Now));
            }
        }

        public static string Format(int count)
        {
            lock (Lock)
            {
                if (Entries.Count == 0)
                    return "(no chat messages yet)";

                var start = Math.Max(0, Entries.Count - count);
                var sb = new StringBuilder();
                for (int i = start; i < Entries.Count; i++)
                {
                    var e = Entries[i];
                    sb.AppendLine("[" + e.Timestamp.ToString("HH:mm:ss") + "] " + e.Role + ": " + e.Content);
                    if (i < Entries.Count - 1)
                        sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Entries.Clear();
            }
        }
    }

    public class ChatHistoryEntry
    {
        public string Role { get; private set; }
        public string Content { get; private set; }
        public DateTime Timestamp { get; private set; }

        public ChatHistoryEntry(string role, string content, DateTime timestamp)
        {
            Role = role;
            Content = content;
            Timestamp = timestamp;
        }
    }
}
