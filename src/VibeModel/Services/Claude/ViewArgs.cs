using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// The office-default flags of the `view` command: --template &lt;name&gt;, --no-template,
    /// --name &lt;view name&gt;, --activate — and the camera flags of `view 3d`: --look &lt;dir|x y z&gt;,
    /// --at, --distance, --pitch, --margin, --box (see <see cref="LookSpec"/>). A value runs until the next "--" flag (or may be quoted), so
    /// multi-word names work unquoted. Without flags the args come back unchanged, so
    /// `view plan Level 2` parses exactly as before. Pure — unit-tested.
    /// </summary>
    internal sealed class ViewArgs
    {
        private const string Value = @"(?<v>""[^""]*""|.+?)(?=\s+--|\s*$)";
        private static readonly Regex TemplateFlag = new Regex(@"(?:^|\s)--template(?:=|\s+)" + Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NameFlag = new Regex(@"(?:^|\s)--name(?:=|\s+)" + Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LookFlag = Valued("look");
        private static readonly Regex AtFlag = Valued("at");
        private static readonly Regex DistanceFlag = Valued("distance");
        private static readonly Regex PitchFlag = Valued("pitch");
        private static readonly Regex MarginFlag = Valued("margin");
        private static readonly Regex BoxFlag = new Regex(@"(?:^|\s)--box(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NoTemplateFlag = new Regex(@"(?:^|\s)--no-template(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ActivateFlag = new Regex(@"(?:^|\s)--activate(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Remaining { get; private set; }
        public string Template { get; private set; }
        public bool NoTemplate { get; private set; }
        public string Name { get; private set; }
        public bool Activate { get; private set; }
        public string Look { get; private set; }
        public string At { get; private set; }
        public string Distance { get; private set; }
        public string Pitch { get; private set; }
        public string Margin { get; private set; }
        public bool Box { get; private set; }

        /// <summary>Any camera flag given (they only make sense with --look on `view 3d`).</summary>
        public bool AnyCameraFlag => Look != null || At != null || Distance != null || Pitch != null || Margin != null || Box;

        private static Regex Valued(string flag)
            => new Regex(@"(?:^|\s)--" + flag + @"(?:=|\s+)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static ViewArgs Parse(string args)
        {
            var r = new ViewArgs { Remaining = args ?? "" };
            if (r.Remaining.IndexOf("--", System.StringComparison.Ordinal) < 0) return r;

            var rest = r.Remaining;
            r.Template = Take(ref rest, TemplateFlag);
            r.Name = Take(ref rest, NameFlag);
            r.Look = Take(ref rest, LookFlag);
            r.At = Take(ref rest, AtFlag);
            r.Distance = Take(ref rest, DistanceFlag);
            r.Pitch = Take(ref rest, PitchFlag);
            r.Margin = Take(ref rest, MarginFlag);
            var bm = BoxFlag.Match(rest);
            if (bm.Success)
            {
                r.Box = true;
                rest = rest.Remove(bm.Index, bm.Length);
            }
            var am = ActivateFlag.Match(rest);
            if (am.Success)
            {
                r.Activate = true;
                rest = rest.Remove(am.Index, am.Length);
            }
            var nm = NoTemplateFlag.Match(rest);
            if (nm.Success)
            {
                r.NoTemplate = true;
                rest = rest.Remove(nm.Index, nm.Length);
            }
            r.Remaining = Regex.Replace(rest, @"\s{2,}", " ").Trim();
            return r;
        }

        private static string Take(ref string s, Regex rx)
        {
            var m = rx.Match(s);
            if (!m.Success) return null;
            s = s.Remove(m.Index, m.Length);
            var v = m.Groups["v"].Value.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v.Length == 0 ? null : v;
        }
    }
}
