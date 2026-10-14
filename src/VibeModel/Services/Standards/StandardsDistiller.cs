using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Plan 11 §4.0: turn the office's own manual into the always-on core, instead of a
    /// hand-typed copy. Selects the rule-bearing chapters from the indexed source documents,
    /// asks an LLM once (fixed prompt) for ≤25 imperative one-liners, and writes
    /// core.generated.md (with source hashes + date) to the pack cache. Explicit only — it runs
    /// when someone types `standards distill`, never on its own. The hand-written core.md is the
    /// reference the result should be compared against.
    /// </summary>
    public static class StandardsDistiller
    {
        public const int MaxSourceChars = 90000;
        public static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(6);

        public const string Instruction =
            "You distill an office's Revit modelling manual into the rules an AI modelling assistant must always follow.\n" +
            "Write AT MOST 25 imperative one-line rules in the manual's own language. Use the exact names from the text " +
            "(phase names, view template names, file prefixes, parameter names, folder names) in backticks. " +
            "Keep only rules that change what gets modelled or how: file and phasing rules, coordinates and links, " +
            "families and their sources, views and templates, sheets, required parameters. " +
            "No procedures, no click paths, no printers, no history. Group the rules under 3-6 short bold headings. " +
            "Output ONLY the markdown rules, nothing before or after.";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Task> Running = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> LastStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The source text for the LLM, built in the pack's priority order: each distill source
        /// names a document (or all) and its chapters (a number includes its subchapters). Every
        /// chapter appears once, with a header naming document and chapter. The text is capped
        /// at maxChars: the chapter that crosses the cap is cut there and nothing after it is
        /// added, so the pack lists the chapters that matter most first.
        /// </summary>
        public static string BuildSource(IList<IndexedDocument> docs, IList<DistillSource> sources, int maxChars, out List<string> used)
        {
            used = new List<string>();
            var sb = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in sources ?? new List<DistillSource> { new DistillSource() })
            {
                var matching = docs.Where(d => src.Document == null ||
                                               d.Name.IndexOf(src.Document, StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var d in matching)
                {
                    IEnumerable<DocChapter> selected = src.Chapters.Count == 0
                        ? d.Chapters.Where(c => c.Number != "0")
                        : src.Chapters.SelectMany(n => d.Chapters.Where(c =>
                              c.Number == n || c.Number.StartsWith(n + ".", StringComparison.Ordinal)));
                    foreach (var c in selected)
                    {
                        if (!seen.Add(d.Name + "|" + c.Number)) continue;
                        var block = "### " + d.Name + " — " + c.Label + "\n" + c.Text + "\n\n";
                        if (sb.Length + block.Length > maxChars)
                        {
                            var room = maxChars - sb.Length;
                            if (room > 500)
                            {
                                sb.Append(block.Substring(0, room)).Append("\n[… cut: text budget reached]\n");
                                used.Add(d.Name + " " + c.Number + " (cut)");
                            }
                            return sb.ToString();
                        }
                        sb.Append(block);
                        used.Add(d.Name + " " + c.Number);
                    }
                }
            }
            return sb.ToString();
        }

        public static string Describe(StandardsPack pack)
        {
            var sb = new StringBuilder();
            lock (Sync)
            {
                sb.AppendLine("Distillation for pack " + pack.Id + ": " +
                              (Running.TryGetValue(pack.Id, out var t) && !t.IsCompleted ? "RUNNING" :
                               LastStatus.TryGetValue(pack.Id, out var s) ? s : "not run in this session"));
            }
            var path = pack.GeneratedCorePath;
            if (path != null && File.Exists(path))
            {
                sb.AppendLine("Generated core: " + path + " (" + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
                sb.AppendLine("In use: " + (string.Equals(pack.CoreFile, StandardsPack.GeneratedCoreMarker, StringComparison.OrdinalIgnoreCase)
                    ? "yes (pack.json core = \"generated\")"
                    : "no — pack.json core = \"" + pack.CoreFile + "\"; set it to \"generated\" to use it"));
                sb.AppendLine();
                sb.AppendLine(File.ReadAllText(path, Encoding.UTF8));
            }
            else sb.AppendLine("No generated core yet.");
            return sb.ToString();
        }

        public static bool Start(StandardsPack pack, out string message)
        {
            var docs = DocumentIndexService.GetIndexed(pack);
            if (docs.Count == 0)
            {
                message = "No indexed source documents for pack " + pack.Id + ".";
                return false;
            }
            var source = BuildSource(docs, pack.DistillSources, MaxSourceChars, out var used);
            if (source.Length == 0)
            {
                message = "Nothing to distil: none of the pack's distill sources (" +
                          string.Join("; ", pack.DistillSources.Select(x => x.Describe())) + ") matched the indexed documents.";
                return false;
            }

            // The CLI is located on the worker thread: probing candidates spawns processes and
            // this method runs inside a command on Revit's main thread.
            var apiKey = SettingsManager.GetApiKey();

            lock (Sync)
            {
                if (Running.TryGetValue(pack.Id, out var t) && !t.IsCompleted)
                {
                    message = "A distillation is already running for " + pack.Id + ".";
                    return true;
                }
                var hashes = string.Join(", ", docs.Select(d => d.Name + "@" + d.Hash));
                Running[pack.Id] = Task.Run(() => Run(pack, source, used, hashes, apiKey));
            }
            message = "Distillation started for " + pack.Id + " over " + used.Count + " chapter(s) (" + source.Length +
                      " chars) via " + (apiKey != null ? "the Anthropic API" : "the Claude Code CLI (if installed)") + ".";
            return true;
        }

        private static void Run(StandardsPack pack, string source, List<string> used, string hashes, string apiKey)
        {
            try
            {
                var cli = apiKey == null ? ClaudeCodeBackend.LocateCli(out _) : null;
                if (apiKey == null && cli == null)
                    throw new Exception("no LLM available — set an Anthropic API key in Settings or install the Claude Code CLI");
                var prompt = Instruction + "\n\n=== MANUAL CHAPTERS ===\n\n" + source;
                var text = apiKey != null ? CallApi(apiKey, prompt) : CallCli(cli, prompt);
                text = (text ?? "").Trim();
                if (text.Length < 40) throw new Exception("the model returned no usable rules");

                var header = "<!-- generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                             " by VibeModel `standards distill` from: " + hashes + " | chapters: " + string.Join(", ", used) + " -->\n";
                Directory.CreateDirectory(Path.GetDirectoryName(pack.GeneratedCorePath));
                // Keep the previous version: a failed or worse run must be recoverable.
                if (File.Exists(pack.GeneratedCorePath))
                    File.Copy(pack.GeneratedCorePath, pack.GeneratedCorePath + ".previous", true);
                File.WriteAllText(pack.GeneratedCorePath, header + text + "\n", Encoding.UTF8);
                lock (Sync) LastStatus[pack.Id] = "done " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) +
                                                  " (" + text.Length + " chars)";
                StandardsContext.MarkPromptDirty();
            }
            catch (Exception ex)
            {
                lock (Sync) LastStatus[pack.Id] = "FAILED: " + ex.Message + " (previous core kept)";
                Logger.Warn("Standards distillation failed: " + ex.Message);
            }
        }

        private static string CallCli(string cli, string prompt)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = "-p --output-format text",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                stdin.Write(prompt);
                stdin.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit((int)CliTimeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("Claude Code CLI timed out");
                }
                if (p.ExitCode != 0) throw new Exception("Claude Code CLI exit " + p.ExitCode + ": " + errTask.Result);
                return outTask.Result;
            }
        }

        private static string CallApi(string apiKey, string prompt)
        {
            using (var http = Chat.AnthropicHttp.CreateClient(TimeSpan.FromMinutes(4)))
            {
                // Current models think by default and the thinking counts toward max_tokens: the
                // ~2,500-token answer needs room on top of it. Only text blocks are read below.
                var body = PackJson.Serializer.Serialize(new Dictionary<string, object>
                {
                    { "model", SettingsManager.GetModel() },
                    { "max_tokens", 16000 },
                    { "messages", new List<object> { new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } } }
                });
                var req = Chat.AnthropicHttp.NewMessagesRequest(apiKey, body);
                var resp = http.SendAsync(req).Result;
                var json = resp.Content.ReadAsStringAsync().Result;
                if (!resp.IsSuccessStatusCode) throw new Exception("API " + (int)resp.StatusCode + ": " + json);
                var d = PackJson.Parse(json);
                var sb = new StringBuilder();
                foreach (var block in PackJson.GetDictList(d, "content"))
                    if (PackJson.GetString(block, "type") == "text") sb.Append(PackJson.GetString(block, "text"));
                return sb.ToString();
            }
        }
    }
}
