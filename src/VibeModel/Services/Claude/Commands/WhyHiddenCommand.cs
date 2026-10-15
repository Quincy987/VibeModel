using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Explains WHY an element is not drawn in a view (and, where cheap, why it cannot be
    /// edited). Every Revit visibility gate is probed in turn and reported as OK / BLOCKED,
    /// so the answer is read out of the actual view state instead of guessed.
    ///
    /// Every probe is wrapped: the Revit API throws freely for view types that do not support
    /// a given query (schedules, legends, non-hideable categories, invalid phases). A probe
    /// that throws reports SKIPPED rather than aborting the whole diagnosis.
    /// </summary>
    public class WhyHiddenCommand : IClaudeCommand
    {
        public string Name => "whyhidden";
        public string Description => "Diagnose why an element is not visible (or not editable) in a view";
        public string Usage => "whyhidden <elementId> [viewId]";

        private const string UsageHelp =
            "Usage: whyhidden <elementId> [viewId]\n\n" +
            "Examples:\n" +
            "  whyhidden 348122          # against the active view\n" +
            "  whyhidden 348122 51203    # against a specific view\n\n" +
            "Tip: run 'selected' to get the ID of the element you clicked, " +
            "and 'views' to list view IDs.";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var parsed = WhyHiddenArgs.Parse(args);
            if (parsed.Error != null)
                return "ERROR: " + parsed.Error + "\n\n" + UsageHelp;

            var elem = doc.GetElement(new ElementId(parsed.ElementId));
            if (elem == null)
                return "ERROR: No element with ID " + parsed.ElementId + " in this document.\n\n" + UsageHelp;

            View view;
            if (parsed.ViewId.HasValue)
            {
                view = doc.GetElement(new ElementId(parsed.ViewId.Value)) as View;
                if (view == null)
                    return "ERROR: ID " + parsed.ViewId.Value + " is not a view.\n\n" + UsageHelp;
            }
            else
            {
                view = FormattingHelper.GetActiveGraphicalView(uiDoc) ?? uiDoc.ActiveView;
                if (view == null)
                    return "ERROR: No active graphical view. Open a plan, section or 3D view, " +
                           "or pass a view ID: whyhidden " + parsed.ElementId + " <viewId>";
            }

            if (view.IsTemplate)
                return "ERROR: View " + view.Id.IntegerValue + " ('" + view.Name + "') is a view template, " +
                       "not a drawn view. Pass the ID of a real view.";

            var d = new VisibilityDiagnosis
            {
                ElementLine = DescribeElement(doc, elem),
                ViewLine = DescribeView(view)
            };

            CheckGroundTruth(doc, view, elem, d);
            CheckIdentityAndCategory(doc, view, elem, d);
            CheckHiddenFlags(view, elem, d);
            CheckViewFilters(doc, view, elem, d);
            CheckDetailLevel(view, elem, d);
            CheckViewRange(doc, view, elem, d);
            CheckLevelAndDiscipline(doc, view, elem, d);
            CheckWorksetPhaseOption(doc, view, elem, d);
            CheckCropAndSectionBox(view, elem, d);
            CheckGraphicOverrides(doc, view, elem, d);
            CheckEditability(doc, view, elem, d);

            return d.Render();
        }

        // ---- headers --------------------------------------------------------

        private static string DescribeElement(Document doc, Element elem)
        {
            var cat = SafeCategoryName(elem);
            var typeName = "";
            try
            {
                var typeId = elem.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var t = doc.GetElement(typeId);
                    if (t != null) typeName = " / " + t.Name;
                }
            }
            catch { }

            return elem.Id.IntegerValue + "  " + cat + typeName + "  \"" + SafeName(elem) + "\"";
        }

        private static string DescribeView(View view)
        {
            var detail = "";
            try { detail = ", detail " + view.DetailLevel; } catch { }
            var scale = "";
            try { scale = ", scale 1:" + view.Scale; } catch { }
            return view.Id.IntegerValue + "  \"" + view.Name + "\"  (" + view.ViewType + detail + scale + ")";
        }

        private static string SafeName(Element elem)
        {
            try { return elem.Name ?? "(unnamed)"; }
            catch { return "(unnamed)"; }
        }

        private static string SafeCategoryName(Element elem)
        {
            try { return elem.Category != null ? elem.Category.Name : "(no category)"; }
            catch { return "(no category)"; }
        }

        // ---- ground truth ---------------------------------------------------

        /// <summary>
        /// Asks Revit directly whether the element is part of the view's element set. This one
        /// query already accounts for most gates at once, so it is the cross-check that keeps
        /// the report honest when every individual probe comes back clean.
        /// </summary>
        private static void CheckGroundTruth(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            try
            {
                var found = new FilteredElementCollector(doc, view.Id)
                    .WherePasses(new ElementIdSetFilter(new List<ElementId> { elem.Id }))
                    .FirstElementId();
                d.InViewElementSet = found != null && found != ElementId.InvalidElementId;
            }
            catch
            {
                d.InViewElementSet = null;
            }
        }

        // ---- 1. identity + category ----------------------------------------

        private static void CheckIdentityAndCategory(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            d.Add("1a", "Element exists", CheckStatus.Ok,
                "found in the document as " + elem.GetType().Name + " (" + SafeCategoryName(elem) + ")");

            // View-specific elements (detail lines, tags, dimensions) only ever draw in
            // the single view that owns them - a very common "it vanished" cause.
            try
            {
                if (elem.ViewSpecific && elem.OwnerViewId != ElementId.InvalidElementId && elem.OwnerViewId != view.Id)
                {
                    var owner = doc.GetElement(elem.OwnerViewId) as View;
                    d.Add("1b", "View ownership", CheckStatus.Blocked,
                        "this is a view-specific element owned by view '" +
                        (owner != null ? owner.Name : elem.OwnerViewId.IntegerValue.ToString()) +
                        "' - it can never appear in '" + view.Name + "'",
                        "Copy/paste it into this view (Paste > Aligned to Current View), or open the owning view.");
                }
                else
                {
                    d.Add("1b", "View ownership", CheckStatus.Ok,
                        elem.ViewSpecific ? "view-specific element owned by this view" : "model element (not view-specific)");
                }
            }
            catch
            {
                d.Add("1b", "View ownership", CheckStatus.Skipped, "Revit did not report view ownership");
            }

            var cat = elem.Category;
            if (cat == null)
            {
                d.Add("1c", "Category visibility", CheckStatus.Skipped,
                    "element has no category, so Visibility/Graphics cannot hide it by category");
            }
            else
            {
                ReportCategoryVisibility(view, cat, d, "1c", "Category visibility");

                // Whole category-TYPE switches (VG's "show model categories" master toggles).
                ReportCategoryTypeMaster(view, cat, d);

                // A subcategory can be off while its parent is on - the classic case for
                // linework that disappears on generic models / anchors.
                if (cat.Parent != null)
                    ReportCategoryVisibility(view, cat.Parent, d, "1e", "Parent category visibility");
            }

            ReportGeometrySubcategories(doc, view, elem, d);
        }

        private static void ReportCategoryVisibility(View view, Category cat, VisibilityDiagnosis d,
                                                     string label, string title)
        {
            try
            {
                if (!view.CanCategoryBeHidden(cat.Id))
                {
                    d.Add(label, title, CheckStatus.Ok, "'" + cat.Name + "' cannot be hidden in this view type");
                    return;
                }

                if (view.GetCategoryHidden(cat.Id))
                {
                    d.Add(label, title, CheckStatus.Blocked,
                        "'" + cat.Name + "' is switched OFF in this view",
                        "Visibility/Graphics (shortcut VV) > " + CategoryTabName(cat) + " > tick '" + cat.Name + "'.");
                }
                else
                {
                    d.Add(label, title, CheckStatus.Ok, "'" + cat.Name + "' is switched on");
                }
            }
            catch
            {
                d.Add(label, title, CheckStatus.Skipped, "Revit refused the category-visibility query for '" + cat.Name + "'");
            }
        }

        private static void ReportCategoryTypeMaster(View view, Category cat, VisibilityDiagnosis d)
        {
            try
            {
                bool off;
                string what;
                switch (cat.CategoryType)
                {
                    case CategoryType.Annotation:
                        off = view.AreAnnotationCategoriesHidden;
                        what = "all annotation categories";
                        break;
                    case CategoryType.AnalyticalModel:
                        off = view.AreAnalyticalModelCategoriesHidden;
                        what = "all analytical categories";
                        break;
                    default:
                        off = view.AreModelCategoriesHidden;
                        what = "all model categories";
                        break;
                }

                if (off)
                {
                    d.Add("1d", "Category master switch", CheckStatus.Blocked,
                        what + " are switched off for this view",
                        "Visibility/Graphics (VV) > " + CategoryTabName(cat) + " > tick 'Show ... categories in this view'.");
                }
                else
                {
                    d.Add("1d", "Category master switch", CheckStatus.Ok, what + " are enabled for this view");
                }
            }
            catch
            {
                d.Add("1d", "Category master switch", CheckStatus.Skipped, "Revit refused the master-switch query");
            }
        }

        private static string CategoryTabName(Category cat)
        {
            try
            {
                switch (cat.CategoryType)
                {
                    case CategoryType.Annotation: return "Annotation Categories";
                    case CategoryType.AnalyticalModel: return "Analytical Model Categories";
                    default: return "Model Categories";
                }
            }
            catch { return "Model Categories"; }
        }

        /// <summary>
        /// Walks the element's view-resolved geometry and reports every subcategory it draws on
        /// that is switched off. This is what catches "the hatch/lines disappeared but the object
        /// is still there" - the object's category is on, one of its subcategories is not.
        /// </summary>
        private static void ReportGeometrySubcategories(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            try
            {
                var geom = elem.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = true });
                if (geom == null)
                {
                    d.Add("1f", "Subcategory visibility", CheckStatus.Skipped, "element exposes no geometry to inspect");
                    return;
                }

                var styleIds = new HashSet<int>();
                CollectStyleIds(geom, styleIds, 0);

                var offNames = new List<string>();
                var onCount = 0;
                foreach (var styleId in styleIds)
                {
                    var gs = doc.GetElement(new ElementId(styleId)) as GraphicsStyle;
                    var gsCat = gs != null ? gs.GraphicsStyleCategory : null;
                    if (gsCat == null || gsCat.Parent == null) continue; // only real subcategories

                    try
                    {
                        if (view.CanCategoryBeHidden(gsCat.Id) && view.GetCategoryHidden(gsCat.Id))
                            offNames.Add(gsCat.Parent.Name + " > " + gsCat.Name);
                        else
                            onCount++;
                    }
                    catch { }
                }

                if (offNames.Count > 0)
                {
                    d.Add("1f", "Subcategory visibility", CheckStatus.Blocked,
                        "geometry is drawn on subcategor(y/ies) switched OFF here: " + string.Join(", ", offNames.Distinct()),
                        "Visibility/Graphics (VV) > Model Categories > expand the parent category > tick the subcategory.");
                }
                else if (onCount > 0)
                {
                    d.Add("1f", "Subcategory visibility", CheckStatus.Ok,
                        onCount + " subcategor(y/ies) used by this element are all switched on");
                }
                else
                {
                    d.Add("1f", "Subcategory visibility", CheckStatus.Ok, "element draws on no subcategories");
                }
            }
            catch
            {
                d.Add("1f", "Subcategory visibility", CheckStatus.Skipped, "geometry could not be read in this view");
            }
        }

        private static void CollectStyleIds(GeometryElement geom, HashSet<int> styleIds, int depth)
        {
            if (geom == null || depth > 4) return;

            foreach (GeometryObject obj in geom)
            {
                if (obj == null) continue;

                try
                {
                    if (obj.GraphicsStyleId != null && obj.GraphicsStyleId != ElementId.InvalidElementId)
                        styleIds.Add(obj.GraphicsStyleId.IntegerValue);
                }
                catch { }

                var inst = obj as GeometryInstance;
                if (inst != null)
                {
                    try { CollectStyleIds(inst.GetInstanceGeometry(), styleIds, depth + 1); }
                    catch { }
                    continue;
                }

                var nested = obj as GeometryElement;
                if (nested != null)
                    CollectStyleIds(nested, styleIds, depth + 1);
            }
        }

        // ---- 2. hide-in-view / temporary hide-isolate -----------------------

        private static void CheckHiddenFlags(View view, Element elem, VisibilityDiagnosis d)
        {
            try
            {
                if (elem.IsHidden(view))
                {
                    d.Add("2a", "Hidden in view", CheckStatus.Blocked,
                        "this element was hidden individually in this view (Hide in View > Elements)",
                        "Right-click the view > 'Reveal Hidden Elements' (light-bulb, shortcut RH), " +
                        "select the element, then Unhide in View. Or run: show " + elem.Id.IntegerValue);
                }
                else
                {
                    d.Add("2a", "Hidden in view", CheckStatus.Ok, "no per-element hide set in this view");
                }
            }
            catch
            {
                d.Add("2a", "Hidden in view", CheckStatus.Skipped, "Revit refused the per-element hide query");
            }

            try
            {
                if (!view.IsTemporaryHideIsolateActive())
                {
                    d.Add("2b", "Temporary Hide/Isolate", CheckStatus.Ok, "not active in this view");
                    return;
                }

                bool visible;
                try
                {
                    visible = view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, elem.Id);
                }
                catch
                {
                    d.Add("2b", "Temporary Hide/Isolate", CheckStatus.Info,
                        "temporary hide/isolate IS active but Revit would not answer for this element",
                        "Click the Temporary Hide/Isolate icon in the view control bar > Reset Temporary Hide/Isolate. Or run: unisolate");
                    return;
                }

                if (visible)
                {
                    d.Add("2b", "Temporary Hide/Isolate", CheckStatus.Info,
                        "active in this view, but this element is inside the isolated set");
                }
                else
                {
                    d.Add("2b", "Temporary Hide/Isolate", CheckStatus.Blocked,
                        "temporary hide/isolate is active and excludes this element",
                        "View control bar > Temporary Hide/Isolate > Reset Temporary Hide/Isolate. Or run: unisolate");
                }
            }
            catch
            {
                d.Add("2b", "Temporary Hide/Isolate", CheckStatus.Skipped, "not supported by this view type");
            }
        }

        // ---- 3. view filters ------------------------------------------------

        private static void CheckViewFilters(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            ICollection<ElementId> filterIds;
            try { filterIds = view.GetFilters(); }
            catch
            {
                d.Add("3", "View filters", CheckStatus.Skipped, "this view type does not support filters");
                return;
            }

            if (filterIds == null || filterIds.Count == 0)
            {
                d.Add("3", "View filters", CheckStatus.Ok, "no filters applied to this view");
                return;
            }

            var index = 0;
            var reportedAny = false;

            foreach (var filterId in filterIds)
            {
                index++;
                var filterElem = doc.GetElement(filterId);
                if (filterElem == null) continue;

                if (!FilterMatches(filterElem, elem)) continue;

                bool enabled = true;
                try { enabled = view.GetIsFilterEnabled(filterId); } catch { }

                bool visible = true;
                try { visible = view.GetFilterVisibility(filterId); } catch { }

                var label = "3." + index;
                if (!enabled)
                {
                    d.Add(label, "View filter '" + filterElem.Name + "'", CheckStatus.Info,
                        "matches this element but the filter is disabled in this view");
                }
                else if (!visible)
                {
                    d.Add(label, "View filter '" + filterElem.Name + "'", CheckStatus.Blocked,
                        "matches this element and its Visibility checkbox is OFF",
                        "Visibility/Graphics (VV) > Filters tab > tick 'Visibility' for filter '" + filterElem.Name + "'.");
                }
                else
                {
                    var overrideNote = DescribeInvisibleOverride(doc, SafeFilterOverrides(view, filterId));
                    if (overrideNote != null)
                    {
                        d.Add(label, "View filter '" + filterElem.Name + "'", CheckStatus.Blocked,
                            "matches this element and its graphic override makes it invisible (" + overrideNote + ")",
                            "Visibility/Graphics (VV) > Filters tab > clear the override on filter '" + filterElem.Name + "'.");
                    }
                    else
                    {
                        d.Add(label, "View filter '" + filterElem.Name + "'", CheckStatus.Ok,
                            "matches this element and leaves it visible");
                    }
                }
                reportedAny = true;
            }

            if (!reportedAny)
            {
                d.Add("3", "View filters", CheckStatus.Ok,
                    filterIds.Count + " filter(s) on this view, none of which match this element");
            }
        }

        private static OverrideGraphicSettings SafeFilterOverrides(View view, ElementId filterId)
        {
            try { return view.GetFilterOverrides(filterId); }
            catch { return null; }
        }

        private static bool FilterMatches(Element filterElem, Element elem)
        {
            var selectionFilter = filterElem as SelectionFilterElement;
            if (selectionFilter != null)
            {
                try { return selectionFilter.Contains(elem.Id); }
                catch { return false; }
            }

            var paramFilter = filterElem as ParameterFilterElement;
            if (paramFilter == null) return false;

            try
            {
                var cats = paramFilter.GetCategories();
                if (elem.Category == null) return false;
                if (cats != null && !cats.Contains(elem.Category.Id)) return false;

                var elementFilter = paramFilter.GetElementFilter();
                // No rules = the filter matches every element of its categories.
                if (elementFilter == null) return true;

                return elementFilter.PassesFilter(elem);
            }
            catch
            {
                return false;
            }
        }

        // ---- 4. detail level ------------------------------------------------

        private static void CheckDetailLevel(View view, Element elem, VisibilityDiagnosis d)
        {
            ViewDetailLevel viewDetail;
            try { viewDetail = view.DetailLevel; }
            catch
            {
                d.Add("4", "Detail level", CheckStatus.Skipped, "this view type has no detail level");
                return;
            }

            if (viewDetail == ViewDetailLevel.Undefined)
            {
                d.Add("4", "Detail level", CheckStatus.Skipped, "this view has no detail level set");
                return;
            }

            // Ask the family WITHOUT a view, one detail level at a time. Reading geometry
            // through the view would also fold in view range, crop and category state, and we
            // must not blame the detail level for those - later checks own them.
            var drawsAt = new List<ViewDetailLevel>();
            var readFailed = false;
            foreach (var level in new[] { ViewDetailLevel.Coarse, ViewDetailLevel.Medium, ViewDetailLevel.Fine })
            {
                try
                {
                    if (CountGeometry(elem.get_Geometry(new Options { DetailLevel = level }), 0) > 0)
                        drawsAt.Add(level);
                }
                catch { readFailed = true; }
            }

            if (readFailed && drawsAt.Count == 0)
            {
                d.Add("4", "Detail level", CheckStatus.Skipped,
                    "view detail is " + viewDetail + "; the element's geometry could not be read");
                return;
            }

            if (drawsAt.Contains(viewDetail))
            {
                d.Add("4", "Detail level", CheckStatus.Ok,
                    "the family draws geometry at " + viewDetail + " (this view's detail level)");
            }
            else if (drawsAt.Count > 0)
            {
                d.Add("4", "Detail level", CheckStatus.Blocked,
                    "the family's geometry is switched off at " + viewDetail +
                    "; it only draws at: " + string.Join(", ", drawsAt),
                    "Set the view's Detail Level to " + drawsAt[drawsAt.Count - 1] +
                    " (view control bar), or edit the family and tick " + viewDetail +
                    " under Visibility/Graphics Overrides on the affected geometry.");
            }
            else
            {
                d.Add("4", "Detail level", CheckStatus.Info,
                    "the element produces no solid geometry at any detail level - it is annotation, " +
                    "or draws purely as symbolic/analytical linework");
            }
        }

        private static int CountGeometry(GeometryElement geom, int depth)
        {
            if (geom == null || depth > 4) return 0;

            var count = 0;
            foreach (GeometryObject obj in geom)
            {
                if (obj == null) continue;

                var inst = obj as GeometryInstance;
                if (inst != null)
                {
                    try { count += CountGeometry(inst.GetInstanceGeometry(), depth + 1); }
                    catch { }
                    continue;
                }

                var nested = obj as GeometryElement;
                if (nested != null)
                {
                    count += CountGeometry(nested, depth + 1);
                    continue;
                }

                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Faces.Size > 0) count++;
                    continue;
                }

                count++;
            }
            return count;
        }

        // ---- 5. view range / far clip ---------------------------------------

        private static void CheckViewRange(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            var plan = view as ViewPlan;
            if (plan == null)
            {
                ReportFarClip(view, d);
                return;
            }

            BoundingBoxXYZ bbox;
            try { bbox = elem.get_BoundingBox(null); }
            catch { bbox = null; }

            if (bbox == null)
            {
                d.Add("5", "View range", CheckStatus.Skipped, "element has no model bounding box to compare");
                return;
            }

            try
            {
                var range = plan.GetViewRange();
                double? top = PlaneElevation(doc, range, PlanViewPlane.TopClipPlane);
                double? bottom = PlaneElevation(doc, range, PlanViewPlane.BottomClipPlane);
                double? depth = PlaneElevation(doc, range, PlanViewPlane.ViewDepthPlane);
                double? cut = PlaneElevation(doc, range, PlanViewPlane.CutPlane);

                var describe = "cut " + Mm(cut) + ", top " + Mm(top) + ", bottom " + Mm(bottom) + ", depth " + Mm(depth) +
                               "; element spans " + Mm(bbox.Min.Z) + " to " + Mm(bbox.Max.Z);

                if (top.HasValue && bbox.Min.Z > top.Value + 1e-6)
                {
                    d.Add("5", "View range", CheckStatus.Blocked,
                        "element sits entirely ABOVE the view range top (" + describe + ")",
                        "View Properties > View Range > raise 'Top' offset, or use the plan of the level above.");
                    return;
                }

                var lowest = depth ?? bottom;
                if (lowest.HasValue && bbox.Max.Z < lowest.Value - 1e-6)
                {
                    d.Add("5", "View range", CheckStatus.Blocked,
                        "element sits entirely BELOW the view depth (" + describe + ")",
                        "View Properties > View Range > lower the 'View Depth' or 'Bottom' offset.");
                    return;
                }

                if (bottom.HasValue && bbox.Max.Z < bottom.Value - 1e-6)
                {
                    d.Add("5", "View range", CheckStatus.Info,
                        "element is below the bottom clip but inside view depth, so it draws as " +
                        "'beyond' linework only (" + describe + ")",
                        "View Properties > View Range > lower the 'Bottom' offset to draw it normally.");
                    return;
                }

                d.Add("5", "View range", CheckStatus.Ok, "element falls inside the view range (" + describe + ")");
            }
            catch
            {
                d.Add("5", "View range", CheckStatus.Skipped, "view range could not be read for this plan view");
            }
        }

        /// <summary>Absolute elevation (feet) of a view-range plane, or null when unbounded.</summary>
        private static double? PlaneElevation(Document doc, PlanViewRange range, PlanViewPlane plane)
        {
            try
            {
                var levelId = range.GetLevelId(plane);
                if (levelId == null || levelId.IntegerValue < 0) return null; // unlimited
                var level = doc.GetElement(levelId) as Level;
                if (level == null) return null;
                return level.Elevation + range.GetOffset(plane);
            }
            catch
            {
                return null;
            }
        }

        private static void ReportFarClip(View view, VisibilityDiagnosis d)
        {
            try
            {
                var active = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                if (active == null)
                {
                    d.Add("5", "View range / far clip", CheckStatus.Skipped,
                        "not a plan view and this view has no far-clip setting");
                    return;
                }

                if (active.AsInteger() == 0)
                {
                    d.Add("5", "View range / far clip", CheckStatus.Ok, "far clipping is off for this view");
                    return;
                }

                var offset = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                var depth = offset != null ? Mm(offset.AsDouble()) : "unknown";
                d.Add("5", "View range / far clip", CheckStatus.Info,
                    "far clipping is ACTIVE at " + depth + " - anything further back is cut off",
                    "View Properties > Far Clip Offset (increase it) or Far Clipping > 'No clip'.");
            }
            catch
            {
                d.Add("5", "View range / far clip", CheckStatus.Skipped, "far-clip setting could not be read");
            }
        }

        // ---- 6. associated level, discipline, datum extents ------------------

        private static void CheckLevelAndDiscipline(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            try
            {
                var viewLevel = view.GenLevel;
                var elemLevelId = elem.LevelId;

                if (viewLevel == null)
                {
                    d.Add("6a", "Associated level", CheckStatus.Skipped, "this view is not associated with a level");
                }
                else if (elemLevelId == null || elemLevelId == ElementId.InvalidElementId)
                {
                    d.Add("6a", "Associated level", CheckStatus.Info,
                        "view is on level '" + viewLevel.Name + "'; the element has no associated level");
                }
                else if (elemLevelId == viewLevel.Id)
                {
                    d.Add("6a", "Associated level", CheckStatus.Ok, "both element and view are on '" + viewLevel.Name + "'");
                }
                else
                {
                    d.Add("6a", "Associated level", CheckStatus.Info,
                        "element is associated with '" + FormattingHelper.GetLevelName(doc, elemLevelId) +
                        "' but the view is on '" + viewLevel.Name + "' - it only draws here if its geometry " +
                        "falls inside this view's range",
                        "Either move the element to '" + viewLevel.Name + "' or widen View Properties > View Range.");
                }
            }
            catch
            {
                d.Add("6a", "Associated level", CheckStatus.Skipped, "level association could not be read");
            }

            try
            {
                if (!view.HasViewDiscipline())
                {
                    d.Add("6b", "View discipline", CheckStatus.Skipped, "this view type has no discipline setting");
                }
                else
                {
                    var discipline = view.Discipline;
                    var wall = elem as Wall;
                    var nonStructural = false;
                    if (wall != null)
                    {
                        var p = elem.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                        nonStructural = p != null && p.StorageType == StorageType.Integer && p.AsInteger() == 0;
                    }

                    if (discipline == ViewDiscipline.Structural && nonStructural)
                    {
                        d.Add("6b", "View discipline", CheckStatus.Blocked,
                            "view discipline is Structural, which hides walls that are not marked structural",
                            "Either tick the wall's 'Structural' instance parameter, or set " +
                            "View Properties > Discipline to Coordination.");
                    }
                    else if (discipline == ViewDiscipline.Coordination)
                    {
                        d.Add("6b", "View discipline", CheckStatus.Ok, "Coordination - shows every discipline");
                    }
                    else
                    {
                        d.Add("6b", "View discipline", CheckStatus.Info,
                            discipline + " - a single-discipline view hides categories belonging to other disciplines",
                            "If the element belongs to another discipline, set View Properties > Discipline to Coordination.");
                    }
                }
            }
            catch
            {
                d.Add("6b", "View discipline", CheckStatus.Skipped, "discipline could not be read");
            }

            ReportDatumExtents(view, elem, d);
        }

        /// <summary>
        /// Grids and levels are datums: they only draw where their per-view 2D extent crosses
        /// the view, which is why "the level lines are gone in my section" is almost never a
        /// category problem.
        /// </summary>
        private static void ReportDatumExtents(View view, Element elem, VisibilityDiagnosis d)
        {
            var datum = elem as DatumPlane;
            if (datum == null) return;

            try
            {
                var curves = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                if (curves == null || curves.Count == 0)
                {
                    d.Add("6c", "Datum extent", CheckStatus.Blocked,
                        "this grid/level has no 2D extent inside this view, so nothing is drawn",
                        "Open the view, select the datum, drag its endpoint grips into the view, " +
                        "or use Modify > Reset/Maximize 3D Extents. Extent type is currently " +
                        datum.GetDatumExtentTypeInView(DatumEnds.End0, view) + ".");
                }
                else
                {
                    d.Add("6c", "Datum extent", CheckStatus.Ok,
                        "grid/level has a drawn extent in this view (" + curves.Count + " curve(s))");
                }
            }
            catch
            {
                d.Add("6c", "Datum extent", CheckStatus.Skipped, "datum extent could not be read for this view");
            }
        }

        // ---- 7. workset / phase / design option ------------------------------

        private static void CheckWorksetPhaseOption(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            // 7a workset
            if (!doc.IsWorkshared)
            {
                d.Add("7a", "Workset visibility", CheckStatus.Skipped, "project is not workshared");
            }
            else
            {
                try
                {
                    var wsId = elem.WorksetId;
                    var workset = doc.GetWorksetTable().GetWorkset(wsId);
                    var wsName = workset != null ? workset.Name : "workset " + wsId.IntegerValue;

                    if (workset != null && !workset.IsOpen)
                    {
                        d.Add("7a", "Workset visibility", CheckStatus.Blocked,
                            "workset '" + wsName + "' is CLOSED in this session",
                            "Collaborate tab > Worksets > open '" + wsName + "'.");
                    }
                    else
                    {
                        var visibility = view.GetWorksetVisibility(wsId);
                        var effectivelyHidden = visibility == WorksetVisibility.Hidden ||
                            (visibility == WorksetVisibility.UseGlobalSetting && workset != null && !workset.IsVisibleByDefault);

                        if (effectivelyHidden)
                        {
                            d.Add("7a", "Workset visibility", CheckStatus.Blocked,
                                "workset '" + wsName + "' is hidden in this view (" + visibility + ")",
                                "Visibility/Graphics (VV) > Worksets tab > set '" + wsName + "' to Visible.");
                        }
                        else
                        {
                            d.Add("7a", "Workset visibility", CheckStatus.Ok,
                                "workset '" + wsName + "' is visible here (" + visibility + ")");
                        }
                    }
                }
                catch
                {
                    d.Add("7a", "Workset visibility", CheckStatus.Skipped, "workset visibility could not be read");
                }
            }

            // 7b phase filter
            try
            {
                var phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                var phaseId = phaseParam != null ? phaseParam.AsElementId() : ElementId.InvalidElementId;
                if (phaseId == null || phaseId == ElementId.InvalidElementId)
                {
                    d.Add("7b", "Phase filter", CheckStatus.Skipped, "this view has no phase set");
                }
                else
                {
                    var phase = doc.GetElement(phaseId) as Phase;
                    var phaseName = phase != null ? phase.Name : phaseId.IntegerValue.ToString();
                    var status = elem.GetPhaseStatus(phaseId);

                    var filterParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                    var phaseFilter = filterParam != null
                        ? doc.GetElement(filterParam.AsElementId()) as PhaseFilter
                        : null;
                    var filterName = phaseFilter != null ? phaseFilter.Name : "<None>";

                    if (status == ElementOnPhaseStatus.None || status == ElementOnPhaseStatus.Future)
                    {
                        d.Add("7b", "Phase filter", CheckStatus.Blocked,
                            "element does not exist yet in the view's phase '" + phaseName + "' (status " + status + ")",
                            "View Properties > Phase - set it to the phase the element was created in, " +
                            "or change the element's 'Phase Created'.");
                    }
                    else if (status == ElementOnPhaseStatus.Past)
                    {
                        d.Add("7b", "Phase filter", CheckStatus.Blocked,
                            "element was already demolished before the view's phase '" + phaseName + "'",
                            "View Properties > Phase - pick an earlier phase, or clear the element's 'Phase Demolished'.");
                    }
                    else if (phaseFilter == null)
                    {
                        d.Add("7b", "Phase filter", CheckStatus.Ok,
                            "phase '" + phaseName + "', status " + status + ", no phase filter applied");
                    }
                    else
                    {
                        var presentation = phaseFilter.GetPhaseStatusPresentation(status);
                        if (presentation == PhaseStatusPresentation.DontShow)
                        {
                            d.Add("7b", "Phase filter", CheckStatus.Blocked,
                                "phase filter '" + filterName + "' is set to 'Not Displayed' for " + status + " elements",
                                "View Properties > Phase Filter - choose a filter that shows " + status +
                                " elements (e.g. 'Show All'), or edit the filter under Manage > Phases > Phase Filters.");
                        }
                        else
                        {
                            d.Add("7b", "Phase filter", CheckStatus.Ok,
                                "phase '" + phaseName + "', status " + status + ", filter '" + filterName +
                                "' shows it (" + presentation + ")");
                        }
                    }
                }
            }
            catch
            {
                d.Add("7b", "Phase filter", CheckStatus.Skipped, "phase status could not be evaluated");
            }

            // 7c design option
            try
            {
                var option = elem.DesignOption;
                if (option == null)
                {
                    d.Add("7c", "Design option", CheckStatus.Ok, "element is in the main model, not a design option");
                }
                else
                {
                    var primary = option.IsPrimary ? "primary" : "secondary";
                    var excluded = d.InViewElementSet.HasValue && !d.InViewElementSet.Value;

                    if (!option.IsPrimary && excluded)
                    {
                        d.Add("7c", "Design option", CheckStatus.Blocked,
                            "element lives in secondary design option '" + option.Name +
                            "', which this view does not display",
                            "View Properties > Visibility/Graphics > Design Options tab - set that option set " +
                            "to '" + option.Name + "', or make the option primary.");
                    }
                    else
                    {
                        d.Add("7c", "Design option", CheckStatus.Info,
                            "element lives in " + primary + " design option '" + option.Name + "'",
                            option.IsPrimary
                                ? null
                                : "If it goes missing elsewhere, set that option set in Visibility/Graphics > Design Options.");
                    }
                }
            }
            catch
            {
                d.Add("7c", "Design option", CheckStatus.Skipped, "design option could not be read");
            }
        }

        // ---- 8. crop box / section box ---------------------------------------

        private static void CheckCropAndSectionBox(View view, Element elem, VisibilityDiagnosis d)
        {
            BoundingBoxXYZ bbox;
            try { bbox = elem.get_BoundingBox(null); }
            catch { bbox = null; }

            // 8a crop region (X/Y in the view's own coordinate system)
            try
            {
                if (!view.CropBoxActive)
                {
                    d.Add("8a", "Crop region", CheckStatus.Ok, "crop region is not active in this view");
                }
                else if (bbox == null)
                {
                    d.Add("8a", "Crop region", CheckStatus.Skipped, "crop is active but the element has no bounding box");
                }
                else
                {
                    var crop = view.CropBox;
                    var local = TransformToLocal(crop.Transform, bbox);
                    var outsideX = local.Max.X < crop.Min.X - 1e-6 || local.Min.X > crop.Max.X + 1e-6;
                    var outsideY = local.Max.Y < crop.Min.Y - 1e-6 || local.Min.Y > crop.Max.Y + 1e-6;

                    if (outsideX || outsideY)
                    {
                        d.Add("8a", "Crop region", CheckStatus.Blocked,
                            "element lies outside the active crop region (" + (outsideX ? "horizontally" : "") +
                            (outsideX && outsideY ? " and " : "") + (outsideY ? "vertically" : "") + ")",
                            "View control bar > 'Do Not Crop View', or drag the crop region handles to include it.");
                    }
                    else
                    {
                        d.Add("8a", "Crop region", CheckStatus.Ok, "element falls inside the active crop region");
                    }
                }
            }
            catch
            {
                d.Add("8a", "Crop region", CheckStatus.Skipped, "crop region could not be evaluated");
            }

            // 8b section box (3D views only)
            var view3d = view as View3D;
            if (view3d == null)
            {
                d.Add("8b", "Section box", CheckStatus.Skipped, "not a 3D view");
                return;
            }

            try
            {
                if (!view3d.IsSectionBoxActive)
                {
                    d.Add("8b", "Section box", CheckStatus.Ok, "section box is not active");
                    return;
                }

                if (bbox == null)
                {
                    d.Add("8b", "Section box", CheckStatus.Skipped, "section box is active but the element has no bounding box");
                    return;
                }

                var box = view3d.GetSectionBox();
                var local = TransformToLocal(box.Transform, bbox);
                var outside = local.Max.X < box.Min.X - 1e-6 || local.Min.X > box.Max.X + 1e-6 ||
                              local.Max.Y < box.Min.Y - 1e-6 || local.Min.Y > box.Max.Y + 1e-6 ||
                              local.Max.Z < box.Min.Z - 1e-6 || local.Min.Z > box.Max.Z + 1e-6;

                if (outside)
                {
                    d.Add("8b", "Section box", CheckStatus.Blocked,
                        "element lies completely outside the active section box",
                        "View Properties > untick 'Section Box', or drag the section box grips to include it.");
                }
                else
                {
                    d.Add("8b", "Section box", CheckStatus.Ok, "element falls inside the active section box");
                }
            }
            catch
            {
                d.Add("8b", "Section box", CheckStatus.Skipped, "section box could not be evaluated");
            }
        }

        /// <summary>Re-expresses a model-space bbox in the box's own coordinate system.</summary>
        private static BoundingBoxXYZ TransformToLocal(Transform boxTransform, BoundingBoxXYZ modelBox)
        {
            var inverse = boxTransform != null ? boxTransform.Inverse : Transform.Identity;

            var corners = new[]
            {
                new XYZ(modelBox.Min.X, modelBox.Min.Y, modelBox.Min.Z),
                new XYZ(modelBox.Max.X, modelBox.Min.Y, modelBox.Min.Z),
                new XYZ(modelBox.Min.X, modelBox.Max.Y, modelBox.Min.Z),
                new XYZ(modelBox.Max.X, modelBox.Max.Y, modelBox.Min.Z),
                new XYZ(modelBox.Min.X, modelBox.Min.Y, modelBox.Max.Z),
                new XYZ(modelBox.Max.X, modelBox.Min.Y, modelBox.Max.Z),
                new XYZ(modelBox.Min.X, modelBox.Max.Y, modelBox.Max.Z),
                new XYZ(modelBox.Max.X, modelBox.Max.Y, modelBox.Max.Z)
            };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var corner in corners)
            {
                var p = inverse.OfPoint(corner);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        // ---- 9. graphic overrides ---------------------------------------------

        private static void CheckGraphicOverrides(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            OverrideGraphicSettings elementOverrides = null;
            try { elementOverrides = view.GetElementOverrides(elem.Id); } catch { }

            var note = DescribeInvisibleOverride(doc, elementOverrides);
            if (note != null)
            {
                d.Add("9a", "Element graphic override", CheckStatus.Blocked,
                    "a per-element override makes it effectively invisible (" + note + ")",
                    "Select the element > right-click > Override Graphics in View > By Element > Reset. " +
                    "Or run: color " + elem.Id.IntegerValue + " reset");
            }
            else
            {
                var soft = DescribeSoftOverride(elementOverrides);
                d.Add("9a", "Element graphic override", soft != null ? CheckStatus.Info : CheckStatus.Ok,
                    soft ?? "no per-element override that would hide it");
            }

            if (elem.Category == null)
            {
                d.Add("9b", "Category graphic override", CheckStatus.Skipped, "element has no category");
                return;
            }

            OverrideGraphicSettings categoryOverrides = null;
            try { categoryOverrides = view.GetCategoryOverrides(elem.Category.Id); } catch { }

            var catNote = DescribeInvisibleOverride(doc, categoryOverrides);
            if (catNote != null)
            {
                d.Add("9b", "Category graphic override", CheckStatus.Blocked,
                    "category '" + elem.Category.Name + "' is overridden to be effectively invisible (" + catNote + ")",
                    "Visibility/Graphics (VV) > Model Categories > clear the override on '" + elem.Category.Name + "'.");
            }
            else
            {
                var soft = DescribeSoftOverride(categoryOverrides);
                d.Add("9b", "Category graphic override", soft != null ? CheckStatus.Info : CheckStatus.Ok,
                    soft ?? "no category override that would hide it");
            }
        }

        /// <summary>Names the reason an override renders something invisible, else null.</summary>
        private static string DescribeInvisibleOverride(Document doc, OverrideGraphicSettings ogs)
        {
            if (ogs == null) return null;

            try
            {
                if (ogs.Transparency >= 100) return "surface transparency 100%";
            }
            catch { }

            try
            {
                var patternName = LinePatternName(doc, ogs.ProjectionLinePatternId);
                if (patternName != null && IsInvisibleLinePattern(patternName))
                    return "projection lines set to '" + patternName + "'";
            }
            catch { }

            try
            {
                var patternName = LinePatternName(doc, ogs.CutLinePatternId);
                if (patternName != null && IsInvisibleLinePattern(patternName))
                    return "cut lines set to '" + patternName + "'";
            }
            catch { }

            return null;
        }

        /// <summary>Overrides worth mentioning that dim rather than hide (halftone, patterns off).</summary>
        private static string DescribeSoftOverride(OverrideGraphicSettings ogs)
        {
            if (ogs == null) return null;

            var notes = new List<string>();
            try { if (ogs.Halftone) notes.Add("halftone"); } catch { }
            try { if (ogs.Transparency > 0) notes.Add("transparency " + ogs.Transparency + "%"); } catch { }
            try
            {
                if (!ogs.IsSurfaceForegroundPatternVisible || !ogs.IsCutForegroundPatternVisible)
                    notes.Add("a fill pattern is switched off (hatching will not draw)");
            }
            catch { }
            try
            {
                if (ogs.DetailLevel != ViewDetailLevel.Undefined)
                    notes.Add("detail level forced to " + ogs.DetailLevel);
            }
            catch { }

            return notes.Count == 0 ? null : "override present but not hiding: " + string.Join(", ", notes);
        }

        private static string LinePatternName(Document doc, ElementId patternId)
        {
            if (patternId == null || patternId == ElementId.InvalidElementId) return null;
            var pattern = doc.GetElement(patternId) as LinePatternElement;
            return pattern != null ? pattern.Name : null;
        }

        private static bool IsInvisibleLinePattern(string name)
        {
            var lower = name.ToLowerInvariant();
            // Revit ships this pattern localised; cover the Dutch UI this add-in is used in.
            return lower.Contains("invisible") || lower.Contains("onzichtbaar") || lower.Contains("onzichtbare");
        }

        // ---- editability -------------------------------------------------------

        private static void CheckEditability(Document doc, View view, Element elem, VisibilityDiagnosis d)
        {
            try
            {
                if (elem.GroupId != null && elem.GroupId != ElementId.InvalidElementId)
                {
                    var group = doc.GetElement(elem.GroupId);
                    d.AddNote("Group membership", CheckStatus.Info,
                        "element belongs to group '" + (group != null ? group.Name : elem.GroupId.IntegerValue.ToString()) +
                        "' - selecting it picks the group",
                        "Tab to the member, or Edit Group / Ungroup to change it individually.");
                }
            }
            catch { }

            try
            {
                if (elem.Pinned)
                    d.AddNote("Pinned", CheckStatus.Info, "element is pinned, so it cannot be moved or deleted",
                        "Select it and click the pin icon (or press UP) to unpin.");
            }
            catch { }

#if !REVIT2022
            // Element.IsModifiable exists from Revit 2023; 2022 simply has no such note.
            try
            {
                if (!elem.IsModifiable)
                    d.AddNote("Modifiable", CheckStatus.Info,
                        "Revit reports this element as not modifiable in the current state");
            }
            catch { }
#endif

            try
            {
                if (doc.IsLinked)
                    d.AddNote("Linked model", CheckStatus.Info,
                        "this element lives in a linked model - it can be viewed but not edited from here",
                        "Open the linked file directly to change it.");
            }
            catch { }

            if (!doc.IsWorkshared) return;

            try
            {
                string owner;
                var status = WorksharingUtils.GetCheckoutStatus(doc, elem.Id, out owner);
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    d.AddNote("Checkout", CheckStatus.Info,
                        "element is currently owned by '" + (owner ?? "another user") + "'",
                        "Ask them to synchronise and relinquish, or use Collaborate > Editing Requests.");
                }
                else if (status == CheckoutStatus.OwnedByCurrentUser)
                {
                    d.AddNote("Checkout", CheckStatus.Ok, "element is checked out to you");
                }

                var workset = doc.GetWorksetTable().GetWorkset(elem.WorksetId);
                if (workset != null && !workset.IsEditable)
                    d.AddNote("Workset editability", CheckStatus.Info,
                        "workset '" + workset.Name + "' is not editable by you",
                        "Collaborate tab > Worksets > make '" + workset.Name + "' editable.");
            }
            catch { }
        }

        // ---- formatting --------------------------------------------------------

        private static string Mm(double? feet)
        {
            if (!feet.HasValue) return "unlimited";
            return Mm(feet.Value);
        }

        private static string Mm(double feet)
        {
            return (feet * 304.8).ToString("F0", CultureInfo.InvariantCulture) + "mm";
        }
    }
}
