using System;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>Flag parsing shared by the link commands (`linkcad`, `linkrvt`).</summary>
    internal static class LinkArgs
    {
        public static string TakeValue(ref string s, string flag)
        {
            var m = Regex.Match(s, @"(?:^|\s)" + Regex.Escape(flag) + @"(?:=|\s+)(?<v>""[^""]+""|\S+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            s = s.Remove(m.Index, m.Length).Trim();
            return m.Groups["v"].Value.Trim('"');
        }

        public static bool TakeFlag(ref string s, string flag)
        {
            var i = s.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            s = (s.Substring(0, i) + s.Substring(i + flag.Length)).Trim();
            return true;
        }
    }
}
