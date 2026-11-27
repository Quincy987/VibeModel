using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Phase 2, the Revit side of <see cref="AttentionReport"/>: reads the document's sheets
    /// (number, name), views (view template, filters) and filter elements before and after a
    /// modifying run. Read-only, no transaction; a failure means "no line", never a failed command.
    /// </summary>
    internal static class AttentionGuard
    {
        private const long SlowCaptureMs = 50;

        public static AttentionReport.Snapshot Capture(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument) return null;
            var clock = Stopwatch.StartNew();
            try
            {
                var s = new AttentionReport.Snapshot();
                foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                    s.Sheets[sheet.Id.IntegerValue] = new AttentionReport.SheetFact { Number = sheet.SheetNumber, Name = sheet.Name };
                int read = 0;
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (v is ViewSheet) continue;
                    if (read >= AttentionReport.MaxViews) { s.UnreadViews.Add(v.Id.IntegerValue); continue; }
                    read++;
                    var fact = new AttentionReport.ViewFact { Name = v.Name, IsTemplate = v.IsTemplate };
                    try { fact.TemplateId = v.ViewTemplateId != null && v.ViewTemplateId != ElementId.InvalidElementId ? v.ViewTemplateId.IntegerValue : 0; } catch { }
                    try
                    {
                        if (v.AreGraphicsOverridesAllowed())
                            fact.FilterIds = v.GetFilters().Select(f => (long)f.IntegerValue).OrderBy(x => x).ToList();
                    }
                    catch { /* a view type without filters */ }
                    s.Views[v.Id.IntegerValue] = fact;
                }
                foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(FilterElement)))
                    s.Filters[f.Id.IntegerValue] = f.Name;
                if (clock.ElapsedMilliseconds > SlowCaptureMs)
                    Logger.Info("Attention: read " + s.Sheets.Count + " sheets, " + read + " views, " + s.Filters.Count + " filters in " + clock.ElapsedMilliseconds + " ms");
                return s;
            }
            catch (Exception ex)
            {
                Logger.Warn("Attention capture skipped: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// What changed since <paramref name="before"/>: the items, and how many touched views were not read
        /// (<paramref name="notChecked"/>). Null when nothing did. The document is read again only when the
        /// run modified or deleted a sheet, view or filter of the snapshot. Run while the run's group is still open.
        /// </summary>
        public static List<AttentionReport.Item> Compare(Document doc, AttentionReport.Snapshot before, RecordedChanges changes, out int notChecked)
        {
            notChecked = 0;
            if (before == null || changes == null || changes.IsEmpty) return null;
            try
            {
                var touched = changes.Modified.Concat(changes.Deleted).Select(i => (long)i.IntegerValue).ToList();
                if (!before.Touches(touched)) return null;
                notChecked = before.UnreadTouched(touched);
                var after = Capture(doc);
                if (after == null) return null;
                var items = AttentionReport.Compare(before, after, changes.Modified.Select(i => (long)i.IntegerValue).ToList());
                return items.Count == 0 && notChecked == 0 ? null : items;
            }
            catch (Exception ex)
            {
                Logger.Warn("Attention compare skipped: " + ex.Message);
                return null;
            }
        }
    }
}
