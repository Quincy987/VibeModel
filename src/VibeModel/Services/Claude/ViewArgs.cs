using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// The office-default flags of the `view` command: --template &lt;name&gt;, --no-template,
    /// --name &lt;view name&gt;, --activate. A value runs until the next "--" flag (or may be quoted), so
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
        private static readonly Regex NoTemplateFlag = new Regex(@"(?:^|\s)--no-template(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ActivateFlag = new Regex(@"(?:^|\s)--activate(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Remaining { get; private set; }
        public string Template { get; private set; }
        public bool NoTemplate { get; private set; }
        public string Name { get; private set; }
        public bool Activate { get; private set; }

        public static ViewArgs Parse(string args)
        {
            var r = new ViewArgs { Remaining = args ?? "" };
            if (r.Remaining.IndexOf("--", System.StringComparison.Ordinal) < 0) return r;

            var rest = r.Remaining;
            r.Template = Take(ref rest, TemplateFlag);
            r.Name = Take(ref rest, NameFlag);
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
