using System;
using System.Text.RegularExpressions;
using VibeModel.Services.Standards;

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

        /// <summary>The error for a file that cannot be linked (share silent, file missing), or null when it exists.</summary>
        public static CommandResult ProbeFile(string path)
        {
            var exists = PathProbe.FileExists(path);
            if (exists == null)
                return CommandResult.Error("SHARE_UNREACHABLE", "The location did not answer within 3 s: " + path, "Is the network drive reachable?");
            if (exists == false)
                return CommandResult.Error("FILE_NOT_FOUND", "File not found: " + path, null);
            return null;
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
