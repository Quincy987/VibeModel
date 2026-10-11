using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 11 mechanism 2: the office standards pack on demand. The always-on core is injected
    /// into every chat turn; the full topic sheets and the office's own source documents are
    /// pulled here only when the task touches them.
    /// </summary>
    public class StandardsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "standards";
        public string Description => "Office standards: active pack, rule topics, source-document chapters (standards <topic> | core | docs | <doc> <chapter> | --refresh | --index | distill)";
        public string Usage => "standards [topic|core|docs|<doc> <chapter>|--refresh|--index|--prompt|distill [--status]] [--pack <id>]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var tokens = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            string packId = null;
            int pi = tokens.FindIndex(t => t.Equals("--pack", StringComparison.OrdinalIgnoreCase));
            if (pi >= 0)
            {
                if (pi + 1 >= tokens.Count)
                    return CommandResult.Error("BAD_ARGS", "--pack needs a pack id.", Usage);
                packId = tokens[pi + 1];
                tokens.RemoveRange(pi, 2);
            }

            if (tokens.Count > 0 && tokens[0].Equals("--prompt", StringComparison.OrdinalIgnoreCase))
            {
                // Exactly what the chat backends inject this turn (re-rendered now).
                StandardsContext.RefreshPrompt(uiApp);
                var combined = StandardsPrompt.Combined;
                return CommandResult.Ok(combined.Length == 0 ? "(nothing injected: no document, or no matching pack and no template facts)\n" : combined,
                    new Dictionary<string, object>
                    {
                        { "template", StandardsPrompt.TemplateBlock },
                        { "standards", StandardsPrompt.StandardsBlock },
                        { "templateBytes", StandardsRender.Bytes(StandardsPrompt.TemplateBlock) },
                        { "standardsBytes", StandardsRender.Bytes(StandardsPrompt.StandardsBlock) }
                    });
            }

            if (tokens.Count > 0 && tokens[0].Equals("--refresh", StringComparison.OrdinalIgnoreCase))
            {
                StandardsContext.Reset();
                tokens.RemoveAt(0);
                if (tokens.Count == 0)
                    return Overview(doc, packId, "Packs re-read from disk.");
            }

            var eval = StandardsContext.Evaluate(doc, false);
            var pack = ResolvePack(eval, packId, out var packNote);

            if (tokens.Count == 0 || tokens[0].Equals("--list", StringComparison.OrdinalIgnoreCase))
                return Overview(doc, packId, null);

            var first = tokens[0].ToLowerInvariant();
            if (pack == null)
                return CommandResult.Error("NO_PACK", "No office standards pack is available" +
                    (packId != null ? " with id '" + packId + "'" : "") + ".",
                    "Run 'standards' to see where packs are searched, or set StandardsPackPath in Settings.");

            switch (first)
            {
                case "core":
                    return CommandResult.Ok(Prefix(packNote) + StandardsRender.RenderCore(pack),
                        new Dictionary<string, object> { { "pack", pack.Id }, { "core", StandardsRender.RenderCore(pack) } });

                case "--index":
                    bool started = DocumentIndexService.StartIndex(pack);
                    var folder = StandardsPackRegistry.ResolveStandardsFolder(pack);
                    return CommandResult.Ok((started ? "Indexing started" : "Indexing already running") +
                        " in the background for pack " + pack.Id + ".\nSources: standards folder '" + (folder ?? "(not set)") +
                        "' (top level) + " + pack.Sources.Count + " declared source(s).\nCheck progress with 'standards docs'.",
                        new Dictionary<string, object> { { "started", started }, { "folder", folder } });

                case "docs":
                    return Docs(pack);

                case "distill":
                    return Distill(pack, tokens.Skip(1).ToList());
            }

            // A topic sheet?
            if (pack.HasTopic(tokens[0]))
            {
                var text = pack.GetTopicText(tokens[0]);
                if (text == null)
                    return CommandResult.Error("TOPIC_MISSING", "Topic '" + tokens[0] + "' is declared by pack " + pack.Id +
                        " but its file is missing.", "Topics: " + string.Join(", ", pack.TopicNames));
                return CommandResult.Ok(Prefix(packNote) + text.TrimEnd() + "\n",
                    new Dictionary<string, object> { { "pack", pack.Id }, { "topic", tokens[0] }, { "text", text } });
            }

            // An indexed source document ("<doc> [chapter]" or "doc <doc> [chapter]")?
            var docTokens = first == "doc" ? tokens.Skip(1).ToList() : tokens;
            if (docTokens.Count > 0)
            {
                var indexed = DocumentIndexService.GetIndexed(pack);
                var match = DocumentIndexService.FindDocument(indexed, docTokens[0]);
                if (match != null)
                    return Chapter(match, string.Join(" ", docTokens.Skip(1)));
            }

            return CommandResult.Error("UNKNOWN_TOPIC", "No topic or indexed document '" + tokens[0] + "' in pack " + pack.Id + ".",
                "Topics: " + string.Join(", ", pack.TopicNames) + ". Source documents: 'standards docs'.");
        }

        private static StandardsPack ResolvePack(StandardsEvaluation eval, string packId, out string note)
        {
            note = null;
            var packs = StandardsPackRegistry.GetPacks();
            if (packId != null)
            {
                var p = packs.FirstOrDefault(x => x.Id.Equals(packId, StringComparison.OrdinalIgnoreCase));
                if (p != null && p != eval.Active) note = "(pack " + p.Id + " — not the active pack for this document)";
                return p;
            }
            if (eval.Active != null) return eval.Active;
            // Readable even when the document does not match: a single installed pack is unambiguous.
            if (packs.Count == 1)
            {
                note = "(pack " + packs[0].Id + " — NOT active for this document: its fingerprint does not match)";
                return packs[0];
            }
            return null;
        }

        private static string Prefix(string note) => note == null ? "" : note + "\n\n";

        private CommandResult Overview(Autodesk.Revit.DB.Document doc, string packId, string headline)
        {
            var eval = StandardsContext.Evaluate(doc, false);
            var discovered = StandardsPackRegistry.GetDiscovered();
            var sb = new StringBuilder();
            if (headline != null) sb.AppendLine(headline);
            sb.AppendLine("OFFICE STANDARDS");
            sb.AppendLine("================");
            if (eval.Disabled)
                sb.AppendLine("Switched OFF in Settings (StandardsEnabled = false): no rules, defaults or checks are applied.");

            var active = eval.Active;
            if (active != null)
            {
                sb.AppendLine("Active pack: " + active.Id + " v" + active.Version + " — " + active.DisplayName);
                sb.AppendLine("  fingerprint: " + eval.Match?.Summary);
                if (!string.IsNullOrEmpty(active.Owner)) sb.AppendLine("  owner: " + active.Owner);
                sb.AppendLine("  folder: " + active.Folder);
                if (active.Sources.Count > 0)
                {
                    sb.AppendLine("  sources:");
                    foreach (var s in active.Sources)
                        sb.AppendLine("    - " + s.Title + (string.IsNullOrEmpty(s.Date) ? "" : " (" + s.Date + ")"));
                }
                sb.AppendLine("  topics: " + string.Join(", ", active.TopicNames) + "  →  'standards <topic>'");
                if (active.PhaseTable != null)
                    sb.AppendLine("  phase default: '" + active.PhaseTable.DefaultKey + "' = " + active.PhaseTable.Default?.Created +
                                  "  (--phase " + string.Join("|", active.PhaseTable.Keys) + ")");
                sb.AppendLine("  checks: " + (active.ChecksPath != null ? active.ChecksFile + "  →  'check'" : "none"));
                sb.AppendLine("  library: " + (active.LibraryPaths.Count == 0 ? "none" : string.Join("; ", active.LibraryPaths)) + "  →  'library <term>'");
                sb.AppendLine("  source documents: " + DocumentIndexService.GetStatus(active) + "  →  'standards docs'");
            }
            else if (!eval.Disabled)
            {
                sb.AppendLine("Active pack: none for this document (generic Revit rules apply).");
                foreach (var r in eval.Rejections) sb.AppendLine("  - " + r);
            }

            sb.AppendLine();
            sb.AppendLine("Packs found (search order: setting StandardsPackPath → " + StandardsPackRegistry.LocalStandardsRoot +
                          " → add-in folder\\standards):");
            if (discovered.Count == 0) sb.AppendLine("  (none)");
            foreach (var d in discovered)
                sb.AppendLine("  " + (d.Pack != null ? "✓ " + d.Pack.Id + " v" + d.Pack.Version : "✗ " + d.Error) +
                              "  [" + d.Origin + "] " + d.Folder);

            var data = new Dictionary<string, object>
            {
                { "enabled", !eval.Disabled },
                { "active", active == null ? null : new Dictionary<string, object>
                    {
                        { "id", active.Id }, { "version", active.Version }, { "name", active.DisplayName },
                        { "owner", active.Owner }, { "folder", active.Folder },
                        { "fingerprint", eval.Match?.Summary },
                        { "topics", active.TopicNames.Cast<object>().ToList() },
                        { "phaseDefault", active.PhaseTable?.Default?.Created }
                    } },
                { "rejections", eval.Rejections.Cast<object>().ToList() },
                { "packs", discovered.Select(d => (object)new Dictionary<string, object>
                    { { "id", d.Pack?.Id }, { "folder", d.Folder }, { "origin", d.Origin }, { "error", d.Error } }).ToList() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static CommandResult Docs(StandardsPack pack)
        {
            var docs = DocumentIndexService.GetIndexed(pack);
            var sb = new StringBuilder();
            sb.AppendLine("SOURCE DOCUMENTS — pack " + pack.Id + " (" + DocumentIndexService.GetStatus(pack) + ")");
            sb.AppendLine("Standards folder: " + (StandardsPackRegistry.ResolveStandardsFolder(pack) ?? "(not set — Settings → Standards folder)"));
            if (docs.Count == 0)
                sb.AppendLine("No documents indexed yet. Run 'standards --index' (needs the standards folder to be reachable).");
            foreach (var d in docs)
            {
                sb.AppendLine();
                sb.AppendLine(d.Name + "  (" + d.PageCount + " pages, " + d.Chapters.Count + " chapters, indexed " +
                              d.IndexedUtc.ToLocalTime().ToString("yyyy-MM-dd") + ")");
                foreach (var c in d.Chapters.Where(c => c.Number != "0" && c.Number.Count(ch => ch == '.') <= 1))
                    sb.AppendLine("  " + c.Number + " " + c.Title + "  p" + c.PageFrom);
            }
            sb.AppendLine();
            sb.AppendLine("Read one: standards <document> <chapter>   e.g. standards leidraad 4.2");
            var data = new Dictionary<string, object>
            {
                { "status", DocumentIndexService.GetStatus(pack) },
                { "documents", docs.Select(d => (object)new Dictionary<string, object>
                    {
                        { "name", d.Name }, { "file", d.File }, { "pages", d.PageCount },
                        { "chapters", d.Chapters.Select(c => (object)(c.Number + " " + c.Title)).ToList() }
                    }).ToList() }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static CommandResult Chapter(IndexedDocument d, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                var sb = new StringBuilder();
                sb.AppendLine(d.Name + " — " + d.Chapters.Count + " chapters:");
                foreach (var c in d.Chapters) sb.AppendLine("  " + c.Label + "  p" + c.PageFrom);
                return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
                    { { "document", d.Name }, { "chapters", d.Chapters.Select(c => (object)c.Label).ToList() } });
            }
            var text = ChapterSplitter.GetChapterText(d.Chapters, query, out var label);
            if (text == null)
                return CommandResult.Error("CHAPTER_NOT_FOUND", "No chapter '" + query + "' in " + d.Name + ".",
                    "Run 'standards " + d.Name + "' to list its chapters.");
            return CommandResult.Ok(d.Name + " — " + label + "\n\n" + text + "\n",
                new Dictionary<string, object> { { "document", d.Name }, { "chapter", label }, { "text", text } });
        }

        private static CommandResult Distill(StandardsPack pack, List<string> rest)
        {
            if (rest.Any(t => t.Equals("--status", StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Ok(StandardsDistiller.Describe(pack));

            var started = StandardsDistiller.Start(pack, out var message);
            if (!started)
                return CommandResult.Error("DISTILL_UNAVAILABLE", message,
                    "Index the source documents first ('standards --index'), and configure the Claude Code CLI or an API key.");
            Logger.Info("Standards distillation started for " + pack.Id);
            return CommandResult.Ok(message + "\nCheck with 'standards distill --status'; the result is used when pack.json has \"core\": \"generated\".");
        }
    }
}
