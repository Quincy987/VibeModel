using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Picks one named thing (a wall type, a floor type, a level) from what the document has:
    /// an id, else the exact name, else the exact name ignoring case, else a unique
    /// case-insensitive substring. Ambiguous or missing → an error that lists the candidates,
    /// so the chat can retry with the right name. Pure — unit-tested.
    /// </summary>
    internal static class NameMatch
    {
        public sealed class Item
        {
            public int Id;
            public string Name;
            public string Detail;     // e.g. "Stacked" — tells same-named items apart in a list
            public Item(int id, string name, string detail = null) { Id = id; Name = name ?? ""; Detail = detail; }
        }

        public const int MaxListed = 25;

        /// <param name="what">"wall type", "level", … — used in the error.</param>
        public static Item Find(IEnumerable<Item> items, string query, string what, out string error)
        {
            error = null;
            var all = items.ToList();
            var q = (query ?? "").Trim();
            if (q.Length == 0) { error = "No " + what + " given."; return null; }

            if (int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                var byId = all.FirstOrDefault(i => i.Id == id);
                if (byId != null) return byId;
            }

            var exact = all.Where(i => i.Name == q).ToList();
            if (exact.Count == 1) return exact[0];
            var exactCi = all.Where(i => i.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactCi.Count == 1) return exactCi[0];

            var sub = all.Where(i => i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (sub.Count == 1) return sub[0];

            if (sub.Count > 1)
                error = "'" + q + "' matches " + sub.Count + " " + what + "s: " + List(sub) + ". Give the full name or the id.";
            else
                error = "No " + what + " matches '" + q + "'. Available: " + (all.Count == 0 ? "(none)" : List(all)) + ".";
            return null;
        }

        private static string List(IEnumerable<Item> items)
        {
            var l = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var shown = l.Take(MaxListed).Select(i => "'" + i.Name + "' (" + i.Id + (string.IsNullOrEmpty(i.Detail) ? "" : ", " + i.Detail) + ")");
            return string.Join(", ", shown) + (l.Count > MaxListed ? " … and " + (l.Count - MaxListed) + " more" : "");
        }
    }

    /// <summary>"Param=value" — the shape of every --set. Pure.</summary>
    internal static class ParamAssign
    {
        public static bool TryParse(string s, out string name, out string value)
        {
            name = value = null;
            s = (s ?? "").Trim();
            int eq = s.IndexOf('=');
            if (eq <= 0) return false;
            name = s.Substring(0, eq).Trim();
            value = Unquote(s.Substring(eq + 1).Trim());
            return name.Length > 0;
        }

        public static string Unquote(string s)
        {
            s = s ?? "";
            return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
        }
    }

    /// <summary>
    /// `wall x1 y1 x2 y2 [height] [--type t] [--level l] [--base-offset mm] [--top-level l] [--top-offset mm]`.
    /// Without flags the positional form parses exactly as before (spaces and commas separate).
    /// </summary>
    internal sealed class WallArgs
    {
        public double X1, Y1, X2, Y2;
        public double? Height;
        public string Type, Level, TopLevel;
        public double? BaseOffset, TopOffset;
        public string Error;

        public const string Usage = "wall <x1> <y1> <x2> <y2> [height] [--type <wall type name|id>] [--level <name>] " +
                                    "[--base-offset <mm>] [--top-level <name>] [--top-offset <mm>] [--phase <kind>]";

        public static WallArgs Parse(string args)
        {
            var r = new WallArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("type", "level", "base-offset", "top-level", "top-offset");
            if (r.Error != null) return r;

            var pos = f.Positional.SelectMany(p => p.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (pos.Count < 4) { r.Error = "Usage: wall <x1> <y1> <x2> <y2> [height_mm]\n\nCoordinates in mm. Default height is 4000mm."; return r; }
            if (!FlagArgs.TryNum(pos[0], out r.X1) || !FlagArgs.TryNum(pos[1], out r.Y1) ||
                !FlagArgs.TryNum(pos[2], out r.X2) || !FlagArgs.TryNum(pos[3], out r.Y2))
            { r.Error = "Invalid coordinates. Use numbers in mm."; return r; }
            if (Math.Abs(r.X1 - r.X2) < 0.1 && Math.Abs(r.Y1 - r.Y2) < 0.1)
            { r.Error = "Start and end points are identical. Wall needs length."; return r; }
            if (pos.Count >= 5 && FlagArgs.TryNum(pos[4], out double h)) r.Height = h;

            r.Type = f.Text("type");
            r.Level = f.Text("level");
            r.TopLevel = f.Text("top-level");
            if (f.Has("type") && r.Type == null) { r.Error = "--type needs a wall type name or id ('familytypes wall' lists them)."; return r; }
            if (f.Has("level") && r.Level == null) { r.Error = "--level needs a level name."; return r; }
            if (f.Has("top-level") && r.TopLevel == null) { r.Error = "--top-level needs a level name."; return r; }
            if (!OptNum(f, "base-offset", v => r.BaseOffset = v, ref r.Error) || !OptNum(f, "top-offset", v => r.TopOffset = v, ref r.Error)) return r;
            if (r.TopOffset.HasValue && r.TopLevel == null)
            { r.Error = "--top-offset needs --top-level <name>: an unconnected wall has a height, not a top offset."; return r; }
            if (r.TopLevel != null && r.Height.HasValue)
            { r.Error = "Give either a height or --top-level, not both."; return r; }
            if (r.Height.HasValue && r.Height <= 0) { r.Error = "The height must be positive."; return r; }
            return r;
        }

        internal static bool OptNum(FlagArgs f, string flag, Action<double> set, ref string error)
        {
            if (!f.Has(flag)) return true;
            if (!f.TryNum(flag, out double v, out string err)) { error = err ?? ("--" + flag + " needs a number."); return false; }
            set(v);
            return true;
        }
    }

    /// <summary>`floor &lt;x,y&gt;... [--type t] [--level l]`. The points stay exactly as before.</summary>
    internal sealed class FloorArgs
    {
        public string Points = "";
        public string Type, Level;
        public string Error;

        public const string Usage = "floor <x1,y1> <x2,y2> <x3,y3> [<x4,y4>] ... [--type <floor type name|id>] [--level <name>] [--phase <kind>]";

        public static FloorArgs Parse(string args)
        {
            var r = new FloorArgs();
            var a = args ?? "";
            if (a.IndexOf("--", StringComparison.Ordinal) < 0) { r.Points = a; return r; }
            var f = FlagArgs.Parse(a);
            r.Error = f.Unknown("type", "level");
            if (r.Error != null) return r;
            r.Points = string.Join(" ", f.Positional);
            r.Type = f.Text("type");
            r.Level = f.Text("level");
            if (f.Has("type") && r.Type == null) r.Error = "--type needs a floor type name or id ('familytypes floor' lists them).";
            else if (f.Has("level") && r.Level == null) r.Error = "--level needs a level name.";
            return r;
        }
    }

    /// <summary>
    /// `place &lt;family&gt; &lt;type&gt; &lt;x&gt; &lt;y&gt; [z]` / `placeid &lt;typeId&gt; &lt;x&gt; &lt;y&gt; [z]`
    /// plus --rotate, --set (repeatable), --level, and --along (with --spacing/--count/--offset)
    /// instead of x y. Pure — unit-tested.
    /// </summary>
    internal sealed class PlaceArgs
    {
        public string Family, TypeName;
        public int TypeId;
        public double X, Y;
        public double? Z;
        public double Rotate;                 // degrees, counter-clockwise from +X
        public bool AutoRotate;               // --rotate auto[+/-n]: follow the line, plus Rotate
        public string Level;
        public readonly List<KeyValuePair<string, string>> Sets = new List<KeyValuePair<string, string>>();

        public bool Along;
        public int? AlongId;                  // detail/model line (or any element with a location curve)
        public double[] AlongLine;            // x1 y1 x2 y2 mm
        public double? Spacing;
        public int? Count;
        public double Offset;
        public string Error;

        public const int MaxCount = 1000;

        public const string Flags = "[--rotate <deg>] [--set \"Param=value\"]... [--level <name>]  |  " +
                                    "{0} --along <lineId | x1 y1 x2 y2> --spacing <mm> [--count <n>] [--offset <mm>] [--rotate <deg>|auto|auto+90] [--set …] [--level <name>]";
        public static readonly string PlaceUsage = "place <family> <type> <x> <y> [z] " + string.Format(Flags, "place <family> <type>") + " [--phase <kind>]";
        public static readonly string PlaceIdUsage = "placeid <typeId> <x> <y> [z] " + string.Format(Flags, "placeid <typeId>") + " [--phase <kind>]";

        public static PlaceArgs Parse(string args, bool byId)
        {
            var r = new PlaceArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("rotate", "set", "level", "along", "spacing", "count", "offset");
            if (r.Error != null) return r;

            var pos = f.Positional;
            int head = byId ? 1 : 2;
            r.Along = f.Has("along");
            if (pos.Count < head + (r.Along ? 0 : 2))
            {
                r.Error = byId ? "Usage: placeid <typeId> <x_mm> <y_mm> [z_mm]  (or --along …)"
                               : "Usage: place <familyName> <typeName> <x_mm> <y_mm> [z_mm]  (or --along …)\n\nUse 'familytypes' to see available families and types.";
                return r;
            }
            if (byId)
            {
                if (!int.TryParse(pos[0], out r.TypeId)) { r.Error = "Invalid type ID"; return r; }
            }
            else { r.Family = pos[0]; r.TypeName = pos[1]; }

            if (r.Along)
            {
                if (pos.Count > head) { r.Error = "With --along, give no x y: the positions come from the line."; return r; }
            }
            else
            {
                if (!FlagArgs.TryNum(pos[head], out r.X) || !FlagArgs.TryNum(pos[head + 1], out r.Y)) { r.Error = "Invalid coordinates"; return r; }
                // As before: a non-numeric 5th token is ignored, not an error.
                if (pos.Count > head + 2 && FlagArgs.TryNum(pos[head + 2], out double z)) r.Z = z;
                foreach (var flag in new[] { "spacing", "count", "offset" })
                    if (f.Has(flag)) { r.Error = "--" + flag + " only works with --along."; return r; }
            }

            if (f.Has("rotate") && !ParseRotate(f.Text("rotate"), r)) return r;
            if (r.AutoRotate && !r.Along) { r.Error = "--rotate auto follows a line; it needs --along. Give degrees instead (e.g. --rotate 30)."; return r; }

            r.Level = f.Text("level");
            if (f.Has("level") && r.Level == null) { r.Error = "--level needs a level name."; return r; }

            foreach (var v in f.All("set"))
            {
                var s = string.Join(" ", v);
                if (!ParamAssign.TryParse(s, out var name, out var value))
                { r.Error = "--set takes \"Parameter=value\", got '" + s + "'."; return r; }
                r.Sets.Add(new KeyValuePair<string, string>(name, value));
            }

            if (r.Along && !ParseAlong(f, r)) return r;
            return r;
        }

        private static bool ParseRotate(string text, PlaceArgs r)
        {
            var t = (text ?? "").Trim().ToLowerInvariant().Replace("°", "").Replace("deg", "").Replace(" ", "");
            if (t.Length == 0) { r.Error = "--rotate needs degrees (e.g. --rotate 30) or auto."; return false; }
            if (t.StartsWith("auto", StringComparison.Ordinal))
            {
                r.AutoRotate = true;
                var extra = t.Substring(4);
                if (extra.Length == 0) return true;
                if (FlagArgs.TryNum(extra, out double e)) { r.Rotate = e; return true; }   // "+90" / "-90"
                r.Error = "--rotate " + text + ": use auto, auto+90 or auto-90.";
                return false;
            }
            if (!FlagArgs.TryNum(t, out double deg)) { r.Error = "--rotate '" + text + "' is not a number of degrees."; return false; }
            r.Rotate = deg;
            return true;
        }

        private static bool ParseAlong(FlagArgs f, PlaceArgs r)
        {
            var parts = string.Join(" ", f.Values("along")).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out int id)) r.AlongId = id;
            else if (parts.Length == 4)
            {
                r.AlongLine = new double[4];
                for (int i = 0; i < 4; i++)
                    if (!FlagArgs.TryNum(parts[i], out r.AlongLine[i])) { r.Error = "--along: '" + parts[i] + "' is not a number (mm)."; return false; }
                if (Math.Abs(r.AlongLine[0] - r.AlongLine[2]) < 1 && Math.Abs(r.AlongLine[1] - r.AlongLine[3]) < 1)
                { r.Error = "--along: the line has no length — the two points are the same."; return false; }
            }
            else { r.Error = "--along takes a line id (detail line, model line, wall…) or x1 y1 x2 y2 in mm."; return false; }

            string err = null;
            if (!WallArgs.OptNum(f, "spacing", v => r.Spacing = v, ref err) || !WallArgs.OptNum(f, "offset", v => r.Offset = v, ref err))
            { r.Error = err; return false; }
            if (f.Has("count"))
            {
                if (!f.TryNum("count", out double c, out err) || c < 1 || c != Math.Floor(c))
                { r.Error = "--count is a whole number ≥ 1."; return false; }
                if (c > MaxCount) { r.Error = "--count is at most " + MaxCount + "."; return false; }
                r.Count = (int)c;
            }
            if (!r.Spacing.HasValue && !r.Count.HasValue) { r.Error = "--along needs --spacing <mm> and/or --count <n>."; return false; }
            if (r.Spacing.HasValue && r.Spacing <= 0) { r.Error = "--spacing must be positive."; return false; }
            if (r.Offset < 0) { r.Error = "--offset is a distance from the line's ends; it cannot be negative."; return false; }
            return true;
        }
    }

    /// <summary>
    /// Where instances go along a line or arc of a given length (all mm, measured along the
    /// curve from its start). Pure — unit-tested.
    ///   --spacing s           : fill from offset to (length − offset); the spacing is reduced so the
    ///                           ends both get an instance (never more than asked, 1 % slack).
    ///   --count n             : n instances evenly from offset to (length − offset); 1 → the middle.
    ///   --spacing s --count n : exactly s apart, starting at offset; refused if it does not fit.
    /// </summary>
    internal static class AlongLayout
    {
        public static List<double> Distances(double length, double? spacing, int? count, double offset,
            out double actualSpacing, out string error)
        {
            actualSpacing = 0; error = null;
            var d = new List<double>();
            const double tol = 0.5;   // mm

            if (spacing.HasValue && count.HasValue)
            {
                double need = offset + (count.Value - 1) * spacing.Value;
                if (need > length + tol)
                {
                    error = count.Value + " × " + Fmt(spacing.Value) + " mm from " + Fmt(offset) + " mm needs " + Fmt(need) +
                            " mm, the line is " + Fmt(length) + " mm. Lower --count or --spacing.";
                    return null;
                }
                for (int i = 0; i < count.Value; i++) d.Add(offset + i * spacing.Value);
                actualSpacing = count.Value > 1 ? spacing.Value : 0;
                return d;
            }

            double usable = length - 2 * offset;
            if (usable < -tol) { error = "--offset " + Fmt(offset) + " mm from both ends is more than the line (" + Fmt(length) + " mm)."; return null; }
            usable = Math.Max(0, usable);

            int n;
            if (count.HasValue) n = count.Value;
            else
            {
                double q = usable / spacing.Value;
                double fl = Math.Floor(q);
                int gaps = q - fl < 0.01 ? (int)fl : (int)Math.Ceiling(q);
                n = gaps + 1;
                if (n > PlaceArgs.MaxCount)
                {
                    error = "That is " + n + " instances (" + Fmt(length) + " mm at " + Fmt(spacing.Value) + " mm); at most " + PlaceArgs.MaxCount + ".";
                    return null;
                }
            }

            if (n == 1) { d.Add(length / 2); return d; }
            if (usable <= 0) { error = "No room for " + n + " instances: --offset " + Fmt(offset) + " mm from both ends leaves nothing of the " + Fmt(length) + " mm line."; return null; }
            actualSpacing = usable / (n - 1);
            for (int i = 0; i < n; i++) d.Add(offset + i * actualSpacing);
            return d;
        }

        /// <summary>The point at distance d (mm) along the straight line x1,y1 → x2,y2.</summary>
        public static double[] LinePoint(double x1, double y1, double x2, double y2, double d)
        {
            double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            double t = len < 1e-9 ? 0 : d / len;
            return new[] { x1 + t * (x2 - x1), y1 + t * (y2 - y1) };
        }

        /// <summary>The rotation (degrees, 0..360, counter-clockwise from +X) that lines a family up with a direction, plus an extra turn.</summary>
        public static double AngleDeg(double dx, double dy, double extraDeg)
            => Normalize(Math.Atan2(dy, dx) * 180 / Math.PI + extraDeg);

        public static double Normalize(double deg)
        {
            var a = deg % 360;
            if (a < 0) a += 360;
            if (a > 359.9999995) a = 0;
            return a;
        }

        private static string Fmt(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// `set &lt;id&gt; &lt;param&gt; &lt;value&gt;` — which (name, value) splits to try, in order. The command
    /// takes the first whose name is a parameter on the element, so all of these work:
    ///   set 123 Comments Hello World           (3-token form, as before)
    ///   set 123 "Base Offset" 500               (quoted name)
    ///   set 123 Base Offset=500                 (Param=value)
    ///   set 123 Base Offset 500                 (unquoted name with spaces: longest matching name wins)
    /// Pure — unit-tested.
    /// </summary>
    internal static class SetArgs
    {
        public const string Usage = "set <elementId> <paramName> <value>  |  set <elementId> \"<param name>\" <value>  |  set <elementId> <param name>=<value>";

        public static bool TryParseId(string args, out int id, out string rest)
        {
            id = 0; rest = "";
            var a = (args ?? "").Trim();
            int sp = a.IndexOf(' ');
            var head = sp < 0 ? a : a.Substring(0, sp);
            rest = sp < 0 ? "" : a.Substring(sp + 1).Trim();
            return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        public static List<KeyValuePair<string, string>> Candidates(string rest)
        {
            var list = new List<KeyValuePair<string, string>>();
            rest = (rest ?? "").Trim();
            if (rest.Length == 0) return list;

            if (rest[0] == '"')
            {
                int close = rest.IndexOf('"', 1);
                if (close > 1)
                {
                    var name = rest.Substring(1, close - 1).Trim();
                    var after = rest.Substring(close + 1).TrimStart();
                    if (after.StartsWith("=", StringComparison.Ordinal)) after = after.Substring(1).TrimStart();
                    list.Add(new KeyValuePair<string, string>(name, ParamAssign.Unquote(after.Trim())));
                    return list;
                }
            }

            if (ParamAssign.TryParse(rest, out var n, out var v) && n.IndexOf('"') < 0)
                list.Add(new KeyValuePair<string, string>(n, v));

            var words = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int k = words.Length - 1; k >= 1; k--)
            {
                var name = string.Join(" ", words.Take(k));
                // The value keeps its own spacing: everything after the k-th word.
                var value = ValueAfterWords(rest, k);
                if (!list.Any(c => c.Key == name && c.Value == value))
                    list.Add(new KeyValuePair<string, string>(name, ParamAssign.Unquote(value)));
            }
            return list;
        }

        private static string ValueAfterWords(string s, int k)
        {
            int i = 0;
            for (int w = 0; w < k; w++)
            {
                while (i < s.Length && s[i] == ' ') i++;
                while (i < s.Length && s[i] != ' ') i++;
            }
            return s.Substring(i).Trim();
        }
    }

    /// <summary>
    /// Camera maths for `look` / `view 3d --look`. A compass word says where the camera STANDS
    /// relative to the target ("zw" = south-west of it, looking north-east), tilted down by the
    /// pitch. Dutch and English letters both work. Vectors are plain double[3] (x east, y north,
    /// z up). Pure — unit-tested.
    /// </summary>
    internal static class CameraMath
    {
        public const double DefaultPitch = 30;

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "n", "n" }, { "noord", "n" }, { "north", "n" },
            { "no", "no" }, { "ne", "no" }, { "noordoost", "no" }, { "northeast", "no" },
            { "o", "o" }, { "e", "o" }, { "oost", "o" }, { "east", "o" },
            { "zo", "zo" }, { "se", "zo" }, { "zuidoost", "zo" }, { "southeast", "zo" },
            { "z", "z" }, { "s", "z" }, { "zuid", "z" }, { "south", "z" },
            { "zw", "zw" }, { "sw", "zw" }, { "zuidwest", "zw" }, { "southwest", "zw" },
            { "w", "w" }, { "west", "w" },
            { "nw", "nw" }, { "noordwest", "nw" }, { "northwest", "nw" },
            { "top", "top" }, { "boven", "top" }, { "plan", "top" },
            { "iso", "iso" }
        };

        // Where the camera stands, seen from the target (unit-ish, normalised below).
        private static readonly Dictionary<string, double[]> Compass = new Dictionary<string, double[]>
        {
            { "n", new[] { 0.0, 1 } }, { "no", new[] { 1.0, 1 } }, { "o", new[] { 1.0, 0 } }, { "zo", new[] { 1.0, -1 } },
            { "z", new[] { 0.0, -1 } }, { "zw", new[] { -1.0, -1 } }, { "w", new[] { -1.0, 0 } }, { "nw", new[] { -1.0, 1 } }
        };

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "n", "north" }, { "no", "north-east" }, { "o", "east" }, { "zo", "south-east" },
            { "z", "south" }, { "zw", "south-west" }, { "w", "west" }, { "nw", "north-west" }
        };

        public static string Canonical(string key)
            => key != null && Aliases.TryGetValue(key.Trim(), out var k) ? k : null;

        public const string Keys = "n|no|o|zo|z|zw|w|nw|top|iso (English ne/e/se/s/sw also work)";

        /// <summary>Unit view direction (camera → target) for a canonical key; null when unknown.</summary>
        public static double[] Forward(string canonical, double pitchDeg)
        {
            if (canonical == "top") return new[] { 0.0, 0, -1 };
            if (canonical == "iso") return Unit(new[] { -1.0, 1, -1 });   // Revit's default 3D: from the south-east, above
            if (canonical == null || !Compass.TryGetValue(canonical, out var c)) return null;
            double p = pitchDeg * Math.PI / 180;
            var h = Unit(new[] { -c[0], -c[1], 0 });
            return Unit(new[] { h[0] * Math.Cos(p), h[1] * Math.Cos(p), -Math.Sin(p) });
        }

        /// <summary>Screen-up for a view direction: world up made perpendicular; north for a straight-down view.</summary>
        public static double[] Up(double[] f)
        {
            if (Math.Abs(f[2]) > 0.9999) return new[] { 0.0, 1, 0 };
            return Unit(new[] { -f[0] * f[2], -f[1] * f[2], 1 - f[2] * f[2] });
        }

        public static double[] Eye(double[] target, double[] forward, double distance)
            => new[] { target[0] - forward[0] * distance, target[1] - forward[1] * distance, target[2] - forward[2] * distance };

        /// <summary>"from the south-west, looking north-east, 30° down" — for the response.</summary>
        public static string Describe(double[] f)
        {
            if (f[2] < -0.9999) return "straight down (top view, north up)";
            double from = Math.Atan2(-f[1], -f[0]) * 180 / Math.PI;      // bearing of the camera from the target, CCW from east
            double pitch = Math.Asin(Math.Max(-1, Math.Min(1, -f[2]))) * 180 / Math.PI;
            return "from the " + CompassName(from) + ", looking " + CompassName(from + 180) + ", " +
                   Math.Round(pitch).ToString(CultureInfo.InvariantCulture) + "° " + (pitch >= 0 ? "down" : "up");
        }

        private static string CompassName(double ccwFromEastDeg)
        {
            var keys = new[] { "o", "no", "n", "nw", "w", "zw", "z", "zo" };
            int i = (int)Math.Round(AlongLayout.Normalize(ccwFromEastDeg) / 45) % 8;
            return Names[keys[i]];
        }

        public static double[] Unit(double[] v)
        {
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return n < 1e-12 ? v : new[] { v[0] / n, v[1] / n, v[2] / n };
        }
    }

    /// <summary>
    /// The camera request shared by `look` and `view 3d --look`: a compass key or a camera
    /// position (x y z mm), and the target (--at x y z | ids | selected; default: the model).
    /// Pure — unit-tested.
    /// </summary>
    internal sealed class LookSpec
    {
        public string Key;                 // canonical compass key, or null when Eye is given
        public double[] Eye;               // camera position, mm
        public List<double> AtNumbers;     // raw --at numbers: 3 → maybe a point, else element ids
        public bool AtSelected;
        public double? Distance;
        public double Pitch = CameraMath.DefaultPitch;
        public double Margin = 2000;
        public bool Box;
        public string Error;

        public const string FlagUsage = "[--at <x y z | id,id… | selected>] [--distance <mm>] [--pitch <deg>] [--margin <mm>] [--box]";
        public static readonly string LookUsage = "look [<3D view id|name>] <" + CameraMath.Keys.Split(' ')[0] + "|x y z> " + FlagUsage;

        /// <summary>direction = a compass word or "x y z" (the camera position, mm).</summary>
        public static LookSpec FromParts(string direction, string at, string distance, string pitch, string margin, bool box)
        {
            var r = new LookSpec { Box = box };
            var d = (direction ?? "").Trim();
            var parts = d.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && CameraMath.Canonical(parts[0]) != null) r.Key = CameraMath.Canonical(parts[0]);
            else if (parts.Length == 3 && parts.All(p => FlagArgs.TryNum(p, out _)))
                r.Eye = parts.Select(p => { FlagArgs.TryNum(p, out var v); return v; }).ToArray();
            else { r.Error = "Direction '" + d + "' is not one of " + CameraMath.Keys + ", nor a camera position x y z (mm)."; return r; }

            if (at != null)
            {
                var a = at.Trim();
                if (a.Equals("selected", StringComparison.OrdinalIgnoreCase) || a.Equals("selectie", StringComparison.OrdinalIgnoreCase)) r.AtSelected = true;
                else
                {
                    var nums = new List<double>();
                    foreach (var p in a.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!FlagArgs.TryNum(p, out var v)) { r.Error = "--at: '" + p + "' is not a number; give x y z (mm), element ids, or 'selected'."; return r; }
                        nums.Add(v);
                    }
                    if (nums.Count == 0) { r.Error = "--at needs x y z (mm), element ids, or 'selected'."; return r; }
                    if (nums.Count != 3 && nums.Any(v => v != Math.Floor(v) || v <= 0)) { r.Error = "--at: element ids are whole numbers; a point is exactly x y z."; return r; }
                    r.AtNumbers = nums;
                }
            }
            if (!Num(distance, "distance", v => r.Distance = v, r) || !Num(pitch, "pitch", v => r.Pitch = v, r) || !Num(margin, "margin", v => r.Margin = v, r))
                return r;
            if (r.Distance.HasValue && r.Distance <= 0) r.Error = "--distance must be positive.";
            else if (r.Pitch < -89 || r.Pitch > 89) r.Error = "--pitch is between -89 and 89 degrees (positive looks down).";
            else if (r.Margin < 0) r.Error = "--margin cannot be negative.";
            else if (r.Eye != null && r.Distance.HasValue) r.Error = "--distance is for a compass direction; with a camera position x y z the distance is given.";
            return r;
        }

        private static bool Num(string s, string flag, Action<double> set, LookSpec r)
        {
            if (s == null) return true;
            if (!FlagArgs.TryNum(s.Replace("°", ""), out var v)) { r.Error = "--" + flag + " needs a number, got '" + s + "'."; return false; }
            set(v);
            return true;
        }

        /// <summary>Is this --at a list of element ids (as opposed to a point)? `exist` says whether a number is an element id in the document.</summary>
        public bool AtIsIds(Func<double, bool> exist)
        {
            if (AtNumbers == null) return false;
            if (AtNumbers.Count != 3) return true;
            return AtNumbers.All(v => v == Math.Floor(v) && v > 0 && exist(v));
        }

        /// <summary>`look [view] dir [flags]`: the view part is whatever precedes the direction.</summary>
        public static LookSpec ParseLook(string args, out string view)
        {
            view = null;
            var f = FlagArgs.Parse(args);
            var err = f.Unknown("at", "distance", "pitch", "margin", "box");
            if (err != null) return new LookSpec { Error = err };
            var pos = f.Positional;
            if (pos.Count == 0) return new LookSpec { Error = "Usage: " + LookUsage };
            string dir;
            if (CameraMath.Canonical(pos[pos.Count - 1]) != null)
            {
                dir = pos[pos.Count - 1];
                view = pos.Count > 1 ? string.Join(" ", pos.Take(pos.Count - 1)) : null;
            }
            else if (pos.Count >= 3 && pos.Skip(pos.Count - 3).All(p => FlagArgs.TryNum(p, out _)))
            {
                dir = string.Join(" ", pos.Skip(pos.Count - 3));
                view = pos.Count > 3 ? string.Join(" ", pos.Take(pos.Count - 3)) : null;
            }
            else return new LookSpec { Error = "No direction: end with one of " + CameraMath.Keys + " or a camera position x y z (mm). Usage: " + LookUsage };
            if (f.Has("at") && f.Text("at") == null) return new LookSpec { Error = "--at needs x y z (mm), element ids, or 'selected'." };
            return FromParts(dir, f.Text("at"), f.Text("distance"), f.Text("pitch"), f.Text("margin"), f.Has("box"));
        }
    }
}
