using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Standards
{
    /// <summary>The standards verdict for one document at one edit count.</summary>
    public sealed class StandardsEvaluation
    {
        public string CacheKey { get; set; }
        public TemplateFacts Facts { get; set; }
        public StandardsPack Active { get; set; }
        public FingerprintResult Match { get; set; }
        public List<string> Rejections { get; set; } = new List<string>();
        public bool Disabled { get; set; }
    }

    /// <summary>
    /// The per-document office-standards state, cached on "document + edit counter" so a
    /// creating command pays for the fingerprint only after the model actually changed.
    /// Also owns the per-turn prompt snapshot: Revit API reads must happen on the main thread,
    /// while the chat backends build prompts on worker threads — so the snapshot is rendered
    /// on Idling (main thread, only when marked dirty) and published to StandardsPrompt.
    /// </summary>
    public static class StandardsContext
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, StandardsEvaluation> Cache =
            new Dictionary<string, StandardsEvaluation>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _promptDirty = true;

        public static StandardsEvaluation Evaluate(Document doc, bool full)
        {
            if (doc == null) return new StandardsEvaluation();
            var docKey = ModelChangeTracker.DocKey(doc.PathName, doc.Title);
            var cacheKey = docKey + "#" + ModelChangeTracker.GetTotalEdits(docKey);

            lock (Sync)
            {
                if (Cache.TryGetValue(docKey, out var hit) && hit.CacheKey == cacheKey && (!full || hit.Facts.IsFull))
                    return hit;
            }

            var facts = TemplateFactsCollector.Collect(doc, full);
            var eval = new StandardsEvaluation { CacheKey = cacheKey, Facts = facts };
            if (!SettingsManager.GetStandardsEnabled())
            {
                eval.Disabled = true;
            }
            else if (!doc.IsFamilyDocument)
            {
                eval.Active = StandardsPackRegistry.PickActive(StandardsPackRegistry.GetPacks(), facts,
                    out var match, eval.Rejections);
                eval.Match = match;
            }

            lock (Sync) Cache[docKey] = eval;
            return eval;
        }

        /// <summary>The pack in force for this document, or null (no match / disabled).</summary>
        public static StandardsPack GetActivePack(Document doc)
        {
            try { return Evaluate(doc, false).Active; }
            catch (Exception ex)
            {
                Logger.Warn("Standards evaluation failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Drop every cached verdict and re-read the packs (setting change, --refresh).</summary>
        public static void Reset()
        {
            SettingsManager.ClearCache(); // pick up settings.json edits made outside the dialog
            StandardsPackRegistry.Refresh();
            lock (Sync) Cache.Clear();
            _promptDirty = true;
        }

        public static void MarkPromptDirty() => _promptDirty = true;

        /// <summary>Idling hook: re-render the prompt snapshot only when something changed.</summary>
        public static void RefreshPromptIfDirty(UIApplication app)
        {
            if (!_promptDirty) return;
            _promptDirty = false;
            RefreshPrompt(app);
        }

        public static void RefreshPrompt(UIApplication app)
        {
            try
            {
                var uidoc = app?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null || doc.IsFamilyDocument)
                {
                    StandardsPrompt.Publish(null, null);
                    return;
                }

                var eval = Evaluate(doc, true);
                ActiveViewFacts av = null;
                try { av = TemplateFactsCollector.ReadActiveView(uidoc.ActiveView); } catch { }

                var template = StandardsRender.RenderTemplateSummary(eval.Facts, av, eval.Active, eval.Match);
                var standards = eval.Active != null ? StandardsRender.RenderCore(eval.Active) : null;
                StandardsPrompt.Publish(template, standards);
            }
            catch (Exception ex)
            {
                Logger.Warn("Standards prompt refresh failed: " + ex.Message);
            }
        }
    }
}
