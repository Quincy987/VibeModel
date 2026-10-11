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
    /// Audit the model against the office rules (the pack's checks.json):
    /// one OK/WARN/FAIL/SKIPPED line per rule, a Fix line per problem, and a Verdict.
    /// </summary>
    public class CheckCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "check";
        public string Description => "Audit the model against the office standards (phasing, pins, coordinates, families, parameters, views)";
        public string Usage => "check [all|selected|<ids>] [--stats]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var pack = StandardsContext.GetActivePack(doc);
            if (pack == null)
                return CommandResult.Error("NO_PACK", "No office standards pack applies to this document — nothing to check against.",
                    "Run 'standards' to see which packs exist and why none matched.");

            var tokens = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Any(t => t.Equals("--stats", StringComparison.OrdinalIgnoreCase)))
                return Stats(pack);

            var rules = ModelChecks.LoadRules(pack, out var ruleError);
            if (rules.Count == 0)
                return CommandResult.Error("NO_RULES", "Pack " + pack.Id + " has no check rules" + (ruleError != null ? " (" + ruleError + ")" : "") + ".",
                    "Add a checks.json to the pack and reference it with \"checks\": \"checks.json\".");

            ICollection<ElementId> scope = null;
            string scopeLabel = "all";
            if (tokens.Count > 0 && !tokens[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens[0].Equals("selected", StringComparison.OrdinalIgnoreCase))
                {
                    scope = uidoc.Selection.GetElementIds();
                    scopeLabel = "selection (" + scope.Count + ")";
                }
                else
                {
                    var ids = new List<ElementId>();
                    foreach (var t in tokens)
                    {
                        if (!int.TryParse(t, out var id))
                            return CommandResult.Error("BAD_ARGS", "'" + t + "' is not an element id.", Usage);
                        ids.Add(new ElementId(id));
                    }
                    scope = ids;
                    scopeLabel = ids.Count + " element(s)";
                }
                if (scope.Count == 0)
                    return CommandResult.Error("BAD_ARGS", "Nothing selected.", Usage);
            }

            var findings = ModelChecks.Run(doc, uidoc.ActiveView, pack, rules, scope);
            CheckLog.Append(ModelChecks.LogPath(pack), pack.Id, "check", findings);

            var header = "CHECK — " + pack.Id + " v" + pack.Version + " (" + rules.Count(r => r.Enabled) + " rules, scope: " + scopeLabel + ")";
            var text = CheckReport.Render(header, findings);
            if (ruleError != null) text += "Note: " + ruleError + "\n";
            if (scope != null) text += "(Scoped run: only element-level rules — " + string.Join(", ", ModelChecks.ElementKinds) + ".)\n";

            return CommandResult.Ok(text, new Dictionary<string, object>
            {
                { "pack", pack.Id }, { "scope", scopeLabel },
                { "verdict", CheckReport.Verdict(findings) },
                { "findings", CheckReport.ToJson(findings) }
            });
        }

        private static CommandResult Stats(StandardsPack pack)
        {
            var stats = CheckLog.Stats(ModelChecks.LogPath(pack));
            var sb = new StringBuilder();
            sb.AppendLine("CHECK STATS — pack " + pack.Id + " (problems reported, most frequent first)");
            sb.AppendLine("===========");
            if (stats.Count == 0) sb.AppendLine("No problems logged yet.");
            foreach (var s in stats)
                sb.AppendLine(s.Item1.PadRight(28) + " reported " + s.Item2 + "×, " + s.Item3 + " element(s) in total");
            sb.AppendLine();
            sb.AppendLine("Log: " + ModelChecks.LogPath(pack) + " — the top rules are candidates for better defaults in the next pack version.");
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "stats", stats.Select(s => (object)new Dictionary<string, object>
                    { { "rule", s.Item1 }, { "times", s.Item2 }, { "elements", s.Item3 } }).ToList() }
            });
        }
    }
}
