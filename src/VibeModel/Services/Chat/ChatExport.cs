using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Keeps secrets out of what is stored and exported: the server token (header, environment
    /// variable, the servers file's "token" field, the live session token itself) and Anthropic
    /// API keys. Paths, questions and answers stay — they are what the export is for. Pure.
    /// </summary>
    public static class ChatExportRedaction
    {
        public const string TokenMask = "<token>";
        public const string KeyMask = "sk-ant-…";

        private static readonly Regex TokenHeader = new Regex(
            @"(X-VibeModel-Token\s*[:=]\s*)(?!\$)([^\s""'`;,)]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EnvAssignment = new Regex(
            @"(\b(?:VIBEMODEL_TOKEN|ANTHROPIC_API_KEY)\s*[=:]\s*[""']?)(?!\$)([^\s""'`;,)]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TokenJson = new Regex(
            @"(""token""\s*:\s*"")([^""]+)("")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnthropicKey = new Regex(
            @"sk-ant-[A-Za-z0-9_\-]{4,}", RegexOptions.Compiled);

        /// <summary>
        /// <paramref name="text"/> with every secret masked; <paramref name="extraSecrets"/> (e.g.
        /// <see cref="ServerAuth.Token"/>) are replaced wherever they appear.
        /// </summary>
        public static string Scrub(string text, params string[] extraSecrets)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var s = text;
            if (extraSecrets != null)
                foreach (var secret in extraSecrets)
                    if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
                        s = s.Replace(secret, TokenMask);
            var live = LiveToken();
            if (live != null) s = s.Replace(live, TokenMask);
            s = AnthropicKey.Replace(s, KeyMask);
            s = TokenHeader.Replace(s, m => m.Groups[1].Value + TokenMask);
            s = EnvAssignment.Replace(s, m => m.Groups[1].Value +
                (m.Groups[1].Value.IndexOf("ANTHROPIC", StringComparison.OrdinalIgnoreCase) >= 0 ? KeyMask : TokenMask));
            s = TokenJson.Replace(s, m => m.Groups[1].Value + TokenMask + m.Groups[3].Value);
            return s;
        }

        private static string LiveToken()
        {
            try
            {
                var t = ServerAuth.Token;
                return string.IsNullOrEmpty(t) || t.Length < 8 ? null : t;
            }
            catch { return null; }
        }
    }

    /// <summary>One "Executing: …" line of the debug log, at its local time.</summary>
    public sealed class LoggedCommand
    {
        public DateTime LocalTime { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// The debug log's command lines (<c>[HH:mm:ss.fff] [INFO] Executing: &lt;cmd&gt; &lt;args&gt;</c>, and
    /// <c>Executing batch: 4 commands</c> for a batch, whose lines are not logged one by one; local
    /// time, one file per day) — the fallback for chats from before the steps were stored. Only
    /// written with VIBEMODEL_DEBUG=1, and every client on this machine logs into it. Pure.
    /// </summary>
    public static class ChatExportLog
    {
        private static readonly Regex Line = new Regex(
            @"^\[(?<t>\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]\s+\[INFO\]\s+Executing(?:\s+(?<cmd>batch:.*)|:\s*(?<cmd>.*))$", RegexOptions.Compiled);

        public static List<LoggedCommand> Parse(DateTime day, IEnumerable<string> lines)
        {
            var result = new List<LoggedCommand>();
            if (lines == null) return result;
            foreach (var raw in lines)
            {
                var m = Line.Match(raw ?? "");
                if (!m.Success) continue;
                TimeSpan t;
                if (!TimeSpan.TryParse(m.Groups["t"].Value, CultureInfo.InvariantCulture, out t)) continue;
                result.Add(new LoggedCommand { LocalTime = day.Date + t, Text = m.Groups["cmd"].Value.Trim() });
            }
            return result;
        }

        /// <summary>The log file name of a day: log_yyyy-MM-dd.txt.</summary>
        public static string FileName(DateTime localDay) =>
            "log_" + localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt";
    }

    /// <summary>What the export found in the debug log for one turn.</summary>
    public sealed class LogLookup
    {
        /// <summary>The log of that day exists and has command lines at all (debug logging was on).</summary>
        public bool Available { get; set; }
        public List<LoggedCommand> Commands { get; set; } = new List<LoggedCommand>();
    }

    /// <summary>One question and what came back.</summary>
    public sealed class ChatExportTurn
    {
        public int Number { get; set; }
        public string Question { get; set; }
        public DateTime QuestionUtc { get; set; }
        public ChatSessionMessage Reply { get; set; }
    }

    public sealed class ChatExportStats
    {
        public int Chats { get; set; }
        public int Turns { get; set; }
        public int Commands { get; set; }
        public int Failed { get; set; }
        /// <summary>Chats in which no turn has commands from the chat file nor from the debug log.</summary>
        public int ChatsWithoutRecord { get; set; }
        public int ChatsFromLog { get; set; }
        public long Tokens { get; set; }

        public void Add(ChatExportStats o)
        {
            Chats += o.Chats; Turns += o.Turns; Commands += o.Commands; Failed += o.Failed;
            ChatsWithoutRecord += o.ChatsWithoutRecord; ChatsFromLog += o.ChatsFromLog; Tokens += o.Tokens;
        }
    }

    /// <summary>
    /// One chat as markdown: header, then per turn the question, the commands it ran (from the chat
    /// file, else the debug log, else a line saying none were recorded), the answer and the token
    /// line. Pure: the log arrives through <c>logLookup(questionUtc, replyUtc)</c>.
    /// </summary>
    public static class ChatExportDocument
    {
        public const int TableArgsLength = 200;

        // The same word in both languages (the Strings test wants every key to differ).
        private const string CommandsLabel = "Commands";

        public static List<ChatExportTurn> Turns(ChatSession session)
        {
            var turns = new List<ChatExportTurn>();
            if (session?.Messages == null) return turns;
            foreach (var m in session.Messages)
            {
                if (m == null) continue;
                if (m.Role == ChatSession.RoleUser)
                    turns.Add(new ChatExportTurn { Number = turns.Count + 1, Question = m.Content ?? "", QuestionUtc = m.TimestampUtc });
                else if (turns.Count > 0 && turns[turns.Count - 1].Reply == null)
                    turns[turns.Count - 1].Reply = m;
            }
            return turns;
        }

        /// <summary>The model of the chat: the first one an answer recorded.</summary>
        public static string ModelOf(ChatSession session) =>
            session?.Messages?.Select(m => m?.Model).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

        public static string Render(ChatSession session, Func<DateTime, DateTime, LogLookup> logLookup,
            string language, out ChatExportStats stats)
        {
            stats = new ChatExportStats { Chats = 1 };
            var sb = new StringBuilder();
            var turns = Turns(session);
            var model = ModelOf(session);

            // Gather per turn first: the header carries the totals.
            var rows = new List<TurnRecord>();
            var anyRecord = false;
            var fromLog = false;
            foreach (var t in turns)
            {
                var rec = new TurnRecord { Turn = t };
                var reply = t.Reply;
                if (reply?.Steps != null)
                {
                    rec.Steps = reply.Steps;
                    anyRecord = true;
                }
                else if (logLookup != null)
                {
                    var end = reply != null && reply.TimestampUtc > t.QuestionUtc ? reply.TimestampUtc : t.QuestionUtc.AddMinutes(30);
                    rec.Log = logLookup(t.QuestionUtc, end) ?? new LogLookup();
                    if (rec.Log.Available) { anyRecord = true; fromLog = true; }
                }
                stats.Turns++;
                if (rec.Steps != null)
                {
                    stats.Commands += rec.Steps.Count;
                    stats.Failed += rec.Steps.Count(s => !s.Ok);
                }
                else if (rec.Log != null && rec.Log.Available)
                    stats.Commands += rec.Log.Commands.Count;
                if (reply?.Usage != null)
                    stats.Tokens += reply.Usage.InputTokens + reply.Usage.OutputTokens + reply.Usage.CacheReadTokens + reply.Usage.CacheWriteTokens;
                rows.Add(rec);
            }
            if (!anyRecord) stats.ChatsWithoutRecord = 1;
            if (fromLog) stats.ChatsFromLog = 1;

            string L(string key, params object[] args) => Strings.For(language, key, args);

            sb.Append("# ").AppendLine(OneLine(Clean(session.Title)));
            sb.AppendLine();
            sb.Append("- ").AppendLine(L(Strings.ChatExportDate, Local(session.CreatedUtc), Local(session.UpdatedUtc)));
            if (!string.IsNullOrWhiteSpace(session.Project))
                sb.Append("- ").AppendLine("Project: " + Clean(session.Project));
            sb.Append("- ").AppendLine("Backend: " + (string.IsNullOrWhiteSpace(session.Backend) ? "?" : session.Backend) + " · model: " + (
                string.IsNullOrWhiteSpace(model) ? "?" : model));
            sb.Append("- ").AppendLine(L(Strings.ChatExportSummary, session.Id, stats.Turns, stats.Commands, stats.Failed));
            sb.AppendLine();

            foreach (var rec in rows)
            {
                var t = rec.Turn;
                sb.Append("## ").AppendLine(L(Strings.ChatExportTurn, t.Number, LocalTime(t.QuestionUtc)));
                sb.AppendLine();
                sb.Append("**").Append(L(Strings.ChatExportQuestion)).AppendLine("**");
                sb.AppendLine();
                sb.AppendLine(Clean(t.Question).Trim());
                sb.AppendLine();

                RenderCommands(sb, rec, language);

                var reply = t.Reply;
                sb.Append("**").Append(L(Strings.ChatExportAnswer)).Append("**");
                if (reply?.DurationMs != null) sb.Append(" (").Append(Seconds(reply.DurationMs.Value)).Append(')');
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine(reply == null ? "_" + L(Strings.ChatExportNoAnswer) + "_" : Clean(reply.Content).Trim());
                sb.AppendLine();

                var usageLine = UsageLine(reply, model, language);
                if (usageLine != null)
                {
                    sb.Append('_').Append(usageLine).AppendLine("_");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private sealed class TurnRecord
        {
            public ChatExportTurn Turn;
            public List<ChatSessionStep> Steps;
            public LogLookup Log;
        }

        private static void RenderCommands(StringBuilder sb, TurnRecord rec, string language)
        {
            string L(string key, params object[] args) => Strings.For(language, key, args);

            if (rec.Steps != null)
            {
                var failed = rec.Steps.Count(s => !s.Ok);
                sb.Append("**").Append(CommandsLabel).Append("** (").Append(rec.Steps.Count);
                if (failed > 0) sb.Append(", ").Append(L(Strings.ChatExportFailedCount, failed));
                sb.AppendLine(")");
                sb.AppendLine();
                if (rec.Steps.Count == 0)
                {
                    sb.AppendLine("_" + L(Strings.ChatExportNoSteps) + "_");
                    sb.AppendLine();
                    return;
                }
                sb.AppendLine("| # | command | args | " + L(Strings.ChatExportResult) + " | " + L(Strings.ChatExportDuration) + " |");
                sb.AppendLine("|---|---|---|---|---|");
                var fullBlocks = new List<KeyValuePair<int, ChatSessionStep>>();
                for (var i = 0; i < rec.Steps.Count; i++)
                {
                    var st = rec.Steps[i];
                    var args = Clean(st.Args ?? "");
                    var shortArgs = ToolCallDescriber.Shorten(args, TableArgsLength);
                    if (args.IndexOf('\n') >= 0 || args.Length > TableArgsLength)
                        fullBlocks.Add(new KeyValuePair<int, ChatSessionStep>(i + 1, st));
                    var result = st.Ok ? "✓" : "✗ " + Clean(st.Error ?? "");
                    sb.Append("| ").Append(i + 1)
                      .Append(" | ").Append(Cell(st.Command))
                      .Append(" | ").Append(Cell(shortArgs))
                      .Append(" | ").Append(Cell(result.Trim()))
                      .Append(" | ").Append(st.DurationMs.HasValue ? Seconds(st.DurationMs.Value) : "")
                      .AppendLine(" |");
                }
                sb.AppendLine();
                foreach (var kv in fullBlocks)
                {
                    var args = Clean(kv.Value.Args ?? "").Trim();
                    sb.AppendLine(L(Strings.ChatExportFullArgs, kv.Key, kv.Value.Command));
                    sb.AppendLine();
                    var fence = args.Contains("```") ? "~~~~" : "```";
                    var lang = string.Equals(kv.Value.Command, "exec", StringComparison.OrdinalIgnoreCase) ? "csharp" : "text";
                    sb.Append(fence).AppendLine(lang);
                    sb.AppendLine(args);
                    sb.AppendLine(fence);
                    sb.AppendLine();
                }
                return;
            }

            if (rec.Log != null && rec.Log.Available)
            {
                sb.Append("**").Append(CommandsLabel).Append("** (").Append(rec.Log.Commands.Count)
                  .Append(") — _").Append(L(Strings.ChatExportFromLog)).AppendLine("_");
                sb.AppendLine();
                if (rec.Log.Commands.Count == 0)
                    sb.AppendLine("_" + L(Strings.ChatExportNoLogLines) + "_");
                else
                    foreach (var c in rec.Log.Commands)
                        sb.Append("- `").Append(c.LocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("` ")
                          .AppendLine(Cell(ToolCallDescriber.Shorten(Clean(c.Text), 300)));
                sb.AppendLine();
                return;
            }

            sb.Append("**").Append(CommandsLabel).AppendLine("**");
            sb.AppendLine();
            sb.AppendLine("_" + L(Strings.ChatExportNotRecorded) + "_");
            sb.AppendLine();
        }

        /// <summary>The pane's own words: "3.9M tokens · lang gesprek · ≈ € 0,04".</summary>
        public static string UsageLine(ChatSessionMessage reply, string chatModel, string language)
        {
            if (reply?.Usage == null) return null;
            var usage = reply.Usage.ToTurnUsage(reply.Model ?? chatModel);
            var text = ConversationLength.Describe(usage, language);
            if (text == null) return null;
            if (usage.ContextTokens > 0)
                text += " · " + Strings.For(language, Strings.UsageTipContext, ModelPricing.Tokens(usage.ContextTokens));
            var euro = ModelPricing.EstimateEuro(usage, language);
            if (euro != null) text += " · ≈ € " + euro;
            return text;
        }

        // Secrets out; never anything else (paths and names stay — see ChatExportRedaction).
        internal static string Clean(string text) => ChatExportRedaction.Scrub(text ?? "");

        private static string OneLine(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

        private static string Cell(string text) =>
            OneLine(text).Replace("|", "\\|");

        internal static string Local(DateTime utc) =>
            utc == DateTime.MinValue ? "?" : ToLocal(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static string LocalTime(DateTime utc) =>
            utc == DateTime.MinValue ? "?" : ToLocal(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        internal static DateTime ToLocal(DateTime utc) =>
            (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime());

        private static string Seconds(long ms) =>
            (ms / 1000.0).ToString(ms < 10000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
    }

    public sealed class ChatExportResult
    {
        public string Path { get; set; }
        public bool IsZip { get; set; }
        public ChatExportStats Stats { get; set; } = new ChatExportStats();
        public TimeSpan Elapsed { get; set; }
        /// <summary>Set when the export failed (no file written, or not finished in time).</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// Writes the export: up to <see cref="SingleFileMax"/> chats as one markdown file, more as a
    /// zip with a file per chat plus INDEX.md and README.md. File I/O, no Revit, no WPF.
    /// </summary>
    public static class ChatExportWriter
    {
        public const int SingleFileMax = 5;

        public static ChatExportResult Export(IList<ChatSession> sessions, string logsDir, string outDir,
            string language, DateTime nowLocal)
        {
            var started = DateTime.UtcNow;
            var result = new ChatExportResult();
            sessions = sessions ?? new List<ChatSession>();
            Directory.CreateDirectory(outDir);
            var stamp = nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var logs = new LogReader(logsDir);

            var docs = new List<KeyValuePair<ChatSession, string>>();
            var perChat = new List<ChatExportStats>();
            foreach (var s in sessions)
            {
                ChatExportStats st;
                docs.Add(new KeyValuePair<ChatSession, string>(s, ChatExportDocument.Render(s, logs.Lookup, language, out st)));
                perChat.Add(st);
                result.Stats.Add(st);
            }

            if (sessions.Count <= SingleFileMax)
            {
                var path = UniquePath(System.IO.Path.Combine(outDir, "chats-" + stamp + ".md"));
                var sb = new StringBuilder();
                sb.AppendLine(Readme(language, result.Stats, nowLocal).TrimEnd());
                foreach (var d in docs)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.Append(d.Value);
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                result.Path = path;
            }
            else
            {
                var path = UniquePath(System.IO.Path.Combine(outDir, "chats-" + stamp + ".zip"));
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = new StringBuilder();
                index.Append("# ").AppendLine(Strings.For(language, Strings.ChatExportIndexTitle));
                index.AppendLine();
                index.AppendLine(Strings.For(language, Strings.ChatExportIndexHeader));
                index.AppendLine("|---|---|---|---|---|---|---|");
                var tmp = path + ".part";
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, "README.md", Readme(language, result.Stats, nowLocal));
                    for (var i = 0; i < docs.Count; i++)
                    {
                        var s = docs[i].Key;
                        var name = EntryName(s, names);
                        WriteEntry(zip, "chats/" + name, docs[i].Value);
                        var st = perChat[i];
                        index.Append("| ").Append(ChatExportDocument.Local(s.CreatedUtc))
                             .Append(" | [").Append(Escape(ChatExportDocument.Clean(s.Title))).Append("](chats/").Append(Uri.EscapeDataString(name)).Append(')')
                             .Append(" | ").Append(Escape(ChatExportDocument.Clean(s.Project)))
                             .Append(" | ").Append(st.Turns)
                             .Append(" | ").Append(st.ChatsWithoutRecord > 0 ? "–" : st.Commands.ToString(CultureInfo.InvariantCulture))
                             .Append(" | ").Append(st.Failed)
                             .Append(" | ").Append(st.Tokens > 0 ? ModelPricing.Tokens(st.Tokens) : "")
                             .AppendLine(" |");
                    }
                    WriteEntry(zip, "INDEX.md", index.ToString());
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                result.Path = path;
                result.IsZip = true;
            }
            result.Elapsed = DateTime.UtcNow - started;
            return result;
        }

        public static string Readme(string language, ChatExportStats stats, DateTime nowLocal)
        {
            return Strings.For(language, Strings.ChatExportReadme,
                nowLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                stats.Chats, stats.Turns, stats.Commands, stats.ChatsWithoutRecord, stats.ChatsFromLog) + "\n";
        }

        private static void WriteEntry(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(text);
        }

        private static string EntryName(ChatSession s, HashSet<string> taken)
        {
            var created = s.CreatedUtc == DateTime.MinValue ? DateTime.Now : ChatExportDocument.ToLocal(s.CreatedUtc);
            var baseName = created.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + " " + Slug(s.Title);
            var name = baseName.Trim() + ".md";
            for (var n = 2; !taken.Add(name); n++)
                name = baseName.Trim() + " (" + n + ").md";
            return name;
        }

        internal static string Slug(string title)
        {
            var t = (title ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in t)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            }
            var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
            if (slug.Length > 40) slug = slug.Substring(0, 40).TrimEnd('-');
            return slug.Length == 0 ? "chat" : slug;
        }

        private static string Escape(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim().Replace("|", "\\|").Replace("]", "\\]");

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = System.IO.Path.GetDirectoryName(path);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var ext = System.IO.Path.GetExtension(path);
            for (var n = 2; ; n++)
            {
                var p = System.IO.Path.Combine(dir, name + " (" + n + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }

        /// <summary>Reads each day's debug log once (shared read: the Logger keeps writing).</summary>
        private sealed class LogReader
        {
            private readonly string _dir;
            private readonly Dictionary<DateTime, List<LoggedCommand>> _days = new Dictionary<DateTime, List<LoggedCommand>>();

            public LogReader(string dir) { _dir = dir; }

            public LogLookup Lookup(DateTime fromUtc, DateTime toUtc)
            {
                var from = ChatExportDocument.ToLocal(fromUtc);
                var to = ChatExportDocument.ToLocal(toUtc);
                var lookup = new LogLookup();
                if (fromUtc == DateTime.MinValue) return lookup;
                for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
                {
                    var lines = Day(day);
                    if (lines == null || lines.Count == 0) continue;
                    lookup.Available = true;
                    lookup.Commands.AddRange(lines.Where(c => c.LocalTime >= from.AddSeconds(-1) && c.LocalTime <= to.AddSeconds(1)));
                }
                return lookup;
            }

            private List<LoggedCommand> Day(DateTime day)
            {
                List<LoggedCommand> cached;
                if (_days.TryGetValue(day, out cached)) return cached;
                cached = null;
                try
                {
                    var path = string.IsNullOrEmpty(_dir) ? null : System.IO.Path.Combine(_dir, ChatExportLog.FileName(day));
                    if (path != null && File.Exists(path))
                    {
                        var lines = new List<string>();
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var r = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null) lines.Add(line);
                        }
                        cached = ChatExportLog.Parse(day, lines);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("Chat export: could not read the log of " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " - " + ex.Message);
                }
                _days[day] = cached;
                return cached;
            }
        }
    }

    /// <summary>
    /// The session facade the command and the buttons share: read the chats, write the export
    /// to %LOCALAPPDATA%\VibeModel\exports\, all on its own thread under a deadline.
    /// </summary>
    public static class ChatExportService
    {
        public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        public static string ExportFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "exports");

        /// <summary><paramref name="max"/> ≤ 0 = every chat. Blocks the caller up to <see cref="Deadline"/>.</summary>
        public static ChatExportResult Export(int max, string language)
        {
            var started = DateTime.UtcNow;
            var result = PathProbe.Run(() =>
            {
                try
                {
                    var sessions = ChatSessionStore.Default.LoadSessions(max);
                    if (sessions.Count == 0)
                        return new ChatExportResult { Error = Strings.For(language, Strings.ExportChatsNone) };
                    return ChatExportWriter.Export(sessions, Logger.LogDirectory, ExportFolder, language, DateTime.Now);
                }
                catch (Exception ex)
                {
                    Logger.Error("Chat export failed", ex);
                    return new ChatExportResult { Error = ex.Message };
                }
            }, Deadline, null);

            if (result == null)
                return new ChatExportResult { Error = Strings.For(language, Strings.ExportChatsTimeout, (int)Deadline.TotalSeconds) };
            result.Elapsed = DateTime.UtcNow - started;
            if (result.Error == null)
                Logger.Info("Chat export: " + result.Stats.Chats + " chats → " + result.Path + " in " + (int)result.Elapsed.TotalMilliseconds + " ms");
            return result;
        }
    }
}
