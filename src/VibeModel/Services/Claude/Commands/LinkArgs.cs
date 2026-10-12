using System;
using System.Text.RegularExpressions;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>Argument parsing, file probing and placement wording shared by the link commands (`linkcad`, `linkrvt`).</summary>
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

        /// <summary>
        /// Why a link goes in by shared coordinates, naming the criterion: the model has them,
        /// shown as what the project base point reads (or the site, when the base point sits at 0,0).
        /// </summary>
        public static string SharedPlacementWhy(CoordinateFacts c)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var evidence = Math.Abs(c.BaseNorthMm) < 0.5 && Math.Abs(c.BaseEastMm) < 0.5
                ? "shared coordinates acquired (site '" + c.SiteName + "')"
                : "base point N/S " + Math.Round(c.BaseNorthMm).ToString(ci) + ", E/W " + Math.Round(c.BaseEastMm).ToString(ci) + " mm ≠ 0";
            return "Auto – By Shared Coordinates (coordinates set up: " + evidence + ")";
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
