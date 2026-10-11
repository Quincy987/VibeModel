using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 11 mechanism 1: read how this document was set up (phases, view templates, scope
    /// boxes, coordinates, families, sheets) live from the model, and say which office standards
    /// pack its fingerprint matches. Never transcribed from a manual, so it cannot go stale.
    /// </summary>
    public class TemplateCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "template";
        public string Description => "Inventory of the document's setup (phases, view templates, coordinates, families) + which office standard applies";
        public string Usage => "template";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var eval = StandardsContext.Evaluate(doc, true);
            var f = eval.Facts;
            var pack = eval.Active;
            ActiveViewFacts av = null;
            try { av = TemplateFactsCollector.ReadActiveView(uidoc.ActiveView); } catch { }

            var sb = new StringBuilder();
            if (pack != null)
                sb.AppendLine("template: " + pack.Id + " (fingerprint match " + eval.Match?.Summary + ")");
            else if (eval.Disabled)
                sb.AppendLine("template: unknown (office standards are switched off in Settings)");
            else
                sb.AppendLine("template: unknown (no pack matches)" +
                              (eval.Rejections.Count > 0 ? " — " + string.Join(" | ", eval.Rejections) : ""));
            sb.AppendLine("=========");
            sb.AppendLine("Document: " + f.Title + (f.IsWorkshared ? " (workshared)" : ""));
            sb.AppendLine();

            sb.AppendLine("PHASES (" + f.Phases.Count + "): " + string.Join(" | ", f.Phases));
            sb.AppendLine("PHASE FILTERS (" + f.PhaseFilters.Count + "): " + string.Join(" | ", f.PhaseFilters));
            if (av != null)
                sb.AppendLine("ACTIVE VIEW: '" + av.Name + "' (" + av.ViewType + ") phase '" + av.Phase +
                              "', filter '" + av.PhaseFilter + "'" +
                              (string.IsNullOrEmpty(av.Template) ? "" : ", template '" + av.Template + "'"));
            sb.AppendLine();

            sb.AppendLine("VIEW TEMPLATES (" + f.ViewTemplates.Count + "):");
            foreach (var g in f.ViewTemplates.GroupBy(StandardsRender.PrefixKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("  " + g.Key + " ×" + g.Count() + ": " + string.Join(", ", g));
            sb.AppendLine("SCOPE BOXES: " + (f.ScopeBoxes.Count == 0 ? "none" : string.Join(", ", f.ScopeBoxes)));
            if (pack != null && pack.Fingerprint.Views.Count > 0)
            {
                var present = pack.Fingerprint.Views.Where(v => f.ViewNames.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
                sb.AppendLine("OFFICE VIEWS PRESENT: " + (present.Count == 0 ? "none" : string.Join(", ", present)) +
                              " (" + present.Count + "/" + pack.Fingerprint.Views.Count + ")");
            }
            sb.AppendLine("VIEWS: " + f.ViewNames.Count + " (excluding templates and sheets)");
            sb.AppendLine();

            sb.AppendLine("COORDINATES: " + StandardsRender.DescribeCoordinates(f));
            var c = f.Coordinates;
            if (c.Available)
                sb.AppendLine("  site '" + c.SiteName + "', base point elevation " + Mm(c.BaseElevationMm) + " mm");
            sb.AppendLine("CAD: " + f.CadLinks + " link(s), " + f.CadLinksPinned + " pinned; " + f.CadImports + " import(s)" +
                          (f.CadImports > 0 ? " — imports instead of links" : ""));
            sb.AppendLine("POINT CLOUDS: " + f.PointClouds + " (" + f.PointCloudsPinned + " pinned); GRIDS: " +
                          f.Grids + " (" + f.GridsPinned + " pinned)");
            sb.AppendLine();

            var prefixes = pack?.FamilyPrefixes ?? new List<string>();
            sb.AppendLine("FAMILIES (" + f.Families.Count + " loaded): " +
                          StandardsRender.PrefixHistogram(f.Families.Select(x => x.Name), prefixes, 12));
            foreach (var cat in f.Families.Where(x => x.IsModelCategory)
                         .GroupBy(x => x.Category).OrderByDescending(g => g.Count()).Take(8))
                sb.AppendLine("  " + cat.Key + " (" + cat.Count() + "): " +
                              string.Join(", ", cat.Select(x => x.Name).Take(6)) + (cat.Count() > 6 ? ", …" : ""));
            sb.AppendLine("MATERIALS: " + f.MaterialsCount);
            sb.AppendLine("BOUND PARAMETERS: " + f.ProjectParameters.Count +
                          (pack != null && pack.Fingerprint.SharedParameters.Count > 0
                              ? " (office: " + string.Join(", ", pack.Fingerprint.SharedParameters
                                    .Select(p => p + (f.ProjectParameters.Contains(p, StringComparer.OrdinalIgnoreCase) ? " ✓" : " ✗"))) + ")"
                              : ""));
            sb.AppendLine("SHEETS (" + f.Sheets.Count + "): " + string.Join(", ", f.Sheets.Take(8)) + (f.Sheets.Count > 8 ? ", …" : ""));
            sb.AppendLine("TITLEBLOCKS: " + (f.Titleblocks.Count == 0 ? "none" : string.Join(", ", f.Titleblocks)));
            if (pack != null)
            {
                sb.AppendLine();
                sb.AppendLine("Office standard " + pack.Id + " v" + pack.Version + " — run 'standards' for its rules.");
            }

            var data = new Dictionary<string, object>
            {
                { "pack", pack?.Id },
                { "fingerprint", eval.Match?.Summary },
                { "rejections", eval.Rejections.Cast<object>().ToList() },
                { "phases", f.Phases.Cast<object>().ToList() },
                { "phaseFilters", f.PhaseFilters.Cast<object>().ToList() },
                { "activeView", av == null ? null : new Dictionary<string, object>
                    { { "name", av.Name }, { "phase", av.Phase }, { "phaseFilter", av.PhaseFilter }, { "template", av.Template } } },
                { "viewTemplates", f.ViewTemplates.Cast<object>().ToList() },
                { "scopeBoxes", f.ScopeBoxes.Cast<object>().ToList() },
                { "coordinates", new Dictionary<string, object>
                    {
                        { "baseNorthMm", c.BaseNorthMm }, { "baseEastMm", c.BaseEastMm },
                        { "baseElevationMm", c.BaseElevationMm }, { "baseAngleDeg", c.BaseAngleDeg },
                        { "basePinned", c.BasePinned }, { "surveyPinned", c.SurveyPinned },
                        { "surveyAtSharedOrigin", c.SurveyAtSharedOrigin }, { "surveyOffsetMm", c.SurveyOffsetFromOriginMm },
                        { "sharedCoordinates", c.SharedCoordinates },
                        { "site", c.SiteName }
                    } },
                { "cadLinks", f.CadLinks }, { "cadLinksPinned", f.CadLinksPinned }, { "cadImports", f.CadImports },
                { "familyPrefixes", StandardsRender.ClassifyPrefixes(f.Families.Select(x => x.Name), prefixes)
                    .ToDictionary(k => k.Key, k => (object)k.Value) },
                { "familyCount", f.Families.Count },
                { "materials", f.MaterialsCount },
                { "sheets", f.Sheets.Cast<object>().ToList() },
                { "titleblocks", f.Titleblocks.Cast<object>().ToList() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
