using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Plan 17: an endless loop in an <c>exec</c> snippet used to freeze Revit for good (the main
    /// thread cannot be aborted; the HTTP timeout only abandons the request). The snippet is
    /// instrumented before it is compiled: every <c>for</c> / <c>foreach</c> / <c>while</c> /
    /// <c>do</c> body that opens with <c>{</c> gets <c>__vm.Tick();</c> as its first statement,
    /// and <see cref="ExecClock.Tick"/> throws once the budget is spent. Honest limits: a
    /// brace-less loop body, recursion, goto and a single long Revit API call are not interrupted.
    /// Pure text processing — strings (regular, verbatim, interpolated), char literals and
    /// comments are skipped — unit-tested.
    /// </summary>
    public static class ExecDeadline
    {
        /// <summary>The statement injected at the start of each loop body.</summary>
        public const string TickCall = " __vm.Tick();";

        /// <summary>Default budget: under the 30 s an HTTP command waits.</summary>
        public const int DefaultSeconds = 25;

        /// <summary>The most ?timeout= can ask for.</summary>
        public const int MaxSeconds = 300;

        /// <summary>The budget for a request: ?timeout=&lt;s&gt; clamped to 1..300, else the default.</summary>
        public static int BudgetSeconds(int? requested)
        {
            if (!requested.HasValue) return DefaultSeconds;
            return Math.Max(1, Math.Min(MaxSeconds, requested.Value));
        }

        /// <summary>
        /// The snippet with a tick at the start of every braced loop body. A snippet without such
        /// a loop comes back unchanged (same string). <paramref name="braceless"/> counts loops whose
        /// body has no braces (not instrumented).
        /// </summary>
        public static string Instrument(string code, out int instrumented, out int braceless)
        {
            instrumented = 0;
            braceless = 0;
            if (string.IsNullOrEmpty(code)) return code;

            var kind = Classify(code);
            var inserts = new List<int>();
            int n = code.Length;
            for (int i = 0; i < n; i++)
            {
                if (kind[i] != Code || !IsIdentStart(code[i])) continue;
                if (i > 0 && kind[i - 1] == Code && (IsIdentPart(code[i - 1]) || code[i - 1] == '@')) continue;
                int end = i;
                while (end < n && kind[end] == Code && IsIdentPart(code[end])) end++;
                var word = code.Substring(i, end - i);
                if (word == "do")
                {
                    int j = NextCode(code, kind, end);
                    if (j < n && code[j] == '{') { inserts.Add(j + 1); instrumented++; }
                    else if (j < n) braceless++;
                }
                else if (word == "for" || word == "foreach" || word == "while")
                {
                    int open = NextCode(code, kind, end);
                    if (open < n && code[open] == '(')
                    {
                        int close = MatchingParen(code, kind, open);
                        if (close >= 0)
                        {
                            int body = NextCode(code, kind, close + 1);
                            if (body < n && code[body] == '{') { inserts.Add(body + 1); instrumented++; }
                            // ";" is an empty loop or the tail of a do-while: nothing to check.
                            else if (body < n && code[body] != ';') braceless++;
                        }
                    }
                }
                i = end - 1;
            }

            if (inserts.Count == 0) return code;
            var sb = new StringBuilder(code.Length + inserts.Count * TickCall.Length);
            int last = 0;
            foreach (var at in inserts)
            {
                sb.Append(code, last, at - last).Append(TickCall);
                last = at;
            }
            sb.Append(code, last, code.Length - last);
            return sb.ToString();
        }

        /// <summary>
        /// How long the HTTP side waits for a request that runs exec: its loop-check budget plus
        /// 10 s for compiling and answering, never less than <paramref name="usual"/> — the check
        /// must fire before the client gives up.
        /// </summary>
        public static TimeSpan Wait(TimeSpan usual, bool runsExec, int? requestedSeconds)
        {
            if (!runsExec) return usual;
            var wait = TimeSpan.FromSeconds(BudgetSeconds(requestedSeconds) + 10);
            return wait > usual ? wait : usual;
        }

        /// <summary>The error text when the clock fired (text mode; the JSON code is EXEC_TIMEOUT).</summary>
        public static string StoppedMessage(int seconds, bool somethingKept)
            => "exec stopped after " + seconds.ToString(CultureInfo.InvariantCulture) + " s at a loop check " +
               (somethingKept
                   ? "(its own transaction was rolled back; transactions the snippet committed itself before that were kept)."
                   : "(nothing kept).");

        public const string StoppedHint = "narrow the collector or raise ?timeout=<s> (max 300).";

        public const string TimeoutCode = "EXEC_TIMEOUT";

        /// <summary>
        /// Appended to the HTTP timeout of an exec that the loop check could not stop: what it
        /// cannot interrupt.
        /// </summary>
        public const string UninterruptibleHint =
            "exec checks its time only at the start of a loop body written with { }: a brace-less loop, recursion or one long Revit call cannot be stopped - Revit stays busy until it ends.";

        // ------------------------------------------------------------------ scanner

        private const byte Code = 0, Comment = 1, Literal = 2;

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        // The next code character that is not whitespace (comments are skipped); n when none.
        private static int NextCode(string s, byte[] kind, int from)
        {
            int n = s.Length;
            int j = from;
            while (j < n && (kind[j] == Comment || (kind[j] == Code && char.IsWhiteSpace(s[j])))) j++;
            return j;
        }

        private static int MatchingParen(string s, byte[] kind, int open)
        {
            int depth = 0;
            for (int j = open; j < s.Length; j++)
            {
                if (kind[j] != Code) continue;
                if (s[j] == '(') depth++;
                else if (s[j] == ')' && --depth == 0) return j;
            }
            return -1;
        }

        /// <summary>Per character: code, comment, or inside a string/char literal.</summary>
        internal static byte[] Classify(string s)
        {
            var kind = new byte[s.Length];
            var scanner = new Scanner(s, kind);
            scanner.ScanCode(hole: false);
            return kind;
        }

        private sealed class Scanner
        {
            private readonly string _s;
            private readonly byte[] _kind;
            private int _i;

            public Scanner(string s, byte[] kind)
            {
                _s = s;
                _kind = kind;
            }

            private char At(int k) => k < _s.Length ? _s[k] : '\0';

            private void Mark(int from, int to, byte k)
            {
                for (int x = from; x < to && x < _kind.Length; x++) _kind[x] = k;
            }

            /// <summary>Code until the end, or — inside an interpolation hole — until its closing '}'.</summary>
            public void ScanCode(bool hole)
            {
                int depth = 0;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '/' && At(_i + 1) == '/')
                    {
                        int start = _i;
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        Mark(start, _i, Comment);
                        continue;
                    }
                    if (c == '/' && At(_i + 1) == '*')
                    {
                        int start = _i;
                        int close = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                        _i = close < 0 ? _s.Length : close + 2;
                        Mark(start, _i, Comment);
                        continue;
                    }
                    if (c == '"') { ScanString(verbatim: false, interpolated: false, start: _i, quote: _i); continue; }
                    if (c == '@' && At(_i + 1) == '"') { ScanString(true, false, _i, _i + 1); continue; }
                    if (c == '$' && At(_i + 1) == '"') { ScanString(false, true, _i, _i + 1); continue; }
                    if ((c == '$' && At(_i + 1) == '@' || c == '@' && At(_i + 1) == '$') && At(_i + 2) == '"')
                    {
                        ScanString(true, true, _i, _i + 2);
                        continue;
                    }
                    if (c == '\'') { ScanChar(); continue; }
                    if (hole)
                    {
                        if (c == '{') depth++;
                        else if (c == '}')
                        {
                            if (depth == 0) return; // the hole's closing brace: the string goes on
                            depth--;
                        }
                    }
                    _kind[_i] = Code;
                    _i++;
                }
            }

            private void ScanChar()
            {
                int start = _i;
                _i++;
                while (_i < _s.Length && _s[_i] != '\'' && _s[_i] != '\n')
                {
                    if (_s[_i] == '\\') _i++;
                    _i++;
                }
                if (_i < _s.Length && _s[_i] == '\'') _i++;
                Mark(start, _i, Literal);
            }

            // From the prefix (start) through the closing quote; interpolation holes are code.
            private void ScanString(bool verbatim, bool interpolated, int start, int quote)
            {
                Mark(start, quote + 1, Literal);
                _i = quote + 1;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (!verbatim && c == '\n') { _i++; return; } // unterminated: stop at the line end
                    if (!verbatim && c == '\\') { Mark(_i, _i + 2, Literal); _i += 2; continue; }
                    if (c == '"')
                    {
                        if (verbatim && At(_i + 1) == '"') { Mark(_i, _i + 2, Literal); _i += 2; continue; }
                        _kind[_i] = Literal;
                        _i++;
                        return;
                    }
                    if (interpolated && c == '{')
                    {
                        if (At(_i + 1) == '{') { Mark(_i, _i + 2, Literal); _i += 2; continue; }
                        _kind[_i] = Literal;
                        _i++;
                        ScanCode(hole: true);
                        if (_i < _s.Length) { _kind[_i] = Literal; _i++; } // the hole's '}'
                        continue;
                    }
                    if (interpolated && c == '}' && At(_i + 1) == '}') { Mark(_i, _i + 2, Literal); _i += 2; continue; }
                    _kind[_i] = Literal;
                    _i++;
                }
            }
        }
    }

    /// <summary>The per-run clock behind <c>__vm.Tick()</c> in an instrumented exec snippet.</summary>
    public sealed class ExecClock
    {
        private readonly long _deadline;

        public ExecClock(int seconds)
            : this(seconds, Stopwatch.GetTimestamp() + (long)seconds * Stopwatch.Frequency)
        {
        }

        private ExecClock(int seconds, long deadline)
        {
            Seconds = seconds;
            _deadline = deadline;
        }

        public int Seconds { get; }

        /// <summary>True once the budget ran out at a loop check — even when the snippet caught the exception.</summary>
        public bool Fired { get; private set; }

        private volatile bool _stopped;

        /// <summary>
        /// The exec run is over: a loop the snippet handed to something that runs later (an
        /// Idling handler, a lambda kept around) must never throw inside Revit's own events.
        /// </summary>
        public void Stop() => _stopped = true;

        /// <summary>
        /// Called at the start of every braced loop body. Reading the performance counter costs
        /// tens of nanoseconds, so it is read every time: a loop whose body takes seconds is
        /// still stopped on time.
        /// </summary>
        public void Tick()
        {
            if (_stopped) return;
            if (Fired) throw new ExecDeadlineException(Seconds);
            if (Stopwatch.GetTimestamp() <= _deadline) return;
            Fired = true;
            throw new ExecDeadlineException(Seconds);
        }

        /// <summary>Tests: a clock that is already past its deadline.</summary>
        internal static ExecClock Expired() => new ExecClock(0, Stopwatch.GetTimestamp() - 1);
    }

    /// <summary>Thrown by <see cref="ExecClock.Tick"/> when the exec budget is spent.</summary>
    public sealed class ExecDeadlineException : Exception
    {
        public ExecDeadlineException(int seconds)
            : base("exec stopped after " + seconds.ToString(CultureInfo.InvariantCulture) + " s at a loop check")
        {
        }
    }
}
