using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat.QuickReplies
{
    /// <summary>One chip under an answer: what the button says, what it sends, and whether it changes the model.</summary>
    public sealed class QuickReply
    {
        public QuickReply(string label, string prompt, bool commits)
        {
            Label = label;
            Prompt = prompt;
            Commits = commits;
        }

        public string Label { get; }
        public string Prompt { get; }
        /// <summary>Changes the Revit model: the chip fills the input box instead of sending.</summary>
        public bool Commits { get; }
    }

    /// <summary>The live facts that keep the labels concrete (never a reason to offer anything).</summary>
    public sealed class QuickReplyContext
    {
        public string ActiveView { get; set; }
        public string Phase { get; set; }
        public string Selection { get; set; }
        public IReadOnlyList<string> ToolsCalled { get; set; }
    }

    /// <summary>One answer to make chips for. <see cref="SessionId"/> + <see cref="MessageIndex"/> say which.</summary>
    public sealed class QuickReplyRequest
    {
        public string SessionId { get; set; }
        /// <summary>The session's message count right after the answer was stored.</summary>
        public int MessageIndex { get; set; }
        public string Language { get; set; }
        public string SystemPrompt { get; set; }
        public string UserInput { get; set; }
    }

    /// <summary>
    /// Quick replies: after an answer is complete, a separate cheap model call (never on the token
    /// stream) guesses what the user was about to type next — none, or two to four one-tap chips.
    /// Transferred from DegenAI's quick replies: the same prompt structure, schema, sanitizer and
    /// UI rules, the domain swapped for Revit. Everything here except <see cref="GenerateAsync"/>
    /// is pure and tested headlessly.
    /// </summary>
    public static class QuickReplyService
    {
        public const int MinReplies = 2;
        public const int MaxReplies = 4;
        public const int MaxLabelLength = 44;
        public const int MaxPromptLength = 240;
        public const int MinAnswerLength = 40;

        // Context limits (characters).
        public const int RecentMessages = 6;
        public const int RecentMessageChars = 500;
        public const int QuestionChars = 1000;
        public const int AnswerChars = 3000;

        /// <summary>The structured-output schema (tool input / --json-schema).</summary>
        public const string SchemaJson =
            "{\"type\":\"object\",\"properties\":{" +
            "\"offer\":{\"type\":\"boolean\"}," +
            "\"replies\":{\"type\":\"array\",\"maxItems\":4,\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"label\":{\"type\":\"string\",\"maxLength\":44}," +
            "\"prompt\":{\"type\":\"string\",\"maxLength\":240}," +
            "\"commits\":{\"type\":\"boolean\"}}," +
            "\"required\":[\"label\",\"prompt\",\"commits\"]}}}," +
            "\"required\":[\"offer\",\"replies\"]}";

        // Answers that are not answers: nothing to follow up on.
        private static readonly string[] ErrorMarkers =
        {
            "[Cancelled]", "[Stopped:", "[Response was empty", "Error:", "ERROR:", "[No response was recorded",
        };

        /// <summary>The free pre-filter: no model call for short answers, cancelled turns and errors.</summary>
        public static bool ShouldOffer(string answer, bool cancelled, bool error)
        {
            if (cancelled || error) return false;
            var a = (answer ?? "").Trim();
            if (a.Length < MinAnswerLength) return false;
            foreach (var marker in ErrorMarkers)
                if (a.StartsWith(marker, StringComparison.Ordinal)) return false;
            return true;
        }

        // --- The prompt ---------------------------------------------------------------------

        /// <summary>The system prompt with {{activeView}}, {{phase}}, {{selection}} and {{toolsCalled}} placeholders.</summary>
        public const string SystemPromptTemplate =
@"You write the one-tap answers that appear directly above the chat box of VibeModel, the chat pane inside Autodesk Revit, right after VibeModel finishes a message.

There are either none of them or two to four. A single answer is never shown: a row of one button is thrown away before it reaches the user, so one good option and nothing to pair it with is the same as offering nothing.

The user reads VibeModel's answer, then either taps one of your buttons or ignores them and types. Your only job is to guess, honestly, what they were about to type.

# Voice

Write every label as the USER, in the first person, the way they would actually say it.

  Good:   ""Selecteer ze""            ""Maak er een doorsnede van""   ""Welke fase hebben ze?""
  Bad:    ""Elementen selecteren""    ""Doorsnede""                   ""Fase-informatie""

Labels are short -- a few words, never a sentence. They must fit on a small button in a narrow pane.

# Language

Write labels and prompts in the language VibeModel answered in: a Dutch answer gets Dutch buttons, an English answer English buttons. Keep Revit terms (Phase Created, View Template, Fire Rating, Level 2) exactly as the answer wrote them.

# How many

There is no quota. The right number is however many real branches the answer left open.

- If VibeModel asked a yes/no question, the ""no"" is NEVER a button. Declining costs the user nothing -- they ignore the row or type something else. Offer the yes, then the variants: the same action with a different parameter, the thing they would ask for instead, the check they would want first. ""Zal ik ze selecteren?"" gets ""Selecteer ze"", ""Alleen die op Level 2"", ""Kleur ze rood in deze view"" -- never ""Nee, laat maar"".
  This holds hardest when the offer is to UNDO or DELETE something, because there the decline sounds like a choice. It is not: leaving it alone is what happens when the user taps nothing. ""Zal ik de oude doorsnede verwijderen?"" gets ""Verwijder hem"", ""Verwijder ze allebei"", ""Hernoem hem eerst"" -- never ""Nee, laat staan"" or ""Houd hem"".
- A genuine either/or between two ACTIONS still gets both, in the order they were asked. ""Zal ik ze nu op fase 2 zetten, of eerst een lijst tonen?"" gets ""Zet ze op fase 2"" and ""Toon eerst de lijst"" -- both of those lead somewhere.
- Otherwise return two or three. Return four only when there are genuinely four distinct directions a reasonable person would go, which is rare.
- If the answer left nothing open, there is no row at all. The next section is about that case, because it is the easiest one to get wrong.

Never pad. Two sharp options beat four where two were invented to fill space.

# When to offer nothing

Before writing a single label, ask what the answer actually left open. An answer leaves something open only when VibeModel asked a question, named something it could do next, or put a decision in front of the user. If none of those three happened, set offer to false and return an empty list.

A plain statement of fact leaves nothing open. A wall height, an element count, a parameter value, a status report, an explanation that ends on a full stop -- the user asked, VibeModel answered, and they are done. ""Wat is de hoogte van deze wand?"" answered with ""3000 mm"" is finished. Do not go hunting for a direction to suggest. ""Maak er een doorsnede van"" and ""Toon alle parameters"" were not invited by that answer; you thought of them, the user did not.

This is the most common correct output, not a rare one. The row exists to save the user typing something they were already about to type. If you cannot name what they were about to type, they were not about to type anything, and an empty row is the honest answer.

# What each field means

- label -- what the button says. The user's voice, short.
- prompt -- what gets sent when they tap it. A faithful, fuller version of the label, phrased as the user talking to VibeModel. It must ask for exactly what the label promises. Never smuggle in a different request: ""Selecteer ze"" must not become ""selecteer ze en verwijder ze"".
- commits -- true when acting on this would change the Revit model: creating, deleting, moving or copying elements, setting a parameter, changing a phase, joining or cutting geometry, colour overrides, creating views, sections or sheets, placing viewports, dimensions, text or tags, loading families or links. Selecting, zooming, temporary isolate/hide, screenshots and questions do not commit. A commit option is not sent on tap; it is typed into the box so the user presses send themselves. When in doubt about whether something changes the model, mark it true.

# Hard rules

- Only propose things VibeModel can actually do: inspect, select, zoom to, colour, isolate or hide elements; explain why an element is not visible; create walls, floors, grids, levels, rooms, views, sections, sheets, viewports, dimensions, text and tags; set parameters and phases; join or cut geometry; check the model against the office standards; read the office manual and the project documents; read imported DWG and point-cloud data; take a screenshot of a view.
- Never propose anything the answer itself said was impossible or unavailable.
- Never offer two options that mean the same thing in different words.
- Never write a label that only makes sense if the user re-reads the whole answer.
- Never invent a number, element id, name, level, phase or size that was not in the conversation.
- Every button must make something happen. Never write a label whose result is that nothing changes: ""Nee bedankt"", ""Laat maar"", ""Nu niet"", ""Niet nodig"", ""Laat het zo"", ""No thanks"", ""Not now"", ""Leave it"", ""Keep it as is"", ""Maybe later"". If the only honest second option is declining, set offer to false and return an empty list instead -- no row is better than a row that wastes a tap.

# Context

Active view: {{activeView}}
Phase: {{phase}}
Selection: {{selection}}
Tools VibeModel used to produce this answer: {{toolsCalled}}

Use these only to keep your labels concrete. They are not a reason to offer anything.";

        public static string BuildSystemPrompt(QuickReplyContext ctx)
        {
            ctx = ctx ?? new QuickReplyContext();
            string Or(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
            var tools = ctx.ToolsCalled == null || ctx.ToolsCalled.Count == 0
                ? "none"
                : string.Join(", ", ctx.ToolsCalled.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
            return SystemPromptTemplate
                .Replace("{{activeView}}", Or(ctx.ActiveView, "unknown"))
                .Replace("{{phase}}", Or(ctx.Phase, "unknown"))
                .Replace("{{selection}}", Or(ctx.Selection, "nothing selected"))
                .Replace("{{toolsCalled}}", tools);
        }

        /// <summary>
        /// The user message: the last <see cref="RecentMessages"/> STORED messages before this turn
        /// (not the model's working transcript), each cut to <see cref="RecentMessageChars"/>, then
        /// the question and the answer. The current turn is cut from <paramref name="stored"/>
        /// when it is there (it is passed separately).
        /// </summary>
        public static string BuildUserInput(IReadOnlyList<ChatSessionMessage> stored, string question, string answer)
        {
            var earlier = (stored ?? new List<ChatSessionMessage>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content) &&
                            (m.Role == ChatSession.RoleUser || m.Role == ChatSession.RoleAssistant))
                .ToList();

            // Drop the current turn: a trailing assistant message, then the user question it answered.
            if (earlier.Count > 0 && earlier[earlier.Count - 1].Role == ChatSession.RoleAssistant)
                earlier.RemoveAt(earlier.Count - 1);
            if (earlier.Count > 0 && earlier[earlier.Count - 1].Role == ChatSession.RoleUser &&
                string.Equals((earlier[earlier.Count - 1].Content ?? "").Trim(), (question ?? "").Trim(), StringComparison.Ordinal))
                earlier.RemoveAt(earlier.Count - 1);

            var sb = new StringBuilder();
            var recent = earlier.Skip(Math.Max(0, earlier.Count - RecentMessages)).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("EARLIER IN THIS CONVERSATION:");
                foreach (var m in recent)
                    sb.Append(m.Role == ChatSession.RoleUser ? "User: " : "VibeModel: ")
                      .AppendLine(Cut(m.Content, RecentMessageChars));
                sb.AppendLine();
            }
            sb.AppendLine("THE USER JUST ASKED:");
            sb.AppendLine(Cut(question, QuestionChars));
            sb.AppendLine();
            sb.AppendLine("VIBEMODEL ANSWERED:");
            sb.AppendLine(Cut(answer, AnswerChars));
            sb.AppendLine();
            sb.Append("What should the user be offered as one-tap answers?");
            return sb.ToString();
        }

        private static string Cut(string text, int max)
        {
            var t = (text ?? "").Trim();
            return t.Length <= max ? t : t.Substring(0, max) + "…";
        }

        // --- The model's output -------------------------------------------------------------

        /// <summary>
        /// The model is never trusted: offer=false → none; an empty label is skipped; a label over
        /// 44 or a prompt over 240 characters is DROPPED (never cut); an empty prompt becomes the
        /// label; decline labels are dropped and logged; duplicates (normalized) dropped; at most 4;
        /// fewer than 2 left → no row at all.
        /// </summary>
        public static IReadOnlyList<QuickReply> ParseAndSanitize(string json)
        {
            var none = new List<QuickReply>();
            if (string.IsNullOrWhiteSpace(json)) return none;

            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex)
            {
                Logger.Info("Quick replies: invalid JSON from the model - " + ex.Message);
                return none;
            }
            if (root == null) return none;
            if (!(root.TryGetValue("offer", out var offer) && offer is bool b && b)) return none;
            if (!root.TryGetValue("replies", out var repliesObj) || !(repliesObj is IEnumerable items)) return none;

            var kept = new List<QuickReply>();
            var seen = new HashSet<string>();
            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object> d)) continue;
                var label = (GetString(d, "label") ?? "").Trim();
                var prompt = (GetString(d, "prompt") ?? "").Trim();
                if (label.Length == 0) continue;
                if (label.Length > MaxLabelLength) continue;
                if (DeclineLabels.IsDecline(label))
                {
                    Logger.Info("Quick replies: dropped decline chip '" + label + "'");
                    continue;
                }
                if (prompt.Length == 0) prompt = label;
                if (prompt.Length > MaxPromptLength) continue;
                if (!seen.Add(DeclineLabels.Normalize(label))) continue;
                var commits = d.TryGetValue("commits", out var c) && c is bool cb && cb;
                kept.Add(new QuickReply(label, prompt, commits));
                if (kept.Count == MaxReplies) break;
            }
            return kept.Count < MinReplies ? none : kept;
        }

        private static string GetString(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) ? v as string : null;

        /// <summary>
        /// The JSON object out of a `claude -p --output-format json --json-schema …` envelope: the
        /// `structured_output` field, else a JSON object inside `result`. Null for an error
        /// envelope or when there is nothing parseable.
        /// </summary>
        public static string ParseCliOutput(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> envelope = null;
            // The envelope is the last JSON line (some versions print warnings first).
            foreach (var line in stdout.Split('\n').Reverse())
            {
                var t = line.Trim();
                if (!t.StartsWith("{", StringComparison.Ordinal)) continue;
                try { envelope = serializer.DeserializeObject(t) as Dictionary<string, object>; }
                catch { envelope = null; }
                if (envelope != null) break;
            }
            if (envelope == null) return null;
            if (envelope.TryGetValue("is_error", out var err) && err is bool e && e) return null;
            if (envelope.TryGetValue("structured_output", out var structured) && structured is Dictionary<string, object>)
                return serializer.Serialize(structured);
            return envelope.TryGetValue("result", out var result) ? ExtractJsonObject(result as string) : null;
        }

        /// <summary>The outermost {...} of a text (a model that wrapped its JSON in prose or a fence).</summary>
        public static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text.Substring(start, end - start + 1) : null;
        }

        // --- Running it ---------------------------------------------------------------------

        /// <summary>
        /// Fire-and-forget: asks <paramref name="model"/> on a pool thread with its own timeout,
        /// sanitizes, and hands the chips (possibly none) to <paramref name="onResult"/> — on the
        /// pool thread; the caller marshals to the UI and checks the result is still current.
        /// Never throws.
        /// </summary>
        public static Task GenerateAsync(ISuggestionModel model, QuickReplyRequest request,
            Action<QuickReplyRequest, IReadOnlyList<QuickReply>> onResult)
        {
            return Task.Run(() =>
            {
                IReadOnlyList<QuickReply> replies = new List<QuickReply>();
                try
                {
                    if (model == null) return;
                    var started = DateTime.UtcNow;
                    string json;
                    using (var cts = new CancellationTokenSource(model.Timeout))
                        json = model.Complete(request.SystemPrompt, request.UserInput, cts.Token);
                    var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        Logger.Info("Quick replies: empty response from " + model.Name + " after " + ms + " ms");
                        return;
                    }
                    Logger.Info("Quick replies: " + model.Name + " answered in " + ms + " ms: " + json);
                    replies = ParseAndSanitize(json);
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("Quick replies: " + model?.Name + " timed out after " + model?.Timeout.TotalSeconds + " s");
                }
                catch (Exception ex)
                {
                    Logger.Info("Quick replies: " + model?.Name + " failed - " + ex.Message);
                }
                finally
                {
                    try { onResult?.Invoke(request, replies); }
                    catch (Exception ex) { Logger.Info("Quick replies: result handler failed - " + ex.Message); }
                }
            });
        }
    }
}
