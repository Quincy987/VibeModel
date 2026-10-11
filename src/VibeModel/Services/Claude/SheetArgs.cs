using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Shared tokenizer for the sheet-assembly commands (plan 12): positional tokens first,
    /// then `--flag value...` where a flag's value runs until the next `--flag` (so multi-word
    /// names work unquoted, and `--at 100 200` collects two numbers). Double quotes group a
    /// token. Negative numbers ("-92722") are values, not flags. Flags may repeat (`--set`).
    /// Pure — unit-tested.
    /// </summary>
    internal sealed class FlagArgs
    {
        public readonly List<string> Positional = new List<string>();
        private readonly List<KeyValuePair<string, List<string>>> _flags = new List<KeyValuePair<string, List<string>>>();

        public static FlagArgs Parse(string args)
        {
            var r = new FlagArgs();
            List<string> current = null;
            foreach (var tok in Tokenize(args ?? ""))
            {
                if (IsFlag(tok))
                {
                    var name = tok.Substring(2);
                    string inline = null;
                    int eq = name.IndexOf('=');
                    if (eq >= 0) { inline = name.Substring(eq + 1); name = name.Substring(0, eq); }
                    current = new List<string>();
                    if (!string.IsNullOrEmpty(inline)) current.Add(inline);
                    r._flags.Add(new KeyValuePair<string, List<string>>(name.ToLowerInvariant(), current));
                }
                else if (current != null) current.Add(tok);
                else r.Positional.Add(tok);
            }
            return r;
        }

        private static bool IsFlag(string tok)
            => tok.Length > 2 && tok.StartsWith("--", StringComparison.Ordinal) && !char.IsDigit(tok[2]) && tok[2] != '.';

        internal static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false, any = false;
            foreach (var c in s)
            {
                if (c == '"') { inQuote = !inQuote; any = true; continue; }
                if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 || any) list.Add(sb.ToString());
                    sb.Clear(); any = false;
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0 || any) list.Add(sb.ToString());
            return list;
        }

        public bool Has(string flag) => _flags.Any(f => f.Key == flag);

        /// <summary>The flag's value tokens (last occurrence), or null when absent.</summary>
        public List<string> Values(string flag)
        {
            for (int i = _flags.Count - 1; i >= 0; i--)
                if (_flags[i].Key == flag) return _flags[i].Value;
            return null;
        }

        public IEnumerable<List<string>> All(string flag) => _flags.Where(f => f.Key == flag).Select(f => f.Value);

        public string Text(string flag)
        {
            var v = Values(flag);
            return v == null || v.Count == 0 ? null : string.Join(" ", v);
        }

        public IEnumerable<string> FlagNames => _flags.Select(f => f.Key);

        /// <summary>Flags that are not in the allowed set — reported, never silently ignored.</summary>
        public string Unknown(params string[] allowed)
        {
            var bad = _flags.Select(f => f.Key).Where(k => !allowed.Contains(k)).Distinct().ToList();
            return bad.Count == 0 ? null : "Unknown flag(s): " + string.Join(", ", bad.Select(b => "--" + b)) +
                                           ". Allowed: " + string.Join(", ", allowed.Select(a => "--" + a)) + ".";
        }

        public static bool TryNum(string s, out double v)
            => double.TryParse((s ?? "").Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        /// <summary>Parse exactly n numbers from a flag's tokens (commas allowed as separators).</summary>
        public bool TryNums(string flag, int n, out double[] nums, out string error)
        {
            nums = null; error = null;
            var v = Values(flag);
            if (v == null) return false;
            var parts = string.Join(" ", v).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != n) { error = "--" + flag + " needs " + n + " number(s), got '" + string.Join(" ", v) + "'."; return false; }
            nums = new double[n];
            for (int i = 0; i < n; i++)
                if (!TryNum(parts[i], out nums[i])) { error = "--" + flag + ": '" + parts[i] + "' is not a number."; return false; }
            return true;
        }

        public bool TryNum(string flag, out double v, out string error)
        {
            v = 0; error = null;
            if (!TryNums(flag, 1, out var a, out error)) return false;
            v = a[0];
            return true;
        }
    }

    /// <summary>`section x1 y1 x2 y2 | section &lt;wallId&gt;` + flags.</summary>
    internal sealed class SectionArgs
    {
        public double[] Line;          // x1 y1 x2 y2 (mm), or null when WallId is used
        public int? WallId;
        public double? Depth, Top, Bottom, Offset, Scale;
        public string Name, Template;
        public bool NoTemplate, Activate;
        public string Error;

        public const string Usage = "section <x1> <y1> <x2> <y2> | section <wallId> [--offset <mm>]  " +
                                    "[--depth <mm>] [--top <mm>] [--bottom <mm>] [--name <name>] [--scale <n>] " +
                                    "[--template <name>|--no-template] [--activate]";

        public static SectionArgs Parse(string args)
        {
            var r = new SectionArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("depth", "top", "bottom", "offset", "name", "scale", "template", "no-template", "activate");
            if (r.Error != null) return r;

            var pos = f.Positional.SelectMany(p => p.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (pos.Count == 1 && int.TryParse(pos[0], out int wid)) r.WallId = wid;
            else if (pos.Count == 4)
            {
                r.Line = new double[4];
                for (int i = 0; i < 4; i++)
                    if (!FlagArgs.TryNum(pos[i], out r.Line[i])) { r.Error = "'" + pos[i] + "' is not a number (mm)."; return r; }
                if (Math.Abs(r.Line[0] - r.Line[2]) < 1 && Math.Abs(r.Line[1] - r.Line[3]) < 1)
                { r.Error = "The section line has no length — the two points are the same."; return r; }
            }
            else { r.Error = "Give either 4 coordinates (x1 y1 x2 y2, mm) or one wall id."; return r; }

            if (!Opt(f, "depth", v => r.Depth = v, ref r.Error) || !Opt(f, "top", v => r.Top = v, ref r.Error) ||
                !Opt(f, "bottom", v => r.Bottom = v, ref r.Error) || !Opt(f, "offset", v => r.Offset = v, ref r.Error) ||
                !Opt(f, "scale", v => r.Scale = v, ref r.Error)) return r;
            if (r.Depth.HasValue && r.Depth <= 0) { r.Error = "--depth must be positive."; return r; }
            if (r.Scale.HasValue && (r.Scale < 1 || r.Scale != Math.Floor(r.Scale.Value))) { r.Error = "--scale is the n of 1:n (a whole number)."; return r; }
            if (r.Top.HasValue && r.Bottom.HasValue && r.Top <= r.Bottom) { r.Error = "--top must be above --bottom."; return r; }
            r.Name = f.Text("name");
            r.Template = f.Text("template");
            r.NoTemplate = f.Has("no-template");
            r.Activate = f.Has("activate");
            return r;
        }

        private static bool Opt(FlagArgs f, string flag, Action<double> set, ref string error)
        {
            if (!f.Has(flag)) return true;
            if (!f.TryNum(flag, out double v, out string err)) { error = err ?? ("--" + flag + " needs a number."); return false; }
            set(v);
            return true;
        }
    }

    /// <summary>`viewport &lt;sheet&gt; &lt;view&gt;[,&lt;view&gt;...]` + layout flags, or --move / --remove.</summary>
    internal sealed class ViewportArgs
    {
        public string Sheet;
        public List<string> Views = new List<string>();
        public double[] At;            // paper mm from the sheet's lower-left
        public double[] Area;          // x0 y0 x1 y1 paper mm
        public int? Scale;
        public int Cols, Rows;         // 0 = not given
        public double Margin = 10;
        public double? Reserve;        // mm kept free on the right for the title-block stamp
        public bool? Title;
        public int? MoveId; public double[] MoveTo;
        public int? RemoveId;
        public string Error;

        public const string Usage = "viewport <sheetNumber|sheetId> <viewId|name>[,<viewId|name>...] [--at <x> <y>] " +
                                    "[--grid <cols>x<rows>] [--area <x0> <y0> <x1> <y1>] [--scale <n>] [--margin <mm>] " +
                                    "[--reserve <mm>] [--title on|off]  |  viewport --move <viewportId> <x> <y>  |  viewport --remove <viewportId>";

        public static ViewportArgs Parse(string args)
        {
            var r = new ViewportArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("at", "grid", "area", "scale", "margin", "reserve", "title", "move", "remove");
            if (r.Error != null) return r;

            if (f.Has("move"))
            {
                var v = (f.Values("move") ?? new List<string>()).SelectMany(t => t.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
                if (v.Count != 3 || !int.TryParse(v[0], out int id) || !FlagArgs.TryNum(v[1], out double x) || !FlagArgs.TryNum(v[2], out double y))
                { r.Error = "Usage: viewport --move <viewportId> <x> <y>  (paper mm from the sheet's lower-left)"; return r; }
                r.MoveId = id; r.MoveTo = new[] { x, y };
                return r;
            }
            if (f.Has("remove"))
            {
                var v = f.Values("remove");
                if (v == null || v.Count != 1 || !int.TryParse(v[0], out int id)) { r.Error = "Usage: viewport --remove <viewportId>"; return r; }
                r.RemoveId = id;
                return r;
            }

            if (f.Positional.Count < 2) { r.Error = "Give a sheet (number or id) and at least one view (id or name)."; return r; }
            r.Sheet = f.Positional[0];
            r.Views = string.Join(" ", f.Positional.Skip(1)).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (r.Views.Count == 0) { r.Error = "No views given."; return r; }

            if (f.Has("at"))
            {
                if (!f.TryNums("at", 2, out r.At, out r.Error)) return r;
            }
            if (f.Has("area"))
            {
                if (!f.TryNums("area", 4, out r.Area, out r.Error)) return r;
                if (r.Area[2] <= r.Area[0] || r.Area[3] <= r.Area[1]) { r.Error = "--area is x0 y0 x1 y1 with x1 > x0 and y1 > y0."; return r; }
            }
            if (f.Has("grid"))
            {
                if (!TryGrid(f.Text("grid"), out r.Cols, out r.Rows))
                { r.Error = "--grid is <cols>x<rows>, e.g. 3x2."; return r; }
                if (r.Cols * r.Rows < r.Views.Count)
                { r.Error = "--grid " + r.Cols + "x" + r.Rows + " has " + r.Cols * r.Rows + " cells for " + r.Views.Count + " views."; return r; }
            }
            if (f.Has("scale"))
            {
                if (!f.TryNum("scale", out double s, out r.Error)) return r;
                if (s < 1 || s != Math.Floor(s)) { r.Error = "--scale is the n of 1:n (a whole number)."; return r; }
                r.Scale = (int)s;
            }
            if (f.Has("margin"))
            {
                if (!f.TryNum("margin", out double m, out r.Error)) return r;
                if (m < 0) { r.Error = "--margin cannot be negative."; return r; }
                r.Margin = m;
            }
            if (f.Has("reserve"))
            {
                if (!f.TryNum("reserve", out double m, out r.Error)) return r;
                if (m < 0) { r.Error = "--reserve cannot be negative."; return r; }
                r.Reserve = m;
            }
            if (f.Has("title"))
            {
                var t = (f.Text("title") ?? "").ToLowerInvariant();
                if (t == "on" || t == "yes" || t == "true") r.Title = true;
                else if (t == "off" || t == "no" || t == "false") r.Title = false;
                else { r.Error = "--title is on or off."; return r; }
            }
            if (r.At != null && r.Views.Count > 1 && r.Cols == 0)
            { r.Error = "--at places a single view; for several views use --grid (optionally inside --area)."; return r; }
            return r;
        }

        public static bool TryGrid(string s, out int cols, out int rows)
        {
            cols = rows = 0;
            var p = (s ?? "").Trim().ToLowerInvariant().Split('x', '×', '*');
            return p.Length == 2 && int.TryParse(p[0].Trim(), out cols) && int.TryParse(p[1].Trim(), out rows) &&
                   cols >= 1 && rows >= 1 && cols <= 12 && rows <= 12;
        }
    }

    internal enum DimDirection { Auto, X, Y }

    /// <summary>`dimension id id ... | dimension x1 y1 x2 y2 | dimension --levels [x]`.</summary>
    internal sealed class DimensionArgs
    {
        public List<int> Ids = new List<int>();
        public double[] Points;        // x1 y1 x2 y2 (mm, plan views)
        public bool Levels;
        public double? LevelsX;
        public double? Offset;
        public DimDirection Dir = DimDirection.Auto;
        public string Error;

        public const string Usage = "dimension <elementId> [<elementId>...] [--dir x|y|auto] [--offset <mm>]  |  " +
                                    "dimension --points <x1> <y1> <x2> <y2>  |  dimension --levels [<x>]";

        public static DimensionArgs Parse(string args)
        {
            var r = new DimensionArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("dir", "offset", "levels", "points");
            if (r.Error != null) return r;

            if (f.Has("offset"))
            {
                if (!f.TryNum("offset", out double o, out r.Error)) return r;
                r.Offset = o;
            }
            if (f.Has("dir"))
            {
                var d = (f.Text("dir") ?? "").ToLowerInvariant();
                if (d == "x" || d == "h" || d == "horizontal") r.Dir = DimDirection.X;
                else if (d == "y" || d == "v" || d == "vertical") r.Dir = DimDirection.Y;
                else if (d == "auto") r.Dir = DimDirection.Auto;
                else { r.Error = "--dir is x, y or auto (x/y are the view's horizontal/vertical)."; return r; }
            }
            if (f.Has("levels"))
            {
                r.Levels = true;
                var v = f.Values("levels");
                if (v.Count > 0)
                {
                    if (v.Count != 1 || !FlagArgs.TryNum(v[0], out double x)) { r.Error = "--levels takes at most one number (x position, mm)."; return r; }
                    r.LevelsX = x;
                }
                if (f.Positional.Count > 0) { r.Error = "--levels takes no element ids."; return r; }
                return r;
            }
            if (f.Has("points"))
            {
                if (!f.TryNums("points", 4, out r.Points, out r.Error)) return r;
                if (Math.Abs(r.Points[0] - r.Points[2]) < 1 && Math.Abs(r.Points[1] - r.Points[3]) < 1)
                { r.Error = "The two points are the same."; return r; }
                return r;
            }

            var pos = f.Positional.SelectMany(p => p.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (pos.Count == 0) { r.Error = "Give element ids, --points x1 y1 x2 y2, or --levels."; return r; }
            foreach (var p in pos)
            {
                if (!int.TryParse(p, out int id)) { r.Error = "'" + p + "' is not an element id (for coordinates use --points)."; return r; }
                if (!r.Ids.Contains(id)) r.Ids.Add(id);
            }
            return r;
        }
    }

    /// <summary>`text x y the text... [--size mm] [--leader ex ey] [--type name]`.</summary>
    internal sealed class TextArgs
    {
        public double X, Y;
        public string Text;
        public double? Size;
        public double[] Leader;
        public string Type;
        public string Error;

        public const string Usage = "text <x> <y> <text...> [--size <paper mm>] [--leader <x> <y>] [--type <text type name>]";

        public static TextArgs Parse(string args)
        {
            var r = new TextArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("size", "leader", "type");
            if (r.Error != null) return r;
            if (f.Positional.Count < 3 || !FlagArgs.TryNum(f.Positional[0], out r.X) || !FlagArgs.TryNum(f.Positional[1], out r.Y))
            { r.Error = "Usage: " + Usage; return r; }
            // "\n" in the args is a line break (curl/batch lines cannot carry real newlines).
            r.Text = string.Join(" ", f.Positional.Skip(2)).Replace("\\n", "\n");
            if (f.Has("size"))
            {
                if (!f.TryNum("size", out double s, out r.Error)) return r;
                if (s <= 0 || s > 50) { r.Error = "--size is the text height on paper in mm (e.g. 2.5)."; return r; }
                r.Size = s;
            }
            if (f.Has("leader") && !f.TryNums("leader", 2, out r.Leader, out r.Error)) return r;
            r.Type = f.Text("type");
            return r;
        }
    }

    /// <summary>`tag id [--leader] [--at x y]` — no flags parses exactly as the old `tag id`.</summary>
    internal sealed class TagArgs
    {
        public int Id;
        public bool Leader;
        public double[] At;
        public string Error;

        public static TagArgs Parse(string args)
        {
            var r = new TagArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("leader", "at");
            if (r.Error != null) return r;
            if (f.Positional.Count != 1 || !int.TryParse(f.Positional[0], out r.Id))
            { r.Error = "Usage: tag <elementId> [--leader] [--at <x> <y>]"; return r; }
            r.Leader = f.Has("leader");
            if (r.Leader && f.Values("leader").Count > 0) { r.Error = "--leader takes no value (put --at <x> <y> for the tag position)."; return r; }
            if (f.Has("at") && !f.TryNums("at", 2, out r.At, out r.Error)) return r;
            return r;
        }
    }

    /// <summary>
    /// `sheet` flags: --name, --number, --set "Param=Value" (repeatable). What remains parses
    /// exactly as before ([titleBlockName] [viewId]).
    /// </summary>
    internal sealed class SheetCreateArgs
    {
        public string Remaining = "";
        public string Name, Number;
        public List<KeyValuePair<string, string>> Sets = new List<KeyValuePair<string, string>>();
        public string Error;

        public static SheetCreateArgs Parse(string args)
        {
            var r = new SheetCreateArgs();
            var a = args ?? "";
            if (a.IndexOf("--", StringComparison.Ordinal) < 0) { r.Remaining = a.Trim(); return r; }
            var f = FlagArgs.Parse(a);
            r.Error = f.Unknown("name", "number", "set");
            if (r.Error != null) return r;
            r.Remaining = string.Join(" ", f.Positional);
            r.Name = f.Text("name");
            r.Number = f.Text("number");
            foreach (var v in f.All("set"))
            {
                var s = string.Join(" ", v);
                int eq = s.IndexOf('=');
                if (eq <= 0) { r.Error = "--set takes \"Parameter=Value\", got '" + s + "'."; return r; }
                r.Sets.Add(new KeyValuePair<string, string>(s.Substring(0, eq).Trim(), s.Substring(eq + 1).Trim()));
            }
            return r;
        }
    }
}

namespace VibeModel.Services.Claude
{
    /// <summary>`crop &lt;view&gt; --fit [--margin mm] | --box x0 y0 x1 y1 | --off`.</summary>
    internal sealed class CropArgs
    {
        public string View;
        public bool Fit, Off;
        public double[] Box;
        public double Margin = 2000;
        public List<int> Elements = new List<int>();
        public string Error;

        public const string Usage = "crop <viewId|name> --fit [--elements <id,id...>] [--margin <mm>]  |  crop <view> --box <x0> <y0> <x1> <y1>  |  crop <view> --off";

        public static CropArgs Parse(string args)
        {
            var r = new CropArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("fit", "box", "off", "margin", "elements");
            if (r.Error != null) return r;
            r.View = string.Join(" ", f.Positional).Trim();
            if (r.View.Length == 0) { r.Error = "Give the view (id or name) first."; return r; }
            int modes = (f.Has("fit") ? 1 : 0) + (f.Has("box") ? 1 : 0) + (f.Has("off") ? 1 : 0);
            if (modes != 1) { r.Error = "Pick one of --fit, --box x0 y0 x1 y1, --off."; return r; }
            r.Fit = f.Has("fit"); r.Off = f.Has("off");
            if (f.Has("box"))
            {
                if (!f.TryNums("box", 4, out r.Box, out r.Error)) return r;
                if (r.Box[2] <= r.Box[0] || r.Box[3] <= r.Box[1]) { r.Error = "--box is x0 y0 x1 y1 with x1 > x0 and y1 > y0."; return r; }
            }
            if (f.Has("margin"))
            {
                if (!f.TryNum("margin", out double m, out r.Error)) return r;
                if (m < 0) { r.Error = "--margin cannot be negative."; return r; }
                r.Margin = m;
            }
            if (f.Has("elements"))
            {
                if (!r.Fit) { r.Error = "--elements goes with --fit."; return r; }
                foreach (var t in string.Join(" ", f.Values("elements")).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(t, out int id)) { r.Error = "--elements: '" + t + "' is not an element id."; return r; }
                    r.Elements.Add(id);
                }
            }
            return r;
        }
    }
}
