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
    /// <summary>
    /// Apply the office phase table to existing elements: "this is temporary work" becomes
    /// Phase Created + Phase Demolished from the pack (leidraad Tabel 1), reported per element.
    /// </summary>
    public class PhaseCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "phase";
        public string Description => "Set the office phase kind of elements (existing|new|temp|demolish|future → Phase Created/Demolished from the standards pack)";
        public string Usage => "phase <ids|selected> <existing|new|temp|demolish|future>";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var tokens = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count < 2)
                return CommandResult.Error("BAD_ARGS", "Usage: " + Usage, "Example: phase 12345 12346 temp, or phase selected existing");

            var kind = tokens[tokens.Count - 1];
            tokens.RemoveAt(tokens.Count - 1);

            // Reuse the creating commands' resolution so the rules and errors are identical.
            var req = PhaseDefaults.Prepare(doc, "--phase " + kind);
            if (req.Error != null) return req.ErrorResult;
            if (!req.IsActive)
                return CommandResult.Error("NO_PACK", "No office phase table applies to this document.",
                    "Run 'standards' to see why.");

            var ids = new List<ElementId>();
            if (tokens.Count == 1 && tokens[0].Equals("selected", StringComparison.OrdinalIgnoreCase))
                ids.AddRange(uidoc.Selection.GetElementIds());
            else
                foreach (var t in tokens)
                {
                    if (!int.TryParse(t, out var id))
                        return CommandResult.Error("BAD_ARGS", "'" + t + "' is not an element id.", Usage);
                    ids.Add(new ElementId(id));
                }
            if (ids.Count == 0)
                return CommandResult.Error("BAD_ARGS", "No elements given (selection is empty).", Usage);

            var rows = new List<Dictionary<string, object>>();
            var lines = new List<string>();
            var error = TransactionHelper.Execute(doc, "VibeModel: Set Phase", () =>
            {
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    if (e == null) { lines.Add(id.IntegerValue + ": not found"); continue; }
                    var before = Describe(doc, e);
                    int appliedBefore = req.Applied, failedBefore = req.Failures.Count;
                    req.Apply(e);
                    var after = Describe(doc, e);
                    bool changed = req.Applied > appliedBefore;
                    lines.Add(id.IntegerValue + " (" + (e.Category?.Name ?? e.GetType().Name) + "): " +
                              (changed ? before + " → " + after
                               : req.Failures.Count > failedBefore ? "unchanged — Revit refused: " + req.Failures[req.Failures.Count - 1]
                               : "unchanged — " + (e.HasPhases() ? "phases not modifiable" : "element has no phases")));
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", id.IntegerValue }, { "before", before }, { "after", after }, { "changed", changed }
                    });
                }
            });
            if (error != null) return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("PHASE SET — '" + req.Mapping.Key + "' = created " + req.Mapping.Created +
                          ", demolished " + (req.Mapping.Demolished ?? "None"));
            sb.AppendLine("==========");
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine();
            sb.AppendLine(req.Applied + " of " + ids.Count + " element(s) changed.");
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "kind", req.Mapping.Key }, { "applied", req.Applied }, { "elements", rows.Cast<object>().ToList() }
            });
        }

        private static string Describe(Document doc, Element e)
        {
            try
            {
                if (!e.HasPhases()) return "no phases";
                var c = TemplateFactsCollector.ElementName(doc, e.CreatedPhaseId) ?? "None";
                var d = TemplateFactsCollector.ElementName(doc, e.DemolishedPhaseId) ?? "None";
                return c + " / " + d;
            }
            catch { return "?"; }
        }
    }
}
