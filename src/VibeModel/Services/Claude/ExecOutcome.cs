using System;
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
    /// JSON gets <c>ok:false</c> with <see cref="CompileCode"/> / <see cref="RuntimeCode"/>. Pure.
    /// </summary>
    public static class ExecOutcome
    {
        public const string CompileCode = "EXEC_COMPILE";
        public const string RuntimeCode = "EXEC_RUNTIME";

        public const string CompileHeader = "COMPILATION ERRORS:";
        public const string RuntimeMarker = "ERROR in exec: ";

        public const string CompileSuggestion =
            "Fix the snippet and send it again; exec compiles C# 5 against this Revit's API (apilookup <Type.Member> shows what exists).";

        public const string RuntimeSuggestion =
            "exec's own transaction was rolled back; anything the snippet committed in transactions of its own was kept. Fix the cause and retry.";

        /// <summary>The result for exec's text (whatever <c>Run</c> returned); <paramref name="extraHint"/> = a hint already appended to the text (run group).</summary>
        public static CommandResult FromText(string text, string extraHint = null)
        {
            if (text == null) return CommandResult.Legacy(text);
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith(CompileHeader, StringComparison.Ordinal))
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
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var errors = lines.Where(l => l.TrimStart().StartsWith("Line ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
            var hintAt = Array.FindIndex(lines, l => l.StartsWith("Hint:", StringComparison.Ordinal));
            var hint = hintAt >= 0 ? string.Join("\n", lines.Skip(hintAt)).Trim() : null;
            var head = "exec did not compile, nothing ran (" + errors.Count + (errors.Count == 1 ? " error" : " errors") + ")";
            var message = head + (errors.Count > 0 ? ": " + string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? "; …" : "") : "");
            return CommandResult.TextError(CompileCode, "ERROR: " + head + ".\n\n" + text.TrimEnd(),
                message, hint ?? CompileSuggestion);
        }

        /// <summary>The message of a closing "ERROR in exec: …" line (the wrapper's catch / commit report); null when there is none.</summary>
        public static string RuntimeError(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = text.Replace("\r\n", "\n").TrimEnd().Split('\n');
            // The wrapper appends it last; a run-group hint may follow it.
            for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 3; i--)
            {
                var l = lines[i];
                if (l.StartsWith(RuntimeMarker, StringComparison.Ordinal)) return l.Substring(RuntimeMarker.Length).Trim();
                if (!l.StartsWith("Hint:", StringComparison.Ordinal) && l.Trim().Length > 0) break;
            }
            return null;
        }

        private static string FirstLine(string text)
        {
            int nl = text.IndexOf('\n');
            return (nl < 0 ? text : text.Substring(0, nl)).TrimEnd('\r');
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(b) ? a : a + " " + b;
    }
}
