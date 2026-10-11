using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// An office standards pack: a folder with pack.json, an always-on core rule sheet and
    /// on-demand topic sheets (plan 11). The pack is DATA — every office-specific name
    /// (phase names, view templates, prefixes, paths) comes from these files, never from C#.
    ///
    /// Pure: file reads only, no Revit API, so it is unit-testable headlessly. Loading never
    /// throws — a broken pack is reported through <see cref="TryLoad"/>'s error and skipped.
    /// </summary>
    public sealed class StandardsPack
    {
        public const string PackFileName = "pack.json";
        public const string GeneratedCoreMarker = "generated";
        public const int DefaultCoreMaxBytes = 4096;

        public string Folder { get; private set; }
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Owner { get; private set; }
        public List<PackSource> Sources { get; private set; } = new List<PackSource>();
        public PackFingerprint Fingerprint { get; private set; } = new PackFingerprint();
        public Dictionary<string, string> Paths { get; private set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Relative core file, or "generated" (= core.generated.md from the distiller).</summary>
        public string CoreFile { get; private set; }
        public int CoreMaxBytes { get; private set; } = DefaultCoreMaxBytes;

        /// <summary>Ordered topic name → relative file.</summary>
        public List<KeyValuePair<string, string>> Topics { get; private set; } =
            new List<KeyValuePair<string, string>>();

        public PhaseTable PhaseTable { get; private set; }
        public Dictionary<string, ViewDefault> ViewDefaults { get; private set; } =
            new Dictionary<string, ViewDefault>(StringComparer.OrdinalIgnoreCase);
        public List<string> FamilyPrefixes { get; private set; } = new List<string>();
        public List<string> LibraryPaths { get; private set; } = new List<string>();
        public string ChecksFile { get; private set; }
        public SheetDefaults Sheets { get; private set; } = new SheetDefaults();
        public double CoordinateRoundingMm { get; private set; } = 1000;
        public LinkCadDefaults LinkCad { get; private set; } = new LinkCadDefaults();
        public string StandardsFolder { get; private set; }
        public List<string> DistillChapters { get; private set; } = new List<string>();

        /// <summary>
        /// Name of the optional office additions file — rules not (yet) in the manual, appended to
        /// the core verbatim. Looked for next to pack.json and in the standards folder.
        /// </summary>
        public string AdditionsFile { get; private set; } = "additions.md";

        /// <summary>
        /// Set by the registry: where a distilled core (core.generated.md) and the office
        /// additions file (aanvullingen.md) live. Optional; null means "not configured".
        /// </summary>
        public string GeneratedCorePath { get; set; }
        public List<string> AdditionsPaths { get; set; } = new List<string>();

        public IEnumerable<string> TopicNames => Topics.Select(t => t.Key);

        public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

        /// <summary>
        /// Load a pack from a folder containing pack.json. Returns null with an error message
        /// when the folder has no usable pack — never throws.
        /// </summary>
        public static StandardsPack TryLoad(string folder, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    error = "empty folder path";
                    return null;
                }
                var file = Path.Combine(folder, PackFileName);
                if (!File.Exists(file))
                {
                    error = "no " + PackFileName + " in " + folder;
                    return null;
                }

                var root = PackJson.Parse(File.ReadAllText(file, Encoding.UTF8));
                if (root == null)
                {
                    error = PackFileName + " is not a JSON object";
                    return null;
                }
                return FromJson(root, folder);
            }
            catch (Exception ex)
            {
                error = "cannot read pack in " + folder + ": " + ex.Message;
                return null;
            }
        }

        internal static StandardsPack FromJson(Dictionary<string, object> root, string folder)
        {
            var pack = new StandardsPack { Folder = folder };

            pack.Id = PackJson.GetString(root, "id");
            if (string.IsNullOrWhiteSpace(pack.Id))
                pack.Id = string.IsNullOrEmpty(folder) ? "pack" : new DirectoryInfo(folder).Name;
            pack.Name = PackJson.GetString(root, "name");
            pack.Version = PackJson.GetString(root, "version");
            pack.Owner = PackJson.GetString(root, "owner");

            foreach (var s in PackJson.GetDictList(root, "sources"))
                pack.Sources.Add(new PackSource
                {
                    Title = PackJson.GetString(s, "title"),
                    Date = PackJson.GetString(s, "date"),
                    Path = PackJson.GetString(s, "path")
                });

            pack.Fingerprint = PackFingerprint.FromJson(PackJson.GetDict(root, "fingerprint"));
            pack.Paths = PackJson.GetStringMap(root, "paths");

            pack.CoreFile = PackJson.GetString(root, "core") ?? "core.md";
            var cap = PackJson.GetDouble(root, "coreMaxBytes");
            if (cap.HasValue && cap.Value >= 256) pack.CoreMaxBytes = (int)cap.Value;

            var topics = PackJson.GetDict(root, "topics");
            if (topics != null)
                foreach (var kv in topics)
                    if (kv.Value is string rel && !string.IsNullOrWhiteSpace(rel))
                        pack.Topics.Add(new KeyValuePair<string, string>(kv.Key, rel));

            pack.PhaseTable = PhaseTable.FromJson(PackJson.GetDict(root, "phaseTable"));

            var views = PackJson.GetDict(root, "viewDefaults");
            if (views != null)
                foreach (var kv in views)
                {
                    var vd = ViewDefault.FromJson(PackJson.AsDict(kv.Value));
                    if (vd != null) pack.ViewDefaults[kv.Key] = vd;
                }

            pack.FamilyPrefixes = PackJson.GetStrings(root, "familyPrefixes");

            pack.LibraryPaths = PackJson.GetStrings(root, "libraryPaths");
            if (pack.LibraryPaths.Count == 0)
            {
                // Fall back to the conventional path keys so an older pack still gets a library.
                foreach (var key in new[] { "library", "templateFamilies" })
                    if (pack.Paths.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p))
                        pack.LibraryPaths.Add(p);
            }

            pack.ChecksFile = PackJson.GetString(root, "checks");
            pack.Sheets = SheetDefaults.FromJson(PackJson.GetDict(root, "sheets"));

            var coords = PackJson.GetDict(root, "coordinates");
            var round = PackJson.GetDouble(coords, "roundToMm");
            if (round.HasValue && round.Value > 0) pack.CoordinateRoundingMm = round.Value;

            pack.LinkCad = LinkCadDefaults.FromJson(PackJson.GetDict(root, "linkCad"));
            pack.StandardsFolder = PackJson.GetString(root, "standardsFolder");
            pack.DistillChapters = PackJson.GetStrings(PackJson.GetDict(root, "distill"), "chapters");
            var additions = PackJson.GetString(root, "additionsFile");
            if (!string.IsNullOrWhiteSpace(additions) && additions.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                pack.AdditionsFile = additions.Trim();

            return pack;
        }

        // --- Content -------------------------------------------------------------------

        /// <summary>
        /// The always-on rule text: the distilled core when the pack asks for it and one
        /// exists, otherwise the hand-written core file; plus any office additions file.
        /// Missing files yield an empty string, never an exception.
        /// </summary>
        public string GetCoreText()
        {
            string text = null;
            if (string.Equals(CoreFile, GeneratedCoreMarker, StringComparison.OrdinalIgnoreCase))
            {
                text = ReadIfExists(GeneratedCorePath);
                if (text == null)
                    text = ReadIfExists(Combine("core.md")); // fallback until a distillation exists
            }
            else
            {
                text = ReadIfExists(Combine(CoreFile));
            }

            var sb = new StringBuilder((text ?? "").TrimEnd());
            foreach (var add in AdditionsPaths ?? new List<string>())
            {
                var extra = ReadIfExists(add);
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    if (sb.Length > 0) sb.AppendLine().AppendLine();
                    sb.Append(extra.TrimEnd());
                }
            }
            return sb.ToString();
        }

        /// <summary>Topic sheet text, or null when the topic is unknown or its file is missing.</summary>
        public string GetTopicText(string topic)
        {
            var rel = FindTopicFile(topic);
            return rel == null ? null : ReadIfExists(Combine(rel));
        }

        public bool HasTopic(string topic) => FindTopicFile(topic) != null;

        private string FindTopicFile(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic)) return null;
            foreach (var t in Topics)
                if (string.Equals(t.Key, topic.Trim(), StringComparison.OrdinalIgnoreCase))
                    return t.Value;
            return null;
        }

        public string ChecksPath => string.IsNullOrWhiteSpace(ChecksFile) ? null : Combine(ChecksFile);

        private string Combine(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            try
            {
                return Path.IsPathRooted(rel) ? rel : Path.Combine(Folder ?? "", rel);
            }
            catch { return null; }
        }

        internal static string ReadIfExists(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { /* unreadable → treated as missing */ }
            return null;
        }
    }

    public sealed class PackSource
    {
        public string Title { get; set; }
        public string Date { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// What a document must contain for the pack to apply. Names are matched case-insensitively;
    /// <see cref="Aliases"/> maps a canonical name to alternative spellings (e.g. the measured
    /// template typo "Projectomstrek_WCS" vs the leidraad's "Projectomtrek_WCS").
    /// </summary>
    public sealed class PackFingerprint
    {
        public List<string> Phases { get; set; } = new List<string>();
        public List<string> ScopeBoxes { get; set; } = new List<string>();
        public List<string> Views { get; set; } = new List<string>();
        public List<string> ViewTemplates { get; set; } = new List<string>();
        public List<string> SharedParameters { get; set; } = new List<string>();
        public Dictionary<string, List<string>> Aliases { get; set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// No REQUIRED criterion (phases, scope boxes, shared parameters) is declared: the pack
        /// applies to every document. Views / view templates are supporting evidence only.
        /// </summary>
        public bool IsEmpty => Phases.Count == 0 && ScopeBoxes.Count == 0 && SharedParameters.Count == 0;

        internal static PackFingerprint FromJson(Dictionary<string, object> d)
        {
            var fp = new PackFingerprint();
            if (d == null) return fp;
            fp.Phases = PackJson.GetStrings(d, "phases");
            fp.ScopeBoxes = PackJson.GetStrings(d, "scopeBoxes");
            fp.Views = PackJson.GetStrings(d, "views");
            fp.ViewTemplates = PackJson.GetStrings(d, "viewTemplates");
            fp.SharedParameters = PackJson.GetStrings(d, "sharedParameters");
            var aliases = PackJson.GetDict(d, "aliases");
            if (aliases != null)
                foreach (var kv in aliases)
                {
                    var list = PackJson.GetStrings(aliases, kv.Key);
                    if (list.Count > 0) fp.Aliases[kv.Key] = list;
                }
            return fp;
        }

        /// <summary>The name plus its declared alternative spellings.</summary>
        public IEnumerable<string> Spellings(string name)
        {
            yield return name;
            if (Aliases.TryGetValue(name, out var alts))
                foreach (var a in alts) yield return a;
        }
    }

    /// <summary>One row of the office phase table: which phases a "kind" of element gets.</summary>
    public sealed class PhaseMapping
    {
        public string Key { get; set; }
        public string Created { get; set; }
        /// <summary>Null = "None" (never demolished).</summary>
        public string Demolished { get; set; }
    }

    /// <summary>
    /// The office's phase table (leidraad "Tabel 1"): kind key (existing/new/temp/demolish/
    /// future) → Phase Created + Phase Demolished, with a default key and optional aliases in
    /// the office language. Creating commands use it for their --phase argument.
    /// </summary>
    public sealed class PhaseTable
    {
        public string DefaultKey { get; private set; } = "new";
        public List<PhaseMapping> Entries { get; } = new List<PhaseMapping>();
        public Dictionary<string, string> Aliases { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Keys => Entries.Select(e => e.Key);

        internal static PhaseTable FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var t = new PhaseTable();
            var def = PackJson.GetString(d, "default");
            if (!string.IsNullOrWhiteSpace(def)) t.DefaultKey = def.Trim();

            var entries = PackJson.GetDict(d, "entries");
            if (entries != null)
                foreach (var kv in entries)
                {
                    var e = PackJson.AsDict(kv.Value);
                    var created = PackJson.GetString(e, "created");
                    if (string.IsNullOrWhiteSpace(created)) continue;
                    var demolished = PackJson.GetString(e, "demolished");
                    if (string.Equals(demolished, "None", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(demolished))
                        demolished = null;
                    t.Entries.Add(new PhaseMapping
                    {
                        Key = kv.Key,
                        Created = created,
                        Demolished = demolished
                    });
                }

            foreach (var kv in PackJson.GetStringMap(d, "aliases"))
                t.Aliases[kv.Key] = kv.Value;

            return t.Entries.Count == 0 ? null : t;
        }

        /// <summary>Resolve a key or alias (case-insensitive) to its row.</summary>
        public PhaseMapping Resolve(string keyOrAlias)
        {
            if (string.IsNullOrWhiteSpace(keyOrAlias)) return null;
            var k = keyOrAlias.Trim();
            if (Aliases.TryGetValue(k, out var canonical)) k = canonical;
            return Entries.FirstOrDefault(e => string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase));
        }

        public PhaseMapping Default => Resolve(DefaultKey);

        /// <summary>The phase an existing element is created in (used by view-phase checks).</summary>
        public string ExistingPhase => Resolve("existing")?.Created;
    }

    /// <summary>What a new view of one kind (plan / threeD / section ...) gets in this office.</summary>
    public sealed class ViewDefault
    {
        public string Template { get; set; }
        public string PhaseKey { get; set; }
        public string PhaseFilter { get; set; }
        public Dictionary<string, string> Parameters { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static ViewDefault FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            return new ViewDefault
            {
                Template = PackJson.GetString(d, "template"),
                PhaseKey = PackJson.GetString(d, "phase"),
                PhaseFilter = PackJson.GetString(d, "phaseFilter"),
                Parameters = PackJson.GetStringMap(d, "parameters")
            };
        }
    }

    public sealed class SheetDefaults
    {
        public string DefaultTitleblock { get; set; }
        public List<string> StampParameters { get; set; } = new List<string>();

        internal static SheetDefaults FromJson(Dictionary<string, object> d)
        {
            var s = new SheetDefaults();
            if (d == null) return s;
            s.DefaultTitleblock = PackJson.GetString(d, "defaultTitleblock");
            s.StampParameters = PackJson.GetStrings(d, "stampParameters");
            return s;
        }
    }

    public sealed class LinkCadDefaults
    {
        public bool PinAfterLink { get; set; } = true;
        public bool CorrectOffAxisLines { get; set; }
        public bool OrientToView { get; set; }
        public string RefuseFirstDgnReason { get; set; }

        internal static LinkCadDefaults FromJson(Dictionary<string, object> d)
        {
            var l = new LinkCadDefaults();
            if (d == null) return l;
            l.PinAfterLink = PackJson.GetBool(d, "pinAfterLink") ?? true;
            l.CorrectOffAxisLines = PackJson.GetBool(d, "correctOffAxisLines") ?? false;
            l.OrientToView = PackJson.GetBool(d, "orientToView") ?? false;
            l.RefuseFirstDgnReason = PackJson.GetString(d, "refuseFirstDgnReason");
            return l;
        }
    }
}
