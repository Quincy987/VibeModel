using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    // Arg classes of the "sheets 2" commands (schedule / callout / region / export), on the
    // shared FlagArgs tokenizer of SheetArgs.cs. Pure — unit-tested; conflicting flags are
    // refused with the reason, never silently ignored.

    internal enum ScheduleOp { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual, Contains, NotContains, BeginsWith, EndsWith }

    /// <summary>`--filter "&lt;field&gt; &lt;op&gt; &lt;value&gt;"` of `schedule` — the field name may contain spaces.</summary>
    internal sealed class ScheduleFilterSpec
    {
        public string Field, Value;
        public ScheduleOp Op;

        // Word operators (URL-friendly: no < > = to encode) and symbols; longer symbols first.
        private static readonly KeyValuePair<string, ScheduleOp>[] Words =
        {
            new KeyValuePair<string, ScheduleOp>("not contains", ScheduleOp.NotContains),
            new KeyValuePair<string, ScheduleOp>("!contains", ScheduleOp.NotContains),
            new KeyValuePair<string, ScheduleOp>("contains", ScheduleOp.Contains),
            new KeyValuePair<string, ScheduleOp>("beginswith", ScheduleOp.BeginsWith),
            new KeyValuePair<string, ScheduleOp>("startswith", ScheduleOp.BeginsWith),
            new KeyValuePair<string, ScheduleOp>("endswith", ScheduleOp.EndsWith),
            new KeyValuePair<string, ScheduleOp>("eq", ScheduleOp.Equal),
            new KeyValuePair<string, ScheduleOp>("ne", ScheduleOp.NotEqual),
            new KeyValuePair<string, ScheduleOp>("gt", ScheduleOp.Greater),
            new KeyValuePair<string, ScheduleOp>("ge", ScheduleOp.GreaterOrEqual),
            new KeyValuePair<string, ScheduleOp>("lt", ScheduleOp.Less),
            new KeyValuePair<string, ScheduleOp>("le", ScheduleOp.LessOrEqual),
        };
        private static readonly KeyValuePair<string, ScheduleOp>[] Symbols =
        {
            new KeyValuePair<string, ScheduleOp>("!=", ScheduleOp.NotEqual),
            new KeyValuePair<string, ScheduleOp>("<>", ScheduleOp.NotEqual),
            new KeyValuePair<string, ScheduleOp>(">=", ScheduleOp.GreaterOrEqual),
            new KeyValuePair<string, ScheduleOp>("<=", ScheduleOp.LessOrEqual),
            new KeyValuePair<string, ScheduleOp>("=", ScheduleOp.Equal),
            new KeyValuePair<string, ScheduleOp>(">", ScheduleOp.Greater),
            new KeyValuePair<string, ScheduleOp>("<", ScheduleOp.Less),
        };

        public const string Help = "--filter \"<field> <op> <value>\" with op = | != | > | >= | < | <= | contains | !contains | beginswith | endswith " +
                                   "(or eq ne gt ge lt le), e.g. --filter \"Length gt 12000\"";

        public static ScheduleFilterSpec Parse(string text, out string error)
        {
            error = null;
            var t = (text ?? "").Trim();
            if (t.Length == 0) { error = "Empty --filter. " + Help; return null; }
            // A word operator must stand alone (spaces around it), so "Contains" inside a field name is safe.
            int bestAt = -1, bestLen = 0;
            var bestOp = ScheduleOp.Equal;
            foreach (var w in Words)
            {
                var m = Regex.Match(t, @"(?<=\s)" + Regex.Escape(w.Key) + @"(?=\s)", RegexOptions.IgnoreCase);
                if (m.Success && (bestAt < 0 || m.Index < bestAt)) { bestAt = m.Index; bestLen = m.Length; bestOp = w.Value; }
            }
            foreach (var sym in Symbols)
            {
                int i = t.IndexOf(sym.Key, StringComparison.Ordinal);
                if (i > 0 && (bestAt < 0 || i < bestAt)) { bestAt = i; bestLen = sym.Key.Length; bestOp = sym.Value; }
            }
            if (bestAt <= 0) { error = "No operator in --filter '" + t + "'. " + Help; return null; }
            var field = t.Substring(0, bestAt).Trim();
            var value = t.Substring(bestAt + bestLen).Trim().Trim('"', '\'');
            if (field.Length == 0) { error = "--filter has no field name before the operator. " + Help; return null; }
            if (value.Length == 0) { error = "--filter has no value after the operator. " + Help; return null; }
            return new ScheduleFilterSpec { Field = field, Op = bestOp, Value = value };
        }

        public bool IsComparison => Op == ScheduleOp.Greater || Op == ScheduleOp.GreaterOrEqual || Op == ScheduleOp.Less || Op == ScheduleOp.LessOrEqual;

        public string OpText()
        {
            switch (Op)
            {
                case ScheduleOp.Equal: return "=";
                case ScheduleOp.NotEqual: return "!=";
                case ScheduleOp.Greater: return ">";
                case ScheduleOp.GreaterOrEqual: return ">=";
                case ScheduleOp.Less: return "<";
                case ScheduleOp.LessOrEqual: return "<=";
                case ScheduleOp.Contains: return "contains";
                case ScheduleOp.NotContains: return "does not contain";
                case ScheduleOp.BeginsWith: return "begins with";
                default: return "ends with";
            }
        }

        public override string ToString() => Field + " " + OpText() + " " + Value;
    }

    /// <summary>
    /// `schedule &lt;category&gt; --fields a,b,c [--filter ...] [--group f] [--name n] [--total]`
    /// creates a quantity schedule; `schedule &lt;name|id&gt; --rows [n]` reads one back.
    /// </summary>
    internal sealed class ScheduleArgs
    {
        public string Category;        // create mode
        public List<string> Fields = new List<string>();
        public ScheduleFilterSpec Filter;
        public string Group, Name;
        public bool Total;
        public string ReadTarget;      // --rows mode: schedule name or id
        public int RowLimit = 200;
        public string Error;

        public const int PreviewRows = 20;

        public const string Usage = "schedule <category> --fields \"Family and Type,Count,Length\" [--filter \"<field> <op> <value>\"] " +
                                    "[--group <field>] [--name <name>] [--total]  |  schedule <name|id> --rows [<max rows>]";

        public static ScheduleArgs Parse(string args)
        {
            var r = new ScheduleArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("fields", "filter", "group", "name", "total", "rows");
            if (r.Error != null) return r;
            var target = string.Join(" ", f.Positional).Trim();

            if (f.Has("rows"))
            {
                foreach (var flag in new[] { "fields", "filter", "group", "name", "total" })
                    if (f.Has(flag)) { r.Error = "--rows reads an existing schedule; --" + flag + " is for creating one."; return r; }
                if (target.Length == 0) { r.Error = "Give the schedule (name or id) before --rows."; return r; }
                r.ReadTarget = target;
                var v = f.Values("rows");
                if (v.Count > 0)
                {
                    if (v.Count != 1 || !int.TryParse(v[0], out int n) || n < 1) { r.Error = "--rows takes at most one number (the maximum rows to show)."; return r; }
                    r.RowLimit = Math.Min(n, 2000);
                }
                return r;
            }

            if (target.Length == 0) { r.Error = "Give the category first, e.g. 'schedule structural foundations --fields ...'."; return r; }
            r.Category = target;
            if (!f.Has("fields")) { r.Error = "--fields is required (comma-separated field names, e.g. --fields \"Family and Type,Count,Length\")."; return r; }
            foreach (var name in (f.Text("fields") ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (r.Fields.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase))) { r.Error = "Field '" + name + "' is given twice in --fields."; return r; }
                r.Fields.Add(name);
            }
            if (r.Fields.Count == 0) { r.Error = "--fields is empty."; return r; }
            if (f.Has("filter"))
            {
                r.Filter = ScheduleFilterSpec.Parse(f.Text("filter"), out r.Error);
                if (r.Error != null) return r;
            }
            if (f.Has("group"))
            {
                r.Group = f.Text("group");
                if (string.IsNullOrWhiteSpace(r.Group)) { r.Error = "--group needs a field name."; return r; }
                if (!r.Fields.Any(x => x.Equals(r.Group, StringComparison.OrdinalIgnoreCase)))
                { r.Error = "--group '" + r.Group + "' must be one of the --fields (" + string.Join(", ", r.Fields) + ")."; return r; }
            }
            if (f.Has("name"))
            {
                r.Name = f.Text("name");
                if (string.IsNullOrWhiteSpace(r.Name)) { r.Error = "--name needs a value."; return r; }
            }
            if (f.Has("total"))
            {
                if (f.Values("total").Count > 0) { r.Error = "--total takes no value (put it after the other flags' values, or before them)."; return r; }
                r.Total = true;
            }
            return r;
        }
    }

    /// <summary>`callout &lt;parent view&gt; --box x1 y1 x2 y2 [--name n] [--scale s] [--section]`, or `... --reference &lt;view&gt;`.</summary>
    internal sealed class CalloutArgs
    {
        public string Parent;
        public double[] Box;           // x0 y0 x1 y1, normalised (min first)
        public string Name, Template, Reference;
        public int? Scale;
        public bool Section, NoTemplate;
        public string Error;

        public const string Usage = "callout <parent view> --box <x1> <y1> <x2> <y2> [--name <name>] [--scale <n>] [--section] " +
                                    "[--template <name>|--no-template]  |  callout <parent view> --box <x1> <y1> <x2> <y2> --reference <existing view>";

        public static CalloutArgs Parse(string args)
        {
            var r = new CalloutArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("box", "name", "scale", "section", "template", "no-template", "reference");
            if (r.Error != null) return r;
            r.Parent = string.Join(" ", f.Positional).Trim();
            if (r.Parent.Length == 0) { r.Error = "Give the parent view (id or name) first."; return r; }
            if (!f.Has("box")) { r.Error = "--box x1 y1 x2 y2 is required (the callout rectangle in the parent view's frame, mm)."; return r; }
            if (!f.TryNums("box", 4, out var b, out r.Error)) return r;
            if (Math.Abs(b[2] - b[0]) < 1 || Math.Abs(b[3] - b[1]) < 1) { r.Error = "--box has no area: x1 ≠ x2 and y1 ≠ y2 are needed."; return r; }
            r.Box = new[] { Math.Min(b[0], b[2]), Math.Min(b[1], b[3]), Math.Max(b[0], b[2]), Math.Max(b[1], b[3]) };
            r.Name = f.Text("name");
            if (f.Has("name") && string.IsNullOrWhiteSpace(r.Name)) { r.Error = "--name needs a value."; return r; }
            if (f.Has("scale"))
            {
                if (!f.TryNum("scale", out double s, out r.Error)) return r;
                if (s < 1 || s != Math.Floor(s)) { r.Error = "--scale is the n of 1:n (a whole number)."; return r; }
                r.Scale = (int)s;
            }
            r.Section = f.Has("section");
            if (r.Section && f.Values("section").Count > 0) { r.Error = "--section takes no value."; return r; }
            r.Template = f.Text("template");
            r.NoTemplate = f.Has("no-template");
            if (r.Template != null && r.NoTemplate) { r.Error = "Use --template <name> or --no-template, not both."; return r; }
            if (f.Has("reference"))
            {
                r.Reference = f.Text("reference");
                if (string.IsNullOrWhiteSpace(r.Reference)) { r.Error = "--reference needs the existing view (id or name) the callout points to."; return r; }
                // A reference callout creates no view: nothing to name, scale or template.
                foreach (var flag in new[] { "name", "scale", "template", "no-template", "section" })
                    if (f.Has(flag)) { r.Error = "--" + flag + " does not go with --reference (a reference callout points to an existing view and creates none)."; return r; }
            }
            return r;
        }
    }

    /// <summary>`region &lt;view&gt; --points x,y x,y x,y ... [--type t]`, or `region &lt;view&gt; --detail &lt;type&gt; --at x y [--rotate deg]`.</summary>
    internal sealed class RegionArgs
    {
        public string View;
        public List<double[]> Points;  // polygon corners (mm, view frame), open: the closing edge is implied
        public string Type, Detail;
        public double[] At;
        public double Rotate;
        public string Error;

        public const string Usage = "region <view> --points <x,y x,y x,y ...> [--type <filled region type>]  |  " +
                                    "region <view> --detail <detail item family type> --at <x> <y> [--rotate <deg>]";

        public static RegionArgs Parse(string args)
        {
            var r = new RegionArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("points", "type", "detail", "at", "rotate");
            if (r.Error != null) return r;
            r.View = string.Join(" ", f.Positional).Trim();
            if (r.View.Length == 0) { r.Error = "Give the view (id or name) first."; return r; }
            if (f.Has("points") == f.Has("detail"))
            { r.Error = "Pick one: --points x,y x,y x,y ... (a filled region) or --detail <type> --at x y (a detail component)."; return r; }

            if (f.Has("points"))
            {
                foreach (var flag in new[] { "at", "rotate" })
                    if (f.Has(flag)) { r.Error = "--" + flag + " goes with --detail, not --points."; return r; }
                var parts = string.Join(" ", f.Values("points")).Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length % 2 != 0) { r.Error = "--points needs x,y pairs; got an odd count of numbers (" + parts.Length + ")."; return r; }
                var pts = new List<double[]>();
                for (int i = 0; i < parts.Length; i += 2)
                {
                    if (!FlagArgs.TryNum(parts[i], out double x) || !FlagArgs.TryNum(parts[i + 1], out double y))
                    { r.Error = "--points: '" + parts[i] + "," + parts[i + 1] + "' is not a number pair."; return r; }
                    // Repeated points (and a closing copy of the first) would make zero-length edges.
                    if (pts.Count > 0 && Near(pts[pts.Count - 1], x, y)) continue;
                    pts.Add(new[] { x, y });
                }
                if (pts.Count > 1 && Near(pts[0], pts[pts.Count - 1][0], pts[pts.Count - 1][1])) pts.RemoveAt(pts.Count - 1);
                if (pts.Count < 3) { r.Error = "--points needs at least 3 distinct corners."; return r; }
                if (Math.Abs(Polygon.Area(pts)) < 1) { r.Error = "--points has no area (the corners lie on one line)."; return r; }
                if (Polygon.SelfIntersects(pts)) { r.Error = "--points crosses itself; give the corners in order around the outline."; return r; }
                r.Points = pts;
                r.Type = f.Text("type");
                if (f.Has("type") && string.IsNullOrWhiteSpace(r.Type)) { r.Error = "--type needs a filled region type name."; return r; }
                return r;
            }

            if (f.Has("type")) { r.Error = "--type is the filled region type (with --points); for a detail item the type goes in --detail."; return r; }
            r.Detail = f.Text("detail");
            if (string.IsNullOrWhiteSpace(r.Detail)) { r.Error = "--detail needs a detail item type (\"Family : Type\" or the type name)."; return r; }
            if (!f.Has("at")) { r.Error = "--detail needs --at x y (where to put it, in the view's frame)."; return r; }
            if (!f.TryNums("at", 2, out r.At, out r.Error)) return r;
            if (f.Has("rotate") && !f.TryNum("rotate", out r.Rotate, out r.Error)) return r;
            return r;
        }

        private static bool Near(double[] p, double x, double y) => Math.Abs(p[0] - x) < 0.5 && Math.Abs(p[1] - y) < 0.5;
    }

    internal enum ExportFormat { Pdf, Dwg, Ifc }

    /// <summary>`export pdf|dwg|ifc &lt;sheets|views&gt; | --all-sheets [--to folder] [--name pattern] [--combine] [--setup name]`.</summary>
    internal sealed class ExportArgs
    {
        public ExportFormat Format;
        public List<string> Targets = new List<string>();
        public bool AllSheets, Combine;
        public string To, NamePattern, Setup;
        public string Error;

        public const string Usage = "export pdf|dwg|ifc <sheetNumber|viewId|name>[,...] | --all-sheets [--to <folder>] [--name <pattern>] " +
                                    "[--combine] [--setup <DWG export setup>]   (ifc without views = the whole model)";

        public static ExportArgs Parse(string args)
        {
            var r = new ExportArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("all-sheets", "to", "name", "combine", "setup");
            if (r.Error != null) return r;
            if (f.Positional.Count == 0) { r.Error = "Give the format first: export pdf|dwg|ifc ..."; return r; }
            switch (f.Positional[0].ToLowerInvariant())
            {
                case "pdf": r.Format = ExportFormat.Pdf; break;
                case "dwg": r.Format = ExportFormat.Dwg; break;
                case "ifc": r.Format = ExportFormat.Ifc; break;
                default: r.Error = "Unknown format '" + f.Positional[0] + "': use pdf, dwg or ifc."; return r;
            }
            // Comma- or space-separated; a quoted view name keeps its spaces.
            foreach (var p in f.Positional.Skip(1))
                foreach (var t in p.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                    if (!r.Targets.Contains(t, StringComparer.OrdinalIgnoreCase)) r.Targets.Add(t);
            r.AllSheets = f.Has("all-sheets");
            if (r.AllSheets && f.Values("all-sheets").Count > 0) { r.Error = "--all-sheets takes no value (sheet numbers go before the flags)."; return r; }
            if (r.AllSheets && r.Targets.Count > 0) { r.Error = "Give sheet numbers/views or --all-sheets, not both."; return r; }
            if (r.AllSheets && r.Format == ExportFormat.Ifc) { r.Error = "IFC exports the model (or the elements of one view), not sheets; drop --all-sheets."; return r; }
            if (!r.AllSheets && r.Targets.Count == 0 && r.Format != ExportFormat.Ifc)
            { r.Error = "Give the sheet numbers or view ids to export, or --all-sheets."; return r; }
            r.Combine = f.Has("combine");
            if (r.Combine && r.Format != ExportFormat.Pdf) { r.Error = "--combine is for pdf (one PDF with all pages)."; return r; }
            if (r.Combine && f.Values("combine").Count > 0) { r.Error = "--combine takes no value."; return r; }
            r.Setup = f.Text("setup");
            if (f.Has("setup") && r.Format != ExportFormat.Dwg) { r.Error = "--setup names a DWG export setup; it does not apply to " + r.Format.ToString().ToLowerInvariant() + "."; return r; }
            if (f.Has("setup") && string.IsNullOrWhiteSpace(r.Setup)) { r.Error = "--setup needs the name of a DWG export setup."; return r; }
            r.To = f.Text("to");
            if (f.Has("to") && string.IsNullOrWhiteSpace(r.To)) { r.Error = "--to needs a folder."; return r; }
            r.NamePattern = f.Text("name");
            if (f.Has("name") && string.IsNullOrWhiteSpace(r.NamePattern)) { r.Error = "--name needs a pattern, e.g. \"{number} - {name}\"."; return r; }
            if (r.Format == ExportFormat.Ifc && r.Targets.Count > 1) { r.Error = "IFC exports one view's elements (or the whole model): give at most one view."; return r; }
            return r;
        }
    }
}
