using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>What a protected item is, which decides how a change to it counts.</summary>
    public enum ProtectedKind
    {
        /// <summary>The base point, the survey point, the shared coordinates: any change counts.</summary>
        Position,
        /// <summary>A grid, level, link or import: only going from pinned to unpinned counts.</summary>
        Pin,
        /// <summary>A view template: any change counts, also deleting it.</summary>
        Template
    }

    /// <summary>One protected item as read before or after a command.</summary>
    public sealed class ProtectedState
    {
        public ProtectedKind Kind { get; }
        public string What { get; }
        public string State { get; }

        public ProtectedState(ProtectedKind kind, string what, string state)
        {
            Kind = kind;
            What = what;
            State = state;
        }
    }

    /// <summary>
    /// What the AI may never do, also when asked: move the base or survey point, change the shared
    /// coordinates, unpin datums or links, edit view templates. Pure: the scan of an exec snippet
    /// and the comparison of two readings. The Revit side is <see cref="ForbiddenGuard"/>.
    /// The scan is a heuristic with one direction of error: a snippet it wrongly refuses costs a
    /// rewrite, one it wrongly passes is caught by the comparison after the run.
    /// </summary>
    public static class ForbiddenActions
    {
        public const string Code = "FORBIDDEN";
        public const string Hint = "this is done by Revit-beheer; do not retry or work around it.";
        public const string PinnedState = "pinned";
        public const string UnpinnedState = "unpinned";

        public const string BasePointWhat = "the project base point or survey point";
        public const string CoordinatesWhat = "the shared coordinates";
        public const string TemplateWhat = "a view template";
        public const string PinWhat = "the pin of a grid, level, link or import";

        public static string RefusedMessage(string what) => "Refused: " + what + " is protected by the office.";

        /// <summary>The message of a command that got past the checks and changed a protected item.</summary>
        public static string RolledBackMessage(string what) => "Refused: this change touched " + what + ", which is protected by the office; it was rolled back.";

        /// <summary>The response line when nothing could undo it (no run group).</summary>
        public static string KeptLine(string what) => "-- forbidden: " + what + " changed; press Ctrl+Z to undo — VibeModel could not undo it automatically";

        private static readonly (Regex Token, string What)[] Tokens =
        {
            (new Regex(@"\b(BasePoint|GetProjectBasePoint|GetSurveyPoint)\b", RegexOptions.Compiled), BasePointWhat),
            (new Regex(@"\b(ProjectLocation|ActiveProjectLocation|ActiveProjectLocationId|SiteLocation)\b", RegexOptions.Compiled), CoordinatesWhat),
            (new Regex(@"\b(ViewTemplateId|IsTemplate)\b", RegexOptions.Compiled), TemplateWhat)
        };

        private static readonly (Regex Call, string What)[] AlwaysRefused =
        {
            (new Regex(@"\bPinned\s*=\s*false\b", RegexOptions.Compiled), PinWhat),
            (new Regex(@"\bApplyViewTemplateParameters\s*\(", RegexOptions.Compiled), TemplateWhat),
            (new Regex(@"\bSetProjectPosition\s*\(", RegexOptions.Compiled), CoordinatesWhat)
        };

        // "!v.IsTemplate" and "v.IsTemplate == false" select the views that are NOT templates.
        private static readonly Regex NotATemplate = new Regex(
            @"!\s*[\w.]*IsTemplate\b|[\w.]*IsTemplate\s*==\s*false\b", RegexOptions.Compiled);

        private static readonly Regex Assignment = new Regex(@"(?<![=!<>+\-*/%&|^])=(?![=>])", RegexOptions.Compiled);

        private static readonly Regex Declaration = new Regex(
            @"^\s*(?:[\w.<>\[\],]+\s+)(\w+)\s*=(?![=>])\s*(.+)$", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex ForeachHead = new Regex(
            @"foreach\s*\(\s*[\w.<>\[\],]+\s+(\w+)\s+in\s+(.+)\)\s*$", RegexOptions.Compiled | RegexOptions.Singleline);

        // A number or a position read from a protected item is a value, not the item.
        private static readonly Regex ValueRead = new Regex(
            @"\.(Position|SharedPosition|X|Y|Z|Name|IntegerValue|Count|Pinned|IsTemplate|EastWest|NorthSouth|Elevation|Angle)\s*$", RegexOptions.Compiled);

        /// <summary>What the snippet would change that it may not, or null when it passes.</summary>
        public static string ScanExec(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var tainted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in StripCommentsAndStrings(code).Split(';', '{', '}'))
            {
                var statement = NotATemplate.Replace(raw, " ");
                if (statement.Trim().Length == 0) continue;

                foreach (var (call, what) in AlwaysRefused)
                    if (call.IsMatch(statement)) return what;

                foreach (var (token, what) in Tokens)
                    foreach (Match m in token.Matches(statement))
                        if (Mutates(statement, m.Value)) return what;

                foreach (var t in tainted)
                    if (Mutates(statement, t.Key)) return t.Value;

                Taint(statement, tainted);
            }
            return null;
        }

        private static void Taint(string statement, Dictionary<string, string> tainted)
        {
            var match = ForeachHead.Match(statement);
            if (!match.Success) match = Declaration.Match(statement);
            if (!match.Success) return;
            var source = match.Groups[2].Value.Trim();
            if (ValueRead.IsMatch(source)) return;

            foreach (var (token, what) in Tokens)
                if (token.IsMatch(source)) { tainted[match.Groups[1].Value] = what; return; }
            foreach (var t in tainted)
                if (Regex.IsMatch(source, @"\b" + Regex.Escape(t.Key) + @"\b")) { tainted[match.Groups[1].Value] = t.Value; return; }
        }

        private static bool Mutates(string statement, string name)
        {
            var id = @"\b" + Regex.Escape(name) + @"\b";

            // x.Position = …, doc.ActiveProjectLocation = …: the name is the assignment's target, with
            // only a member path between ("if (v.IsTemplate) n = 1" reads it). A declaration
            // ("BasePoint bp = …") names the type there and assigns nothing to it.
            var assign = Assignment.Match(statement);
            if (assign.Success && !Declaration.IsMatch(statement) &&
                Regex.IsMatch(statement.Substring(0, assign.Index), id + @"[\w.\[\]\s]*$")) return true;

            // template.AddFilter(…), list.Remove(…): only on the name itself ("if (v.IsTemplate) list.Add(v)" reads).
            if (Regex.IsMatch(statement, id + @"\.(Add\w*|Remove\w*)\s*\(")) return true;

            // x.Set(…), x.get_Parameter(…).Set(…)
            if (Regex.IsMatch(statement, id + @"[^;]*?\.(Set\w*|Move\w*|Rotate\w*)\s*\(")) return true;

            // ElementTransformUtils.MoveElement(doc, x.Id, …), doc.Delete(x.Id)
            return Regex.IsMatch(statement, @"\b(Move\w*|Rotate\w*|Delete)\s*\([^;]*" + id);
        }

        /// <summary>The code with its comments gone and every string and char literal emptied.</summary>
        public static string StripCommentsAndStrings(string code)
        {
            var sb = new StringBuilder(code.Length);
            int i = 0;
            while (i < code.Length)
            {
                char c = code[i];
                char next = i + 1 < code.Length ? code[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < code.Length && code[i] != '\n') i++;
                }
                else if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++;
                    i += 2;
                }
                else if (c == '@' && next == '"')
                {
                    i += 2;
                    while (i < code.Length)
                    {
                        if (code[i] == '"' && i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; }
                        if (code[i] == '"') break;
                        i++;
                    }
                    i++;
                    sb.Append("\"\"");
                }
                else if (c == '"' || c == '\'')
                {
                    i++;
                    while (i < code.Length && code[i] != c && code[i] != '\n')
                        i += code[i] == '\\' ? 2 : 1;
                    i++;
                    sb.Append("\"\"");
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>True when the arguments of a <c>set</c> name the Pinned parameter.</summary>
        public static bool SetsPinned(string args)
            => !string.IsNullOrEmpty(args) && Regex.IsMatch(args, @"(^|[\s""=])pinned($|[\s""=])", RegexOptions.IgnoreCase);

        /// <summary>
        /// What changed between two readings, or null. Only items of the first reading count: a new
        /// pinned link or a new grid is not a change to something protected.
        /// </summary>
        public static string Difference(IDictionary<string, ProtectedState> before, IDictionary<string, ProtectedState> after)
        {
            if (before == null || after == null) return null;
            var changed = new List<string>();
            foreach (var b in before)
            {
                if (!after.TryGetValue(b.Key, out var a))
                {
                    if (b.Value.Kind == ProtectedKind.Template) changed.Add(b.Value.What);
                    continue;
                }
                if (a.State == b.Value.State) continue;
                if (b.Value.Kind == ProtectedKind.Pin && (b.Value.State != PinnedState || a.State != UnpinnedState)) continue;
                changed.Add(b.Value.What);
            }
            return changed.Count == 0 ? null : string.Join(", ", changed.Distinct());
        }
    }
}
