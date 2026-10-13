using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>Which family a profile becomes (plan 12 §2.2 --category).</summary>
    internal enum ProfileCategory { Generic, Foundation, Framing }

    /// <summary>
    /// Arguments of `profile`, `extrude`, `sweep` and `lwall` (plan 12). One class, one parser per
    /// command; every flag the command does not take is refused by name. Pure — unit-tested.
    /// </summary>
    internal sealed class ProfileArgs
    {
        // ---- what to extrude ----
        public string ProfileName;          // a named profile (`profile <name>`), or null with --points
        public List<P2> Points;             // inline --points (profile: the definition)
        public string Name;                 // --name: family name for inline points / a sweep

        // ---- where ----
        public double[] Line;               // x1 y1 x2 y2 mm
        public List<int> AlongIds = new List<int>();   // line / wall / beam ids (sweep: a chain)
        public bool Vertical;               // extrude --vertical
        public double VerticalHeight;
        public double[] At;                 // --at x y (vertical)
        public double Rotate;               // --rotate deg (vertical)
        public double Z;                    // --z mm above the level
        public string Level;
        public bool Flip;                   // mirror u
        public bool RightSide;              // lwall --side right

        // ---- what it becomes ----
        public string TypeName;
        public ProfileCategory? Category;
        public string Material;
        public readonly List<KeyValuePair<string, string>> Sets = new List<KeyValuePair<string, string>>();
        public bool Replace;
        public bool NoJoin;

        // ---- lwall ----
        public LwallSizes Sizes;

        public string Error;

        public const string ProfileUsage = "profile <name> --points <u,v> <u,v> <u,v>... [--replace]  |  profile <name>  |  profile";
        private const string PlaceFlags = "[--z <mm>] [--level <name>] [--type <name>] [--material <name>] [--set \"Param=value\"]... [--replace] [--phase <kind>]";
        public const string ExtrudeUsage = "extrude <profile | --points u,v ...> --along <line/wall id | x1 y1 x2 y2> [--flip] " +
                                           "[--category generic|foundation|framing] [--name <family>] [--no-join] " + PlaceFlags +
                                           "  |  extrude <profile | --points u,v ...> --vertical <height> --at <x y> [--rotate <deg>] " + PlaceFlags;
        public const string SweepUsage = "sweep <profile | --points u,v ...> --along <id,id,... (lines/arcs, one chain) | wall id> [--flip] [--name <family>] [--no-join] " + PlaceFlags;
        public const string LwallUsage = "lwall <x1> <y1> <x2> <y2> | lwall --along <line/wall id> --stem <thickness>x<height> --foot <width>x<thickness> " +
                                         "[--toe <mm>] [--slope <mm at the back>] [--side left|right] [--category generic|foundation] [--no-join] " + PlaceFlags;

        private static readonly string[] Common = { "z", "level", "type", "material", "set", "replace" };

        // ------------------------------------------------------------------ profile

        /// <summary>`profile` (list) / `profile &lt;name&gt;` (show) / `profile &lt;name&gt; --points …` (define).</summary>
        public static ProfileArgs ParseProfile(string args)
        {
            var r = new ProfileArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("points", "replace");
            if (r.Error != null) return r;
            r.ProfileName = Join(f.Positional);
            r.Replace = f.Has("replace");
            if (f.Has("points"))
            {
                if (r.ProfileName == null) { r.Error = "Give the profile a name: profile <name> --points u,v u,v u,v ..."; return r; }
                if (!ParsePoints(f, r)) return r;
            }
            if (r.ProfileName != null && FamilyNaming.Sanitize(r.ProfileName).Length == 0) r.Error = "'" + r.ProfileName + "' is not a usable name.";
            return r;
        }

        // ------------------------------------------------------------------ extrude

        public static ProfileArgs ParseExtrude(string args)
        {
            var r = new ProfileArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown(Common.Concat(new[] { "points", "along", "flip", "category", "name", "no-join", "vertical", "at", "rotate" }).ToArray());
            if (r.Error != null) return r;
            if (!Source(f, r, "extrude")) return r;
            r.Vertical = f.Has("vertical");
            if (r.Vertical)
            {
                if (!f.TryNum("vertical", out r.VerticalHeight, out var err)) { r.Error = err ?? "--vertical needs the height in mm (e.g. --vertical 15000)."; return r; }
                if (r.VerticalHeight <= 0) { r.Error = "--vertical: the height must be positive."; return r; }
                if (!f.TryNums("at", 2, out r.At, out err)) { r.Error = err ?? "--vertical needs --at <x y> (mm): where the profile's 0,0 goes."; return r; }
                if (f.Has("rotate") && !f.TryNum("rotate", out r.Rotate, out err)) { r.Error = err ?? "--rotate needs degrees."; return r; }
                foreach (var flag in new[] { "along", "flip", "category", "no-join" })
                    if (f.Has(flag)) { r.Error = "--" + flag + " does not go with --vertical (a vertical extrusion stands on --at)."; return r; }
            }
            else
            {
                if (f.Has("at") || f.Has("rotate")) { r.Error = "--at / --rotate are for --vertical; a horizontal extrusion follows --along."; return r; }
                if (!Along(f, r, true)) return r;
                r.Flip = f.Has("flip");
                r.NoJoin = f.Has("no-join");
                if (f.Has("category") && !ParseCategory(f.Text("category"), r, true)) return r;
            }
            Shared(f, r);
            return r;
        }

        // ------------------------------------------------------------------ sweep

        public static ProfileArgs ParseSweep(string args)
        {
            var r = new ProfileArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown(Common.Concat(new[] { "points", "along", "flip", "name", "no-join" }).ToArray());
            if (r.Error != null) return r;
            if (!Source(f, r, "sweep")) return r;
            if (!f.Has("along")) { r.Error = "sweep needs --along <id,id,...>: the lines/arcs (or one wall) the profile follows."; return r; }
            var parts = string.Join(" ", f.Values("along")).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { r.Error = "--along needs one or more line/arc ids (or a wall id)."; return r; }
            foreach (var p in parts)
            {
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
                { r.Error = "--along: '" + p + "' is not an element id. sweep follows existing lines/arcs (draw them first) or a wall."; return r; }
                if (!r.AlongIds.Contains(id)) r.AlongIds.Add(id);
            }
            r.Flip = f.Has("flip");
            r.NoJoin = f.Has("no-join");
            Shared(f, r);
            return r;
        }

        // ------------------------------------------------------------------ lwall

        public static ProfileArgs ParseLwall(string args)
        {
            var r = new ProfileArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown(Common.Concat(new[] { "along", "stem", "foot", "toe", "slope", "side", "type-name", "category", "no-join" }).ToArray());
            if (r.Error != null) return r;

            if (f.Positional.Count > 0)
            {
                if (f.Has("along")) { r.Error = "Give the line once: either x1 y1 x2 y2 or --along."; return r; }
                var pos = f.Positional.SelectMany(p => p.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
                if (!Line4(pos, r, "lwall")) return r;
            }
            else if (!Along(f, r, false)) return r;
            if (r.Line == null && r.AlongIds.Count == 0) { r.Error = "Usage: " + LwallUsage; return r; }

            var s = new LwallSizes();
            if (!Pair(f, "stem", "--stem <thickness>x<height>, e.g. --stem 600x4000 (height = underside of the foot to the top)", out s.Stem, out s.Height, r)) return r;
            if (!Pair(f, "foot", "--foot <width>x<thickness>, e.g. --foot 2500x800 (width = the whole foot, toe included)", out s.Foot, out s.FootThickness, r)) return r;
            string err = null;
            if (f.Has("toe") && !WallArgs.OptNum(f, "toe", v => s.Toe = v, ref err)) { r.Error = err; return r; }
            if (f.Has("slope") && !WallArgs.OptNum(f, "slope", v => s.Slope = v, ref err)) { r.Error = err; return r; }
            r.Error = ProfileGeometry.CheckLwall(s);
            if (r.Error != null) return r;
            r.Sizes = s;

            if (f.Has("side"))
            {
                var side = (f.Text("side") ?? "").Trim().ToLowerInvariant();
                if (side == "left" || side == "links" || side == "l") r.RightSide = false;
                else if (side == "right" || side == "rechts" || side == "r") r.RightSide = true;
                else { r.Error = "--side is left or right: where the foot's heel lies, seen along x1,y1 → x2,y2."; return r; }
            }
            if (f.Has("category") && !ParseCategory(f.Text("category"), r, false)) return r;
            r.NoJoin = f.Has("no-join");
            Shared(f, r);
            if (r.Error == null && f.Has("type-name"))
            {
                if (r.TypeName != null) { r.Error = "Give --type or --type-name, not both."; return r; }
                r.TypeName = f.Text("type-name");
                if (r.TypeName == null) r.Error = "--type-name needs a name.";
            }
            return r;
        }

        // ------------------------------------------------------------------ shared pieces

        private static bool Source(FlagArgs f, ProfileArgs r, string cmd)
        {
            r.ProfileName = Join(f.Positional);
            if (f.Has("points"))
            {
                if (r.ProfileName != null) { r.Error = "Give a profile name or --points, not both (to save points under a name: profile <name> --points ...)."; return false; }
                if (!ParsePoints(f, r)) return false;
                r.Name = f.Text("name");
                if (f.Has("name") && r.Name == null) { r.Error = "--name needs a family name."; return false; }
                return true;
            }
            if (r.ProfileName == null) { r.Error = cmd + " needs a profile: a name from 'profile', or --points u,v u,v u,v ..."; return false; }
            if (f.Has("name"))
            {
                if (cmd != "sweep") { r.Error = "--name is for --points (a named profile gives the family its name)."; return false; }
                r.Name = f.Text("name");
                if (r.Name == null) { r.Error = "--name needs a family name."; return false; }
            }
            return true;
        }

        private static bool ParsePoints(FlagArgs f, ProfileArgs r)
        {
            var text = string.Join(" ", f.Values("points") ?? new List<string>());
            if (text.Trim().Length == 0) { r.Error = "--points needs u,v pairs in mm, e.g. --points 0,0 500,0 500,250 0,280"; return false; }
            var pts = ProfileGeometry.ParsePoints(text, out var err);
            if (pts == null) { r.Error = "--points: " + err; return false; }
            r.Points = pts;
            return true;
        }

        private static bool Along(FlagArgs f, ProfileArgs r, bool required)
        {
            if (!f.Has("along"))
            {
                if (required) r.Error = "--along <line id | wall id | x1 y1 x2 y2> is the line the profile runs along.";
                return !required;
            }
            var parts = string.Join(" ", f.Values("along")).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0)
            {
                r.AlongIds.Add(id);
                return true;
            }
            if (parts.Count == 4) return Line4(parts, r, "--along");
            r.Error = "--along takes one line/wall id or x1 y1 x2 y2 in mm.";
            return false;
        }

        private static bool Line4(List<string> parts, ProfileArgs r, string what)
        {
            if (parts.Count != 4) { r.Error = what + " needs x1 y1 x2 y2 in mm (got " + parts.Count + " value" + (parts.Count == 1 ? "" : "s") + ")."; return false; }
            r.Line = new double[4];
            for (int i = 0; i < 4; i++)
                if (!FlagArgs.TryNum(parts[i], out r.Line[i])) { r.Error = what + ": '" + parts[i] + "' is not a number (mm)."; return false; }
            if (Math.Abs(r.Line[0] - r.Line[2]) < 1 && Math.Abs(r.Line[1] - r.Line[3]) < 1)
            { r.Error = what + ": the two points are the same — the line has no length."; return false; }
            return true;
        }

        private static void Shared(FlagArgs f, ProfileArgs r)
        {
            if (r.Error != null) return;
            string err = null;
            if (f.Has("z") && !WallArgs.OptNum(f, "z", v => r.Z = v, ref err)) { r.Error = err; return; }
            r.Level = f.Text("level");
            if (f.Has("level") && r.Level == null) { r.Error = "--level needs a level name."; return; }
            r.TypeName = f.Text("type");
            if (f.Has("type") && r.TypeName == null) { r.Error = "--type needs a type name."; return; }
            if (r.TypeName != null && FamilyNaming.Sanitize(r.TypeName).Length == 0) { r.Error = "--type '" + r.TypeName + "' is not a usable name."; return; }
            r.Material = f.Text("material");
            if (f.Has("material") && r.Material == null) { r.Error = "--material needs a material name ('list materials' shows them)."; return; }
            r.Replace = f.Has("replace");
            foreach (var v in f.All("set"))
            {
                var s = string.Join(" ", v);
                if (!ParamAssign.TryParse(s, out var name, out var value)) { r.Error = "--set takes \"Parameter=value\", got '" + s + "'."; return; }
                r.Sets.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        private static bool ParseCategory(string text, ProfileArgs r, bool framingAllowed)
        {
            var c = ParseCategoryName(text);
            if (c == null || (c == ProfileCategory.Framing && !framingAllowed))
            {
                r.Error = "--category is " + (framingAllowed ? "generic, foundation or framing" : "generic or foundation") + " (got '" + text + "').";
                return false;
            }
            r.Category = c;
            return true;
        }

        /// <summary>generic|gm|generic model / foundation|fundering|sf / framing|ligger|beam → the category; else null.</summary>
        public static ProfileCategory? ParseCategoryName(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "generic": case "gm": case "generic model": case "generic models": case "generiek":
                    return ProfileCategory.Generic;
                case "foundation": case "fundering": case "sf": case "structural foundation": case "structural foundations":
                    return ProfileCategory.Foundation;
                case "framing": case "beam": case "ligger": case "balk": case "structural framing":
                    return ProfileCategory.Framing;
                default:
                    return null;
            }
        }

        /// <summary>"600x4000" (also 600*4000, 600 x 4000, 600×4000) → two positive numbers.</summary>
        public static bool TryParsePair(string text, out double a, out double b)
        {
            a = b = 0;
            var t = (text ?? "").Replace(" ", "").Replace("×", "x").Replace("*", "x").ToLowerInvariant();
            var parts = t.Split('x');
            return parts.Length == 2 && FlagArgs.TryNum(parts[0], out a) && FlagArgs.TryNum(parts[1], out b);
        }

        private static bool Pair(FlagArgs f, string flag, string help, out double a, out double b, ProfileArgs r)
        {
            a = b = 0;
            if (!f.Has(flag)) { r.Error = "lwall needs " + help + "."; return false; }
            if (!TryParsePair(f.Text(flag), out a, out b)) { r.Error = "--" + flag + " '" + f.Text(flag) + "': use " + help + "."; return false; }
            return true;
        }

        private static string Join(List<string> tokens)
        {
            var s = string.Join(" ", tokens ?? new List<string>()).Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
