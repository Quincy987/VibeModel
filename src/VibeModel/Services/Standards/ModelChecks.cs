using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Plan 11 mechanism 4: lint the model against the pack's checks.json. C# implements a fixed
    /// set of rule KINDS; the pack decides which rules exist, their ids, severity, message, fix
    /// and parameters. Two modes: a full audit (`check`), and an element-scoped pass over what a
    /// command just created, appended to its output so the AI can fix its own phase / pin /
    /// parameter mistakes before the modeller sees them.
    /// </summary>
    public static class ModelChecks
    {
        /// <summary>Kinds that judge individual elements and therefore run in scoped mode.</summary>
        public static readonly HashSet<string> ElementKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "phase-not-none", "param-filled", "family-source", "pinned"
        };

        public static readonly string[] KnownKinds =
        {
            "pinned", "phase-not-none", "phase-filter-digit", "view-phase-for-new-work", "view-template-required",
            "family-source", "param-filled", "survey-point-origin", "base-point-rounded", "base-point-pinned",
            "file-name", "schedule-phase", "links-are-links"
        };

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Tuple<DateTime, List<CheckRule>, string>> RuleCache =
            new Dictionary<string, Tuple<DateTime, List<CheckRule>, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pack's rules, re-read only when checks.json changes on disk.</summary>
        public static List<CheckRule> LoadRules(StandardsPack pack, out string error)
        {
            error = null;
            var path = pack?.ChecksPath;
            if (path == null) return new List<CheckRule>();
            try
            {
                if (!File.Exists(path)) { error = "checks file not found: " + path; return new List<CheckRule>(); }
                var stamp = File.GetLastWriteTimeUtc(path);
                lock (Sync)
                {
                    if (RuleCache.TryGetValue(path, out var hit) && hit.Item1 == stamp)
                    {
                        error = hit.Item3;
                        return hit.Item2;
                    }
                }
                var rules = CheckRule.ParseAll(File.ReadAllText(path), out error);
                lock (Sync) RuleCache[path] = Tuple.Create(stamp, rules, error);
                return rules;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return new List<CheckRule>();
            }
        }

        public static string LogPath(StandardsPack pack) =>
            Path.Combine(StandardsPackRegistry.CacheDir(pack.Id), "check-log.jsonl");

        /// <summary>
        /// Run the rules. <paramref name="scope"/> null = whole document; otherwise only
        /// element-level kinds run, restricted to those elements (others are omitted, not
        /// reported as skipped, so the post-command line stays quiet).
        /// </summary>
        public static List<CheckFinding> Run(Document doc, View activeView, StandardsPack pack,
            IEnumerable<CheckRule> rules, ICollection<ElementId> scope)
        {
            var findings = new List<CheckFinding>();
            var ctx = new Ctx(doc, activeView, pack, scope);
            foreach (var r in rules.Where(r => r.Enabled))
            {
                if (scope != null && !ElementKinds.Contains(r.Kind)) continue;
                try
                {
                    var f = Evaluate(ctx, r);
                    if (f != null) findings.Add(f);
                }
                catch (Exception ex)
                {
                    findings.Add(CheckFinding.Skip(r, "rule failed: " + ex.Message));
                    Logger.Warn("check " + r.Id + " failed: " + ex.Message);
                }
            }
            return findings;
        }

        private sealed class Ctx
        {
            public readonly Document Doc;
            public readonly View ActiveView;
            public readonly StandardsPack Pack;
            public readonly ICollection<ElementId> Scope;
            private List<Element> _modelElements;
            private CoordinateFacts _coords;
            private int? _links;

            public Ctx(Document doc, View v, StandardsPack pack, ICollection<ElementId> scope)
            {
                Doc = doc; ActiveView = v; Pack = pack; Scope = scope;
            }

            /// <summary>Model-category instances with phases (whole model, or the scope).</summary>
            public List<Element> ModelElements
            {
                get
                {
                    if (_modelElements != null) return _modelElements;
                    IEnumerable<Element> source = Scope != null
                        ? Scope.Select(id => Doc.GetElement(id)).Where(e => e != null)
                        : new FilteredElementCollector(Doc).WhereElementIsNotElementType().WhereElementIsViewIndependent();
                    _modelElements = source.Where(IsModelInstance).ToList();
                    return _modelElements;
                }
            }

            public CoordinateFacts Coords => _coords ?? (_coords = TemplateFactsCollector.ReadCoordinates(Doc));

            public int CadLinkCount => _links ?? (_links = new FilteredElementCollector(Doc).OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>().Count(i => i.IsLinked)).Value;

            /// <summary>Coordinates are "set up" once something georeferenced exists.</summary>
            public bool CoordinatesSetUp => Coords.SharedCoordinates || CadLinkCount > 0;
        }

        private static bool IsModelInstance(Element e)
        {
            try
            {
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) return false;
                if (e is ElementType || e.ViewSpecific) return false;
                if (cat.Id.IntegerValue == (int)BuiltInCategory.OST_Cameras) return false;
                return e.HasPhases();
            }
            catch { return false; }
        }

        private static CheckFinding Evaluate(Ctx c, CheckRule r)
        {
            switch (r.Kind.ToLowerInvariant())
            {
                case "pinned": return Pinned(c, r);
                case "phase-not-none": return PhaseNotNone(c, r);
                case "phase-filter-digit": return PhaseFilterDigit(c, r);
                case "view-phase-for-new-work": return ViewPhaseForNewWork(c, r);
                case "view-template-required": return ViewTemplateRequired(c, r);
                case "family-source": return FamilySource(c, r);
                case "param-filled": return ParamFilled(c, r);
                case "survey-point-origin": return SurveyPointOrigin(c, r);
                case "base-point-rounded": return BasePointRounded(c, r);
                case "base-point-pinned": return BasePointPinned(c, r);
                case "file-name": return FileName(c, r);
                case "schedule-phase": return SchedulePhase(c, r);
                case "links-are-links": return LinksAreLinks(c, r);
                default: return CheckFinding.Skip(r, "unknown rule kind '" + r.Kind + "' (known: " + string.Join(", ", KnownKinds) + ")");
            }
        }

        // --- kinds --------------------------------------------------------------------------

        private static CheckFinding Pinned(Ctx c, CheckRule r)
        {
            var targets = r.GetStrings("targets");
            if (targets.Count == 0) targets = new List<string> { "cadLinks", "pointClouds", "grids" };
            bool requireSetup = PackJson.GetBool(r.Params, "requireSetup") ?? true;
            if (c.Scope == null && requireSetup && !c.CoordinatesSetUp)
                return CheckFinding.Skip(r, "no underlay linked and no shared coordinates yet — pinning applies once the project is set up");

            var unpinned = new List<int>();
            int total = 0;
            IEnumerable<Element> Pool(Type t) => c.Scope != null
                ? c.Scope.Select(id => c.Doc.GetElement(id)).Where(e => e != null && t.IsInstanceOfType(e))
                : new FilteredElementCollector(c.Doc).OfClass(t).ToElements();

            foreach (var t in targets)
            {
                IEnumerable<Element> pool;
                switch (t.ToLowerInvariant())
                {
                    case "cadlinks": pool = Pool(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => i.IsLinked); break;
                    case "pointclouds": pool = Pool(typeof(PointCloudInstance)); break;
                    case "grids": pool = Pool(typeof(Grid)); break;
                    case "revitlinks": pool = Pool(typeof(RevitLinkInstance)); break;
                    default: continue;
                }
                foreach (var e in pool)
                {
                    total++;
                    if (!e.Pinned) unpinned.Add(e.Id.IntegerValue);
                }
            }
            if (total == 0) return c.Scope != null ? null : CheckFinding.Ok(r, "nothing to pin (" + string.Join(", ", targets) + ")");
            return unpinned.Count == 0
                ? (c.Scope != null ? null : CheckFinding.Ok(r, total + " underlay/grid element(s), all pinned"))
                : CheckFinding.Problem(r, unpinned.Count + " of " + total + " not pinned", unpinned);
        }

        private static CheckFinding PhaseNotNone(Ctx c, CheckRule r)
        {
            var bad = c.ModelElements.Where(e => e.CreatedPhaseId == null || e.CreatedPhaseId == ElementId.InvalidElementId)
                .Select(e => e.Id.IntegerValue).ToList();
            if (bad.Count == 0) return c.Scope != null ? null : CheckFinding.Ok(r, c.ModelElements.Count + " model element(s), all have a creation phase");
            return CheckFinding.Problem(r, bad.Count + " model element(s) without a creation phase", bad);
        }

        private static readonly Regex LeadingDigit = new Regex(@"^\s*(\d+)", RegexOptions.CultureInvariant);

        private static CheckFinding PhaseFilterDigit(Ctx c, CheckRule r)
        {
            var bad = new List<int>();
            var names = new List<string>();
            int checkedViews = 0;
            foreach (View v in new FilteredElementCollector(c.Doc).OfClass(typeof(View)))
            {
                if (v.IsTemplate) continue;
                var ph = TemplateFactsCollector.ElementName(c.Doc, v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId());
                var pf = TemplateFactsCollector.ElementName(c.Doc, v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId());
                if (ph == null || pf == null) continue;
                var a = LeadingDigit.Match(ph);
                var b = LeadingDigit.Match(pf);
                if (!a.Success || !b.Success) continue; // "Show All", "None": always allowed
                checkedViews++;
                if (a.Groups[1].Value != b.Groups[1].Value)
                {
                    bad.Add(v.Id.IntegerValue);
                    if (names.Count < 5) names.Add("'" + v.Name + "' (" + ph + " / " + pf + ")");
                }
            }
            return bad.Count == 0
                ? CheckFinding.Ok(r, checkedViews + " view(s) with numbered phase + filter, all consistent")
                : CheckFinding.Problem(r, bad.Count + " view(s) mix phase and filter numbers: " + string.Join(", ", names), bad);
        }

        private static CheckFinding ViewPhaseForNewWork(Ctx c, CheckRule r)
        {
            var existing = c.Pack?.PhaseTable?.ExistingPhase;
            var v = c.ActiveView;
            if (existing == null || v == null) return CheckFinding.Skip(r, "no phase table or no active view");
            var ph = TemplateFactsCollector.ElementName(c.Doc, v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId());
            if (ph == null) return CheckFinding.Skip(r, "active view '" + v.Name + "' has no phase");
            return string.Equals(ph, existing, StringComparison.OrdinalIgnoreCase)
                ? CheckFinding.Problem(r, "active view '" + v.Name + "' is on phase '" + ph + "': new work drawn here by hand becomes existing and VibeModel-created new work is not visible in it",
                    new[] { v.Id.IntegerValue })
                : CheckFinding.Ok(r, "active view '" + v.Name + "' is on phase '" + ph + "'");
        }

        private static CheckFinding ViewTemplateRequired(Ctx c, CheckRule r)
        {
            var pattern = r.GetString("namePattern");
            Regex rx = string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool onSheets = PackJson.GetBool(r.Params, "onSheets") ?? true;

            var placed = new HashSet<int>();
            if (onSheets)
                foreach (Viewport vp in new FilteredElementCollector(c.Doc).OfClass(typeof(Viewport)))
                    placed.Add(vp.ViewId.IntegerValue);

            var bad = new List<int>();
            var names = new List<string>();
            int considered = 0;
            foreach (View v in new FilteredElementCollector(c.Doc).OfClass(typeof(View)))
            {
                if (v.IsTemplate || v is ViewSheet || v is ViewSchedule || v.ViewType == ViewType.Legend ||
                    v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.ProjectBrowser || v.ViewType == ViewType.SystemBrowser)
                    continue;
                bool plotView = (rx != null && rx.IsMatch(v.Name)) || placed.Contains(v.Id.IntegerValue);
                if (!plotView) continue;
                considered++;
                if (v.ViewTemplateId == null || v.ViewTemplateId == ElementId.InvalidElementId)
                {
                    bad.Add(v.Id.IntegerValue);
                    if (names.Count < 5) names.Add("'" + v.Name + "'");
                }
            }
            return bad.Count == 0
                ? CheckFinding.Ok(r, considered + " plot/sheet view(s), all with a view template")
                : CheckFinding.Problem(r, bad.Count + " plot/sheet view(s) without a view template: " + string.Join(", ", names), bad);
        }

        private static CheckFinding FamilySource(Ctx c, CheckRule r)
        {
            var prefixes = r.GetStrings("prefixes");
            if (prefixes.Count == 0) prefixes = c.Pack?.FamilyPrefixes ?? new List<string>();
            if (prefixes.Count == 0) return CheckFinding.Skip(r, "pack declares no family prefixes");
            var ignore = new HashSet<string>(r.GetStrings("ignoreCategories"), StringComparer.OrdinalIgnoreCase);

            IEnumerable<Family> families;
            if (c.Scope != null)
                families = c.Scope.Select(id => c.Doc.GetElement(id)).OfType<FamilyInstance>()
                    .Select(fi => fi.Symbol?.Family).Where(f => f != null).GroupBy(f => f.Id.IntegerValue).Select(g => g.First());
            else
                families = new FilteredElementCollector(c.Doc).OfClass(typeof(Family)).Cast<Family>();

            var bad = new List<int>();
            var names = new List<string>();
            int total = 0;
            foreach (var f in families)
            {
                Category cat = null;
                try { cat = f.FamilyCategory; } catch { }
                if (cat == null || cat.CategoryType != CategoryType.Model || ignore.Contains(cat.Name)) continue;
                if (f.IsInPlace) continue;
                total++;
                if (!prefixes.Any(p => f.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    bad.Add(f.Id.IntegerValue);
                    if (names.Count < 8) names.Add(f.Name);
                }
            }
            if (total == 0) return c.Scope != null ? null : CheckFinding.Ok(r, "no loadable model families");
            return bad.Count == 0
                ? (c.Scope != null ? null : CheckFinding.Ok(r, total + " model famil(ies), all from the office sources"))
                : CheckFinding.Problem(r, bad.Count + " of " + total + " model famil(ies) without an office prefix: " + string.Join(", ", names), bad);
        }

        private static CheckFinding ParamFilled(Ctx c, CheckRule r)
        {
            var parameters = r.GetStrings("parameters");
            if (parameters.Count == 0) return CheckFinding.Skip(r, "rule lists no parameters");

            // Which elements: those created in the phase of the given kind (default: new work).
            var key = r.GetString("phaseKey", "new");
            var phaseName = c.Pack?.PhaseTable?.Resolve(key)?.Created;
            var phase = phaseName != null ? PhaseDefaults.FindPhase(c.Doc, phaseName) : null;

            var bad = new List<int>();
            var missingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int considered = 0;
            foreach (var e in c.ModelElements)
            {
                if (phase != null && e.CreatedPhaseId != phase.Id) continue;
                bool any = false, missing = false;
                foreach (var pn in parameters)
                {
                    var p = e.LookupParameter(pn);
                    if (p == null) continue; // not bound to this category: not this rule's business
                    any = true;
                    if (!p.HasValue || string.IsNullOrWhiteSpace(p.AsString() ?? p.AsValueString()))
                    {
                        missing = true;
                        missingCounts[pn] = missingCounts.TryGetValue(pn, out var n) ? n + 1 : 1;
                    }
                }
                if (!any) continue;
                considered++;
                if (missing) bad.Add(e.Id.IntegerValue);
            }
            if (considered == 0)
                return c.Scope != null ? null : CheckFinding.Ok(r, "no elements" + (phaseName != null ? " in phase '" + phaseName + "'" : "") + " carry these parameters");
            return bad.Count == 0
                ? (c.Scope != null ? null : CheckFinding.Ok(r, considered + " element(s) have " + string.Join(", ", parameters) + " filled"))
                : CheckFinding.Problem(r, bad.Count + " of " + considered + " element(s)" + (phaseName != null ? " in '" + phaseName + "'" : "") +
                    " have empty " + string.Join(", ", missingCounts.Select(kv => kv.Key + " (" + kv.Value + ")")), bad);
        }

        private static CheckFinding SurveyPointOrigin(Ctx c, CheckRule r)
        {
            if (!c.Coords.Available) return CheckFinding.Skip(r, "no survey point");
            var tol = r.GetDouble("toleranceMm", 1);
            return c.Coords.SurveyOffsetFromOriginMm <= tol
                ? CheckFinding.Ok(r, "survey point at the internal origin")
                : CheckFinding.Problem(r, "survey point moved " + Math.Round(c.Coords.SurveyOffsetFromOriginMm) + " mm from the internal origin");
        }

        private static CheckFinding BasePointRounded(Ctx c, CheckRule r)
        {
            if (!c.Coords.Available) return CheckFinding.Skip(r, "no project base point");
            if (!c.Coords.SharedCoordinates) return CheckFinding.Skip(r, "no shared coordinates yet — rounding applies after acquiring them");
            var step = r.GetDouble("roundToMm", c.Pack?.CoordinateRoundingMm ?? 1000);
            bool ok = StandardsRender.IsRound(c.Coords.BaseNorthMm, step) &&
                      StandardsRender.IsRound(c.Coords.BaseEastMm, step);
            return ok
                ? CheckFinding.Ok(r, "base point on round values (" + step + " mm)")
                : CheckFinding.Problem(r, "base point N/S " + c.Coords.BaseNorthMm + ", E/W " + c.Coords.BaseEastMm + " mm are not multiples of " + step + " mm");
        }

        private static CheckFinding BasePointPinned(Ctx c, CheckRule r)
        {
            if (!c.Coords.Available) return CheckFinding.Skip(r, "no project base point");
            bool requireSetup = PackJson.GetBool(r.Params, "requireSetup") ?? true;
            if (requireSetup && !c.CoordinatesSetUp) return CheckFinding.Skip(r, "coordinates not set up yet — pin after acquiring them");
            return c.Coords.BasePinned
                ? CheckFinding.Ok(r, "project base point pinned")
                : CheckFinding.Problem(r, "project base point is not pinned", new[] { BasePoint.GetProjectBasePoint(c.Doc).Id.IntegerValue });
        }

        private static readonly Regex BackupName = new Regex(@"\.\d{4}\.rvt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static CheckFinding FileName(Ctx c, CheckRule r)
        {
            var path = c.Doc.PathName;
            if (string.IsNullOrEmpty(path)) return CheckFinding.Skip(r, "document not saved yet");
            var name = Path.GetFileName(path);
            if (BackupName.IsMatch(name))
                return CheckFinding.Problem(r, "'" + name + "' is a Revit backup copy (.NNNN.rvt) — work in the main file");
            var pattern = r.GetString("pattern");
            if (string.IsNullOrWhiteSpace(pattern)) return CheckFinding.Ok(r, "'" + name + "' (no naming pattern declared)");
            return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                ? CheckFinding.Ok(r, "'" + name + "' matches the naming rule")
                : CheckFinding.Problem(r, "'" + name + "' does not match " + pattern);
        }

        private static CheckFinding SchedulePhase(Ctx c, CheckRule r)
        {
            var pattern = r.GetString("namePattern");
            if (string.IsNullOrWhiteSpace(pattern)) return CheckFinding.Skip(r, "rule has no namePattern");
            var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var key = r.GetString("phaseKey", "new");
            var expectedPhase = c.Pack?.PhaseTable?.Resolve(key)?.Created;
            var expectedFilter = r.GetString("phaseFilter");

            var bad = new List<int>();
            var names = new List<string>();
            int considered = 0;
            foreach (ViewSchedule s in new FilteredElementCollector(c.Doc).OfClass(typeof(ViewSchedule)))
            {
                if (s.IsTemplate || !rx.IsMatch(s.Name)) continue;
                considered++;
                var ph = TemplateFactsCollector.ElementName(c.Doc, s.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId());
                var pf = TemplateFactsCollector.ElementName(c.Doc, s.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId());
                bool okPhase = expectedPhase == null || string.Equals(ph, expectedPhase, StringComparison.OrdinalIgnoreCase);
                bool okFilter = expectedFilter == null || string.Equals(pf, expectedFilter, StringComparison.OrdinalIgnoreCase);
                if (!okPhase || !okFilter)
                {
                    bad.Add(s.Id.IntegerValue);
                    names.Add("'" + s.Name + "' (" + ph + " / " + pf + ")");
                }
            }
            if (considered == 0) return CheckFinding.Ok(r, "no schedule matches " + pattern);
            return bad.Count == 0
                ? CheckFinding.Ok(r, considered + " schedule(s) on phase '" + expectedPhase + "'" + (expectedFilter != null ? " / '" + expectedFilter + "'" : ""))
                : CheckFinding.Problem(r, string.Join(", ", names) + " — expected '" + expectedPhase + "'" + (expectedFilter != null ? " / '" + expectedFilter + "'" : ""), bad);
        }

        private static CheckFinding LinksAreLinks(Ctx c, CheckRule r)
        {
            var imports = new FilteredElementCollector(c.Doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => !i.IsLinked).Select(i => i.Id.IntegerValue).ToList();
            return imports.Count == 0
                ? CheckFinding.Ok(r, "no imported CAD (" + c.CadLinkCount + " link(s))")
                : CheckFinding.Problem(r, imports.Count + " CAD file(s) imported instead of linked", imports);
        }
    }
}
