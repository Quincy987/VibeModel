using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Strips an optional <c>--phase &lt;key&gt;</c> (or <c>--phase=&lt;key&gt;</c>) from a creating
    /// command's arguments. When the flag is absent the arguments are returned UNCHANGED (same
    /// string instance), so every existing command parses exactly as before. Validation of the
    /// key against the office phase table happens later (PhaseDefaults), where the pack is known.
    /// Pure — unit-tested.
    /// </summary>
    internal sealed class PhaseArgs
    {
        private static readonly Regex Flag = new Regex(
            @"(?:^|\s)--phase(?:=|\s+)(?<key>[^\s]+)?|(?:^|\s)--phase\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Remaining { get; private set; }
        public string Key { get; private set; }
        public bool Explicit => Key != null;
        public string Error { get; private set; }

        public static PhaseArgs Parse(string args)
        {
            var r = new PhaseArgs { Remaining = args };
            if (string.IsNullOrEmpty(args) || args.IndexOf("--phase", StringComparison.OrdinalIgnoreCase) < 0)
                return r;

            var matches = Flag.Matches(args);
            if (matches.Count == 0)
                return r;
            if (matches.Count > 1)
            {
                r.Error = "--phase given more than once.";
                return r;
            }

            var m = matches[0];
            var key = m.Groups["key"].Success ? m.Groups["key"].Value.Trim() : null;
            if (string.IsNullOrEmpty(key) || key.StartsWith("--", StringComparison.Ordinal))
            {
                r.Error = "--phase needs a value (e.g. --phase new).";
                return r;
            }

            r.Key = key;
            // Close the gap left behind ("a --phase new b" -> "a b"); everything else stays as typed.
            var before = args.Substring(0, m.Index).TrimEnd();
            var after = args.Substring(m.Index + m.Length).TrimStart();
            r.Remaining = (before.Length > 0 && after.Length > 0 ? before + " " + after : before + after).Trim();
            return r;
        }

        /// <summary>Human list "existing|new|temp" for error messages.</summary>
        public static string KeyList(IEnumerable<string> keys) => string.Join("|", keys ?? Enumerable.Empty<string>());
    }
}
