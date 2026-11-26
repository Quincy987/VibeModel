using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// exec's text turned into a result: a compile failure and a run-time error are FAILURES, not
    /// output. Before this, both reached <see cref="CommandResult.Legacy"/>, which fails only a text
    /// that starts with "ERROR" — a compile failure starts "COMPILATION ERRORS:" and a run-time error
    /// is appended after the snippet's output ("ERROR in exec: …"), so the server, the journal, the
    /// approval flow and the pane all recorded them as succeeded (10 of them in a colleague's
    /// export). Text mode starts with an ERROR line and keeps the whole original text below it;
    /// JSON gets <c>ok:false</c> with <see cref="CompileCode"/> / <see cref="RuntimeCode"/>. The
    /// pane reads the same markers through <see cref="CompileErrors"/> / <see cref="RuntimeError"/>.
    /// Pure.
    /// </summary>
    public static class ExecOutcome
    {
        public const string CompileCode = "EXEC_COMPILE";
        public const string RuntimeCode = "EXEC_RUNTIME";

        public const string CompileHeader = "COMPILATION ERRORS:";
        public const string RuntimeMarker = "ERROR in exec: ";

        // The line exec prints after the compiler's errors (ExecCommand): the error list ends there.
        private const string WrapperNote = "Your code was wrapped";

        public const string CompileSuggestion =
            "Fix the snippet and send it again; exec compiles C# 5 against this Revit's API (apilookup <Type.Member> shows what exists).";

        public const string RuntimeSuggestion =
            "The snippet stopped at this error: exec's open transaction was rolled back, anything the snippet had already committed was kept. Fix the cause and retry.";

        /// <summary>The result for exec's text (whatever <c>Run</c> returned); <paramref name="extraHint"/> = a hint already appended to the text (run group).</summary>
        public static CommandResult FromText(string text, string extraHint = null)
        {
            if (text == null) return CommandResult.Legacy(null);
            if (text.TrimStart().StartsWith(CompileHeader, StringComparison.Ordinal))
                return Compile(text);
            var runtime = RuntimeError(text);
            if (runtime != null && !text.StartsWith("ERROR:", StringComparison.Ordinal))
                return CommandResult.TextError(RuntimeCode,
                    "ERROR: exec failed: " + runtime + "\nHint: " + RuntimeSuggestion + "\n\n" + text.TrimEnd(),
                    "exec failed: " + runtime, Join(RuntimeSuggestion, extraHint));
            // "ERROR: Runtime exception: …" / "ERROR: Execution failed: …" already fail; give them the run-time code.
            if (text.StartsWith("ERROR: Runtime exception:", StringComparison.Ordinal) ||
                text.StartsWith("ERROR: Execution failed:", StringComparison.Ordinal))
                return CommandResult.TextError(RuntimeCode, text, FirstLine(text).Substring("ERROR: ".Length), Join(RuntimeSuggestion, extraHint));
            return CommandResult.Legacy(text);
        }

        private static CommandResult Compile(string text)
        {
            var errors = CompileErrors(text) ?? new List<string>();
            var lines = Lines(text);
            var hintAt = Array.FindIndex(lines, l => l.StartsWith("Hint:", StringComparison.Ordinal));
            var hint = hintAt >= 0 ? string.Join("\n", lines.Skip(hintAt)).Trim() : null;
            var head = "exec did not compile, nothing ran (" + errors.Count + (errors.Count == 1 ? " error" : " errors") + ")";
            var message = head + (errors.Count > 0 ? ": " + string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? "; …" : "") : "");
            return CommandResult.TextError(CompileCode, "ERROR: " + head + ".\n\n" + text.TrimEnd(),
                message, hint ?? CompileSuggestion);
        }

        /// <summary>
        /// The compiler's "Line N: …" lines of a <see cref="CompileHeader"/> block anywhere in the
        /// text (up to exec's own note or a hint); null when the text holds no such block with at
        /// least one error line.
        /// </summary>
        public static List<string> CompileErrors(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = Lines(text);
            var at = Array.FindIndex(lines, l => l.TrimEnd().StartsWith(CompileHeader, StringComparison.Ordinal));
            if (at < 0) return null;
            var errors = new List<string>();
            foreach (var raw in lines.Skip(at + 1))
            {
                var l = raw.Trim();
                if (l.StartsWith(WrapperNote, StringComparison.Ordinal) || l.StartsWith("Hint:", StringComparison.Ordinal)) break;
                if (l.StartsWith("Line ", StringComparison.Ordinal)) errors.Add(l);
            }
            return errors.Count > 0 ? errors : null;
        }

        /// <summary>
        /// The message of the closing "ERROR in exec: …" line (the wrapper's catch / commit report:
        /// always the last line exec prints); null when there is none. Hint lines and VibeModel's
        /// own "-- …" status and stamp lines after it are skipped, so a whole response works too.
        /// </summary>
        public static string RuntimeError(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = Lines(text);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i].TrimEnd();
                if (l.StartsWith(RuntimeMarker, StringComparison.Ordinal)) return l.Substring(RuntimeMarker.Length).Trim();
                if (l.Length == 0 || l.StartsWith("Hint:", StringComparison.Ordinal) || l.StartsWith("-- ", StringComparison.Ordinal)) continue;
                break;
            }
            return null;
        }

        private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

        private static string FirstLine(string text)
        {
            int nl = text.IndexOf('\n');
            return (nl < 0 ? text : text.Substring(0, nl)).TrimEnd('\r');
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(b) ? a : a + " " + b;
    }
}
