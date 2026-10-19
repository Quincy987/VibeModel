using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Plan 16: API signatures by reflection over the assemblies actually loaded — in Revit the
    /// RevitAPI / RevitAPIUI of THIS Revit year plus VibeModel's own helpers — so <c>exec</c> code
    /// is written against what exists, not against a remembered API. Pure .NET reflection, no
    /// Revit calls: unit-tested on the framework's own types. Built once per process (the
    /// "how to get one" index scans every public member once).
    /// </summary>
    public sealed class ApiLookup
    {
        /// <summary>Members shown before "(+N more — --filter x)".</summary>
        public const int MaxMembers = 60;

        /// <summary>"How to get one" lines shown.</summary>
        public const int MaxFactories = 12;

        /// <summary>Closest names offered when nothing matches.</summary>
        public const int MaxCandidates = 5;

        // The namespaces exec's wrapper imports (ExecCommand); a type elsewhere needs its full name there.
        private static readonly HashSet<string> ExecImports = new HashSet<string>(StringComparer.Ordinal)
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Text", "Autodesk.Revit.DB", "Autodesk.Revit.UI"
        };

        private readonly List<Type> _types;
        private readonly Dictionary<string, List<Type>> _bySimpleName;
        private readonly Dictionary<string, Type> _byFullName;
        private readonly string[] _preferredNamespaces;
        private Dictionary<Type, List<MemberInfo>> _producers;

        /// <param name="assemblies">The assemblies to search (exported public types only).</param>
        /// <param name="preferredNamespaces">Tie-break when several types share a name: earlier wins.</param>
        public ApiLookup(IEnumerable<Assembly> assemblies, params string[] preferredNamespaces)
        {
            _preferredNamespaces = preferredNamespaces ?? new string[0];
            _types = new List<Type>();
            foreach (var asm in assemblies.Where(a => a != null).Distinct())
            {
                Type[] exported;
                try { exported = asm.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t != null && t.IsPublic).ToArray(); }
                catch { continue; }
                _types.AddRange(exported.Where(t => !t.IsGenericParameter && !IsCompilerGenerated(t)));
            }
            _bySimpleName = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            _byFullName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _types)
            {
                var simple = SimpleName(t);
                if (!_bySimpleName.TryGetValue(simple, out var list)) _bySimpleName[simple] = list = new List<Type>();
                list.Add(t);
                _byFullName[DisplayFullName(t)] = t;
            }
        }

        private static bool IsCompilerGenerated(Type t) => t.Name.IndexOf('<') >= 0;

        // ------------------------------------------------------------------ query

        /// <summary>A parsed <c>apilookup</c> argument string.</summary>
        public sealed class Query
        {
            public string Name { get; set; }
            public string Filter { get; set; }
            public bool All { get; set; }
            public string Error { get; set; }
        }

        /// <summary>"Wall.Create --filter x --all" → name, filter, all.</summary>
        public static Query ParseArgs(string args)
        {
            var q = new Query();
            var tokens = (args ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (t.Equals("--all", StringComparison.OrdinalIgnoreCase)) q.All = true;
                else if (t.Equals("--filter", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Length) { q.Error = "--filter needs a text"; return q; }
                    q.Filter = tokens[++i];
                }
                else if (q.Name == null) q.Name = t;
                else { q.Error = "Unexpected '" + t + "' (one type or Type.Member at a time)"; return q; }
            }
            if (q.Name == null && q.Error == null) q.Error = "Name a type, e.g. apilookup Wall or apilookup Wall.Create";
            return q;
        }

        /// <summary>The result of one lookup: text for the client and the JSON data.</summary>
        public sealed class Result
        {
            public bool Found { get; set; }
            public string Text { get; set; }
            public Dictionary<string, object> Data { get; set; }
        }

        public Result Lookup(string args)
        {
            var q = ParseArgs(args);
            if (q.Error != null)
                return new Result { Found = false, Text = "ERROR: " + q.Error, Data = new Dictionary<string, object> { { "found", false }, { "error", q.Error } } };
            return Lookup(q.Name, q.Filter, q.All);
        }

        /// <summary>
        /// "Wall", "Autodesk.Revit.DB.Wall", "Wall.Create", "StructuralType.Footing". A type is
        /// matched exactly (full or simple name, case-insensitive), else by a unique substring of
        /// its simple name; a member after the last dot narrows the output to that member.
        /// </summary>
        public Result Lookup(string name, string filter, bool all)
        {
            name = (name ?? "").Trim().Trim('.');
            // "List<T>" style input: look up the generic definition.
            int lt = name.IndexOf('<');
            if (lt > 0) name = name.Substring(0, lt);

            var type = ResolveType(name, out var ambiguous);
            string member = null;
            if (type == null && ambiguous == null)
            {
                int dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    var typePart = name.Substring(0, dot);
                    var t = ResolveType(typePart, out var amb2);
                    if (t != null) { type = t; member = name.Substring(dot + 1); }
                    else if (amb2 != null) ambiguous = amb2;
                }
            }

            if (type == null)
            {
                var candidates = ambiguous ?? ClosestForDotted(name);
                return NotFound(name, candidates, ambiguous != null);
            }
            return member == null ? DescribeType(type, filter, all) : DescribeMember(type, member, all);
        }

        // "Autodesk.Revit.DB.Waal" → closest to "Waal"; "Wal.Create" (no such type) → nothing is
        // close to "Create", so closest to "Wal".
        private List<string> ClosestForDotted(string name)
        {
            var segments = name.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var c = ClosestTypes(segments[i]);
                if (c.Count > 0) return c;
            }
            return new List<string>();
        }

        private Type ResolveType(string name, out List<string> ambiguous)
        {
            ambiguous = null;
            if (string.IsNullOrEmpty(name)) return null;
            if (_byFullName.TryGetValue(name, out var full)) return full;
            if (_bySimpleName.TryGetValue(name, out var same))
                return same.Count == 1 ? same[0] : Prefer(same);
            // A dotted name is a full or a nested name; never a substring search.
            if (name.IndexOf('.') >= 0) return null;
            var hits = _types.Where(t => SimpleName(t).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
                ambiguous = hits.OrderBy(t => SimpleName(t).Length).ThenBy(t => DisplayFullName(t), StringComparer.Ordinal)
                    .Take(MaxCandidates).Select(DisplayFullName).ToList();
            return null;
        }

        private Type Prefer(List<Type> types)
        {
            for (int i = 0; i < _preferredNamespaces.Length; i++)
            {
                var hit = types.FirstOrDefault(t => t.Namespace == _preferredNamespaces[i]);
                if (hit != null) return hit;
            }
            return types.OrderBy(t => DisplayFullName(t), StringComparer.Ordinal).First();
        }

        /// <summary>The closest type names: prefix matches first, then by edit distance (≤ 2), then substrings.</summary>
        public List<string> ClosestTypes(string name)
        {
            return Closest(name, _bySimpleName.Keys).Select(n => DisplayFullName(Prefer(_bySimpleName[n]))).ToList();
        }

        internal static List<string> Closest(string name, IEnumerable<string> pool)
        {
            name = name ?? "";
            var scored = new List<Tuple<int, int, string>>();
            foreach (var candidate in pool)
            {
                int rank;
                if (candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase)) rank = 0;
                else
                {
                    int d = EditDistance(name.ToLowerInvariant(), candidate.ToLowerInvariant(), 2);
                    if (d <= 2) rank = 1 + d;
                    else if (name.Length >= 3 && candidate.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) rank = 4;
                    else continue;
                }
                scored.Add(Tuple.Create(rank, candidate.Length, candidate));
            }
            return scored.OrderBy(s => s.Item1).ThenBy(s => s.Item2).ThenBy(s => s.Item3, StringComparer.Ordinal)
                .Select(s => s.Item3).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxCandidates).ToList();
        }

        /// <summary>Levenshtein distance, stopping early above <paramref name="max"/> (returns max + 1).</summary>
        internal static int EditDistance(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    rowMin = Math.Min(rowMin, cur[j]);
                }
                if (rowMin > max) return max + 1;
                var swap = prev; prev = cur; cur = swap;
            }
            return prev[b.Length];
        }

        private static Result NotFound(string name, List<string> candidates, bool ambiguous)
        {
            var sb = new StringBuilder();
            if (ambiguous)
                sb.AppendLine("'" + name + "' matches several types; name one exactly:");
            else
                sb.AppendLine("No type or member '" + name + "' in this Revit's API (RevitAPI / RevitAPIUI / VibeModel).");
            if (candidates.Count > 0)
            {
                if (!ambiguous) sb.AppendLine("Closest:");
                foreach (var c in candidates) sb.AppendLine("  " + c);
            }
            return new Result
            {
                Found = false,
                Text = sb.ToString().TrimEnd(),
                Data = new Dictionary<string, object>
                {
                    { "found", false },
                    { "query", name },
                    { "ambiguous", ambiguous },
                    { "candidates", candidates.Cast<object>().ToList() }
                }
            };
        }

        // ------------------------------------------------------------------ type

        private Result DescribeType(Type type, string filter, bool all)
        {
            var sb = new StringBuilder();
            var data = new Dictionary<string, object> { { "found", true } };
            var fullName = DisplayFullName(type);
            data["type"] = fullName;
            data["namespace"] = type.Namespace;
            data["kind"] = Kind(type);

            var header = fullName + "  (" + KindDetail(type) + ")";
            sb.AppendLine(header);
            sb.AppendLine(UsingLine(type));
            data["using"] = UsingLine(type);
            if (type.BaseType != null && type.BaseType != typeof(object) && !type.IsEnum && !type.IsValueType)
            {
                sb.AppendLine("Base: " + TypeName(type.BaseType, true));
                data["baseType"] = DisplayFullName(type.BaseType);
            }
            var obsoleteType = ObsoleteText(type);
            if (obsoleteType != null) sb.AppendLine("[Obsolete" + obsoleteType + "]");

            if (type.IsEnum)
            {
                var values = new List<object>();
                var names = Enum.GetNames(type);
                var shown = names.Where(n => Matches(n, filter)).ToList();
                sb.AppendLine("Values:");
                foreach (var n in shown.Take(MaxMembers))
                {
                    object raw;
                    try { raw = Convert.ChangeType(Enum.Parse(type, n), Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture); }
                    catch { raw = null; }
                    var field = type.GetField(n);
                    var obs = field != null ? ObsoleteText(field) : null;
                    sb.AppendLine("  " + n + (raw != null ? " = " + Convert.ToString(raw, CultureInfo.InvariantCulture) : "") +
                                  (obs != null ? "   [Obsolete" + obs + "]" : ""));
                    values.Add(n);
                }
                if (shown.Count > MaxMembers) sb.AppendLine("  (+" + (shown.Count - MaxMembers) + " more — --filter <x>)");
                data["enumValues"] = values;
                return new Result { Found = true, Text = sb.ToString().TrimEnd(), Data = data };
            }

            // How to get one.
            var factories = Factories(type);
            if (factories.Count > 0)
            {
                sb.AppendLine("How to get one:");
                foreach (var f in factories.Take(MaxFactories)) sb.AppendLine("  " + f);
                if (factories.Count > MaxFactories) sb.AppendLine("  (+" + (factories.Count - MaxFactories) + " more)");
            }
            data["factories"] = factories.Take(MaxFactories).Cast<object>().ToList();

            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                        (all ? BindingFlags.FlattenHierarchy : BindingFlags.DeclaredOnly);
            int budget = MaxMembers, hidden = 0;
            var obsolete = new List<object>();

            var properties = SafeGet(() => type.GetProperties(flags)).Where(p => Matches(p.Name, filter))
                .Where(p => all || p.DeclaringType == type)
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            var propertyLines = new List<object>();
            if (properties.Count > 0) sb.AppendLine("Properties:");
            foreach (var p in properties)
            {
                var line = PropertySignature(p);
                var obs = ObsoleteText(p);
                if (obs != null) { line += "   [Obsolete" + obs + "]"; obsolete.Add(p.Name + obs); }
                if (budget-- > 0) { sb.AppendLine("  " + line); propertyLines.Add(line); }
                else hidden++;
            }
            data["properties"] = propertyLines;

            var methods = SafeGet(() => type.GetMethods(flags)).Where(m => !m.IsSpecialName && Matches(m.Name, filter))
                .Where(m => all ? !IsObjectNoise(m) : m.DeclaringType == type)
                .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.GetParameters().Length).ToList();
            var methodGroups = new List<object>();
            if (methods.Count > 0) sb.AppendLine("Methods:");
            foreach (var group in methods.GroupBy(m => m.Name))
            {
                var overloads = new List<object>();
                foreach (var m in group)
                {
                    var line = MethodSignature(m, withType: false);
                    var obs = ObsoleteText(m);
                    if (obs != null) { line += "   [Obsolete" + obs + "]"; obsolete.Add(m.Name + obs); }
                    if (budget-- > 0) { sb.AppendLine("  " + line); overloads.Add(line); }
                    else hidden++;
                }
                if (overloads.Count > 0)
                    methodGroups.Add(new Dictionary<string, object> { { "name", group.Key }, { "overloads", overloads } });
            }
            data["methods"] = methodGroups;

            var events = SafeGet(() => type.GetEvents(flags)).Where(e => Matches(e.Name, filter))
                .Where(e => all || e.DeclaringType == type).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            var eventLines = new List<object>();
            if (events.Count > 0) sb.AppendLine("Events:");
            foreach (var e in events)
            {
                var line = e.Name + " : " + TypeName(e.EventHandlerType, false);
                if (budget-- > 0) { sb.AppendLine("  " + line); eventLines.Add(line); }
                else hidden++;
            }
            data["events"] = eventLines;

            if (properties.Count + methods.Count + events.Count == 0)
                sb.AppendLine(filter != null
                    ? "No member matches '" + filter + "'" + (all ? "." : " (declared here; --all adds inherited members).")
                    : all ? "No public members." : "No members declared here; --all shows the inherited ones.");
            if (hidden > 0) sb.AppendLine("(+" + hidden + " more — --filter <x>)");
            data["more"] = hidden;
            data["obsolete"] = obsolete;
            return new Result { Found = true, Text = sb.ToString().TrimEnd(), Data = data };
        }

        private Result DescribeMember(Type type, string member, bool all)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var members = SafeGet(() => type.GetMember(member, flags | BindingFlags.IgnoreCase))
                .Where(m => !(m is MethodInfo mi && mi.IsSpecialName)).ToList();
            var ctorRequested = member.Equals("ctor", StringComparison.OrdinalIgnoreCase) ||
                                member.Equals(SimpleName(type), StringComparison.OrdinalIgnoreCase) && !members.Any();
            if (ctorRequested) members = SafeGet(() => type.GetConstructors()).Cast<MemberInfo>().ToList();

            var fullName = DisplayFullName(type);
            if (members.Count == 0)
            {
                var pool = SafeGet(() => type.GetMembers(flags)).Where(m => !(m is MethodInfo mi && mi.IsSpecialName))
                    .Select(m => m.Name).Distinct().ToList();
                var candidates = Closest(member, pool);
                var sb2 = new StringBuilder();
                sb2.AppendLine(fullName + " has no public member '" + member + "'.");
                if (candidates.Count > 0)
                {
                    sb2.AppendLine("Closest:");
                    foreach (var c in candidates) sb2.AppendLine("  " + SimpleName(type) + "." + c);
                }
                sb2.Append(UsingLine(type));
                return new Result
                {
                    Found = false,
                    Text = sb2.ToString().TrimEnd(),
                    Data = new Dictionary<string, object>
                    {
                        { "found", false }, { "type", fullName }, { "query", member },
                        { "candidates", candidates.Select(c => (object)(SimpleName(type) + "." + c)).ToList() }
                    }
                };
            }

            var sb = new StringBuilder();
            sb.AppendLine(fullName + "." + members[0].Name);
            sb.AppendLine(UsingLine(type));
            var lines = new List<object>();
            var obsolete = new List<object>();
            foreach (var m in members.OrderBy(m => m.MemberType).ThenBy(m => m is MethodBase mb ? mb.GetParameters().Length : 0).Take(MaxMembers))
            {
                string line;
                switch (m)
                {
                    case ConstructorInfo c: line = "new " + SimpleName(type) + "(" + Parameters(c) + ")"; break;
                    case MethodInfo mi: line = MethodSignature(mi, withType: false) + (mi.DeclaringType != type ? "   (from " + SimpleName(mi.DeclaringType) + ")" : ""); break;
                    case PropertyInfo p: line = PropertySignature(p); break;
                    case FieldInfo f: line = (f.IsStatic ? "static " : "") + f.Name + " : " + TypeName(f.FieldType, false) + (f.IsLiteral ? " (const)" : ""); break;
                    case EventInfo e: line = "event " + e.Name + " : " + TypeName(e.EventHandlerType, false); break;
                    default: line = m.Name; break;
                }
                var obs = ObsoleteText(m);
                if (obs != null) { line += "   [Obsolete" + obs + "]"; obsolete.Add(line); }
                sb.AppendLine("  " + line);
                lines.Add(line);
            }
            return new Result
            {
                Found = true,
                Text = sb.ToString().TrimEnd(),
                Data = new Dictionary<string, object>
                {
                    { "found", true }, { "type", fullName }, { "member", members[0].Name },
                    { "signatures", lines }, { "obsolete", obsolete }, { "using", UsingLine(type) }
                }
            };
        }

        // ------------------------------------------------------------------ how to get one

        /// <summary>
        /// Constructors, then every public member in the scanned assemblies that hands out this
        /// type (or a subtype): static ones on the type itself first (Wall.Create), then static
        /// methods elsewhere, then instance methods and properties elsewhere (doc.ActiveView). An
        /// Element subclass also gets the collector line.
        /// </summary>
        public List<string> Factories(Type type)
        {
            var list = new List<string>();
            foreach (var c in SafeGet(() => type.GetConstructors()).OrderBy(c => c.GetParameters().Length))
                list.Add("new " + SimpleName(type) + "(" + Parameters(c) + ")" + Obs(c));

            if (IsElementSubclass(type))
                list.Add("new FilteredElementCollector(doc).OfClass(typeof(" + SimpleName(type) + ")).Cast<" + SimpleName(type) + ">()");

            if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return list;
            var producers = Producers();
            if (!producers.TryGetValue(type, out var members)) return list;
            // An instance member of the type's own hierarchy needs one already (StringBuilder.Append).
            var ordered = members
                .Where(m => IsStatic(m) || !(type.IsAssignableFrom(m.DeclaringType) || m.DeclaringType.IsAssignableFrom(type)))
                .OrderBy(m => m.DeclaringType == type ? 0 : 1)
                .ThenBy(m => IsStatic(m) ? 0 : 1)
                .ThenBy(m => m is PropertyInfo ? 1 : 0)
                .ThenBy(m => SimpleName(m.DeclaringType), StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal);
            foreach (var m in ordered)
            {
                if (m is MethodInfo mi) list.Add(MethodSignature(mi, withType: true) + Obs(mi));
                else if (m is PropertyInfo p) list.Add(Receiver(p.DeclaringType, IsStatic(p)) + "." + p.Name + " : " + TypeName(p.PropertyType, false) + Obs(p));
            }
            return list;
        }

        private static string Obs(MemberInfo m)
        {
            var o = ObsoleteText(m);
            return o == null ? "" : "   [Obsolete" + o + "]";
        }

        // Every public member returning a scanned (non-trivial) type, indexed by that exact type.
        private Dictionary<Type, List<MemberInfo>> Producers()
        {
            if (_producers != null) return _producers;
            var map = new Dictionary<Type, List<MemberInfo>>();
            var scanned = new HashSet<Type>(_types);
            foreach (var t in _types)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var m in SafeGet(() => t.GetMethods(flags)))
                {
                    if (m.IsSpecialName || m.IsGenericMethodDefinition) continue;
                    Add(map, scanned, m.ReturnType, m);
                }
                foreach (var p in SafeGet(() => t.GetProperties(flags)))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    Add(map, scanned, p.PropertyType, p);
                }
            }
            // A member returning a base type is not a factory of its subtypes (doc.GetElement → Element),
            // but one returning a subtype IS a way to get the base: add each producer to its base types.
            var expanded = new Dictionary<Type, List<MemberInfo>>();
            foreach (var kv in map)
            {
                for (var b = kv.Key; b != null && b != typeof(object); b = b.BaseType)
                {
                    if (!scanned.Contains(b)) continue;
                    if (!expanded.TryGetValue(b, out var list)) expanded[b] = list = new List<MemberInfo>();
                    list.AddRange(kv.Value);
                }
            }
            _producers = expanded;
            return _producers;
        }

        private static void Add(Dictionary<Type, List<MemberInfo>> map, HashSet<Type> scanned, Type returned, MemberInfo m)
        {
            if (returned == null || returned == typeof(void) || !scanned.Contains(returned)) return;
            if (returned.IsPrimitive || returned.IsEnum || returned == typeof(string)) return;
            if (!map.TryGetValue(returned, out var list)) map[returned] = list = new List<MemberInfo>();
            list.Add(m);
        }

        private static bool IsStatic(MemberInfo m)
        {
            if (m is MethodBase mb) return mb.IsStatic;
            if (m is PropertyInfo p) return (p.GetGetMethod() ?? p.GetSetMethod())?.IsStatic == true;
            return false;
        }

        private static bool IsElementSubclass(Type type)
        {
            // Strict subclasses only: OfClass(typeof(Element)) is refused by Revit.
            for (var b = type.BaseType; b != null; b = b.BaseType)
                if (b.FullName == "Autodesk.Revit.DB.Element") return true;
            return false;
        }

        // ------------------------------------------------------------------ exec compile errors

        private static readonly Regex MissingMember = new Regex(
            @"'(?<type>[\w\.\+`<>,\s]+?)' does not contain a definition for '(?<member>\w+)'", RegexOptions.Compiled);
        private static readonly Regex MissingType = new Regex(
            @"type or namespace name '(?<type>[\w\.]+)' (?:could not be found|does not exist)", RegexOptions.Compiled);

        /// <summary>
        /// The lookups that would have prevented these exec compile errors: "Wall.Foo" for
        /// "'Autodesk.Revit.DB.Wall' does not contain a definition for 'Foo'" (CS1061/CS0117),
        /// "Waal" for "The type or namespace name 'Waal' could not be found" (CS0246). Distinct, in
        /// order of appearance.
        /// </summary>
        public static List<string> LookupsForCompileErrors(IEnumerable<string> errors)
        {
            var list = new List<string>();
            foreach (var e in errors ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(e)) continue;
                var m = MissingMember.Match(e);
                if (m.Success)
                {
                    var type = m.Groups["type"].Value.Trim();
                    // A generic instance ("List<Wall>") or an array is no API type to look up.
                    if (type.IndexOfAny(new[] { '<', '[', ',' }) >= 0) continue;
                    var simple = type.Substring(type.LastIndexOf('.') + 1);
                    Add(list, simple + "." + m.Groups["member"].Value);
                    continue;
                }
                var t = MissingType.Match(e);
                if (t.Success) Add(list, t.Groups["type"].Value);
            }
            return list;
        }

        private static void Add(List<string> list, string item)
        {
            if (!list.Contains(item, StringComparer.Ordinal)) list.Add(item);
        }

        // ------------------------------------------------------------------ formatting

        private static IEnumerable<T> SafeGet<T>(Func<IEnumerable<T>> get)
        {
            try { return get() ?? Enumerable.Empty<T>(); }
            catch { return Enumerable.Empty<T>(); }
        }

        private static bool Matches(string name, string filter)
            => string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsObjectNoise(MethodInfo m)
            => m.DeclaringType == typeof(object) || m.DeclaringType == typeof(MarshalByRefObject);

        internal static string SimpleName(Type t)
        {
            var name = t.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            return t.IsNested && t.DeclaringType != null ? SimpleName(t.DeclaringType) + "." + name : name;
        }

        internal static string DisplayFullName(Type t)
            => string.IsNullOrEmpty(t.Namespace) ? SimpleName(t) : t.Namespace + "." + SimpleName(t);

        private static string Kind(Type t)
        {
            if (t.IsEnum) return "enum";
            if (t.IsInterface) return "interface";
            if (t.IsValueType) return "struct";
            if (typeof(Delegate).IsAssignableFrom(t)) return "delegate";
            return "class";
        }

        private static string KindDetail(Type t)
        {
            var kind = Kind(t);
            if (kind != "class") return kind;
            if (t.IsAbstract && t.IsSealed) return "static class";
            if (t.IsAbstract) return "abstract class";
            if (t.IsSealed) return "sealed class";
            return "class";
        }

        /// <summary>"using Autodesk.Revit.DB;" — or, for a namespace exec does not import, the full name to write there.</summary>
        internal static string UsingLine(Type t)
        {
            if (string.IsNullOrEmpty(t.Namespace)) return "(global namespace)";
            if (ExecImports.Contains(t.Namespace)) return "using " + t.Namespace + ";   (exec imports it)";
            return "using " + t.Namespace + ";   (exec does not import it: write " + DisplayFullName(t) + ")";
        }

        private static readonly Dictionary<Type, string> Aliases = new Dictionary<Type, string>
        {
            { typeof(void), "void" }, { typeof(bool), "bool" }, { typeof(byte), "byte" }, { typeof(char), "char" },
            { typeof(short), "short" }, { typeof(int), "int" }, { typeof(long), "long" }, { typeof(float), "float" },
            { typeof(double), "double" }, { typeof(decimal), "decimal" }, { typeof(string), "string" },
            { typeof(object), "object" }, { typeof(uint), "uint" }, { typeof(ulong), "ulong" }, { typeof(ushort), "ushort" },
            { typeof(sbyte), "sbyte" }
        };

        /// <summary>C#-style type name: int, IList&lt;ElementId&gt;, XYZ[], double?, Outer.Inner.</summary>
        internal static string TypeName(Type t, bool full)
        {
            if (t == null) return "?";
            if (t.IsByRef) return TypeName(t.GetElementType(), full);
            if (t.IsArray) return TypeName(t.GetElementType(), full) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (Aliases.TryGetValue(t, out var alias)) return alias;
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType)
            {
                if (t.GetGenericTypeDefinition() == typeof(Nullable<>)) return TypeName(t.GetGenericArguments()[0], full) + "?";
                var name = full ? DisplayFullName(t) : SimpleName(t);
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(a => TypeName(a, full))) + ">";
            }
            return full ? DisplayFullName(t) : SimpleName(t);
        }

        private static string Parameters(MethodBase m)
        {
            return string.Join(", ", m.GetParameters().Select(p =>
            {
                var prefix = p.ParameterType.IsByRef ? (p.IsOut ? "out " : "ref ") : "";
                if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0) prefix = "params ";
                var text = prefix + TypeName(p.ParameterType, false) + " " + p.Name;
                if (p.IsOptional && p.HasDefaultValue)
                    text += " = " + (p.DefaultValue == null ? "null" : Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture));
                return text;
            }));
        }

        private static string Receiver(Type declaring, bool isStatic)
        {
            var name = SimpleName(declaring);
            if (isStatic) return name;
            // doc.X, uidoc.X, app.X, uiApp.X read naturally in an exec snippet.
            switch (declaring.FullName)
            {
                case "Autodesk.Revit.DB.Document": return "doc";
                case "Autodesk.Revit.UI.UIDocument": return "uiDoc";
                case "Autodesk.Revit.UI.UIApplication": return "uiApp";
                default: return "(" + name + " x)";
            }
        }

        /// <summary>"static Create(Document document, Curve curve, …) : Wall" — withType prefixes the receiver.</summary>
        internal static string MethodSignature(MethodInfo m, bool withType)
        {
            var generic = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
            var name = withType ? Receiver(m.DeclaringType, m.IsStatic) + "." + m.Name : (m.IsStatic ? "static " : "") + m.Name;
            return name + generic + "(" + Parameters(m) + ") : " + TypeName(m.ReturnType, false);
        }

        internal static string PropertySignature(PropertyInfo p)
        {
            var get = p.GetGetMethod();
            var set = p.GetSetMethod();
            bool isStatic = (get ?? set)?.IsStatic == true;
            var index = p.GetIndexParameters();
            var name = index.Length > 0
                ? "this[" + string.Join(", ", index.Select(i => TypeName(i.ParameterType, false) + " " + i.Name)) + "]"
                : p.Name;
            return (isStatic ? "static " : "") + name + " : " + TypeName(p.PropertyType, false) +
                   " { " + (get != null ? "get; " : "") + (set != null ? "set; " : "") + "}";
        }

        /// <summary>": message" for an [Obsolete] member (": " alone without a message), else null.</summary>
        internal static string ObsoleteText(MemberInfo m)
        {
            try
            {
                var attr = m.GetCustomAttributes(typeof(ObsoleteAttribute), false).OfType<ObsoleteAttribute>().FirstOrDefault();
                if (attr == null) return null;
                return string.IsNullOrWhiteSpace(attr.Message) ? "" : ": " + attr.Message.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
