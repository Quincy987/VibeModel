using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Revit side of <see cref="ScheduleScopeSummary"/> and <see cref="CoverageReport"/>: what a
    /// schedule leaves out of its category, and which new elements an existing schedule or a view
    /// VibeModel put on a sheet this session does not show. Read-only — never a transaction (the
    /// coverage runs after the command's group ended; the stamp and the edit counter react to
    /// DocumentChanged). Every failure is logged and returns null: a report never fails the command.
    /// </summary>
    internal static class DerivedCoverage
    {
        private const int MaxSchedules = 20, MaxViews = 10;

        // ------------------------------------------------------------------ schedule scope

        public static ScheduleScopeSummary ComputeScope(Document doc, ViewSchedule vs, int rows)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var def = vs.Definition;
                var s = new ScheduleScopeSummary { Rows = rows };
                if (def.IsKeySchedule) { s.SkippedReason = "key schedule"; return s; }
                if (def.IsMaterialTakeoff) { s.SkippedReason = "material takeoff"; return s; }
                if (def.CategoryId == null || def.CategoryId == ElementId.InvalidElementId) { s.SkippedReason = "multi-category schedule"; return s; }
                var cat = Category.GetCategory(doc, def.CategoryId);
                if (cat == null) { s.SkippedReason = "category unknown"; return s; }
                s.Category = cat.Name;
                s.IncludesLinks = def.IncludeLinkedFiles;
                try { s.Embedded = def.HasEmbeddedSchedule; } catch { /* no embedded schedule */ }

                var areaScheme = AreaSchemeOf(doc, def);
                if (areaScheme != null) s.AreaScheme = areaScheme.Name;

                var inSchedule = new HashSet<int>(new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType()
                    .ToElementIds().Select(i => i.IntegerValue));
                var all = CategoryMembers(doc, def.CategoryId, areaScheme?.Id).Select(i => i.IntegerValue).ToList();
                // A schedule can list what the category collector misses (an embedded or linked row): never "shows 30 of 22".
                s.InSchedule = inSchedule.Count;
                s.TotalInCategory = Math.Max(all.Count, inSchedule.Count);
                s.FilterDescriptions = DescribeFilters(doc, def);
                s.FilteredBySheet = IsFilteredBySheet(def);

                var phaseId = vs.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                var phaseFilter = doc.GetElement(vs.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId) as PhaseFilter;
                if (phaseId != null && phaseId != ElementId.InvalidElementId) s.PhaseName = doc.GetElement(phaseId)?.Name;
                s.PhaseFilterName = phaseFilter?.Name;

                var outside = all.Where(i => !inSchedule.Contains(i)).ToList();
                if (outside.Count > ScheduleScopeSummary.ReasonCap) { s.ReasonsSkipped = true; return s; }

                // Phase and design option are decided per element; the rest is put down to the filters /
                // filter-by-sheet as a whole (ScheduleScopeSummary.Rest), never re-evaluated here.
                foreach (var idValue in outside)
                {
                    var e = doc.GetElement(new ElementId(idValue));
                    if (e == null) continue;
                    if (OutByPhase(e, phaseId, phaseFilter)) s.OutByPhase++;
                    else if (InSecondaryOption(e)) s.OutByDesignOption++;
                }
                return s;
            }
            catch (Exception ex)
            {
                Logger.Warn("Schedule scope skipped: " + ex.Message);
                return null;
            }
            finally
            {
                if (sw.ElapsedMilliseconds > 200) Logger.Warn("Schedule scope took " + sw.ElapsedMilliseconds + " ms (" + vs?.Name + ")");
            }
        }

        private static bool IsFilteredBySheet(ScheduleDefinition def)
        {
#if REVIT2022
            return false; // the property arrived in Revit 2023
#else
            try { return def.IsFilteredBySheet; } catch { return false; }
#endif
        }

        /// <summary>
        /// An area schedule lists one area scheme (`ViewSchedule.CreateSchedule(doc, areas, scheme)`); null
        /// for every other schedule.
        /// </summary>
        private static AreaScheme AreaSchemeOf(Document doc, ScheduleDefinition def)
        {
            if (def.CategoryId.IntegerValue != (int)BuiltInCategory.OST_Areas) return null;
            try { return doc.GetElement(def.AreaSchemeId) as AreaScheme; } catch { return null; }
        }

        /// <summary>The instances a schedule of this category could list: every instance, or the areas of one scheme.</summary>
        private static IEnumerable<ElementId> CategoryMembers(Document doc, ElementId categoryId, ElementId areaSchemeId)
        {
            var collector = new FilteredElementCollector(doc).OfCategoryId(categoryId).WhereElementIsNotElementType();
            if (areaSchemeId == null) return collector.ToElementIds();
            return collector.OfType<Area>().Where(a => a.AreaScheme?.Id == areaSchemeId).Select(a => a.Id).ToList();
        }

        /// <summary>
        /// True when the schedule's phase / phase filter hides the element at the schedule's phase. A
        /// future element (not built yet) or a past one (demolished in an earlier phase) is never shown,
        /// whatever the filter; the filter only decides for new, existing, demolished and temporary.
        /// </summary>
        private static bool OutByPhase(Element e, ElementId phaseId, PhaseFilter filter)
        {
            if (phaseId == null || phaseId == ElementId.InvalidElementId) return false;
            try
            {
                if (!e.HasPhases()) return false;
                var status = e.GetPhaseStatus(phaseId);
                switch (status)
                {
                    case ElementOnPhaseStatus.Future:
                    case ElementOnPhaseStatus.Past:
                        return true;
                    case ElementOnPhaseStatus.New:
                    case ElementOnPhaseStatus.Existing:
                    case ElementOnPhaseStatus.Demolished:
                    case ElementOnPhaseStatus.Temporary:
                        return filter != null && filter.GetPhaseStatusPresentation(status) == PhaseStatusPresentation.DontShow;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        private static bool InSecondaryOption(Element e)
        {
            try
            {
                var option = e.DesignOption;
                return option != null && !option.IsPrimary;
            }
            catch { return false; }
        }

        /// <summary>Revit's own filters, worded: "Partition = Boven", "Length > 12000 mm". Never re-evaluated.</summary>
        private static List<string> DescribeFilters(Document doc, ScheduleDefinition def)
        {
            var list = new List<string>();
            int n;
            try { n = def.GetFilterCount(); } catch { return list; }
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var f = def.GetFilter(i);
                    var field = def.GetField(f.FieldId);
                    list.Add(ScheduleFilterText.Describe(field.GetName(), f.FilterType.ToString(), FilterValue(doc, field, f)));
                }
                catch (Exception ex) { list.Add("filter " + (i + 1) + " (unreadable: " + ex.Message + ")"); }
            }
            return list;
        }

        private static string FilterValue(Document doc, ScheduleField field, ScheduleFilter f)
        {
            if (f.IsNullValue) return null;
            if (f.IsStringValue) return "'" + f.GetStringValue() + "'";
            if (f.IsIntegerValue) return f.GetIntegerValue().ToString(CultureInfo.InvariantCulture);
            if (f.IsElementIdValue)
            {
                var id = f.GetElementIdValue();
                var e = doc.GetElement(id);
                if (e is ElementType t) return "'" + t.FamilyName + ": " + t.Name + "'";
                return e != null ? "'" + e.Name + "'" : id.IntegerValue.ToString(CultureInfo.InvariantCulture);
            }
            if (f.IsDoubleValue)
            {
                double v = f.GetDoubleValue();
                var spec = Commands.ScheduleCommand.SpecOf(field);
                if (spec != null && UnitUtils.IsMeasurableSpec(spec))
                {
                    var unit = Commands.ScheduleCommand.UnitOf(doc, field, spec);
                    if (unit != null)
                        return Math.Round(UnitUtils.ConvertFromInternalUnits(v, unit), 3).ToString(CultureInfo.InvariantCulture) +
                               (ScheduleText.UnitSymbol(unit.TypeId) is string sym ? " " + sym : "");
                }
                return v.ToString(CultureInfo.InvariantCulture);
            }
            return null;
        }

        // ------------------------------------------------------------------ after a modifying command

        /// <summary>
        /// The new elements an existing schedule of their category, or a view VibeModel put on a sheet
        /// this session, does not show. Only ADDED elements: a modified element that a filtered schedule
        /// rightly leaves out would otherwise be reported on every later edit. Null when nothing is missing.
        /// </summary>
        public static CoverageReport Check(Document doc, RecordedChanges changes)
        {
            if (doc == null || changes == null || changes.Added.Count == 0) return null;
            var sw = Stopwatch.StartNew();
            try
            {
                var byCategory = new Dictionary<int, List<ElementId>>();
                foreach (var id in changes.Added)
                {
                    var e = doc.GetElement(id);
                    if (e == null || e is ElementType || e.ViewSpecific || e is View) continue;
                    var cat = e.Category;
                    if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                    if (!byCategory.TryGetValue(cat.Id.IntegerValue, out var list)) byCategory[cat.Id.IntegerValue] = list = new List<ElementId>();
                    list.Add(id);
                }
                if (byCategory.Count == 0) return null;

                var report = new CoverageReport();
                CheckSchedules(doc, byCategory, report);
                CheckViews(doc, byCategory, report);
                return report.IsEmpty ? null : report;
            }
            catch (Exception ex)
            {
                Logger.Warn("Schedule/view coverage skipped: " + ex.Message);
                return null;
            }
            finally
            {
                if (sw.ElapsedMilliseconds > 200) Logger.Warn("Schedule/view coverage took " + sw.ElapsedMilliseconds + " ms");
            }
        }

        private static void CheckSchedules(Document doc, Dictionary<int, List<ElementId>> byCategory, CoverageReport report)
        {
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule && !v.IsInternalKeynoteSchedule)
                .Where(v =>
                {
                    try
                    {
                        var d = v.Definition;
                        return !d.IsKeySchedule && !d.IsMaterialTakeoff && d.CategoryId != null && byCategory.ContainsKey(d.CategoryId.IntegerValue);
                    }
                    catch { return false; }
                })
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
            report.SchedulesNotChecked = Math.Max(0, schedules.Count - MaxSchedules);
            foreach (var vs in schedules.Take(MaxSchedules))
            try
            {
                var ids = byCategory[vs.Definition.CategoryId.IntegerValue];
                // An area schedule lists one scheme: a new area of another scheme is not "missing" from it.
                var scheme = AreaSchemeOf(doc, vs.Definition);
                if (scheme != null) ids = ids.Where(i => (doc.GetElement(i) as Area)?.AreaScheme?.Id == scheme.Id).ToList();
                if (ids.Count == 0) continue;
                var shown = new HashSet<int>(new FilteredElementCollector(doc, vs.Id).WherePasses(new ElementIdSetFilter(ids))
                    .ToElementIds().Select(i => i.IntegerValue));
                var missing = ids.Where(i => !shown.Contains(i.IntegerValue)).ToList();
                if (missing.Count == 0) continue;
                report.Schedules.Add(new ScheduleMiss
                {
                    Schedule = vs.Name,
                    Id = vs.Id.IntegerValue,
                    Category = Category.GetCategory(doc, vs.Definition.CategoryId)?.Name ?? "elements",
                    Missing = missing.Count,
                    Ids = missing.Take(CoverageReport.MaxIds).Select(i => (long)i.IntegerValue).ToList()
                });
            }
            catch (Exception ex)
            {
                // One schedule that cannot be read never costs the lines of the others.
                Logger.Warn("Schedule coverage skipped for '" + vs.Name + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Views VibeModel created or placed on a sheet this session that already show elements of a
        /// touched category: per new element, rebar hidden inside its host while the view's other
        /// bars are unobscured, or not shown at all.
        /// </summary>
        private static void CheckViews(Document doc, Dictionary<int, List<ElementId>> byCategory, CoverageReport report)
        {
            var created = new HashSet<int>(VibeModelCreated.Ids(doc));
            if (created.Count == 0) return;
            var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
            var candidates = new List<KeyValuePair<View, ViewSheet>>();
            var seen = new HashSet<int>();
            foreach (var vp in viewports)
            {
                if (!created.Contains(vp.Id.IntegerValue) && !created.Contains(vp.ViewId.IntegerValue)) continue;
                if (!seen.Add(vp.ViewId.IntegerValue)) continue;
                if (!(doc.GetElement(vp.ViewId) is View v) || v.IsTemplate || v is ViewSchedule || v is ViewSheet) continue;
                if (v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Legend) continue;
                candidates.Add(new KeyValuePair<View, ViewSheet>(v, doc.GetElement(vp.SheetId) as ViewSheet));
            }
            report.ViewsNotChecked = Math.Max(0, candidates.Count - MaxViews);
            foreach (var pair in candidates.Take(MaxViews))
            {
                var view = pair.Key;
                foreach (var kv in byCategory)
                try
                {
                    var newIds = kv.Value;
                    var catId = new ElementId(kv.Key);
                    // Only a view that already draws this category is the "overview" of it.
                    Func<FilteredElementCollector> others = () => new FilteredElementCollector(doc, view.Id).OfCategoryId(catId)
                        .WhereElementIsNotElementType().Excluding(newIds);
                    var first = others().FirstElementId();
                    if (first == null || first == ElementId.InvalidElementId) continue;
                    var shown = new HashSet<int>(new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(newIds))
                        .ToElementIds().Select(i => i.IntegerValue));

                    var obscured = new List<ElementId>();
                    var notVisible = new List<ElementId>();
                    // Both read lazily, once per view and category, and only when a new element needs them.
                    bool? othersUnobscured = null;
                    bool areaRead = false, hasArea = false;
                    double[] areaMin = null, areaMax = null;
                    var axes = ViewAxes(view);
                    foreach (var id in newIds)
                    {
                        var e = doc.GetElement(id);
                        bool isShown = shown.Contains(id.IntegerValue);
                        if (!isShown)
                        {
                            // Only inside what the view already draws of this category: a wall on the next
                            // level is not "missing" from the plan of this one, a bar in another slab not
                            // from this slab's drawing.
                            if (!areaRead) { hasArea = ShownArea(others(), axes, out areaMin, out areaMax); areaRead = true; }
                            if (!hasArea || !Within(e, view, axes, areaMin, areaMax)) continue;
                        }
                        // A bar hidden inside its host where the view's other bars are unobscured — decided
                        // from the bar itself, whether or not the view's element set lists it.
                        if (IsUnobscured(e, view) == false)
                        {
                            if (othersUnobscured == null) othersUnobscured = others().Any(o => IsUnobscured(o, view) == true);
                            if (othersUnobscured == true) { obscured.Add(id); continue; }
                        }
                        if (!isShown) notVisible.Add(id);
                    }
                    var catName = Category.GetCategory(doc, catId)?.Name ?? "elements";
                    var sheet = pair.Value != null ? SheetHelper.Label(pair.Value) : null;
                    if (obscured.Count > 0) report.Views.Add(Miss(view, sheet, catName, obscured, ViewMiss.Obscured));
                    if (notVisible.Count > 0) report.Views.Add(Miss(view, sheet, catName, notVisible, ViewMiss.NotVisible));
                }
                catch (Exception ex)
                {
                    // One view that cannot be read never costs the lines of the others.
                    Logger.Warn("View coverage skipped for '" + view.Name + "': " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Rebar's "View Unobscured" in this view; null for anything that is not rebar, and in 3D views
        /// (since Revit 2023 rebar always draws solid there at Fine detail). Revit hides a bar inside its
        /// concrete host in plans, sections and elevations until this is on.
        /// </summary>
        private static bool? IsUnobscured(Element e, View view)
        {
            if (view is View3D) return null;
            try
            {
                if (e is Rebar r) return r.IsUnobscuredInView(view);
                if (e is RebarInSystem s) return s.IsUnobscuredInView(view);
            }
            catch { /* a view type that has no such setting */ }
            return null;
        }

        private const double TouchMm = 10;

        /// <summary>
        /// The axes a view is compared in: its own drawing plane (right, up) — a plan ignores height, a
        /// section ignores depth — and for a 3D view also its view direction.
        /// </summary>
        private static XYZ[] ViewAxes(View view)
        {
            try
            {
                return view is View3D
                    ? new[] { view.RightDirection, view.UpDirection, view.ViewDirection }
                    : new[] { view.RightDirection, view.UpDirection };
            }
            catch { return new[] { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ }; }
        }

        /// <summary>The extent, along the view's axes, of the elements the view already draws of this category.</summary>
        private static bool ShownArea(IEnumerable<Element> shown, XYZ[] axes, out double[] min, out double[] max)
        {
            min = Enumerable.Repeat(double.MaxValue, axes.Length).ToArray();
            max = Enumerable.Repeat(double.MinValue, axes.Length).ToArray();
            bool any = false;
            foreach (var e in shown)
            {
                if (!Extent(e, axes, out var lo, out var hi)) continue;
                any = true;
                for (int i = 0; i < axes.Length; i++) { min[i] = Math.Min(min[i], lo[i]); max[i] = Math.Max(max[i], hi[i]); }
            }
            return any;
        }

        /// <summary>An element's bounding box projected on each axis (all eight corners).</summary>
        private static bool Extent(Element e, XYZ[] axes, out double[] lo, out double[] hi)
        {
            lo = hi = null;
            var bb = e?.get_BoundingBox(null);
            if (bb == null) return false;
            lo = Enumerable.Repeat(double.MaxValue, axes.Length).ToArray();
            hi = Enumerable.Repeat(double.MinValue, axes.Length).ToArray();
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
            {
                var p = bb.Transform.OfPoint(new XYZ(x, y, z));
                for (int i = 0; i < axes.Length; i++)
                {
                    var d = p.DotProduct(axes[i]);
                    lo[i] = Math.Min(lo[i], d);
                    hi[i] = Math.Max(hi[i], d);
                }
            }
            return true;
        }

        /// <summary>
        /// A new element the view does not show only counts as "missing" when it lies where the view
        /// already draws this category — and, in a plan, on the plan's own level when it has one (a wall
        /// on the next level is not missing from this plan). Without a level, the plane alone decides.
        /// </summary>
        private static bool Within(Element e, View view, XYZ[] axes, double[] areaMin, double[] areaMax)
        {
            if (e == null) return false;
            if (view is ViewPlan plan && plan.GenLevel != null && e.LevelId != null &&
                e.LevelId != ElementId.InvalidElementId && e.LevelId != plan.GenLevel.Id) return false;
            if (!Extent(e, axes, out var lo, out var hi)) return false;
            return CoverageGeometry.Overlaps(lo, hi, areaMin, areaMax, RevitUnitHelper.MmToFeet(TouchMm));
        }

        private static ViewMiss Miss(View view, string sheet, string category, List<ElementId> ids, string cause) => new ViewMiss
        {
            View = view.Name,
            Id = view.Id.IntegerValue,
            Sheet = sheet,
            Category = category,
            Missing = ids.Count,
            Cause = cause,
            Ids = ids.Take(CoverageReport.MaxIds).Select(i => (long)i.IntegerValue).ToList()
        };
    }
}
