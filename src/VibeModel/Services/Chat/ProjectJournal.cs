using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// The chat turn a command belongs to: a short id and the first characters of the user's
    /// question. Travels as the X-VibeModel-Turn header ("&lt;id&gt; &lt;url-encoded question&gt;").
    /// </summary>
    public sealed class ChatTurnRef
    {
        public string Id { get; set; }
        public string Question { get; set; }

        public string ToHeader() => Id + " " + Uri.EscapeDataString(Question ?? "");

        /// <summary>Null for a missing or malformed header.</summary>
        public static ChatTurnRef FromHeader(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            int space = v.IndexOf(' ');
            var id = space < 0 ? v : v.Substring(0, space);
            if (id.Length == 0 || id.Length > 40 || !id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')) return null;
            string q = "";
            if (space >= 0)
            {
                try { q = Uri.UnescapeDataString(v.Substring(space + 1)); }
                catch { q = v.Substring(space + 1); }
            }
            return new ChatTurnRef { Id = id, Question = ProjectJournal.Shorten(q, ProjectJournal.QuestionChars) };
        }
    }

    /// <summary>
    /// Plan 08 (project part): the project journal — an audit trail in journal.md next to
    /// memory.md. One line per modifying command or batch (written by the add-in itself) and one
    /// per chat turn (the question and the sources the answer cited). Writing never touches
    /// Revit's main thread: lines are queued and a background writer appends them with a 3 s
    /// deadline; an offline folder drops lines with one logged warning. Formatting, source
    /// parsing, filtering and rotation are pure and covered headlessly.
    /// </summary>
    public static class ProjectJournal
    {
        public const string FileName = "journal.md";
        public const string TurnHeader = "X-VibeModel-Turn";
        public const int QuestionChars = 120;
        public const long RotateBytes = 2L * 1024 * 1024;
        internal const int MaxArgsChars = 200;
        internal const int MaxIdsShown = 20;
        public const int DefaultLast = 20;
        internal const int MaxLinesShown = 200;
        internal const string Sep = " · ";

        // --- The chat turn in flight ------------------------------------------------------

        private static ChatTurnRef _current;

        /// <summary>
        /// The chat turn running in this Revit session, if any. Commands that arrive without an
        /// X-VibeModel-Turn header while a turn runs (the Claude Code CLI's own curl calls) are
        /// attributed to it.
        /// </summary>
        public static ChatTurnRef CurrentTurn => Volatile.Read(ref _current);

        /// <summary>Starts a turn (null when the journal is off).</summary>
        public static ChatTurnRef BeginTurn(string question)
        {
            if (!ProjectData.Enabled) return null;
            var turn = new ChatTurnRef
            {
                Id = "t" + Guid.NewGuid().ToString("N").Substring(0, 6),
                Question = Shorten(question, QuestionChars)
            };
            Volatile.Write(ref _current, turn);
            return turn;
        }

        /// <summary>
        /// Ends a turn. With an answer, appends the turn line (question + cited sources); with
        /// null it only clears the in-flight turn. Idempotent: the first call with an answer wins.
        /// </summary>
        public static void EndTurn(ChatTurnRef turn, string answer)
        {
            if (turn == null) return;
            Interlocked.CompareExchange(ref _current, null, turn);
            if (answer == null) return;
            lock (EndedTurns)
            {
                if (EndedTurns.Contains(turn.Id)) return;
                EndedTurns.Add(turn.Id);
                if (EndedTurns.Count > 50) EndedTurns.Clear();
            }
            try
            {
                bool cancelled = answer == "[Cancelled]";
                Enqueue(ActiveDocumentTracker.Title, ActiveDocumentTracker.PathName,
                    FormatTurnLine(DateTime.Now, turn, cancelled ? new List<string>() : ParseSources(answer), cancelled));
            }
            catch (Exception ex)
            {
                Logger.Warn("Journal turn line skipped: " + ex.Message);
            }
        }

        private static readonly HashSet<string> EndedTurns = new HashSet<string>(StringComparer.Ordinal);

        // --- Pure formatting --------------------------------------------------------------

        /// <summary>One line: whitespace collapsed (newlines become " ⏎ "), capped with "…".</summary>
        public static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = Regex.Replace(s.Trim(), @"\s*\r?\n\s*", " ⏎ ");
            t = Regex.Replace(t, @"[ \t]+", " ");
            return t.Length <= max ? t : t.Substring(0, Math.Max(1, max - 1)).TrimEnd() + "…";
        }

        internal static string Stamp(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        internal static string Ids(string label, IList<long> ids)
        {
            if (ids == null || ids.Count == 0) return null;
            var shown = ids.Take(MaxIdsShown).Select(i => i.ToString(CultureInfo.InvariantCulture));
            return label + " " + string.Join(" ", shown) + (ids.Count > MaxIdsShown ? " +" + (ids.Count - MaxIdsShown) + " more" : "");
        }

        internal static string Quote(ChatTurnRef turn)
            => "chat " + turn.Id + (string.IsNullOrEmpty(turn.Question) ? "" : " \"" + turn.Question.Replace("\"", "'") + "\"");

        /// <summary>
        /// "- 2026-10-12 14:03:12 · wall 0 0 5000 0 3000 · created 1543814 · modified 1543700 ·
        /// chat t3f9a2c "maak een muur van 5 m"". Empty id groups and the chat part are left out.
        /// </summary>
        public static string FormatCommandLine(DateTime local, string command, string args,
            IList<long> created, IList<long> modified, IList<long> deleted, ChatTurnRef turn)
        {
            var parts = new List<string> { "- " + Stamp(local), Shorten((command ?? "") + (string.IsNullOrWhiteSpace(args) ? "" : " " + args), MaxArgsChars) };
            foreach (var p in new[] { Ids("created", created), Ids("modified", modified), Ids("deleted", deleted) })
                if (p != null) parts.Add(p);
            if (turn != null) parts.Add(Quote(turn));
            return string.Join(Sep, parts);
        }

        /// <summary>"- 2026-10-12 14:03:25 · chat t3f9a2c "vraag" · sources: a; b" (or "sources: none", "cancelled").</summary>
        public static string FormatTurnLine(DateTime local, ChatTurnRef turn, IList<string> sources, bool cancelled)
        {
            var parts = new List<string> { "- " + Stamp(local), Quote(turn) };
            if (cancelled) parts.Add("cancelled");
            else parts.Add("sources: " + (sources == null || sources.Count == 0 ? "none" : string.Join("; ", sources)));
            return string.Join(Sep, parts);
        }

        /// <summary>The batch as one command text: "batch: wall 0 0 5000 0; floor …".</summary>
        public static string BatchText(IEnumerable<KeyValuePair<string, string>> commands)
        {
            return "batch: " + string.Join("; ", commands.Select(c => c.Key + (string.IsNullOrWhiteSpace(c.Value) ? "" : " " + c.Value)));
        }

        private static readonly Regex SourceMarker = new Regex(
            @"\[\s*(?:bron(?:nen)?|sources?)\s*:\s*([^\]\r\n]+?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Every distinct [bron: …] (or [source: …]) marker in the answer, in order of first use.</summary>
        public static List<string> ParseSources(string answer)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(answer)) return list;
            foreach (Match m in SourceMarker.Matches(answer))
            {
                var s = Shorten(m.Groups[1].Value, 160);
                if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
            }
            return list;
        }

        // --- Reading back -----------------------------------------------------------------

        /// <summary>What <c>journal</c> asks for: today, one date, or the last n lines.</summary>
        public sealed class Query
        {
            public string Date { get; set; }   // yyyy-MM-dd, or null for "last n"
            public int Last { get; set; } = DefaultLast;
            public string Label { get; set; }
        }

        /// <summary>Parses "today" / "vandaag" / "yesterday" / "gisteren" / yyyy-MM-dd / dd-MM-yyyy / "--last n" / "n".</summary>
        public static Query ParseQuery(string args, DateTime today, out string error)
        {
            error = null;
            var a = (args ?? "").Trim().ToLowerInvariant();
            if (a.Length == 0) return new Query { Label = "last " + DefaultLast };
            if (a == "today" || a == "vandaag") return new Query { Date = today.ToString("yyyy-MM-dd"), Label = "today" };
            if (a == "yesterday" || a == "gisteren")
                return new Query { Date = today.AddDays(-1).ToString("yyyy-MM-dd"), Label = "yesterday" };
            var last = Regex.Match(a, @"^(?:--last\s+|last\s+)?(\d{1,4})$");
            if (last.Success)
            {
                int n = Math.Max(1, int.Parse(last.Groups[1].Value));
                return new Query { Last = n, Label = "last " + n };
            }
            if (DateTime.TryParseExact(a, new[] { "yyyy-MM-dd", "d-M-yyyy", "dd-MM-yyyy" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                return new Query { Date = d.ToString("yyyy-MM-dd"), Label = d.ToString("yyyy-MM-dd") };
            error = "Use: journal [today|yesterday|<yyyy-mm-dd>|--last <n>]";
            return null;
        }

        /// <summary>The entry lines (oldest first) that match the query.</summary>
        public static List<string> Filter(IEnumerable<string> lines, Query q)
        {
            var entries = lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
            if (q.Date != null) return entries.Where(l => l.StartsWith("- " + q.Date + " ", StringComparison.Ordinal)).ToList();
            return entries.Skip(Math.Max(0, entries.Count - q.Last)).ToList();
        }

        /// <summary>Rotated files that can hold <paramref name="date"/>: journal-yyyymm*.md of that month and the next.</summary>
        internal static IEnumerable<string> RotatedCandidates(string directory, string date)
        {
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                yield break;
            foreach (var month in new[] { d, d.AddMonths(1) })
            {
                string[] files;
                try { files = Directory.GetFiles(directory, "journal-" + month.ToString("yyyyMM") + "*.md"); }
                catch { continue; }
                foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
            }
        }

        // --- Writing ----------------------------------------------------------------------

        internal static string Header(string documentTitle)
            => "# VibeModel journal" + (string.IsNullOrWhiteSpace(documentTitle) ? "" : " — " + documentTitle.Trim()) + "\r\n\r\n";

        /// <summary>journal-yyyymm.md, or journal-yyyymm-2.md … when that name is taken.</summary>
        public static string RotationTarget(string directory, DateTime now, Func<string, bool> exists)
        {
            var stem = "journal-" + now.ToString("yyyyMM");
            var path = Path.Combine(directory, stem + ".md");
            for (int i = 2; exists(path); i++) path = Path.Combine(directory, stem + "-" + i + ".md");
            return path;
        }

        /// <summary>
        /// Appends <paramref name="lines"/> to journal.md in <paramref name="directory"/>, rotating it
        /// first when it reached <paramref name="rotateBytes"/>. Direct file access — the writer
        /// thread calls this under a deadline.
        /// </summary>
        internal static void AppendNow(string directory, string documentTitle, IList<string> lines, DateTime now,
            long rotateBytes = RotateBytes)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= rotateBytes)
            {
                File.Move(path, RotationTarget(directory, now, File.Exists));
                info.Refresh();
            }
            var sb = new StringBuilder();
            if (!info.Exists) sb.Append(Header(documentTitle));
            foreach (var l in lines) sb.Append(l).Append("\r\n");
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private sealed class Pending
        {
            public string Title;
            public string PathName;
            public string Line;
        }

        private static readonly BlockingCollection<Pending> Queue = new BlockingCollection<Pending>(2000);
        private static readonly object WriterLock = new object();
        private static Thread _writer;
        private static int _inFlight;
        private static bool _queueFullWarned;

        // A folder that missed its deadline is skipped (lines dropped) until this time.
        private static readonly Dictionary<string, DateTime> OfflineUntil =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> OfflineWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly TimeSpan OfflineBackoff = TimeSpan.FromSeconds(60);

        /// <summary>Queues one line for the document's journal. Cheap and never blocks — safe on Revit's main thread.</summary>
        public static void Enqueue(string documentTitle, string documentPathName, string line)
        {
            if (string.IsNullOrEmpty(line) || !ProjectData.Enabled) return;
            EnsureWriter();
            Interlocked.Increment(ref _inFlight);
            if (!Queue.TryAdd(new Pending { Title = documentTitle, PathName = documentPathName, Line = line }))
            {
                Interlocked.Decrement(ref _inFlight);
                if (!_queueFullWarned)
                {
                    _queueFullWarned = true;
                    Logger.Warn("Journal queue full — lines are dropped until the writer catches up.");
                }
            }
        }

        /// <summary>Waits until every queued line has been written or dropped (true), or the timeout passed (false).</summary>
        public static bool Flush(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _inFlight) > 0)
            {
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(25);
            }
            return true;
        }

        private static void EnsureWriter()
        {
            if (_writer != null) return;
            lock (WriterLock)
            {
                if (_writer != null) return;
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "VibeModel journal writer" };
                _writer.Start();
            }
        }

        private static void WriterLoop()
        {
            foreach (var item in Queue.GetConsumingEnumerable())
            {
                // Lines that queued up meanwhile for the same document go out in one append.
                var batch = new List<Pending> { item };
                while (batch.Count < 200 && Queue.TryTake(out var more))
                {
                    if (more.Title == batch[0].Title && more.PathName == batch[0].PathName) batch.Add(more);
                    else { WriteBatch(batch); batch = new List<Pending> { more }; }
                }
                WriteBatch(batch);
            }
        }

        private static void WriteBatch(List<Pending> batch)
        {
            try
            {
                var first = batch[0];
                // Resolving may probe a configured folder (≤ 3 s) — fine here, off the main thread.
                var loc = ProjectData.LocateForDocument(first.Title, first.PathName);
                if (loc == null) return;

                lock (OfflineUntil)
                {
                    if (OfflineUntil.TryGetValue(loc.Directory, out var until) && DateTime.UtcNow < until) return;
                }

                var lines = batch.Select(p => p.Line).ToList();
                var task = Task.Run(() => AppendNow(loc.Directory, first.Title, lines, DateTime.Now));
                bool finished;
                try { finished = task.Wait(ProjectData.IoTimeout); }
                catch (AggregateException ex)
                {
                    Logger.Warn("Journal write failed (" + loc.Directory + "): " + (ex.InnerException?.Message ?? ex.Message));
                    return;
                }
                bool warn;
                lock (OfflineUntil)
                {
                    if (finished)
                    {
                        OfflineWarned.Remove(loc.Directory);
                        return;
                    }
                    OfflineUntil[loc.Directory] = DateTime.UtcNow + OfflineBackoff;
                    warn = OfflineWarned.Add(loc.Directory);
                }
                // One warning per offline spell; lines are dropped silently until a write succeeds again.
                if (warn)
                    Logger.Warn("Journal folder did not answer within " + ProjectData.IoTimeout.TotalSeconds +
                                " s (offline?) — journal lines are dropped until it answers again: " + loc.Directory);
            }
            catch (Exception ex)
            {
                Logger.Warn("Journal write skipped: " + ex.Message);
            }
            finally
            {
                Interlocked.Add(ref _inFlight, -batch.Count);
            }
        }
    }
}
