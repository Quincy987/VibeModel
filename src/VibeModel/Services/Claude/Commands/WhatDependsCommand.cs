using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Read-only: what hangs on an element before you replace it — the spot elevations,
    /// dimensions and tags that reference it (per view, with the sheet it is on), family
    /// instances it hosts and elements joined to it. A family instance's edge-referenced spot is
    /// not in Revit's dependent-element list, so the annotations are also scanned directly.
    /// </summary>
    public class WhatDependsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "whatdepends";
        public string Description => "Read-only: annotations (spot elevations, dimensions, tags per view/sheet), hosted and joined elements that depend on elements — check before replacing them";
        public string Usage => "whatdepends <id> [id...] | selected";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var a = (args ?? "").Trim();
            var ids = new List<int>();
            if (a.Equals("selected", StringComparison.OrdinalIgnoreCase))
                ids.AddRange(uidoc.Selection.GetElementIds().Select(i => i.IntegerValue));
            else
                foreach (var t in a.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(t, out int id)) return CommandResult.Error("BAD_ARGS", "'" + t + "' is not an element id.", "Usage: " + Usage);
                    if (!ids.Contains(id)) ids.Add(id);
                }
            if (ids.Count == 0)
                return CommandResult.Error("BAD_ARGS", a.Equals("selected", StringComparison.OrdinalIgnoreCase) ? "Nothing is selected." : "Give one or more element ids.", "Usage: " + Usage);

            var elements = new List<Element>();
            foreach (var id in ids)
            {
                var e = doc.GetElement(new ElementId(id));
                if (e == null) return CommandResult.Error("ELEMENT_NOT_FOUND", "Element " + id + " not found.", null);
                elements.Add(e);
            }

            var sheets = new Dictionary<int, string>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (doc.GetElement(vp.SheetId) is ViewSheet s) sheets[vp.ViewId.IntegerValue] = s.SheetNumber;

            // One scan for all ids (instances need it — their edge spots are no dependents).
            var scanned = AnnotationGuard.ReferencingAnnotations(doc, ids);
            var annotationFilter = new ElementMulticlassFilter(new List<Type> { typeof(Dimension), typeof(SpotDimension), typeof(IndependentTag) });
            var allHosted = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Host != null && ids.Contains(fi.Host.Id.IntegerValue)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("WHAT DEPENDS");
            sb.AppendLine("============");
            var perElement = new List<object>();
            var allAnnotations = new HashSet<int>();
            var viewLabels = new List<string>();
            int hostedTotal = 0;
            foreach (var e in elements)
            {
                int id = e.Id.IntegerValue;
                var annotations = new Dictionary<int, Element>();
                try
                {
                    foreach (var dep in e.GetDependentElements(annotationFilter))
                    {
                        var de = doc.GetElement(dep);
                        if (de != null && de.OwnerViewId != ElementId.InvalidElementId) annotations[de.Id.IntegerValue] = de;   // not a floor's sketch dimensions
                    }
                }
                catch { /* some elements refuse dependents — the scan covers them */ }
                foreach (var de in scanned)
                    if (References(de, id)) annotations[de.Id.IntegerValue] = de;

                var hosted = allHosted.Where(fi => fi.Host.Id.IntegerValue == id).Select(fi => fi.Id.IntegerValue).ToList();
                List<int> joined;
                try { joined = JoinGeometryUtils.GetJoinedElements(doc, e).Select(j => j.IntegerValue).ToList(); }
                catch { joined = new List<int>(); }
                bool vm = VibeModelCreated.Contains(doc, e.Id);

                sb.AppendLine();
                sb.AppendLine(id + " " + (e.Category?.Name ?? e.GetType().Name) + " '" + e.Name + "'" + (vm ? " (created by VibeModel this session)" : ""));
                var annJson = new List<object>();
                if (annotations.Count == 0) sb.AppendLine("  Annotations: none");
                else
                {
                    sb.AppendLine("  Annotations (" + annotations.Count + "):");
                    foreach (var de in annotations.Values.OrderBy(x => x.Id.IntegerValue))
                    {
                        var view = doc.GetElement(de.OwnerViewId) as View;
                        sheets.TryGetValue(de.OwnerViewId.IntegerValue, out var sheet);
                        var kind = AnnotationGuard.KindOf(de);
                        var label = view != null ? view.Name + (sheet != null ? ", sheet " + sheet : "") : null;
                        sb.AppendLine("    " + AnnotationText.KindWord(kind, 1) + " " + de.Id.IntegerValue + (label != null ? " — view " + label : ""));
                        if (label != null && !viewLabels.Contains(label)) viewLabels.Add(label);
                        allAnnotations.Add(de.Id.IntegerValue);
                        annJson.Add(new Dictionary<string, object>
                        {
                            { "id", de.Id.IntegerValue }, { "kind", AnnotationText.KindKey(kind) },
                            { "view", view?.Name }, { "viewId", de.OwnerViewId.IntegerValue }, { "sheet", sheet }
                        });
                    }
                }
                sb.AppendLine("  Hosted: " + (hosted.Count == 0 ? "none" : string.Join(", ", hosted)));
                sb.AppendLine("  Joined: " + (joined.Count == 0 ? "none" : string.Join(", ", joined.Select(j => j + " (" + (doc.GetElement(new ElementId(j))?.Category?.Name ?? "?") + ")"))));
                sb.AppendLine("  Created by VibeModel this session: " + (vm ? "yes" : "no"));
                hostedTotal += hosted.Count;
                perElement.Add(new Dictionary<string, object>
                {
                    { "id", id }, { "category", e.Category?.Name },
                    { "annotations", annJson },
                    { "hosted", hosted.Cast<object>().ToList() },
                    { "joined", joined.Cast<object>().ToList() },
                    { "vibeModelCreated", vm }
                });
            }

            var verdict = AnnotationText.DependsVerdict(ids.Select(i => (long)i).ToList(), allAnnotations.Count, viewLabels, hostedTotal);
            sb.AppendLine();
            sb.AppendLine("Verdict: " + verdict);
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object> { { "elements", perElement }, { "verdict", verdict } });
        }

        private static bool References(Element annotation, int id)
        {
            try
            {
                if (annotation is IndependentTag t) return t.GetTaggedLocalElementIds().Any(i => i.IntegerValue == id);
                if (annotation is Dimension d)
                    foreach (Reference r in d.References) if (r.ElementId.IntegerValue == id) return true;
            }
            catch { }
            return false;
        }
    }
}
