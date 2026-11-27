using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class FilterCommand : IClaudeCommand
    {
        public string Name => "filter";
        public string Description => "Filter elements by category and parameter value; 'filter any <param> <op> <value>' searches every category at once " +
                                     "(the way to find an example by its value — never loop over every parameter in exec)";
        public string Usage => "filter <category|any> [paramName operator value] [--view] [--select] [--ids] [--count paramName]";

        /// <summary>A whole-model search taking longer than this is logged.</summary>
        private const long SlowAnyMs = 500;

        /// <summary>At most this many elements (one per category and per type) are read whole to find a parameter's spelling.</summary>
        private const int SpellingSample = 300;

        private const int Shown = 50;

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            if (string.IsNullOrWhiteSpace(args))
                return "ERROR: Usage: " + Usage + "\n\nOperators: = != > < >= <= contains\nExamples:\n  filter walls\n  filter walls Mark = K01\n  filter walls Height > 3000\n" +
                       "  filter walls --view --select\n  filter walls --count Mark\n  filter any Mark = K01\n  filter any \"Type Name\" contains AZ --count Category";

            // A quoted parameter name or value stays one token: "Type Name" contains AZ.
            var tokens = FlagArgs.Tokenize(args);

            // Extract flags
            bool viewScoped = ExtractFlag(tokens, "--view");
            bool selectResults = ExtractFlag(tokens, "--select");
            bool idsOnly = ExtractFlag(tokens, "--ids");

            string countParam = null;
            int countIdx = tokens.FindIndex(t => t.Equals("--count", StringComparison.OrdinalIgnoreCase));
            if (countIdx >= 0 && countIdx + 1 < tokens.Count)
            {
                countParam = tokens[countIdx + 1];
                tokens.RemoveAt(countIdx + 1);
                tokens.RemoveAt(countIdx);
            }
            else if (countIdx >= 0)
            {
                tokens.RemoveAt(countIdx);
                return "ERROR: --count requires a parameter name. Example: --count Mark";
            }

            var q = FilterQuery.Parse(tokens);
            if (q.Error != null) return "ERROR: " + q.Error;
            string categoryInput = q.Category, paramName = q.Parameter, foundOp = q.Operator, filterValue = q.Value;

            // Get active view for view-scoped queries
            View activeView = null;
            if (viewScoped)
            {
                activeView = FormattingHelper.GetActiveGraphicalView(uiDoc);
                if (activeView == null)
                    return "ERROR: No graphical view active. Switch to a plan, section, or 3D view to use --view.";
            }

            // 'any' is caught before the category is resolved: its substring match must never turn it into a real category.
            if (FilterQuery.IsAny(categoryInput))
                return ExecuteAny(uiDoc, doc, q, countParam, activeView, selectResults, idsOnly);

            var (cat, error) = FormattingHelper.ResolveCategory(categoryInput, doc);
            if (cat == null)
                return error;

            // Collect elements
            var collector = activeView != null
                ? new FilteredElementCollector(doc, activeView.Id)
                : new FilteredElementCollector(doc);

            var elements = collector
                .OfCategory(cat.Value)
                .WhereElementIsNotElementType()
                .ToList();

            // Apply parameter filter if specified
            if (q.HasExpression)
            {
                elements = elements.Where(e => MatchesFilter(e, doc, paramName, foundOp, filterValue)).ToList();
            }

            // --count mode: group by parameter value
            if (countParam != null)
            {
                return BuildCountOutput(elements, doc, countParam, categoryInput, activeView);
            }

            // --select mode
            if (selectResults && elements.Count > 0)
            {
                try
                {
                    uiDoc.Selection.SetElementIds(elements.Select(e => e.Id).ToList());
                }
                catch { /* selection may fail in some views */ }
            }

            // --ids mode
            if (idsOnly)
            {
                if (elements.Count == 0)
                    return "No elements found.";
                return string.Join(" ", elements.Select(e => e.Id.IntegerValue));
            }

            // Standard output
            var sb = new StringBuilder();
            var shown = elements.Take(Shown).ToList();
            sb.AppendLine("FILTER: " + categoryInput.ToUpper() +
                (paramName != null ? " | " + paramName + " " + foundOp + " " + filterValue : "") +
                (viewScoped ? " [view: " + activeView.Name + "]" : ""));
            sb.AppendLine("Found: " + elements.Count + (elements.Count > shown.Count
                ? " (" + Truncation.Line(shown.Count, elements.Count, "--count groups them all, --ids lists every id") + ")" : ""));
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            foreach (var elem in shown)
            {
                sb.Append("ID: " + elem.Id.IntegerValue + " | ");
                sb.Append(elem.Name);
                AppendTypeAndMark(sb, doc, elem);
                sb.AppendLine();
            }

            if (selectResults && elements.Count > 0)
                sb.AppendLine("\n(" + elements.Count + " elements selected)");

            return sb.ToString();
        }

        // ---------------------------------------------------------------- filter any

        /// <summary>
        /// `filter any`: one collector over the whole model (or the active view), every element
        /// read for ONE parameter. Never enumerates an element's parameters — reading every
        /// parameter of every element crashed Revit twice in a colleague's export.
        /// </summary>
        private static string ExecuteAny(UIDocument uiDoc, Document doc, FilterQuery.Split q, string countParam, View view,
                                         bool selectResults, bool idsOnly)
        {
            if (!q.HasExpression && countParam == null)
                return "ERROR: 'filter any' searches every category for one parameter: give one.\n" +
                       "Usage: filter any <paramName> <operator> <value> [--count <paramName>] [--view] [--select] [--ids]\n" +
                       "Examples:\n  filter any Mark = K01\n  filter any Comments contains kade\n  filter any \"Type Name\" contains AZ --count Category";

            var sw = Stopwatch.StartNew();
            var collector = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
            var all = collector.WhereElementIsNotElementType().Where(e => e.Category != null && !ApprovalRun.Hidden(e)).ToList();

            // The filter's own parameter first, then the one --count groups by.
            List<Element> matched = all;
            ParamReader filterReader = null;
            string spellingNote = null;
            if (q.HasExpression)
            {
                filterReader = Resolve(doc, all, q.Parameter, out spellingNote, out var missing);
                if (filterReader == null) return missing;
                matched = all.Where(e => { var p = filterReader.Read(e); return p != null && p.HasValue && Matches(p, q.Operator, q.Value); }).ToList();
            }

            ParamReader countReader = null;
            string countNote = null;
            if (countParam != null)
            {
                // Without a filter only the elements that carry the parameter are counted.
                countReader = Resolve(doc, all, countParam, out countNote, out var missing);
                if (countReader == null) return missing;
                if (!q.HasExpression) matched = all.Where(e => countReader.Read(e) != null).ToList();
            }

            var cats = FilterQuery.ByCount(matched.Select(CategoryName));
            matched = matched.OrderBy(e => cats.FindIndex(c => c.Key == CategoryName(e))).ThenBy(e => e.Id.IntegerValue).ToList();
            sw.Stop();
            if (sw.ElapsedMilliseconds > SlowAnyMs)
                Logger.Warn("filter any read " + all.Count + " elements in " + sw.ElapsedMilliseconds + " ms (" +
                            (q.Parameter ?? countParam) + ")");

            string expression = q.HasExpression ? q.Parameter + " " + q.Operator + " " + q.Value : null;
            string scope = view != null ? " [view: " + view.Name + "]" : "";

            if (countParam != null)
                return CountAny(matched, countReader, countParam, expression, scope, all.Count, countNote, spellingNote);

            if (selectResults && matched.Count > 0)
            {
                try { uiDoc.Selection.SetElementIds(matched.Select(e => e.Id).ToList()); }
                catch { /* selection may fail in some views */ }
            }

            if (idsOnly)
                return matched.Count == 0 ? "No elements found." : string.Join(" ", matched.Select(e => e.Id.IntegerValue));

            var sb = new StringBuilder();
            sb.AppendLine("FILTER: ANY | " + expression + scope);
            if (spellingNote != null) sb.AppendLine(spellingNote);
            var shown = matched.Take(Shown).ToList();
            sb.AppendLine("Found: " + matched.Count + " in " + cats.Count + " categor" + (cats.Count == 1 ? "y" : "ies") +
                          " (" + all.Count + " elements read" + (view != null ? " in this view" : "") +
                          (matched.Count > shown.Count ? "; " + Truncation.Line(shown.Count, matched.Count, "--count Category groups them all, --ids lists every id") : "") + ")");
            sb.AppendLine("=".PadRight(40, '='));
            foreach (var c in cats) sb.AppendLine("  " + c.Key + ": " + c.Value);
            sb.AppendLine();

            foreach (var elem in shown)
            {
                sb.Append("ID: " + elem.Id.IntegerValue + " | " + CategoryName(elem) + " | " + elem.Name);
                AppendTypeAndMark(sb, doc, elem);
                var p = filterReader.Read(elem);
                if (p != null && p.HasValue) sb.Append(" | " + filterReader.Name + ": " + FormattingHelper.GetDisplayValue(p));
                sb.AppendLine();
            }

            if (selectResults && matched.Count > 0)
                sb.AppendLine("\n(" + matched.Count + " elements selected)");
            return sb.ToString();
        }

        private static string CountAny(List<Element> elements, ParamReader reader, string countParam, string expression, string scope,
                                       int read, string countNote, string spellingNote)
        {
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in elements)
            {
                var p = reader.Read(e);
                var val = p == null || !p.HasValue ? null : FormattingHelper.GetDisplayValue(p);
                if (string.IsNullOrWhiteSpace(val)) val = "(empty)";
                groups[val] = groups.TryGetValue(val, out int n) ? n + 1 : 1;
            }
            var sb = new StringBuilder();
            sb.AppendLine("COUNT: ANY" + (expression != null ? " | " + expression : "") + " grouped by " + reader.Name + scope);
            if (spellingNote != null) sb.AppendLine(spellingNote);
            if (countNote != null) sb.AppendLine(countNote);
            sb.AppendLine("Total: " + elements.Count + (expression == null ? " elements carry '" + reader.Name + "'" : "") + " (" + read + " elements read)");
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();
            foreach (var kvp in groups.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("  " + kvp.Key + ": " + kvp.Value);
            return sb.ToString();
        }

        /// <summary>
        /// A reader for <paramref name="name"/> that at least one element carries. Revit's lookup by
        /// name is case-sensitive: when nobody carries the name as typed, the spelling is taken once
        /// from a bounded sample (one element per category and per type, read whole). Null +
        /// <paramref name="missing"/> when no element has the parameter.
        /// </summary>
        private static ParamReader Resolve(Document doc, List<Element> all, string name, out string note, out string missing)
        {
            note = null; missing = null;
            var reader = new ParamReader(doc, name);
            if (all.Any(e => reader.Read(e) != null)) return reader;

            var names = SampleNames(doc, all);
            var spelled = FilterQuery.Spelling(name, names);
            if (spelled != null && spelled != name)
            {
                var again = new ParamReader(doc, spelled);
                if (all.Any(e => again.Read(e) != null))
                {
                    note = "Parameter: '" + spelled + "' (as Revit spells it)";
                    return again;
                }
            }
            var close = FilterQuery.Closest(name, names);
            missing = "ERROR: no element has a parameter '" + name + "'" + (all.Count == 0 ? " (nothing to read here)" : "") + "." +
                      (close.Count > 0 ? "\nHint: closest names: " + string.Join(", ", close.Select(c => "'" + c + "'")) +
                                         (close.Any(c => c.Contains(" ")) ? " — quote a name with spaces: filter any \"" + close.First(c => c.Contains(" ")) + "\" = <value>" : "")
                                       : "\nHint: run 'params' on an element that should have it to see its parameter names.");
            return null;
        }

        /// <summary>Parameter names of a bounded sample: the first element of each category and of each type, and its type.</summary>
        private static List<string> SampleNames(Document doc, List<Element> all)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cats = new HashSet<int>();
            var types = new HashSet<int>();
            int read = 0;
            foreach (var e in all)
            {
                if (read >= SpellingSample) break;
                int cat = e.Category?.Id.IntegerValue ?? 0;
                var tid = e.GetTypeId();
                bool newCat = cats.Add(cat);
                bool newType = tid != ElementId.InvalidElementId && types.Add(tid.IntegerValue);
                if (!newCat && !newType) continue;
                read++;
                AddNames(seen, e);
                if (newType) AddNames(seen, doc.GetElement(tid));
            }
            return seen.ToList();
        }

        private static void AddNames(HashSet<string> names, Element e)
        {
            if (e == null) return;
            try
            {
                foreach (Parameter p in e.Parameters)
                {
                    var n = p?.Definition?.Name;
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            catch { /* an element that cannot list its parameters adds nothing */ }
        }

        /// <summary>One named parameter of an element, else of its type (cached per type id). Never enumerates parameters.</summary>
        private sealed class ParamReader
        {
            private readonly Document _doc;
            private readonly Dictionary<int, Parameter> _byType = new Dictionary<int, Parameter>();
            public readonly string Name;

            public ParamReader(Document doc, string name) { _doc = doc; Name = name; }

            public Parameter Read(Element e)
            {
                Parameter p = null;
                try { p = e.LookupParameter(Name); } catch { p = null; }
                if (p != null && p.HasValue) return p;
                var tid = e.GetTypeId();
                if (tid == ElementId.InvalidElementId) return p;
                if (!_byType.TryGetValue(tid.IntegerValue, out var onType))
                {
                    try { onType = _doc.GetElement(tid)?.LookupParameter(Name); } catch { onType = null; }
                    _byType[tid.IntegerValue] = onType;
                }
                return onType ?? p;
            }
        }

        private static string CategoryName(Element e)
        {
            try { return e.Category?.Name ?? "(no category)"; } catch { return "(no category)"; }
        }

        // ---------------------------------------------------------------- shared

        private static void AppendTypeAndMark(StringBuilder sb, Document doc, Element elem)
        {
            var tid = elem.GetTypeId();
            if (tid != ElementId.InvalidElementId)
            {
                var et = doc.GetElement(tid);
                if (et != null)
                    sb.Append(" | Type: " + et.Name);
            }

            var mark = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (mark != null && mark.HasValue && !string.IsNullOrEmpty(mark.AsString()))
                sb.Append(" | Mark: " + mark.AsString());
        }

        private static bool MatchesFilter(Element elem, Document doc, string paramName, string op, string filterValue)
        {
            var param = FormattingHelper.FindParameter(elem, doc, paramName);
            if (param == null || !param.HasValue) return false;
            return Matches(param, op, filterValue);
        }

        /// <summary>The value comparison both paths share, on a parameter already found.</summary>
        private static bool Matches(Parameter param, string op, string filterValue)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return FilterQuery.Text(param.AsString(), op, filterValue);
                case StorageType.Double:
                    // Compare in the unit 'params' prints (mm / m² / m³ / °).
                    return FilterQuery.Number(RevitUnitHelper.ToDisplay(FormattingHelper.GetInputKind(param), param.AsDouble()), op, filterValue);
                case StorageType.ElementId:
                    // Type, Level, …: compare what the user reads, the name, not the id behind it.
                    return FilterQuery.Text(FormattingHelper.GetDisplayValue(param), op, filterValue);
                case StorageType.Integer:
                    string shown = null;
                    try { shown = param.AsValueString(); } catch { }
                    return FilterQuery.Integer(param.AsInteger(), shown, op, filterValue);
                default:
                    return false;
            }
        }

        private static string BuildCountOutput(List<Element> elements, Document doc, string countParam, string category, View activeView)
        {
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elements)
            {
                var param = FormattingHelper.FindParameter(elem, doc, countParam);
                string val;
                if (param == null || !param.HasValue)
                    val = "(empty)";
                else
                    val = FormattingHelper.GetDisplayValue(param);

                if (!groups.ContainsKey(val))
                    groups[val] = 0;
                groups[val]++;
            }

            var sb = new StringBuilder();
            sb.AppendLine("COUNT: " + category.ToUpper() + " grouped by " + countParam +
                (activeView != null ? " [view: " + activeView.Name + "]" : ""));
            sb.AppendLine("Total: " + elements.Count);
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine();

            foreach (var kvp in groups.OrderByDescending(x => x.Value))
            {
                sb.AppendLine("  " + kvp.Key + ": " + kvp.Value);
            }

            return sb.ToString();
        }

        private static bool ExtractFlag(List<string> tokens, string flag)
        {
            int idx = tokens.FindIndex(t => t.Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                tokens.RemoveAt(idx);
                return true;
            }
            return false;
        }
    }
}
