using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    public class ViewCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "view";
        public string Description => "Create a view (floor plan or 3D); with an office standard it gets the office view template, phase and browser folder";
        public string Usage => "view plan|3d [levelName] [--name <viewName>] [--template <name>|--no-template] [--activate]  " +
                               "|  view 3d --look <" + CameraMath.Keys.Split(' ')[0] + "|x y z> " + LookSpec.FlagUsage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            // Office-default overrides; without these flags the args parse exactly as before.
            var va = ViewArgs.Parse(args);
            args = va.Remaining;
            bool activate = va.Activate;
            string templateOverride = va.Template;
            bool noTemplate = va.NoTemplate;
            string viewName = va.Name;
            if (viewName != null && new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Any(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Error("BAD_ARGS", "A view named '" + viewName + "' already exists.", "Pick another --name.");

            var parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
                return CommandResult.Error("BAD_ARGS", "Usage: view plan|3d [levelName]", Usage);

            var kind = parts[0].ToLowerInvariant();
            var levelName = parts.Length >= 2 ? parts[1].Trim() : null;

            LookSpec look = null;
            if (va.AnyCameraFlag)
            {
                if (kind != "3d")
                    return CommandResult.Error("BAD_ARGS", "--look and its flags are for 'view 3d'.", Usage);
                if (va.Look == null)
                    return CommandResult.Error("BAD_ARGS", "--at/--distance/--pitch/--margin/--box go with --look <direction>.", Usage);
                look = LookSpec.FromParts(va.Look, va.At, va.Distance, va.Pitch, va.Margin, va.Box);
                if (look.Error != null) return CommandResult.Error("BAD_ARGS", look.Error, Usage);
            }

            var defaults = OfficeViewSetup.Resolve(doc, kind == "3d" ? "threeD" : kind, templateOverride, noTemplate, out var setupError);
            if (setupError != null) return setupError;

            CommandResult result;
            if (kind == "plan")
                result = CreatePlan(doc, levelName, uiApp.ActiveUIDocument, defaults, viewName);
            else if (kind == "3d")
                result = Create3D(doc, defaults, viewName);
            else
                return CommandResult.Error("BAD_ARGS", "Unknown view type '" + parts[0] + "'.", Usage);

            // --look: aim the new 3D view (its own transaction, after the template is applied).
            if (look != null && result.Success && result.Data is Dictionary<string, object> ld &&
                ld.TryGetValue("created", out var lc) && lc is List<object> lids && lids.Count > 0 &&
                doc.GetElement(new ElementId((int)lids[0])) is View3D new3d)
            {
                var aimed = LookCommand.Orient(doc, uiApp.ActiveUIDocument, new3d, look);
                ld["look"] = aimed.Success ? aimed.Data : null;
                var lookText = aimed.Success ? aimed.Text : "Not aimed: " + aimed.Message + "\n";
                result = CommandResult.Ok(result.Text.TrimEnd() + "\n\n" + lookText, ld);
            }

            // --activate: open the new view (outside the creating transaction).
            if (activate && result.Success && result.Data is Dictionary<string, object> d &&
                d.TryGetValue("created", out var created) && created is List<object> ids && ids.Count > 0 &&
                doc.GetElement(new ElementId((int)ids[0])) is View newView)
            {
                var act = ActivateCommand.Activate(uiApp.ActiveUIDocument, newView);
                d["activated"] = act.Success;
                return CommandResult.Ok(result.Text.TrimEnd() + "\n" + (act.Success ? "Activated: now the active view." : "Not activated: " + act.Message) + "\n", d);
            }
            return result;

        }

        private CommandResult CreatePlan(Document doc, string levelName, UIDocument uiDoc, OfficeViewSetup setup, string viewName)
        {
            // Floor plan type first; documents started from a structural template may only
            // have structural plans, which serve the same purpose here.
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan)
                ?? new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.StructuralPlan);
            if (vft == null)
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "No floor-plan view type found in the document.", null);

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (levels.Count == 0)
                return CommandResult.Error("BAD_ARGS", "No levels in the document to host a plan view.",
                    "Create a level first with 'level <elevation_mm> [name]'.");

            Level level;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND",
                        "No level named '" + levelName + "'. Available: " +
                        string.Join(", ", levels.Select(l => l.Name)), null);
            }
            else
            {
                // Prefer the active view's level (matches WallCommand); fall back to lowest.
                level = FormattingHelper.GetPreferredLevel(doc, uiDoc) ?? levels.OrderBy(l => l.Elevation).First();
            }

            View view = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create View", () =>
            {
                view = ViewPlan.Create(doc, vft.Id, level.Id);
                if (viewName != null) view.Name = viewName;
                setup?.Apply(doc, view);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "TRANSACTION_FAILED",
                    "That level may already have a plan view of this type.");

            var sb = new StringBuilder();
            sb.AppendLine("VIEW CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Name: " + view.Name);
            sb.AppendLine("Type: " + view.ViewType);
            sb.AppendLine("Level: " + level.Name);
            setup?.AppendReport(sb);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { view.Id.IntegerValue } },
                { "name", view.Name },
                { "type", view.ViewType.ToString() },
                { "level", level.Name }
            };
            if (setup != null) data["office"] = setup.ToJson();
            return CommandResult.Ok(sb.ToString(), data);
        }

        private CommandResult Create3D(Document doc, OfficeViewSetup setup, string viewName)
        {
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null)
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "No 3D view type found in the document.", null);

            View3D view = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create View", () =>
            {
                view = View3D.CreateIsometric(doc, vft.Id);
                if (viewName != null) view.Name = viewName;
                setup?.Apply(doc, view);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("VIEW CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Name: " + view.Name);
            sb.AppendLine("Type: " + view.ViewType);
            setup?.AppendReport(sb);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { view.Id.IntegerValue } },
                { "name", view.Name },
                { "type", view.ViewType.ToString() }
            };
            if (setup != null) data["office"] = setup.ToJson();
            return CommandResult.Ok(sb.ToString(), data);
        }
    }

    /// <summary>
    /// The office defaults for a new view (pack viewDefaults): view template, phase + phase
    /// filter, browser-folder parameters. Resolved before the transaction, applied inside it,
    /// and every choice is reported so the modeller can see and override it. Null when no
    /// pack applies and no --template was given, so plain `view` is unchanged.
    /// </summary>
    internal sealed class OfficeViewSetup
    {
        public string Kind;
        public View Template;
        public bool TemplateFromOffice;
        public Phase Phase;
        public PhaseFilter PhaseFilter;
        public Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Report = new List<string>();
        public bool NoTemplate;
        public string PackId;

        public static OfficeViewSetup Resolve(Document doc, string kind, string templateOverride, bool noTemplate,
            out CommandResult error)
        {
            error = null;
            var pack = StandardsContext.GetActivePack(doc);
            ViewDefault vd = null;
            if (pack != null) pack.ViewDefaults.TryGetValue(kind, out vd);
            if (vd == null && templateOverride == null) return null;

            var setup = new OfficeViewSetup { Kind = kind, NoTemplate = noTemplate, PackId = pack?.Id };
            var templateName = templateOverride ?? (noTemplate ? null : vd?.Template);
            if (templateName != null)
            {
                setup.Template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
                setup.TemplateFromOffice = templateOverride == null;
                if (setup.Template == null)
                {
                    if (templateOverride != null)
                    {
                        error = CommandResult.Error("ELEMENT_NOT_FOUND", "No view template named '" + templateOverride + "'.",
                            "Run 'template' to list the view templates.");
                        return null;
                    }
                    setup.Report.Add("Template: office default '" + templateName + "' is not in this document — none applied");
                }
            }

            if (vd != null && !string.IsNullOrWhiteSpace(vd.PhaseKey) && pack?.PhaseTable != null)
            {
                var m = pack.PhaseTable.Resolve(vd.PhaseKey);
                if (m != null) setup.Phase = PhaseDefaults.FindPhase(doc, m.Created);
            }
            if (vd != null && !string.IsNullOrWhiteSpace(vd.PhaseFilter))
                setup.PhaseFilter = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                    .FirstOrDefault(f => f.Name.Equals(vd.PhaseFilter, StringComparison.OrdinalIgnoreCase));
            if (vd != null)
                foreach (var kv in vd.Parameters) setup.Parameters[kv.Key] = kv.Value;
            return setup;
        }

        /// <summary>Inside the creating transaction.</summary>
        public void Apply(Document doc, View view)
        {
            if (Template != null)
            {
                try
                {
                    view.ViewTemplateId = Template.Id;
                    Report.Add("Template: " + Template.Name + (TemplateFromOffice
                        ? " (office default for " + Kind + "; --template <name> or --no-template to override)"
                        : " (--template)"));
                }
                catch (Exception ex) { Report.Add("Template: could not apply '" + Template.Name + "': " + ex.Message); }
            }
            else if (NoTemplate) Report.Add("Template: none (--no-template)");

            // Phase is never template-controlled; the filter may be — then the template wins.
            if (Phase != null) SetId(view, BuiltInParameter.VIEW_PHASE, Phase.Id, "Phase", Phase.Name);
            if (PhaseFilter != null) SetId(view, BuiltInParameter.VIEW_PHASE_FILTER, PhaseFilter.Id, "Phase filter", PhaseFilter.Name);

            foreach (var kv in Parameters)
            {
                var p = view.LookupParameter(kv.Key);
                if (p == null) { Report.Add(kv.Key + ": parameter not in this document"); continue; }
                if (p.IsReadOnly) { Report.Add(kv.Key + ": set by the view template"); continue; }
                try
                {
                    if (p.StorageType == StorageType.String) { p.Set(kv.Value); Report.Add(kv.Key + ": " + kv.Value); }
                    else Report.Add(kv.Key + ": not a text parameter, skipped");
                }
                catch (Exception ex) { Report.Add(kv.Key + ": " + ex.Message); }
            }
        }

        private void SetId(View view, BuiltInParameter bip, ElementId id, string label, string name)
        {
            var p = view.get_Parameter(bip);
            if (p == null) return;
            if (p.IsReadOnly) { Report.Add(label + ": controlled by the view template (kept)"); return; }
            try { p.Set(id); Report.Add(label + ": " + name + " (office default)"); }
            catch (Exception ex) { Report.Add(label + ": could not set '" + name + "': " + ex.Message); }
        }

        public void AppendReport(StringBuilder sb)
        {
            if (Report.Count == 0) return;
            sb.AppendLine("Office setup" + (PackId != null ? " (" + PackId + ")" : "") + ":");
            foreach (var r in Report) sb.AppendLine("  " + r);
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            { "pack", PackId },
            { "template", Template?.Name },
            { "phase", Phase?.Name },
            { "phaseFilter", PhaseFilter?.Name },
            { "report", Report.Cast<object>().ToList() }
        };
    }
}
