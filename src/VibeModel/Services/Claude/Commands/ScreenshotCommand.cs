using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Exports the active view to a PNG and returns a plain-text payload with the absolute
    /// path + metadata. Vision-capable backends read the file and inline the image.
    /// Path-over-bytes keeps the text/plain HTTP contract intact — no server changes.
    /// <para>
    /// --at / --box (plan 23) zoom in on part of a view at full resolution: the view is duplicated,
    /// the copy cropped and exported, and the whole thing rolled back — the view itself (also one
    /// on a sheet) never changes, no view is left behind and no undo entry appears.
    /// </para>
    /// </summary>
    public class ScreenshotCommand : IClaudeCommand
    {
        public string Name => "screenshot";
        public string Description => "Export the active view (or --view <id|name|sheet number>, sheets included) to a PNG so it can be viewed; " +
                                     "--at <ids|selected> or --box x0 y0 x1 y1 (the view's frame; on a sheet paper mm) zooms in on part of it at full resolution " +
                                     "without changing the view — the way to check a corner, an offset or an annotation";
        // Vision sweet spot: big enough to read geometry, small enough to keep tokens sane.
        // Anthropic downscales images > ~1568px long edge anyway, so 1536 sits just under (ZoomArgs.DefaultPixels).
        public string Usage => ZoomArgs.Usage;

        // Keep only the most recent N screenshot temp dirs to bound disk growth.
        private const int KeepRecent = 20;

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var a = ZoomArgs.Parse(args);
            if (a.Error != null) return "ERROR: " + a.Error + "\nUsage: " + Usage;

            // --view exports another view or a sheet without switching to it (a view switch
            // only lands after the request, so "activate then screenshot" in one batch can't work).
            string target = a.View;
            View view = null;
            if (target != null)
            {
                view = SheetHelper.FindView(doc, target);
                if (view == null) return "ERROR: No view or sheet '" + target + "'. Run 'views' or 'list sheets'.";
            }
            else
            {
                try { view = uiDoc.ActiveGraphicalView; } catch { view = null; }
                if (view == null) view = uiDoc.ActiveView;
            }
            if (view == null) return "ERROR: No active view to capture.";

            // Guard view types Revit can't raster-export as a single useful image.
            if (view is ViewSchedule || view.ViewType == ViewType.Legend)
                return "ERROR: View type '" + view.ViewType + "' cannot be exported as an image. " +
                       "Switch to a plan, section, elevation, 3D view or sheet.";

            if (a.Zoom) return Zoom(uiDoc, doc, view, a);

            bool isActive = target == null && !(view is ViewSheet);
            var png = Export(doc, view, a.Pixels, isActive, out var error);
            if (png == null) return error;

            var info = new FileInfo(png);
            var sb = new StringBuilder();
            sb.AppendLine("SCREENSHOT");
            sb.AppendLine("==========");
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ")");
            sb.AppendLine("Pixels (long edge): " + a.Pixels);
            sb.AppendLine("Size: " + (info.Length / 1024) + " KB");
            // NOTE: backends locate the file by parsing this "Path:" line. Keep the prefix
            // stable — this is the text contract until Plan 03 (structured I/O) replaces it.
            sb.AppendLine("Path: " + png);
            sb.AppendLine();
            sb.AppendLine("The PNG at the path above shows " + (target == null ? "the active view" : "'" + view.Name + "'") + ". " +
                          "Open/read it to see the current state of the model.");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- zoom (plan 23)

        private static string Zoom(UIDocument uiDoc, Document doc, View view, ZoomArgs a)
        {
            var sw = Stopwatch.StartNew();
            var ids = a.AtIds.ToList();
            if (a.AtSelected)
            {
                ids = uiDoc.Selection.GetElementIds().Select(i => i.IntegerValue).ToList();
                if (ids.Count == 0) return "ERROR: Nothing is selected.\nHint: give element ids with --at, or select them first.";
            }

            // ---- a sheet: zoom the view on it that shows the elements, or that holds the paper box ----
            string fromSheet = null;
            List<XYZ> zone = null;   // world points whose extent in the view is the zone
            if (view is ViewSheet sheet)
            {
                var vps = sheet.GetAllViewports().Select(i => doc.GetElement(i)).OfType<Viewport>().ToList();
                if (ids.Count > 0)
                {
                    var set = ids.Select(i => new ElementId(i)).ToList();
                    var hit = vps.FirstOrDefault(p => Zoomable(doc.GetElement(p.ViewId) as View) && Shows(doc, doc.GetElement(p.ViewId) as View, set));
                    if (hit == null)
                        return "ERROR: none of the " + vps.Count + " views on sheet " + SheetHelper.Label(sheet) + " that can be zoomed (a perspective view cannot) shows " + IdList(ids) + ".\n" +
                               "Hint: name the view with --view <view>, or run 'whyhidden <id> <view>'.";
                    view = (View)doc.GetElement(hit.ViewId);
                    fromSheet = "the view on sheet " + SheetHelper.Label(sheet) + " (viewport " + hit.Id.IntegerValue + ") that shows them";
                }
                else
                {
                    SheetHelper.PaperRect(doc, sheet, out var origin, out _);
                    var rects = vps.Select(p => SheetHelper.ToPaper(origin, p.GetBoxOutline())).ToList();
                    int i = ZoomArgs.PickViewport(rects, a.Box, out var why);
                    if (i < 0)
                        return "ERROR: --box " + BoxText(a.Box) + " (paper mm): " + why + " of sheet " + SheetHelper.Label(sheet) + ".\n" +
                               "Hint: run 'sheetinfo " + sheet.SheetNumber + "' for the viewport outlines, or name the view with --view and give the box in its own frame.";
                    var vp = vps[i];
                    view = (View)doc.GetElement(vp.ViewId);
                    if (view is View3D)
                        return "ERROR: the box lies on a 3D view ('" + view.Name + "'); a paper box cannot say how deep to look.\n" +
                               "Hint: zoom it with --view " + view.Id.IntegerValue + " --at <ids>.";
                    if (!PaperToModel(vp, view, origin, a.Box, out zone, out var mapError))
                        return "ERROR: cannot map the paper box onto '" + view.Name + "': " + mapError + "\nHint: name the view with --view and give the box in its own frame.";
                    fromSheet = "viewport " + vp.Id.IntegerValue + " on sheet " + SheetHelper.Label(sheet) + ", paper box " + BoxText(a.Box) + " mm";
                }
            }

            var v3 = view as View3D;
            if (v3 != null && v3.IsPerspective)
                return "ERROR: '" + view.Name + "' is a perspective view; it cannot be cropped to a zone.\nHint: aim a 3D view with 'look … --at <ids>' first.";
            if (v3 == null && !SheetHelper.HasCropRegion(view))
                return "ERROR: '" + view.Name + "' (" + view.ViewType + ") has no crop region to zoom with.\nHint: take the whole view: screenshot --view " + view.Id.IntegerValue + ".";
            if (v3 != null && a.Box != null && fromSheet == null)
                return "ERROR: --box is for plans, sections and sheets; for a 3D view use --at <ids>.";

            // ---- the zone: world points whose extent in the view is shown ----
            string source, note = null;
            if (zone == null && ids.Count > 0)
            {
                CropCommand.FitExtent(doc, view, ids, out var lo, out var hi, out int used, out _);
                if (used == 0) return "ERROR: none of " + IdList(ids) + " exists or has a size.\nHint: check the ids with 'get <id>'.";
                var m = RevitUnitHelper.MmToFeet(a.Margin);
                zone = SheetHelper.Corners(lo - new XYZ(m, m, m), hi + new XYZ(m, m, m)).ToList();
                source = used + " element(s) + " + Mm.F(a.Margin) + " mm margin" +
                         (used < ids.Count ? "; " + (ids.Count - used) + " of the ids skipped (missing or without a size)" : "");
                if (fromSheet == null && !Shows(doc, view, ids.Select(i => new ElementId(i)).ToList()))
                    note = "Note: '" + view.Name + "' does not draw " + IdList(ids) + " — the image shows the place, not the element; run 'whyhidden <id> " + view.Id.IntegerValue + "'.";
            }
            else if (zone == null)
            {
                zone = SheetHelper.ViewBoxPoints(doc, view, a.Box);
                source = "--box";
            }
            else source = "paper box";

            // ---- duplicate, crop, export, roll back ----
            double wMm = 0, hMm = 0;
            string png = null, error = null, copyKind = null;
            var key = ModelChangeTracker.DocKey(doc.PathName, doc.Title);
            long edits = ModelChangeTracker.GetTotalEdits(key);
            using (TemporaryWork.Begin())
            using (var group = new TransactionGroup(doc, "VibeModel: Zoom Screenshot (temporary)"))
            {
                group.Start();
                try
                {
                    View copy = null;
                    var txError = TransactionHelper.Execute(doc, "VibeModel: Zoom Copy (temporary)", () =>
                    {
                        ViewDuplicateOption option;
                        if (view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing)) option = ViewDuplicateOption.WithDetailing;
                        else if (view.CanViewBeDuplicated(ViewDuplicateOption.Duplicate)) option = ViewDuplicateOption.Duplicate;
                        else throw new InvalidOperationException("Revit cannot duplicate this view, so it cannot be zoomed without changing it.");
                        copyKind = option == ViewDuplicateOption.WithDetailing ? "with its annotations" : "without its annotations (Revit cannot copy them for this view)";
                        copy = (View)doc.GetElement(view.Duplicate(option));
                        // A scope box fixes the crop region; the copy lets go of it.
                        var scope = copy.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                        if (scope != null && !scope.IsReadOnly && scope.AsElementId() != ElementId.InvalidElementId)
                            scope.Set(ElementId.InvalidElementId);
                        if (copy is View3D c3)
                        {
                            SheetHelper.Extent(zone, out var lo, out var hi);
                            c3.SetSectionBox(new BoundingBoxXYZ { Min = lo, Max = hi });
                            c3.IsSectionBoxActive = true;
                        }
                        CropCommand.CropToPoints(copy, zone, false, out wMm, out hMm);
                        copy.CropBoxVisible = false;
                        // Notes far outside the zone would widen the image until the zone is a speck
                        // (measured on a plan): clip annotations to the zone, at Revit's smallest offset.
                        ClipAnnotations(copy);
                        // Section, elevation and callout markers are views, not annotations: the annotation
                        // crop leaves them, and one far off shrinks the zone to a stamp (measured on a plan).
                        HideMarkersOutside(doc, copy);
                    });
                    if (txError != null) error = txError;
                    else png = Export(doc, copy, a.Pixels, false, out error);
                }
                catch (Exception ex)
                {
                    error = "ERROR: " + ex.Message;
                }
                finally
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                }
            }
            // Belt and braces: whatever the copy's commit may have counted is taken back.
            if (ModelChangeTracker.GetTotalEdits(key) != edits) ModelChangeTracker.RewindTo(key, edits);
            sw.Stop();
            Logger.Info("screenshot zoom of '" + view.Name + "' (" + view.ViewType + ") took " + sw.ElapsedMilliseconds + " ms");

            if (png == null)
                return (error ?? "ERROR: the zoomed export produced no PNG.") + "\nHint: nothing was changed (the temporary copy was rolled back); " +
                       "take the whole view with screenshot --view " + view.Id.IntegerValue + ".";

            var info = new FileInfo(png);
            var sb = new StringBuilder();
            sb.AppendLine("SCREENSHOT (ZOOM)");
            sb.AppendLine("=================");
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + (view.Scale > 0 ? ", 1:" + view.Scale : "") + ")" + (fromSheet != null ? " — " + fromSheet : ""));
            sb.AppendLine("Zoom: " + FrameBox(view, zone) + ", " + Mm.F(wMm) + " × " + Mm.F(hMm) + " mm (" + source + "), " +
                          "on a temporary copy of the view " + copyKind + " (nothing changed)");
            sb.AppendLine("Pixels (long edge): " + a.Pixels + (wMm > 0 ? " (" + Math.Round(a.Pixels / Math.Max(wMm, hMm) * 1000) + " px per m)" : ""));
            sb.AppendLine("Size: " + (info.Length / 1024) + " KB");
            sb.AppendLine("Path: " + png);
            if (note != null) sb.AppendLine(note);
            sb.AppendLine();
            sb.AppendLine("The PNG at the path above shows that zone of '" + view.Name + "'. Open/read it to check the detail.");
            return sb.ToString();
        }

        /// <summary>A view a zone can be cut from: a crop region, or an orthographic 3D view.</summary>
        private static bool Zoomable(View v)
            => v != null && (v is View3D v3 ? !v3.IsPerspective : SheetHelper.HasCropRegion(v));

        private static readonly BuiltInCategory[] MarkerCategories =
            { BuiltInCategory.OST_Viewers, BuiltInCategory.OST_Elev, BuiltInCategory.OST_ReferenceViewer };

        /// <summary>Hides, in the copy only, the view markers that do not lie wholly inside its crop region.</summary>
        private static void HideMarkersOutside(Document doc, View copy)
        {
            try
            {
                doc.Regenerate();
                var cb = copy.CropBox;
                var inv = cb.Transform.Inverse;
                const double tol = 0.01;
                var hide = new List<ElementId>();
                var markers = new FilteredElementCollector(doc, copy.Id)
                    .WherePasses(new ElementMulticategoryFilter(MarkerCategories.ToList())).ToElements();
                foreach (var e in markers)
                {
                    var bb = e.get_BoundingBox(copy);
                    if (bb == null) continue;
                    bool inside = SheetHelper.Corners(bb.Min, bb.Max).Select(c => inv.OfPoint(c))
                        .All(l => l.X >= cb.Min.X - tol && l.X <= cb.Max.X + tol && l.Y >= cb.Min.Y - tol && l.Y <= cb.Max.Y + tol);
                    if (!inside && e.CanBeHidden(copy)) hide.Add(e.Id);
                }
                if (hide.Count > 0) copy.HideElements(hide);
            }
            catch { /* markers stay; the zone is then smaller in the image */ }
        }

        private static void ClipAnnotations(View copy)
        {
            try
            {
                var shape = copy.GetCropRegionShapeManager();
                if (shape == null || !shape.CanHaveAnnotationCrop) return;
                var active = copy.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (active == null || active.IsReadOnly) return;
                active.Set(1);
                const double min = 1.0 / 96;   // Revit's smallest annotation crop offset (1/8")
                shape.TopAnnotationCropOffset = min; shape.BottomAnnotationCropOffset = min;
                shape.LeftAnnotationCropOffset = min; shape.RightAnnotationCropOffset = min;
            }
            catch { /* a view without annotation crop is exported as it is */ }
        }

        /// <summary>Whether the view draws any of the ids (Revit's own view element set).</summary>
        private static bool Shows(Document doc, View v, ICollection<ElementId> ids)
        {
            if (v == null || v is ViewSchedule || v.ViewType == ViewType.Legend || v.IsTemplate) return false;
            try { return new FilteredElementCollector(doc, v.Id).WherePasses(new ElementIdSetFilter(ids)).GetElementCount() > 0; }
            catch { return false; }
        }

        /// <summary>
        /// A paper box (mm from the sheet's lower-left) as the world points of its four corners in the
        /// viewport's view — all four: on a plan turned against the project axes, the world box of two
        /// opposite corners is larger than the paper box (measured: 60 × 40 mm at 1:500 now crops 30 × 20 m).
        /// </summary>
        private static bool PaperToModel(Viewport vp, View view, XYZ origin, double[] box, out List<XYZ> corners, out string error)
        {
            corners = null; error = null;
            try
            {
                if (!vp.HasViewportTransforms()) { error = "the viewport has no view transform."; return false; }
                var toProjection = vp.GetProjectionToSheetTransform().Inverse;
                var projections = view.GetModelToProjectionTransforms();
                if (projections == null || projections.Count == 0) { error = "the view has no projection transform."; return false; }
                if (projections.Count > 1) { error = "the view is split into " + projections.Count + " parts on the sheet."; return false; }
                var toModel = projections[0].GetModelToProjectionTransform().Inverse;
                corners = new[] { new[] { box[0], box[1] }, new[] { box[2], box[1] }, new[] { box[2], box[3] }, new[] { box[0], box[3] } }
                    .Select(c => toModel.OfPoint(toProjection.OfPoint(SheetHelper.ToSheet(origin, c[0], c[1])))).ToList();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>The zone in the frame the other commands use: plan x,y; section along + elevation; 3D the world box.</summary>
        private static string FrameBox(View view, IList<XYZ> zone)
        {
            Func<double, string> mm = f => Mm.F(RevitUnitHelper.FeetToMm(f));
            Func<Func<XYZ, double>, string> range = f => mm(zone.Min(f)) + "…" + mm(zone.Max(f));
            if (view is ViewPlan)
                return "x " + range(p => p.X) + ", y " + range(p => p.Y) + " (project x,y mm)";
            if (view is View3D)
                return "x " + range(p => p.X) + ", y " + range(p => p.Y) + ", z " + range(p => p.Z) + " (model mm)";
            return "x " + range(p => SheetHelper.Along(view, p)) + ", y " + range(p => p.Z) + " (along the view, elevation mm)";
        }
        private static string BoxText(double[] b) => Mm.F(b[0]) + " " + Mm.F(b[1]) + " " + Mm.F(b[2]) + " " + Mm.F(b[3]);

        private static string IdList(List<int> ids)
            => ids.Count <= 5 ? string.Join(", ", ids) : string.Join(", ", ids.Take(5)) + " +" + (ids.Count - 5) + " more";

        // ---------------------------------------------------------------- export

        /// <summary>The view as a PNG in a fresh temp folder; null + an ERROR line when Revit made none.</summary>
        private static string Export(Document doc, View view, int pixels, bool isActive, out string error)
        {
            error = null;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel", "screenshots");
            PruneOldScreenshots(root);

            // Export into a fresh, empty temp dir so we can unambiguously find the PNG.
            // (Revit mangles the filename — appends " - <ViewType> - <ViewName>.png".)
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var basePath = Path.Combine(dir, "view");

            var opts = new ImageExportOptions
            {
                FilePath = basePath,
                ExportRange = isActive ? ExportRange.CurrentView : ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels,                 // long-edge pixels when FitToPage
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };

            if (!isActive) opts.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                doc.ExportImage(opts);   // read-only — no Transaction required
            }
            catch (Exception ex)
            {
                error = "ERROR: ExportImage failed: " + ex.Message;
                return null;
            }

            var png = Directory.GetFiles(dir, "*.png").FirstOrDefault();
            if (png == null) error = "ERROR: ExportImage produced no PNG (view may be empty or hidden).";
            return png;
        }

        // Best-effort: keep only the newest KeepRecent screenshot temp dirs.
        private static void PruneOldScreenshots(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var stale = new DirectoryInfo(root).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Skip(KeepRecent);
                foreach (var d in stale)
                {
                    try { d.Delete(true); } catch { /* best effort */ }
                }
            }
            catch { /* best effort — never fail a screenshot over cleanup */ }
        }
    }
}
